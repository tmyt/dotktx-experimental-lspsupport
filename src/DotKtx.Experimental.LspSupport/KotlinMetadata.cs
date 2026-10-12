using System.Reflection;
using Google.Protobuf;
using JvmClass = DotKt.Jvm.Metadata.Class;
using JvmConstructor = DotKt.Jvm.Metadata.Constructor;
using JvmEnumEntry = DotKt.Jvm.Metadata.EnumEntry;
using JvmFunction = DotKt.Jvm.Metadata.Function;
using JvmProperty = DotKt.Jvm.Metadata.Property;
using JvmType = DotKt.Jvm.Metadata.Type;
using JvmTypeParameter = DotKt.Jvm.Metadata.TypeParameter;
using JvmValueParameter = DotKt.Jvm.Metadata.ValueParameter;

namespace DotKtx.Experimental.LspSupport;

/// <summary>The payload of one <c>kotlin.Metadata</c> annotation.</summary>
internal sealed record KotlinMetadataAnnotation(
    int Kind,
    IReadOnlyList<int> MetadataVersion,
    int ExtraInt,
    IReadOnlyList<string> Data1,
    IReadOnlyList<string> Data2);

/// <summary>
/// Encodes the metadata payload the way <c>kotlin.Metadata.d1</c> expects: a marker char followed by
/// one char per byte, split into chunks a constant-pool UTF-8 entry can hold. Verified against the
/// Kotlin 2.4.10 reference implementation on generated vectors.
/// </summary>
internal static class BitEncoding
{
    // A leading NUL selects the reader's UTF-8 mode, in which every payload byte is one char.
    private const char Utf8ModeMarker = '\0';
    private const int MaxCharsPerString = 32767;

    public static string[] Encode(byte[] data)
    {
        var chars = new char[data.Length + 1];
        chars[0] = Utf8ModeMarker;
        for (var i = 0; i < data.Length; i++) chars[i + 1] = (char)data[i];

        var parts = new List<string>((chars.Length + MaxCharsPerString - 1) / MaxCharsPerString);
        for (var offset = 0; offset < chars.Length; offset += MaxCharsPerString)
            parts.Add(new string(chars, offset, Math.Min(MaxCharsPerString, chars.Length - offset)));
        return parts.ToArray();
    }
}

/// <summary>
/// The annotation's own string table. Every index resolves to its literal string, which is expressed
/// by a single default record spanning the whole table; the compiler's predefined-index and descriptor
/// rewriting shortcuts only exist to reuse constant-pool entries and are not needed to author a table.
/// </summary>
internal sealed class JvmStringTable
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly List<string> _strings = [];

    public IReadOnlyList<string> Strings => _strings;

    public int Get(string value)
    {
        if (_indices.TryGetValue(value, out var existing)) return existing;
        var index = _strings.Count;
        _strings.Add(value);
        _indices[value] = index;
        return index;
    }

    public DotKt.Jvm.Metadata.StringTableTypes BuildTypes()
    {
        var types = new DotKt.Jvm.Metadata.StringTableTypes();
        if (_strings.Count != 0)
            types.Record.Add(new DotKt.Jvm.Metadata.StringTableTypes.Types.Record { Range = _strings.Count });
        return types;
    }
}

/// <summary>The JVM name and descriptor a projected member actually received in the classfile.</summary>
internal sealed record JvmMemberSignature(string Name, string Descriptor);

/// <summary>
/// Transcribes a KLIB declaration into the metadata a Kotlin analyzer reads from a compiled library.
/// This is what plain Java classfiles cannot do: exact platform types (<c>String!</c>), declaration-site
/// variance, extension receivers, default arguments, and a Kotlin name that differs from the JVM one.
/// A class that carries this annotation is read <em>only</em> through it, so anything omitted here
/// disappears from the editor even though the classfile still declares it.
/// </summary>
internal sealed class KotlinMetadataWriter(JavaEmitContext context)
{
    private const int KindClass = 1;
    private const int KindFileFacade = 2;

    /// <summary>
    /// Names the module mapping and the module every declaration claims membership of. Both have to
    /// agree, otherwise an `internal` declaration would be invisible across the boundary.
    /// </summary>
    public const string ModuleName = "dotkt_clr";
    private const string PlatformTypeCapabilityId = "kotlin.jvm.PlatformType";

    // JVM IR plus stable ABI, which is what the 2.x compiler stamps on an ordinary release build. The
    // pre-release bit is deliberately absent: this metadata is authored, not compiled.
    private const int ExtraIntJvmIr = 1 << 4;
    private const int ExtraIntJvmIrStableAbi = 1 << 5;

    private static readonly int[] MetadataVersion = ReadMetadataVersion();

    public static IReadOnlyList<int> MetadataVersionNumbers => MetadataVersion;

    private readonly JavaEmitContext _context = context;

    /// <summary>The JVM setter each extension property received, keyed by the declaration it came from.</summary>
    public IReadOnlyDictionary<KlibProperty, JvmMemberSignature> PropertySetters { get; set; } =
        new Dictionary<KlibProperty, JvmMemberSignature>();

    public KotlinMetadataAnnotation WriteClass(
        KlibType type,
        IReadOnlyList<(KlibProperty Source, JvmMemberSignature Signature)> fields,
        IReadOnlyList<(KlibFunction Source, JvmMemberSignature Signature)> methods,
        IReadOnlyList<(KlibConstructor Source, JvmMemberSignature Signature)> constructors)
    {
        var isAnnotation = type.Kind == KlibTypeKind.AnnotationClass;
        var strings = new JvmStringTable();
        var message = new JvmClass
        {
            Flags = type.RawFlags,
            FqName = strings.Get(type.JvmClassId),
        };
        foreach (var parameter in type.TypeParameters)
            message.TypeParameter.Add(TypeParameter(parameter, type, strings));
        foreach (var supertype in type.Supertypes)
            if (Supertype(supertype, type, strings) is { } written) message.Supertype.Add(written);
        foreach (var nested in type.NestedTypes.Where(nested => _context.IsEmitted(nested.FullName)))
        {
            message.NestedClassName.Add(strings.Get(nested.SimpleName));
            if (nested.Kind == KlibTypeKind.CompanionObject)
                message.CompanionObjectName = strings.Get(nested.SimpleName);
        }
        foreach (var entry in type.EnumEntries)
            message.EnumEntry.Add(new JvmEnumEntry { Name = strings.Get(entry) });

        foreach (var (source, signature) in constructors)
            message.Constructor.Add(Constructor(source, signature, type, strings, isAnnotation));
        foreach (var (source, signature) in methods)
            message.Function.Add(Function(source, signature, type, strings));
        foreach (var (source, signature) in fields)
            message.Property.Add(Property(source, signature, type, strings, isAnnotation));

        return Annotate(KindClass, strings, message);
    }

    /// <summary>
    /// A file facade holds the top-level callables of one package. Kotlin has no other JVM shape for
    /// them: they are not members of any class, so without a facade plus the module mapping that names
    /// it, they simply do not exist for the analyzer.
    /// </summary>
    public KotlinMetadataAnnotation WriteFacade(
        KlibType facade,
        IReadOnlyList<(KlibProperty Source, JvmMemberSignature Signature)> fields,
        IReadOnlyList<(KlibFunction Source, JvmMemberSignature Signature)> methods)
    {
        var strings = new JvmStringTable();
        var message = new DotKt.Jvm.Metadata.Package { JvmModuleName = strings.Get(ModuleName) };
        foreach (var (source, signature) in methods)
            message.Function.Add(Function(source, signature, facade, strings));
        foreach (var (source, signature) in fields)
            message.Property.Add(Property(source, signature, facade, strings));
        return Annotate(KindFileFacade, strings, message);
    }

    private KotlinMetadataAnnotation Annotate(int kind, JvmStringTable strings, IMessage message)
    {
        using var payload = new MemoryStream();
        // The reader takes a length-delimited StringTableTypes followed by the bare message.
        strings.BuildTypes().WriteDelimitedTo(payload);
        message.WriteTo(payload);
        return new KotlinMetadataAnnotation(
            kind,
            MetadataVersion,
            ExtraIntJvmIr | ExtraIntJvmIrStableAbi,
            BitEncoding.Encode(payload.ToArray()),
            strings.Strings);
    }

    private JvmConstructor Constructor(
        KlibConstructor constructor,
        JvmMemberSignature signature,
        KlibType owner,
        JvmStringTable strings,
        bool isAnnotation)
    {
        var message = new JvmConstructor { Flags = constructor.RawFlags };
        // An annotation interface has no `<init>`, and the compiler writes no signature for one either.
        if (!isAnnotation)
            message.JvmSignature = new DotKt.Jvm.Metadata.JvmMethodSignature
            {
                Name = strings.Get(signature.Name),
                Desc = strings.Get(signature.Descriptor),
            };
        foreach (var parameter in constructor.Parameters)
            message.ValueParameter.Add(ValueParameter(parameter, owner, strings));
        return message;
    }

    private JvmFunction Function(
        KlibFunction function,
        JvmMemberSignature signature,
        KlibType owner,
        JvmStringTable strings)
    {
        var message = new JvmFunction
        {
            Flags = function.RawFlags,
            Name = strings.Get(function.Name),
            JvmSignature = new DotKt.Jvm.Metadata.JvmMethodSignature
            {
                Name = strings.Get(signature.Name),
                Desc = strings.Get(signature.Descriptor),
            },
        };
        message.ReturnType = RequiredType(
            function.ReturnType, owner, strings, _context.SignatureScope(function, owner));
        var receiverScope = _context.SignatureScope(function, owner);
        if (Type(function.ReceiverType, owner, strings, receiverScope) is { } receiverType)
            message.ReceiverType = receiverType;
        foreach (var parameter in function.TypeParameters)
            message.TypeParameter.Add(TypeParameter(parameter, owner, strings));
        foreach (var parameter in function.ContextParameters)
            message.ContextParameter.Add(ValueParameter(parameter, owner, strings, receiverScope));
        foreach (var parameter in function.Parameters)
            message.ValueParameter.Add(ValueParameter(parameter, owner, strings, receiverScope));
        return message;
    }

    private JvmProperty Property(
        KlibProperty property,
        JvmMemberSignature signature,
        KlibType owner,
        JvmStringTable strings,
        bool accessorIsMethod = false)
    {
        var jvmMember = new DotKt.Jvm.Metadata.JvmMethodSignature
        {
            Name = strings.Get(signature.Name),
            Desc = strings.Get(signature.Descriptor),
        };
        var message = new JvmProperty
        {
            Flags = property.RawFlags,
            Name = strings.Get(property.Name),
            // The projection gives a CLR property a backing field rather than accessors, so the field
            // is the only JVM member the metadata can point at.
            // An extension property is linked through its accessors; a plain one through its field.
            JvmSignature = accessorIsMethod || property.ReceiverType is not null
                ? BuildAccessorSignature(property, jvmMember, strings)
                : new DotKt.Jvm.Metadata.JvmPropertySignature
                {
                    Field = new DotKt.Jvm.Metadata.JvmFieldSignature
                    {
                        Name = jvmMember.Name,
                        Desc = jvmMember.Desc,
                    },
                },
        };
        // A property whose flags declare a setter must carry the setter's value parameter: the
        // deserializer reads it unconditionally and a missing type aborts the whole class.
        if (KlibFlags.PropertyHasSetter(property.RawFlags))
            message.SetterValueParameter = new JvmValueParameter
            {
                Name = strings.Get("<set-?>"),
                Type = RequiredType(property.ReturnType, owner, strings),
            };
        if (property.GetterFlags is { } getterFlags) message.GetterFlags = getterFlags;
        if (property.SetterFlags is { } setterFlags) message.SetterFlags = setterFlags;
        message.ReturnType = RequiredType(property.ReturnType, owner, strings);
        if (Type(property.ReceiverType, owner, strings) is { } receiverType) message.ReceiverType = receiverType;
        foreach (var parameter in property.TypeParameters)
            message.TypeParameter.Add(TypeParameter(parameter, owner, strings));
        return message;
    }

    private DotKt.Jvm.Metadata.JvmPropertySignature BuildAccessorSignature(
        KlibProperty property,
        DotKt.Jvm.Metadata.JvmMethodSignature getter,
        JvmStringTable strings)
    {
        var signature = new DotKt.Jvm.Metadata.JvmPropertySignature { Getter = getter };
        if (PropertySetters.TryGetValue(property, out var setter))
            signature.Setter = new DotKt.Jvm.Metadata.JvmMethodSignature
            {
                Name = strings.Get(setter.Name),
                Desc = strings.Get(setter.Descriptor),
            };
        return signature;
    }

    private JvmValueParameter ValueParameter(
        KlibValueParameter parameter,
        KlibType owner,
        JvmStringTable strings,
        KlibType? extraScope = null)
    {
        var message = new JvmValueParameter
        {
            Flags = parameter.RawFlags,
            Name = strings.Get(parameter.Name),
        };
        if (Type(parameter.VarargElementType, owner, strings, extraScope) is { } vararg)
        {
            message.VarargElementType = vararg;
            // A vararg's declared type is the array; the KLIB may record only the element, but the
            // deserializer rejects a value parameter that carries no type at all.
            message.Type = Type(parameter.Type, owner, strings, extraScope) ?? ArrayOf(vararg, strings);
        }
        else message.Type = RequiredType(parameter.Type, owner, strings, extraScope);
        return message;
    }

    private JvmTypeParameter TypeParameter(
        KlibTypeParameter parameter,
        KlibType owner,
        JvmStringTable strings)
    {
        var message = new JvmTypeParameter
        {
            Id = parameter.Id,
            Name = strings.Get(parameter.Name),
            Reified = parameter.IsReified,
            Variance = parameter.Variance switch
            {
                KlibVariance.In => JvmTypeParameter.Types.Variance.In,
                KlibVariance.Out => JvmTypeParameter.Types.Variance.Out,
                _ => JvmTypeParameter.Types.Variance.Inv,
            },
        };
        foreach (var bound in parameter.UpperBounds)
            if (Type(bound, owner, strings) is { } written) message.UpperBound.Add(written);
        return message;
    }

    /// <summary>A type for a position the deserializer treats as mandatory; an absent one becomes Any?.</summary>
    private JvmType RequiredType(
        KlibTypeRef? reference,
        KlibType owner,
        JvmStringTable strings,
        KlibType? extraScope = null) =>
        Type(reference, owner, strings, extraScope) ?? new JvmType
        {
            ClassName = strings.Get("kotlin/Any"),
            Nullable = true,
        };

    private static JvmType ArrayOf(JvmType element, JvmStringTable strings)
    {
        var array = new JvmType { ClassName = strings.Get("kotlin/Array") };
        array.Argument.Add(new JvmType.Types.Argument
        {
            Projection = JvmType.Types.Argument.Types.Projection.Out,
            Type = element,
        });
        return array;
    }

    private JvmType? Supertype(KlibTypeRef supertype, KlibType owner, JvmStringTable strings)
    {
        if (supertype.ClassName is not { } name) return null;
        if (name is "kotlin.Enum" or "kotlin.Annotation" or "kotlin.Any") return Type(supertype, owner, strings);
        return _context.ResolveSupertype(supertype, owner) is null ? null : Type(supertype, owner, strings);
    }

    private JvmType? Type(
        KlibTypeRef? reference,
        KlibType owner,
        JvmStringTable strings,
        KlibType? extraScope = null)
    {
        if (reference is null) return null;
        var message = new JvmType { Nullable = reference.IsNullable };
        if (reference.TypeParameterId is { } id) message.TypeParameter = id;
        else if (reference.TypeParameterName is { } variable)
            message.TypeParameterName = strings.Get(variable);
        else if (reference.ClassName is not null)
            message.ClassName = strings.Get(_context.ClassId(reference, owner, extraScope));
        else return null;

        foreach (var argument in reference.Arguments)
        {
            var written = new JvmType.Types.Argument
            {
                Projection = argument.Projection switch
                {
                    KlibProjection.In => JvmType.Types.Argument.Types.Projection.In,
                    KlibProjection.Out => JvmType.Types.Argument.Types.Projection.Out,
                    KlibProjection.Star => JvmType.Types.Argument.Types.Projection.Star,
                    _ => JvmType.Types.Argument.Types.Projection.Inv,
                },
            };
            if (argument.Projection != KlibProjection.Star &&
                Type(argument.Type, owner, strings, extraScope) is { } argumentType)
                written.Type = argumentType;
            message.Argument.Add(written);
        }

        // This is the payload that no plain Java classfile can carry: a CLR reference type is flexible,
        // and naming the capability turns the pair of bounds back into `String!` in the editor.
        if (reference.FlexibleUpperBound is { } upperBound &&
            Type(upperBound, owner, strings, extraScope) is { } writtenUpperBound)
        {
            message.FlexibleUpperBound = writtenUpperBound;
            message.FlexibleTypeCapabilitiesId = strings.Get(PlatformTypeCapabilityId);
            _context.Report.Count("metadata.type.flexible");
        }
        return message;
    }

    private static int[] ReadMetadataVersion()
    {
        // The metadata version tracks the Kotlin release the analysis libraries approximate, so it comes
        // from the same property that pins the shipped stdlib rather than from a constant that can drift.
        var configured = typeof(KotlinMetadataWriter).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DotKtKotlinVersion")?.Value;
        var parts = (configured ?? "2.4.10").Split('.');
        return [
            parts.Length > 0 && int.TryParse(parts[0], out var major) ? major : 2,
            parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 4,
            parts.Length > 2 && int.TryParse(parts[2], out var patch) ? patch : 10,
        ];
    }

}

/// <summary>
/// Writes <c>META-INF/&lt;module&gt;.kotlin_module</c>, the mapping from a package to the facade classes
/// holding its top-level callables. The file is the metadata version as big-endian int32s followed by a
/// bare protobuf; the shape was read off the module file the Kotlin compiler emits.
/// </summary>
internal static class KotlinModuleMapping
{
    public static byte[] Write(IReadOnlyDictionary<string, IReadOnlyList<string>> facadesByPackage)
    {
        var module = new DotKt.Jvm.Metadata.Module
        {
            StringTable = new DotKt.Jvm.Metadata.StringTableTypes(),
            QualifiedNames = new DotKt.Jvm.Metadata.QualifiedNameTable(),
        };
        foreach (var (packageName, facades) in facadesByPackage.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var parts = new DotKt.Jvm.Metadata.PackageParts { PackageFqn = packageName };
            foreach (var facade in facades.Order(StringComparer.Ordinal)) parts.ClassName.Add(facade);
            module.PackageParts.Add(parts);
        }

        using var output = new MemoryStream();
        var version = KotlinMetadataWriter.MetadataVersionNumbers;
        WriteBigEndian(output, version.Count);
        foreach (var part in version) WriteBigEndian(output, part);
        WriteBigEndian(output, 0); // no module flags
        module.WriteTo(output);
        return output.ToArray();
    }

    private static void WriteBigEndian(Stream output, int value) =>
        output.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
