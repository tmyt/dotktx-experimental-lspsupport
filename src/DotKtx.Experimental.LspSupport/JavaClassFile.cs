using System.Buffers.Binary;
using System.Text;

namespace DotKtx.Experimental.LspSupport;

/// <summary>
/// Writes one Java 8 classfile per projected KLIB declaration. Nothing here is meant to run: instance
/// methods are <c>native</c>, and the few members the JVM format insists must carry code get a body that
/// only returns a default value. What matters is that every descriptor, generic signature and nested
/// binary name is exactly what a Kotlin analyzer would read from a real compiled library.
/// </summary>
internal static class JavaClassFile
{
    private const ushort AccPublic = 0x0001;
    private const ushort AccPrivate = 0x0002;
    private const ushort AccProtected = 0x0004;
    private const ushort AccStatic = 0x0008;
    private const ushort AccFinal = 0x0010;
    private const ushort AccSuper = 0x0020;
    private const ushort AccVarargs = 0x0080;
    private const ushort AccNative = 0x0100;
    private const ushort AccInterface = 0x0200;
    private const ushort AccAbstract = 0x0400;
    private const ushort AccSynthetic = 0x1000;
    private const ushort AccAnnotation = 0x2000;
    private const ushort AccEnum = 0x4000;

    private const string ObjectInternalName = "java/lang/Object";
    private const string EnumInternalName = "java/lang/Enum";

    public static byte[] Write(KlibType type, JavaEmitContext context, bool isFacade = false)
    {
        var pool = new ConstantPool();
        var internalName = type.JvmInternalName;
        var isInterface = type.Kind == KlibTypeKind.Interface;
        var isEnum = type.Kind == KlibTypeKind.EnumClass;
        var isObject = type.Kind == KlibTypeKind.Object;
        // A CLR attribute becomes a JVM annotation interface: its properties are the annotation's
        // members, declared as abstract no-argument methods, and it has no constructor on the JVM side.
        var isAnnotation = type.Kind == KlibTypeKind.AnnotationClass;
        var selfDescriptor = "L" + internalName + ";";

        // A non-inner nested type does not see its outer type variables, exactly as in Kotlin and Java.
        var outerScope = type.IsInner && context.World.Find(type.DeclaringTypeFullName) is { } outer
            ? context.Scope(outer, null, outer.TypeParameters)
            : null;
        var scope = context.Scope(type, outerScope, type.TypeParameters);

        var hierarchy = ResolveHierarchy(type, context, isInterface || isAnnotation, isEnum) with { };
        if (isAnnotation)
            hierarchy = hierarchy with
            {
                InterfaceInternalNames = ["java/lang/annotation/Annotation"],
                InterfaceReferences = [],
            };
        var thisClass = pool.Class(internalName);
        var superclass = pool.Class(hierarchy.SuperclassInternalName);
        var interfaces = hierarchy.InterfaceInternalNames.Select(pool.Class).ToArray();
        var superclassConstructor = pool.MethodRef(hierarchy.SuperclassInternalName, "<init>", "()V");

        var fields = new List<FieldEntry>();
        if (isEnum)
            foreach (var entry in type.EnumEntries.Where(IsValidMemberName).Distinct(StringComparer.Ordinal))
                fields.Add(new FieldEntry(
                    (ushort)(AccPublic | AccStatic | AccFinal | AccEnum),
                    entry, selfDescriptor, pool.Utf8(entry), pool.Utf8(selfDescriptor), null));
        if (isObject)
            fields.Add(new FieldEntry(
                (ushort)(AccPublic | AccStatic | AccFinal),
                "INSTANCE", selfDescriptor, pool.Utf8("INSTANCE"), pool.Utf8(selfDescriptor), null));
        if (!isAnnotation)
            fields.AddRange(type.Properties
                .Where(property => property.ReceiverType is null)
                .Select(property => Field(property, type, context, pool, scope, isFacade))
                .OfType<FieldEntry>());
        var uniqueFields = ResolveCollisions(
            fields,
            field => (field.Name, field.Descriptor),
            (field, name) => field with { Name = name, NameIndex = pool.Utf8(name) },
            type, context, "field");

        var methods = new List<MethodEntry>();
        var declaresAbstractMember = false;
        // An extension property has no backing field - it is a getter, and a setter when it is a `var`,
        // both taking the receiver as their first parameter. Dropping them would lose core Kotlin API
        // such as `CharSequence.lastIndex` and `Collection.indices`.
        if (!isAnnotation)
            foreach (var property in type.Properties.Where(property => property.ReceiverType is not null))
                methods.AddRange(ExtensionPropertyAccessors(property, type, context, pool, scope, isFacade));
        if (isAnnotation)
            methods.AddRange(type.Properties
                .Select(property => AnnotationMember(property, type, context, pool, scope))
                .OfType<MethodEntry>());
        foreach (var function in type.Functions)
        {
            var method = Method(function, type, context, pool, scope, isInterface, isFacade);
            if (method is null) continue;
            declaresAbstractMember |= (method.Access & AccAbstract) != 0;
            methods.Add(method);
        }
        if (isEnum)
        {
            methods.Add(NativeStatic(pool, "values", "()[" + selfDescriptor));
            methods.Add(NativeStatic(pool, "valueOf", "(Ljava/lang/String;)" + selfDescriptor));
        }
        if (!isInterface && !isFacade && !isAnnotation)
        {
            methods.AddRange(type.Constructors
                .Select(constructor => Constructor(constructor, type, context, pool, scope, superclassConstructor))
                .OfType<MethodEntry>());
            // Every constructor body calls the superclass' no-argument constructor, so each projected
            // class has to offer one. A synthetic filler keeps the archive internally consistent without
            // showing up as an extra completion candidate.
            if (methods.All(method => method.Name != "<init>" || method.Descriptor != "()V"))
            {
                context.Report.Count("java.constructor.synthetic");
                methods.Add(new MethodEntry(
                    (ushort)((isEnum ? AccPrivate : AccProtected) | AccSynthetic), "<init>", "()V",
                    pool.Utf8("<init>"), pool.Utf8("()V"), null, [],
                    ConstructorBody(superclassConstructor), MaxStack: 1, MaxLocals: 1));
            }
        }
        var uniqueMethods = ResolveCollisions(
            methods,
            method => (method.Name, method.Descriptor),
            (method, name) => method with { Name = name, NameIndex = pool.Utf8(name) },
            type, context, "method")
            .OrderBy(method => method.Name == "<init>" ? 0 : 1)
            .ThenBy(method => method.Name, StringComparer.Ordinal)
            .ThenBy(method => method.Descriptor, StringComparer.Ordinal)
            .ToArray();

        // A class that carries kotlin.Metadata is read only through it, so the annotation is built from
        // the members that actually survived deduplication rather than from the KLIB declaration.
        byte[]? metadataBody = null;
        if (context.EmitKotlinMetadata)
        {
            var metadataWriter = new KotlinMetadataWriter(context);
            var projectedFields = uniqueFields
                .Where(field => field.Source is not null)
                .Select(field => (field.Source!, new JvmMemberSignature(field.Name, field.Descriptor)))
                .Concat(uniqueMethods
                    .Where(method => method.SourceProperty is not null && !method.IsPropertySetter)
                    .Select(method => (method.SourceProperty!, new JvmMemberSignature(method.Name, method.Descriptor))))
                .ToArray();
            var projectedSetters = uniqueMethods
                .Where(method => method.SourceProperty is not null && method.IsPropertySetter)
                .ToDictionary(
                    method => method.SourceProperty!,
                    method => new JvmMemberSignature(method.Name, method.Descriptor));
            var projectedMethods = uniqueMethods
                .Where(method => method.Source is not null)
                .Select(method => (method.Source!, new JvmMemberSignature(method.Name, method.Descriptor)))
                .ToArray();
            metadataWriter.PropertySetters = projectedSetters;
            var metadata = isFacade
                ? metadataWriter.WriteFacade(type, projectedFields, projectedMethods)
                : metadataWriter.WriteClass(
                    type,
                    projectedFields,
                    projectedMethods,
                    isAnnotation
                        ? AnnotationConstructor(type, context)
                        : uniqueMethods
                            .Where(method => method.SourceConstructor is not null)
                            .Select(method => (method.SourceConstructor!, new JvmMemberSignature(method.Name, method.Descriptor)))
                            .ToArray());
            metadataBody = AnnotationBody(metadata, pool);
            context.Report.Count("metadata.class");
        }

        context.Report.Count(isFacade ? "output.facade" : "output.class");
        context.Report.Count(
            isFacade ? "output.top-level-function" : "output.function",
            uniqueMethods.Count(method => method.Source is not null));
        context.Report.Count(
            isFacade ? "output.top-level-property" : "output.property",
            uniqueFields.Count(field => field.Source is not null) +
                uniqueMethods.Count(method => method.SourceProperty is not null));
        context.Report.Count(
            "output.constructor",
            uniqueMethods.Count(method => method.SourceConstructor is not null) +
                (isAnnotation ? type.Constructors.Count != 0 ? 1 : 0 : 0));
        context.Report.Count("output.enum-entry", isEnum ? uniqueFields.Count(field => (field.Access & AccEnum) != 0) : 0);

        var classAccess = ClassAccess(type, isInterface || isAnnotation, isEnum, declaresAbstractMember, isAnnotation);
        var classSignature = ClassSignature(type, context, scope, hierarchy, isEnum, selfDescriptor);
        var innerClasses = InnerClasses(type, context, pool);

        var codeAttribute = pool.Utf8("Code");
        var methodParametersAttribute = pool.Utf8("MethodParameters");
        var signatureAttribute = pool.Utf8("Signature");
        var sourceFileAttribute = pool.Utf8("SourceFile");
        var sourceFile = pool.Utf8(type.SimpleName + ".java");
        var innerClassesAttribute = innerClasses.Length == 0 ? (ushort)0 : pool.Utf8("InnerClasses");
        var annotationsAttribute = metadataBody is null ? (ushort)0 : pool.Utf8("RuntimeVisibleAnnotations");
        var classSignatureIndex = classSignature is null ? (ushort?)null : pool.Utf8(classSignature);

        using var output = new MemoryStream();
        var writer = new BigEndianWriter(output);
        writer.U4(0xCAFEBABE);
        writer.U2(0); // minor version
        writer.U2(52); // Java 8: understood by every supported Kotlin LSP runtime
        pool.Write(writer);
        writer.U2(classAccess);
        writer.U2(thisClass);
        writer.U2(superclass);
        writer.U2(CheckedU2(interfaces.Length));
        foreach (var implemented in interfaces) writer.U2(implemented);

        writer.U2(CheckedU2(uniqueFields.Count));
        foreach (var field in uniqueFields)
        {
            writer.U2(field.Access);
            writer.U2(field.NameIndex);
            writer.U2(field.DescriptorIndex);
            writer.U2((ushort)(field.SignatureIndex is null ? 0 : 1));
            if (field.SignatureIndex is { } fieldSignature)
            {
                writer.U2(signatureAttribute);
                writer.U4(2);
                writer.U2(fieldSignature);
            }
        }

        writer.U2(CheckedU2(uniqueMethods.Length));
        foreach (var method in uniqueMethods)
        {
            writer.U2(method.Access);
            writer.U2(method.NameIndex);
            writer.U2(method.DescriptorIndex);
            var hasParameters = method.ParameterNameIndices.Length != 0;
            writer.U2((ushort)(
                (method.Code is null ? 0 : 1) +
                (hasParameters ? 1 : 0) +
                (method.SignatureIndex is null ? 0 : 1)));
            if (method.Code is { } code)
            {
                writer.U2(codeAttribute);
                writer.U4((uint)(12 + code.Length));
                writer.U2(method.MaxStack);
                writer.U2(method.MaxLocals);
                writer.U4((uint)code.Length);
                writer.Bytes(code);
                writer.U2(0); // exception table
                writer.U2(0); // code attributes
            }
            if (hasParameters)
            {
                writer.U2(methodParametersAttribute);
                writer.U4((uint)(1 + method.ParameterNameIndices.Length * 4));
                writer.U1(checked((byte)method.ParameterNameIndices.Length));
                foreach (var nameIndex in method.ParameterNameIndices)
                {
                    writer.U2(nameIndex);
                    writer.U2(0);
                }
            }
            if (method.SignatureIndex is { } methodSignature)
            {
                writer.U2(signatureAttribute);
                writer.U4(2);
                writer.U2(methodSignature);
            }
        }

        writer.U2((ushort)(
            1 +
            (classSignatureIndex is null ? 0 : 1) +
            (innerClasses.Length == 0 ? 0 : 1) +
            (metadataBody is null ? 0 : 1)));
        writer.U2(sourceFileAttribute);
        writer.U4(2);
        writer.U2(sourceFile);
        if (classSignatureIndex is { } signature)
        {
            writer.U2(signatureAttribute);
            writer.U4(2);
            writer.U2(signature);
        }
        if (innerClasses.Length != 0)
        {
            writer.U2(innerClassesAttribute);
            writer.U4((uint)(2 + innerClasses.Length * 8));
            writer.U2(CheckedU2(innerClasses.Length));
            foreach (var entry in innerClasses)
            {
                writer.U2(entry.Inner);
                writer.U2(entry.Outer);
                writer.U2(entry.Name);
                writer.U2(entry.Access);
            }
        }
        if (metadataBody is { } body)
        {
            writer.U2(annotationsAttribute);
            writer.U4((uint)body.Length);
            writer.Bytes(body);
        }
        return output.ToArray();
    }

    /// <summary>Encodes a single <c>kotlin.Metadata</c> annotation as a RuntimeVisibleAnnotations body.</summary>
    private static byte[] AnnotationBody(KotlinMetadataAnnotation metadata, ConstantPool pool)
    {
        const byte TagInt = (byte)'I';
        const byte TagString = (byte)'s';
        const byte TagArray = (byte)'[';

        using var stream = new MemoryStream();
        var writer = new BigEndianWriter(stream);
        writer.U2(1); // one annotation
        writer.U2(pool.Utf8("Lkotlin/Metadata;"));
        writer.U2(5); // mv, k, xi, d1, d2
        IntArray("mv", metadata.MetadataVersion);
        Int("k", metadata.Kind);
        Int("xi", metadata.ExtraInt);
        StringArray("d1", metadata.Data1);
        StringArray("d2", metadata.Data2);
        return stream.ToArray();

        void Int(string name, int value)
        {
            writer.U2(pool.Utf8(name));
            writer.U1(TagInt);
            writer.U2(pool.Integer(value));
        }

        void IntArray(string name, IReadOnlyList<int> values)
        {
            writer.U2(pool.Utf8(name));
            writer.U1(TagArray);
            writer.U2(CheckedU2(values.Count));
            foreach (var value in values)
            {
                writer.U1(TagInt);
                writer.U2(pool.Integer(value));
            }
        }

        void StringArray(string name, IReadOnlyList<string> values)
        {
            writer.U2(pool.Utf8(name));
            writer.U1(TagArray);
            writer.U2(CheckedU2(values.Count));
            foreach (var value in values)
            {
                writer.U1(TagString);
                writer.U2(pool.Utf8(value));
            }
        }
    }

    private sealed record Hierarchy(
        string SuperclassInternalName,
        KlibTypeRef? SuperclassReference,
        IReadOnlyList<string> InterfaceInternalNames,
        IReadOnlyList<KlibTypeRef> InterfaceReferences);

    /// <summary>
    /// A Kotlin annotation class has exactly one constructor and its parameters are its members, while a
    /// CLR attribute has constructor overloads and settable properties. The representable shape is the
    /// members as one parameter list; the overloads the KLIB declared are counted as lost.
    /// </summary>
    private static IReadOnlyList<(KlibConstructor, JvmMemberSignature)> AnnotationConstructor(
        KlibType type,
        JavaEmitContext context)
    {
        foreach (var dropped in type.Constructors.Skip(1))
        {
            context.Report.Tally(
                "annotation.constructor-overload-dropped",
                $"{type.FullName}({dropped.Parameters.Count})");
            context.Report.RepresentationLimit(
                "a CLR attribute has constructor overloads, a Kotlin annotation class has exactly one " +
                "constructor; its members become that one parameter list");
        }

        var parameters = type.Properties
            .Where(property => property.ReceiverType is null && IsValidMemberName(property.Name))
            .Select(property => new KlibValueParameter(
                property.Name, property.ReturnType, null, HasDefaultValue: true, new KlibRawFlags(1 << 1)))
            .ToArray();
        return [(
            new KlibConstructor(KlibVisibility.Public, IsSecondary: false, parameters, new KlibRawFlags(6)),
            new JvmMemberSignature("<init>", "()V"))];
    }

    /// <summary>
    /// Classifies supertypes with the whole-world registry rather than by guessing from the locally
    /// selected classes, which is what previously made a superclass outside the selection look like an
    /// interface. A supertype the registry cannot classify is dropped and reported instead of guessed.
    /// </summary>
    private static Hierarchy ResolveHierarchy(
        KlibType type,
        JavaEmitContext context,
        bool isInterface,
        bool isEnum)
    {
        KlibTypeRef? superclassReference = null;
        var superclassInternalName = isEnum ? EnumInternalName : ObjectInternalName;
        var interfaceNames = new List<string>();
        var interfaceReferences = new List<KlibTypeRef>();

        foreach (var supertype in type.Supertypes)
        {
            var name = supertype.ClassName;
            // `kotlin.Enum` and `kotlin.Annotation` are implied by the class kind, not written out.
            if (name is "kotlin.Enum" or "kotlin.Annotation") continue;
            if (context.ResolveSupertype(supertype, type) is not var (resolvedName, resolvedIsInterface))
                continue;
            if (resolvedIsInterface)
            {
                if (interfaceNames.Contains(resolvedName)) continue;
                interfaceNames.Add(resolvedName);
                interfaceReferences.Add(supertype);
                continue;
            }
            if (isInterface || isEnum)
            {
                // A Kotlin interface cannot have a class supertype, and an enum's superclass is fixed.
                context.Report.Sample("supertype.demoted", $"{type.FullName} : {name}");
                continue;
            }
            if (superclassReference is not null)
            {
                context.Report.Sample("supertype.extra-class", $"{type.FullName} : {name}");
                continue;
            }
            superclassReference = supertype;
            superclassInternalName = resolvedName;
        }

        return new Hierarchy(superclassInternalName, superclassReference, interfaceNames, interfaceReferences);
    }

    private static string? ClassSignature(
        KlibType type,
        JavaEmitContext context,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        Hierarchy hierarchy,
        bool isEnum,
        string selfDescriptor)
    {
        // A JVM enum's superclass is always the generic `Enum<Self>`; without the signature it reads as a
        // raw `java.lang.Enum` and Kotlin no longer treats the class as an enum at all.
        var needsSignature = isEnum ||
            type.TypeParameters.Count != 0 ||
            JavaEmitContext.NeedsSignature(hierarchy.SuperclassReference) ||
            hierarchy.InterfaceReferences.Any(JavaEmitContext.NeedsSignature);
        if (!needsSignature) return null;

        var builder = new StringBuilder();
        builder.Append(context.TypeParameterSignature(type.TypeParameters, scope, type));
        builder.Append(isEnum
            ? "L" + EnumInternalName + "<" + selfDescriptor + ">;"
            : hierarchy.SuperclassReference is { } superclass
                ? context.Signature(superclass, scope, type, allowVoid: false)
                : "L" + ObjectInternalName + ";");
        foreach (var implemented in hierarchy.InterfaceReferences)
            builder.Append(context.Signature(implemented, scope, type, allowVoid: false));
        return builder.ToString();
    }

    private static ushort ClassAccess(
        KlibType type,
        bool isInterface,
        bool isEnum,
        bool declaresAbstractMember,
        bool isAnnotation = false)
    {
        if (isInterface)
            return (ushort)(AccPublic | AccInterface | AccAbstract | (isAnnotation ? AccAnnotation : 0));
        var isAbstract = declaresAbstractMember || type.Modality is KlibModality.Abstract or KlibModality.Sealed;
        var isFinal = !isAbstract && type.Modality == KlibModality.Final;
        return (ushort)(
            AccPublic |
            AccSuper |
            (isEnum ? AccEnum : 0) |
            (isAbstract ? AccAbstract : 0) |
            (isFinal ? AccFinal : 0));
    }

    private static InnerClassEntry[] InnerClasses(KlibType type, JavaEmitContext context, ConstantPool pool)
    {
        var entries = new List<InnerClassEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The chain that names this class, outermost first, then the members it declares itself. Both are
        // required: IntelliJ reads the entry to learn that `Outer$Inner` is displayed as `Outer.Inner`.
        var chain = new List<KlibType>();
        for (var current = type; current is not null; current = context.World.Find(current.DeclaringTypeFullName))
        {
            if (current.DeclaringTypeFullName is null) break;
            chain.Add(current);
        }
        chain.Reverse();
        foreach (var nested in chain.Concat(type.NestedTypes.Where(nested => context.IsEmitted(nested.FullName))))
        {
            if (nested.DeclaringTypeFullName is null || !seen.Add(nested.JvmInternalName)) continue;
            var declaring = context.World.Find(nested.DeclaringTypeFullName);
            if (declaring is null) continue;
            entries.Add(new InnerClassEntry(
                pool.Class(nested.JvmInternalName),
                pool.Class(declaring.JvmInternalName),
                pool.Utf8(nested.SimpleName),
                NestedAccess(nested)));
        }
        return entries.ToArray();
    }

    private static ushort NestedAccess(KlibType nested)
    {
        var isInterface = nested.IsInterface;
        var isAbstract = isInterface || nested.Modality is KlibModality.Abstract or KlibModality.Sealed;
        return (ushort)(
            MemberAccess(nested.Visibility) |
            (nested.IsInner ? 0 : AccStatic) |
            (isInterface ? AccInterface : 0) |
            (isAbstract ? AccAbstract : 0) |
            (!isAbstract && nested.Modality == KlibModality.Final ? AccFinal : 0) |
            (nested.Kind == KlibTypeKind.EnumClass ? AccEnum : 0));
    }

    private static IEnumerable<MethodEntry> ExtensionPropertyAccessors(
        KlibProperty property,
        KlibType owner,
        JavaEmitContext context,
        ConstantPool pool,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        bool forceStatic)
    {
        if (!IsValidMemberName(property.Name) ||
            property.Visibility is KlibVisibility.PrivateToThis or KlibVisibility.Local)
        {
            context.Report.DroppedMember("extension-property", owner.FullName, property.Name);
            yield break;
        }

        var receiverScope = context.World.Resolve(property.ReceiverType!, owner);
        var receiver = context.Descriptor(property.ReceiverType, scope, owner, false, receiverScope);
        var value = context.Descriptor(property.ReturnType, scope, owner, false, receiverScope);
        var access = (ushort)(
            MemberAccess(property.Visibility) |
            (property.IsStatic || forceStatic ? AccStatic : 0) |
            AccNative);
        var slots = (property.IsStatic || forceStatic ? 0 : 1) + LocalSlots(receiver);
        context.Report.Count("member.extension-property");

        var getterName = AccessorName("get", property.Name);
        var getterDescriptor = "(" + receiver + ")" + value;
        yield return new MethodEntry(
            access, getterName, getterDescriptor,
            pool.Utf8(getterName), pool.Utf8(getterDescriptor), null,
            [pool.Utf8("$this$" + SafeParameterName(property.Name))],
            null, ReturnStackSlots(value), CheckedU2(slots),
            SourceProperty: property);

        if (!property.IsVar) yield break;
        var setterName = AccessorName("set", property.Name);
        var setterDescriptor = "(" + receiver + value + ")V";
        yield return new MethodEntry(
            access, setterName, setterDescriptor,
            pool.Utf8(setterName), pool.Utf8(setterDescriptor), null,
            [pool.Utf8("$this$" + SafeParameterName(property.Name)), pool.Utf8("value")],
            null, 0, CheckedU2(slots + LocalSlots(value)),
            SourceProperty: property,
            IsPropertySetter: true);
    }

    /// <summary>Kotlin's accessor naming: `isEmpty` keeps its name, everything else gets a get/set prefix.</summary>
    private static string AccessorName(string prefix, string name) =>
        prefix == "get" && name.StartsWith("is", StringComparison.Ordinal) &&
        name.Length > 2 && !char.IsLower(name[2])
            ? name
            : prefix + char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>One member of a JVM annotation: an abstract accessor named after the Kotlin property.</summary>
    private static MethodEntry? AnnotationMember(
        KlibProperty property,
        KlibType owner,
        JavaEmitContext context,
        ConstantPool pool,
        IReadOnlyDictionary<int, JavaTypeVariable> scope)
    {
        if (property.ReceiverType is not null || !IsValidMemberName(property.Name)) return null;
        var descriptor = "()" + context.Descriptor(property.ReturnType, scope, owner, allowVoid: false);
        return new MethodEntry(
            (ushort)(AccPublic | AccAbstract),
            property.Name,
            descriptor,
            pool.Utf8(property.Name),
            pool.Utf8(descriptor),
            null,
            [],
            null,
            0,
            0,
            SourceProperty: property);
    }

    private static FieldEntry? Field(
        KlibProperty property,
        KlibType owner,
        JavaEmitContext context,
        ConstantPool pool,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        bool forceStatic)
    {
        if (property.ReceiverType is not null)
        {
            context.Report.DroppedMember("extension-property", owner.FullName, property.Name);
            return null;
        }
        if (!IsValidMemberName(property.Name))
        {
            context.Report.DroppedMember("invalid-name", owner.FullName, property.Name);
            return null;
        }
        if (property.Visibility is KlibVisibility.PrivateToThis or KlibVisibility.Local)
        {
            context.Report.DroppedMember("visibility", owner.FullName, property.Name);
            return null;
        }

        var descriptor = context.Descriptor(property.ReturnType, scope, owner, allowVoid: false);
        var signature = JavaEmitContext.NeedsSignature(property.ReturnType)
            ? context.Signature(property.ReturnType, scope, owner, allowVoid: false)
            : null;
        return new FieldEntry(
            (ushort)(
                MemberAccess(property.Visibility) |
                (property.IsStatic || forceStatic ? AccStatic : 0) |
                (property.IsVar ? 0 : AccFinal)),
            property.Name,
            descriptor,
            pool.Utf8(property.Name),
            pool.Utf8(descriptor),
            signature is null ? null : pool.Utf8(signature),
            property);
    }

    private static MethodEntry? Method(
        KlibFunction function,
        KlibType owner,
        JavaEmitContext context,
        ConstantPool pool,
        IReadOnlyDictionary<int, JavaTypeVariable> classScope,
        bool ownerIsInterface,
        bool forceStatic = false)
    {
        // A member extension function is an ordinary JVM method whose first parameter is the receiver.
        // Only metadata can say which parameter that is, so without it the declaration has no shape.
        if (function.ReceiverType is not null && !context.EmitKotlinMetadata)
        {
            context.Report.DroppedMember("extension-function", owner.FullName, function.Name);
            return null;
        }
        if (!IsValidMemberName(function.Name))
        {
            context.Report.DroppedMember("invalid-name", owner.FullName, function.Name);
            return null;
        }
        if (function.Visibility is KlibVisibility.PrivateToThis or KlibVisibility.Local)
        {
            context.Report.DroppedMember("visibility", owner.FullName, function.Name);
            return null;
        }

        var scope = context.Scope(owner, classScope, function.TypeParameters);
        var receiverScope = context.SignatureScope(function, owner);
        var receiverDescriptor = function.ReceiverType is { } receiver
            ? context.Descriptor(receiver, scope, owner, false, receiverScope)
            : null;
        // Kotlin lays a method out as context parameters, then the extension receiver, then the declared
        // parameters. Only the metadata says which leading slots are which.
        var contextDescriptors = function.ContextParameters
            .Select(parameter => ParameterDescriptor(parameter, scope, owner, context, receiverScope))
            .ToArray();
        var parameterDescriptors = contextDescriptors
            .Concat(receiverDescriptor is null ? [] : new[] { receiverDescriptor })
            .Concat(function.Parameters.Select(parameter =>
                ParameterDescriptor(parameter, scope, owner, context, receiverScope)))
            .ToArray();
        if (contextDescriptors.Length != 0) context.Report.Count("member.context-parameters");
        if (receiverDescriptor is not null) context.Report.Count("member.extension-function");
        var returnDescriptor = context.Descriptor(function.ReturnType, scope, owner, true, receiverScope);
        var descriptor = "(" + string.Concat(parameterDescriptors) + ")" + returnDescriptor;

        var isAbstract = function.Modality == KlibModality.Abstract;
        // An interface method the JVM does not call abstract must carry code, and a static interface
        // method may never be abstract; both would otherwise produce a structurally invalid classfile.
        var needsBody = ownerIsInterface && !isAbstract;
        var isStatic = function.IsStatic || forceStatic;
        var access = (ushort)(
            MemberAccess(function.Visibility) |
            (isStatic ? AccStatic : 0) |
            (function.Parameters.LastOrDefault()?.IsVararg == true ? AccVarargs : 0) |
            (isAbstract && !isStatic ? AccAbstract : 0) |
            (!isAbstract && !needsBody ? AccNative : 0));
        if (isAbstract && isStatic)
            context.Report.Sample("member.static-abstract", $"{owner.FullName}.{function.Name}");

        var signature = MethodSignature(function, owner, context, scope, receiverScope);
        var maxLocals = (isStatic ? 0 : 1) + parameterDescriptors.Sum(LocalSlots);
        return new MethodEntry(
            access,
            function.Name,
            descriptor,
            pool.Utf8(function.Name),
            pool.Utf8(descriptor),
            signature is null ? null : pool.Utf8(signature),
            function.ContextParameters
                .Select((parameter, index) => pool.Utf8("$context$" + index))
                .Concat(receiverDescriptor is null
                    ? []
                    : [pool.Utf8("$this$" + SafeParameterName(function.Name))])
                .Concat(function.Parameters.Select(parameter => pool.Utf8(SafeParameterName(parameter.Name))))
                .ToArray(),
            needsBody || (access & (AccAbstract | AccNative)) == 0 ? ReturnBody(returnDescriptor) : null,
            ReturnStackSlots(returnDescriptor),
            CheckedU2(maxLocals),
            function);
    }

    private static string ParameterDescriptor(
        KlibValueParameter parameter,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        KlibType owner,
        JavaEmitContext context,
        KlibType? extraScope = null) =>
        parameter.VarargElementType is { } element
            ? "[" + context.Descriptor(element, scope, owner, false, extraScope)
            : context.Descriptor(parameter.Type, scope, owner, false, extraScope);

    private static string? MethodSignature(
        KlibFunction function,
        KlibType owner,
        JavaEmitContext context,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        KlibType? receiverScope)
    {
        var needsSignature = function.TypeParameters.Count != 0 ||
            function.ContextParameters.Any(parameter => JavaEmitContext.NeedsSignature(parameter.Type)) ||
            JavaEmitContext.NeedsSignature(function.ReceiverType) ||
            JavaEmitContext.NeedsSignature(function.ReturnType) ||
            function.Parameters.Any(parameter =>
                JavaEmitContext.NeedsSignature(parameter.Type) ||
                JavaEmitContext.NeedsSignature(parameter.VarargElementType));
        if (!needsSignature) return null;

        var builder = new StringBuilder();
        builder.Append(context.TypeParameterSignature(function.TypeParameters, scope, owner));
        builder.Append('(');
        foreach (var parameter in function.ContextParameters)
            builder.Append(context.Signature(parameter.Type, scope, owner, false, false, receiverScope));
        if (function.ReceiverType is { } receiver)
            builder.Append(context.Signature(receiver, scope, owner, false, false, receiverScope));
        foreach (var parameter in function.Parameters)
            builder.Append(parameter.VarargElementType is { } element
                ? "[" + context.Signature(element, scope, owner, false, false, receiverScope)
                : context.Signature(parameter.Type, scope, owner, false, false, receiverScope));
        builder.Append(')').Append(context.Signature(function.ReturnType, scope, owner, true, false, receiverScope));
        return builder.ToString();
    }

    private static MethodEntry? Constructor(
        KlibConstructor constructor,
        KlibType owner,
        JavaEmitContext context,
        ConstantPool pool,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        ushort superclassConstructor)
    {
        if (constructor.Visibility is KlibVisibility.PrivateToThis or KlibVisibility.Local) return null;
        var parameterDescriptors = constructor.Parameters
            .Select(parameter => ParameterDescriptor(parameter, scope, owner, context))
            .ToArray();
        var descriptor = "(" + string.Concat(parameterDescriptors) + ")V";
        var access = (ushort)(
            (owner.Kind == KlibTypeKind.EnumClass ? AccPrivate : MemberAccess(constructor.Visibility)) |
            (constructor.Parameters.LastOrDefault()?.IsVararg == true ? AccVarargs : 0));
        return new MethodEntry(
            access,
            "<init>",
            descriptor,
            pool.Utf8("<init>"),
            pool.Utf8(descriptor),
            null,
            constructor.Parameters.Select(parameter => pool.Utf8(SafeParameterName(parameter.Name))).ToArray(),
            ConstructorBody(superclassConstructor),
            MaxStack: 1,
            CheckedU2(1 + parameterDescriptors.Sum(LocalSlots)),
            SourceConstructor: constructor);
    }

    private static MethodEntry NativeStatic(ConstantPool pool, string name, string descriptor) =>
        new((ushort)(AccPublic | AccStatic | AccFinal | AccNative),
            name, descriptor, pool.Utf8(name), pool.Utf8(descriptor), null, [], null, 0, 0);

    private static byte[] ConstructorBody(ushort superclassConstructor) => [
        0x2a, // aload_0
        0xb7, (byte)(superclassConstructor >> 8), (byte)(superclassConstructor & 0xFF), // invokespecial
        0xb1, // return
    ];

    /// <summary>The shortest body that satisfies the verifier for a method the JVM format requires code on.</summary>
    private static byte[] ReturnBody(string returnDescriptor) => returnDescriptor switch
    {
        "V" => [0xb1],
        "Z" or "B" or "C" or "S" or "I" => [0x03, 0xac], // iconst_0; ireturn
        "J" => [0x09, 0xad], // lconst_0; lreturn
        "F" => [0x0b, 0xae], // fconst_0; freturn
        "D" => [0x0e, 0xaf], // dconst_0; dreturn
        _ => [0x01, 0xb0], // aconst_null; areturn
    };

    private static ushort ReturnStackSlots(string returnDescriptor) => returnDescriptor switch
    {
        "V" => 0,
        "J" or "D" => 2,
        _ => 1,
    };

    /// <summary>
    /// Two Kotlin overloads can erase to a single JVM descriptor - `f(Any?)` and `f(Any)` are the common
    /// CLR case. Plain Java has no way to keep both, but metadata holds the Kotlin name separately from
    /// the JVM one, so the collision is resolved by mangling only the JVM side. Without metadata there is
    /// nowhere to record the real name, and the member is dropped and counted instead.
    /// </summary>
    private static List<T> ResolveCollisions<T>(
        IEnumerable<T> members,
        Func<T, (string Name, string Descriptor)> key,
        Func<T, string, T> rename,
        KlibType owner,
        JavaEmitContext context,
        string kind)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<T>();
        foreach (var member in members)
        {
            var (name, descriptor) = key(member);
            if (seen.Add((name, descriptor)))
            {
                result.Add(member);
                continue;
            }
            if (!context.EmitKotlinMetadata)
            {
                context.Report.DroppedMember($"erasure-clash.{kind}", owner.FullName, name + descriptor);
                continue;
            }
            var mangled = MangledName(name, descriptor);
            while (!seen.Add((mangled, descriptor))) mangled += "_";
            context.Report.Count($"member.mangled.{kind}");
            result.Add(rename(member, mangled));
        }
        return result;
    }

    /// <summary>Kotlin's own shape for a JVM name that had to differ from the declared one.</summary>
    private static string MangledName(string name, string descriptor)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var hash = 2166136261u;
        foreach (var character in descriptor) hash = (hash ^ character) * 16777619u;
        var suffix = new StringBuilder();
        for (var remaining = hash; suffix.Length < 7; remaining /= 36) suffix.Insert(0, digits[(int)(remaining % 36)]);
        return name + "-" + suffix;
    }

    private static ushort MemberAccess(KlibVisibility visibility) => visibility switch
    {
        KlibVisibility.Private => AccPrivate,
        KlibVisibility.Protected => AccProtected,
        _ => AccPublic,
    };

    private static bool IsValidMemberName(string value) =>
        value.Length != 0 &&
        value.IndexOfAny(['.', ';', '[', '/', '<', '>']) < 0;

    private static string SafeParameterName(string value) =>
        IsValidMemberName(value) ? value : "arg";

    private static int LocalSlots(string descriptor) => descriptor is "J" or "D" ? 2 : 1;

    private static ushort CheckedU2(int value) => checked((ushort)value);

    private sealed record FieldEntry(
        ushort Access,
        string Name,
        string Descriptor,
        ushort NameIndex,
        ushort DescriptorIndex,
        ushort? SignatureIndex,
        KlibProperty? Source = null);

    private sealed record MethodEntry(
        ushort Access,
        string Name,
        string Descriptor,
        ushort NameIndex,
        ushort DescriptorIndex,
        ushort? SignatureIndex,
        ushort[] ParameterNameIndices,
        byte[]? Code,
        ushort MaxStack,
        ushort MaxLocals,
        KlibFunction? Source = null,
        KlibConstructor? SourceConstructor = null,
        KlibProperty? SourceProperty = null,
        bool IsPropertySetter = false);

    private sealed record InnerClassEntry(ushort Inner, ushort Outer, ushort Name, ushort Access);
}

internal sealed class ConstantPool
{
    private readonly List<PoolEntry> _entries = [];
    private readonly Dictionary<string, ushort> _indices = new(StringComparer.Ordinal);

    public ushort Utf8(string value) => Add("U:" + value, () => new Utf8Entry(value));

    public ushort Integer(int value) => Add("I:" + value, () => new IntegerEntry(value));

    public ushort Class(string internalName)
    {
        var name = Utf8(internalName);
        return Add("C:" + internalName, () => new ClassEntry(name));
    }

    public ushort NameAndType(string name, string descriptor)
    {
        var nameIndex = Utf8(name);
        var descriptorIndex = Utf8(descriptor);
        return Add("NT:" + name + ":" + descriptor, () => new NameAndTypeEntry(nameIndex, descriptorIndex));
    }

    public ushort MethodRef(string owner, string name, string descriptor)
    {
        var classIndex = Class(owner);
        var nameAndTypeIndex = NameAndType(name, descriptor);
        return Add("MR:" + owner + ":" + name + ":" + descriptor,
            () => new MethodRefEntry(classIndex, nameAndTypeIndex));
    }

    public void Write(BigEndianWriter writer)
    {
        writer.U2(checked((ushort)(_entries.Count + 1)));
        foreach (var entry in _entries) entry.Write(writer);
    }

    private ushort Add(string key, Func<PoolEntry> create)
    {
        if (_indices.TryGetValue(key, out var existing)) return existing;
        var index = checked((ushort)(_entries.Count + 1));
        _entries.Add(create());
        _indices[key] = index;
        return index;
    }

    private abstract record PoolEntry(byte Tag)
    {
        public abstract void Write(BigEndianWriter writer);
    }

    private sealed record Utf8Entry(string Value) : PoolEntry(1)
    {
        public override void Write(BigEndianWriter writer)
        {
            var bytes = ModifiedUtf8(Value);
            writer.U1(Tag);
            writer.U2(checked((ushort)bytes.Length));
            writer.Bytes(bytes);
        }

        /// <summary>
        /// The classfile format uses modified UTF-8, which differs from the ordinary encoding exactly
        /// where it matters here: a NUL is two bytes, never one. The kotlin.Metadata payload begins with
        /// a NUL marker and its protobuf body is full of zero bytes, so the ordinary encoder would
        /// produce a constant pool entry no JVM or IDE parser accepts.
        /// </summary>
        private static byte[] ModifiedUtf8(string value)
        {
            var bytes = new List<byte>(value.Length + 8);
            foreach (var character in value)
            {
                if (character is >= '\u0001' and <= '\u007f') bytes.Add((byte)character);
                else if (character <= '\u07ff')
                {
                    bytes.Add((byte)(0xC0 | (character >> 6)));
                    bytes.Add((byte)(0x80 | (character & 0x3F)));
                }
                else
                {
                    bytes.Add((byte)(0xE0 | (character >> 12)));
                    bytes.Add((byte)(0x80 | ((character >> 6) & 0x3F)));
                    bytes.Add((byte)(0x80 | (character & 0x3F)));
                }
            }
            return bytes.ToArray();
        }
    }

    private sealed record IntegerEntry(int Value) : PoolEntry(3)
    {
        public override void Write(BigEndianWriter writer)
        {
            writer.U1(Tag);
            writer.U4(unchecked((uint)Value));
        }
    }

    private sealed record ClassEntry(ushort Name) : PoolEntry(7)
    {
        public override void Write(BigEndianWriter writer)
        {
            writer.U1(Tag);
            writer.U2(Name);
        }
    }

    private sealed record MethodRefEntry(ushort Class, ushort NameAndType) : PoolEntry(10)
    {
        public override void Write(BigEndianWriter writer)
        {
            writer.U1(Tag);
            writer.U2(Class);
            writer.U2(NameAndType);
        }
    }

    private sealed record NameAndTypeEntry(ushort Name, ushort Descriptor) : PoolEntry(12)
    {
        public override void Write(BigEndianWriter writer)
        {
            writer.U1(Tag);
            writer.U2(Name);
            writer.U2(Descriptor);
        }
    }
}

internal sealed class BigEndianWriter(Stream output)
{
    private readonly Stream _output = output;
    private readonly byte[] _buffer = new byte[4];

    public void U1(byte value) => _output.WriteByte(value);

    public void U2(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer, value);
        _output.Write(_buffer, 0, 2);
    }

    public void U4(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer, value);
        _output.Write(_buffer, 0, 4);
    }

    public void Bytes(ReadOnlySpan<byte> value) => _output.Write(value);
}
