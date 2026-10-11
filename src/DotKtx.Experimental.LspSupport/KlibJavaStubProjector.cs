using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DotKtx.Experimental.LspSupport;

internal sealed record JavaStubProjection(
    byte[] Jar,
    IReadOnlySet<string> ProjectedRoots,
    bool ProjectsTopLevelDeclarations,
    int ClassCount);

/// <summary>
/// Projects KLIB declarations onto minimal JVM classfiles. An unannotated Java type is a flexible
/// platform type in Kotlin, which is the only representation that keeps the CLR interop surface honest;
/// statics, inheritance, interfaces, generics and nested names all survive the trip as well. The archive
/// is deliberately not executable - method bodies are absent or trivial and the KLIB carries no code.
/// </summary>
internal static class KlibJavaStubProjector
{
    public static JavaStubProjection Project(KlibWorld world, ProjectionPolicy policy, ProjectionReport report)
    {
        var selected = policy.Select(world, report);
        var families = GroupIntoFamilies(selected, world);
        var emitted = new List<KlibType>();
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (rootName, members) in families)
        {
            report.Count("java.family", 1);
            foreach (var member in members.Where(member => member.Kind == KlibTypeKind.EnumEntry))
                report.Sample("member.dropped.enum-entry-body", member.FullName);
            emitted.AddRange(members.Where(member =>
                member.Kind != KlibTypeKind.EnumEntry && !world.IsBuiltinClassifier(member.FullName)));
            roots.Add(rootName);
        }

        var context = new JavaEmitContext(
            world,
            emitted.ToDictionary(type => type.FullName, StringComparer.Ordinal),
            report,
            policy.KotlinMetadata);
        var classes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var type in emitted)
        {
            report.Count("java.class." + type.Kind.ToString().ToLowerInvariant());
            classes[type.JvmInternalName + ".class"] = JavaClassFile.Write(type, context);
        }
        var moduleMapping = policy.KotlinMetadata && policy.TopLevelFacades
            ? EmitFacades(world, context, classes, report)
            : null;
        report.Count("java.class.total", classes.Count);
        report.CompletePhase("java projection");

        return new JavaStubProjection(
            CreateJar(classes, moduleMapping), roots, moduleMapping is not null, classes.Count);
    }

    /// <summary>
    /// Emits one facade class per package and library that declares top-level callables, and the module
    /// mapping that points at them. Without metadata there is no way to say "this class holds package
    /// level declarations", so the whole mechanism only exists on that path.
    /// </summary>
    private static byte[]? EmitFacades(
        KlibWorld world,
        JavaEmitContext context,
        SortedDictionary<string, byte[]> classes,
        ProjectionReport report)
    {
        var facadesByPackage = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var library in world.Libraries)
        {
            var byPackage = library.Fragments
                .Where(fragment => fragment.Functions.Count != 0 || fragment.Properties.Count != 0)
                .GroupBy(fragment => fragment.PackageName, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal);
            foreach (var group in byPackage)
            {
                var facade = SynthesiseFacade(group.Key, library, group);
                CountDuplicatedExtensionMethods(facade, context, report);
                // A package whose own name is also a type is not a package the CLR declares: dll2klib
                // used to copy a static class' extension methods into one, so the same declarations
                // appeared under both `System.Linq` and `System.Linq.ParallelEnumerable`.
                if (world.Find(group.Key) is not null)
                {
                    report.Tally("package.named-after-a-type", group.Key);
                    report.UpstreamGap(
                        "dll2klib copies a static class' extension methods into a package named after " +
                        "that class, so the same top-level declarations exist under two packages");
                }
                classes[facade.JvmInternalName + ".class"] = JavaClassFile.Write(facade, context, isFacade: true);
                // The mapping names the facade relative to its package, not by binary name.
                (facadesByPackage.TryGetValue(group.Key, out var names)
                    ? names
                    : facadesByPackage[group.Key] = []).Add(facade.SimpleName);
                report.Count("java.class.facade");
                report.Count("java.facade.callable", facade.Functions.Count + facade.Properties.Count);
                report.Count("java.facade.extension", facade.Functions.Count(f => f.ReceiverType is not null));
            }
        }
        if (facadesByPackage.Count == 0) return null;
        return KotlinModuleMapping.Write(
            facadesByPackage.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    /// <summary>
    /// A facade is not a KLIB declaration, so it is modelled as one. Everything downstream - scoping,
    /// name resolution, descriptors - then works on it without a second code path.
    /// </summary>
    private static KlibType SynthesiseFacade(
        string packageName,
        KlibLibrary library,
        IEnumerable<KlibFragment> fragments)
    {
        var simpleName = SafeIdentifier(library.Name) + "Kt";
        var prefix = packageName.Length == 0 ? "" : packageName.Replace('.', '/') + "/";
        return new KlibType(
            FullName: packageName.Length == 0 ? simpleName : packageName + "." + simpleName,
            SimpleName: simpleName,
            PackageName: packageName,
            JvmInternalName: prefix + simpleName,
            JvmClassId: prefix + simpleName,
            DeclaringTypeFullName: null,
            Kind: KlibTypeKind.Class,
            Modality: KlibModality.Final,
            Visibility: KlibVisibility.Public,
            IsInner: false,
            IsValue: false,
            TypeParameters: [],
            Supertypes: [],
            Constructors: [],
            Functions: fragments.SelectMany(fragment => fragment.Functions).ToArray(),
            Properties: fragments.SelectMany(fragment => fragment.Properties).ToArray(),
            EnumEntries: [],
            NestedTypes: [],
            LibraryName: library.Name,
            RawFlags: new KlibRawFlags(6));
    }

    /// <summary>
    /// A C# extension method is callable both ways - `ParallelEnumerable.AsParallel(x)` and
    /// `x.AsParallel()` - and the KLIB declares both, because Kotlin has no single declaration that
    /// means both. The two land in different scopes, so neither completion list shows a duplicate; this
    /// is counted to keep the shape visible, not because anything is lost.
    /// </summary>
    private static void CountDuplicatedExtensionMethods(
        KlibType facade,
        JavaEmitContext context,
        ProjectionReport report)
    {
        var staticsInPackage = context.EmittedByFullName.Values
            .Where(type => string.Equals(type.PackageName, facade.PackageName, StringComparison.Ordinal))
            .SelectMany(type => type.Functions.Where(function => function.IsStatic))
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(f => f.Parameters.Count).ToHashSet());

        foreach (var function in facade.Functions.Where(function => function.ReceiverType is not null))
            if (staticsInPackage.TryGetValue(function.Name, out var arities) &&
                arities.Contains(function.Parameters.Count + 1))
                report.Tally("extension-method.both-call-forms", facade.PackageName + "." + function.Name);
    }

    private static string SafeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        return builder.Length == 0 || char.IsAsciiDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    /// <summary>Groups the selection by outermost declaring type so a decision applies to a whole family.</summary>
    private static SortedDictionary<string, List<KlibType>> GroupIntoFamilies(
        IReadOnlyList<KlibType> selected,
        KlibWorld world)
    {
        var families = new SortedDictionary<string, List<KlibType>>(StringComparer.Ordinal);
        foreach (var type in selected)
        {
            var root = type;
            var guard = 0;
            while (world.Find(root.DeclaringTypeFullName) is { } outer && guard++ < 64) root = outer;
            (families.TryGetValue(root.FullName, out var members)
                ? members
                : families[root.FullName] = []).Add(type);
        }
        return families;
    }

    private static byte[] CreateJar(IReadOnlyDictionary<string, byte[]> classes, byte[]? moduleMapping)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes(
                "Manifest-Version: 1.0\r\n" +
                "Created-By: DotKtx.Experimental.LspSupport\r\n" +
                "DotKt-Analysis-Only: true\r\n\r\n"));
            if (moduleMapping is not null)
                AddEntry($"META-INF/{KotlinMetadataWriter.ModuleName}.kotlin_module", moduleMapping);
            foreach (var (path, contents) in classes) AddEntry(path, contents);

            void AddEntry(string path, byte[] contents)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var destination = entry.Open();
                destination.Write(contents);
            }
        }
        return output.ToArray();
    }
}

/// <summary>A type variable in scope, together with the descriptor its uses erase to.</summary>
internal sealed record JavaTypeVariable(string Name, string ErasedDescriptor);

/// <summary>Shared lookup surface for one projection run: names, descriptors and generic signatures.</summary>
internal sealed class JavaEmitContext(
    KlibWorld world,
    IReadOnlyDictionary<string, KlibType> emittedByFullName,
    ProjectionReport report,
    bool emitKotlinMetadata)
{
    private const string ObjectDescriptor = "Ljava/lang/Object;";

    public KlibWorld World { get; } = world;
    public IReadOnlyDictionary<string, KlibType> EmittedByFullName { get; } = emittedByFullName;
    public ProjectionReport Report { get; } = report;

    /// <summary>
    /// Whether each classfile also carries the Kotlin declaration it was projected from. This is the
    /// difference between an approximation of the CLR surface and the surface itself, but it is an
    /// all-or-nothing switch per class: a Kotlin analyzer stops reading the Java members once it finds
    /// the annotation.
    /// </summary>
    public bool EmitKotlinMetadata { get; } = emitKotlinMetadata;

    /// <summary>
    /// Kotlin's built-in classifiers have no KLIB declaration of their own, but every one of them has a
    /// fixed JVM counterpart. Mapping them here is what keeps <c>kotlin.Throwable</c> from silently
    /// turning a CLR exception hierarchy into a bare <c>Object</c> subclass.
    /// </summary>
    private static readonly Dictionary<string, (string InternalName, bool IsInterface)> Builtins =
        new(StringComparer.Ordinal)
        {
            ["kotlin.Annotation"] = ("java/lang/annotation/Annotation", true),
            ["kotlin.CharSequence"] = ("java/lang/CharSequence", true),
            ["kotlin.Cloneable"] = ("java/lang/Cloneable", true),
            ["kotlin.Comparable"] = ("java/lang/Comparable", true),
            ["kotlin.Enum"] = ("java/lang/Enum", false),
            ["kotlin.Number"] = ("java/lang/Number", false),
            ["kotlin.Throwable"] = ("java/lang/Throwable", false),
        };

    private KlibType? Find(string? fullName) => World.Find(fullName);

    public bool IsEmitted(string? fullName) =>
        fullName is not null && EmittedByFullName.ContainsKey(fullName);

    /// <summary>
    /// Classifies one supertype. <c>null</c> means "nothing to write": either the supertype is implied by
    /// the class kind, or it could not be resolved and has been reported rather than guessed at.
    /// </summary>
    public (string InternalName, bool IsInterface)? ResolveSupertype(KlibTypeRef reference, KlibType owner)
    {
        if (reference.ClassName is not { } name || name == "kotlin.Any") return null;
        if (World.Resolve(reference, owner, Report) is { } resolved)
        {
            if (!ReferenceEquals(resolved, owner) && resolved.FullName != owner.FullName)
                return (resolved.JvmInternalName, resolved.IsInterface);
            // The short name resolved back to this very type. `Sse2.X64 : X64` really means `Sse.X64`,
            // so re-resolve the sibling under the outer type's own supertypes before giving up; a class
            // that extends itself would be a corrupt classfile, never merely an incomplete one.
            if (InheritedNested(owner) is { } inherited)
                return (inherited.JvmInternalName, inherited.IsInterface);
            Report.Tally("supertype.self-reference", owner.FullName);
            Report.UpstreamGap(
                "a supertype resolved to the declaring type itself, so dll2klib's qualified name for it " +
                "is ambiguous and the supertype had to be dropped");
            return null;
        }
        if (Builtins.TryGetValue(name, out var builtin)) return builtin;
        Report.UnknownSupertype(owner.FullName, name);
        return null;
    }

    private KlibType? InheritedNested(KlibType owner)
    {
        if (World.Find(owner.DeclaringTypeFullName) is not { } declaring) return null;
        foreach (var outerSupertype in declaring.Supertypes)
        {
            if (World.Resolve(outerSupertype, declaring) is not { } resolvedOuter) continue;
            if (World.Find(resolvedOuter.FullName + "." + owner.SimpleName) is { } sibling &&
                sibling.FullName != owner.FullName)
                return sibling;
        }
        return null;
    }

    /// <summary>
    /// Resolves a referenced class to its binary name. The whole-world registry answers first; the
    /// qualified-name table's own package/class split answers second. Only a reference that satisfies
    /// neither falls back to a guess, and every such guess is counted.
    /// </summary>
    public string InternalName(
        KlibTypeRef reference,
        KlibType owner,
        bool report = true,
        KlibType? extraScope = null)
    {
        if (World.Resolve(reference, owner, report ? Report : null, extraScope) is { } known)
        {
            if (extraScope is not null && Find(reference.ClassName) is null && report)
                Report.Count("reference.resolved-by-signature");
            return known.JvmInternalName;
        }
        if (reference.ClassName is { } name && Builtins.TryGetValue(name, out var builtin))
            return builtin.InternalName;
        // The qualified-name table already knows which segments are packages, so a name it spelled out
        // in full needs no further guessing even when no KLIB declares that type.
        if (reference.ClassJvmInternalName is { Length: > 0 } encoded && encoded.Contains('/')) return encoded;

        var simpleName = reference.ClassJvmInternalName ?? reference.ClassName ?? "Unknown";
        if (report) Report.UnresolvedReference(owner.FullName, reference.ClassName ?? simpleName);
        return owner.PackageName.Length == 0
            ? simpleName
            : owner.PackageName.Replace('.', '/') + "/" + simpleName;
    }

    /// <summary>
    /// The type a bare nested name in this signature most likely belongs to. The KLIB sometimes records
    /// only `Builder` with no parent, but the declaration itself names the owner: the receiver of an
    /// extension, or - as in `ToImmutableArray(Builder&lt;T&gt;): ImmutableArray&lt;T&gt;` - its result.
    /// </summary>
    public KlibType? SignatureScope(KlibFunction function, KlibType owner)
    {
        if (function.ReceiverType is { } receiver && World.Resolve(receiver, owner) is { } resolvedReceiver)
            return resolvedReceiver;
        if (function.ReturnType is { } returnType && World.Resolve(returnType, owner) is { } resolvedReturn)
            return resolvedReturn;
        foreach (var parameter in function.Parameters)
            if (parameter.Type is { } type && World.Resolve(type, owner) is { } resolved)
                return resolved;
        return null;
    }

    /// <summary>
    /// The same target spelled as a Kotlin class id, which separates the nesting chain with a dot where
    /// the binary name uses a dollar. Reported diagnostics are suppressed because the descriptor pass
    /// has already counted whatever failed to resolve.
    /// </summary>
    public string ClassId(KlibTypeRef reference, KlibType owner, KlibType? extraScope = null)
    {
        if (World.Resolve(reference, owner, null, extraScope) is { } known) return known.JvmClassId;
        if (reference.ClassJvmClassId is { Length: > 0 } encoded && encoded.Contains('/')) return encoded;
        return InternalName(reference, owner, report: false, extraScope).Replace('$', '.');
    }

    /// <summary>Builds the type variables a declaration may mention, erasing each to its first upper bound.</summary>
    public IReadOnlyDictionary<int, JavaTypeVariable> Scope(
        KlibType owner,
        IReadOnlyDictionary<int, JavaTypeVariable>? outer,
        IEnumerable<KlibTypeParameter> parameters)
    {
        var declared = parameters.ToArray();
        var scope = outer is null
            ? new Dictionary<int, JavaTypeVariable>()
            : new Dictionary<int, JavaTypeVariable>(outer);
        foreach (var parameter in declared)
            scope[parameter.Id] = new JavaTypeVariable(SafeTypeVariableName(parameter), ObjectDescriptor);
        // A bound may itself mention a sibling variable, so the erasures are computed against the scope
        // that already names every variable of this declaration.
        foreach (var parameter in declared)
            scope[parameter.Id] = scope[parameter.Id] with
            {
                ErasedDescriptor = EraseBound(parameter, scope, owner),
            };
        return scope;
    }

    private string EraseBound(
        KlibTypeParameter parameter,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        KlibType owner)
    {
        var bound = parameter.UpperBounds.FirstOrDefault(candidate =>
            candidate.ClassName is not (null or "kotlin.Any"));
        if (bound is null) return ObjectDescriptor;
        // Guard against `<T : U>`: the sibling's own erasure is already seeded to Object, so one level
        // of indirection resolves and a cycle cannot recurse.
        return Descriptor(bound, scope, owner, allowVoid: false);
    }

    private static string SafeTypeVariableName(KlibTypeParameter parameter)
    {
        var value = parameter.Name;
        return value.Length != 0 && (char.IsLetter(value[0]) || value[0] is '_' or '$') &&
            value.Skip(1).All(character => char.IsLetterOrDigit(character) || character is '_' or '$')
                ? value
                : "T" + parameter.Id;
    }

    public string Descriptor(
        KlibTypeRef? type,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        KlibType owner,
        bool allowVoid,
        KlibType? extraScope = null)
    {
        if (type is null) return allowVoid ? "V" : ObjectDescriptor;
        if (Variable(type, scope) is { } variable) return variable.ErasedDescriptor;
        if (type.TypeParameterId is { } id) return Unresolved(owner, id);
        if (type.ClassName is null) return ObjectDescriptor;

        if (Primitive(type.ClassName) is { } primitive)
            // A nullable CLR value type is `Nullable<T>`, which has no primitive JVM shape.
            return type.IsNullable ? Boxed(type.ClassName)! : primitive;
        return type.ClassName switch
        {
            "kotlin.Unit" when allowVoid => "V",
            "kotlin.Nothing" => "Ljava/lang/Void;",
            "kotlin.String" => "Ljava/lang/String;",
            "kotlin.Any" => ObjectDescriptor,
            "kotlin.BooleanArray" => "[Z",
            "kotlin.ByteArray" => "[B",
            "kotlin.CharArray" => "[C",
            "kotlin.DoubleArray" => "[D",
            "kotlin.FloatArray" => "[F",
            "kotlin.IntArray" => "[I",
            "kotlin.LongArray" => "[J",
            "kotlin.ShortArray" => "[S",
            "kotlin.Array" => "[" + (type.Arguments.FirstOrDefault()?.Type is { } element
                ? Descriptor(element, scope, owner, false, extraScope)
                : ObjectDescriptor),
            _ => "L" + InternalName(type, owner, true, extraScope) + ";",
        };
    }

    /// <summary>Finds the type variable a reference names, whether it was written by id or by name.</summary>
    public static JavaTypeVariable? Variable(
        KlibTypeRef type,
        IReadOnlyDictionary<int, JavaTypeVariable> scope)
    {
        if (type.TypeParameterId is { } id && scope.TryGetValue(id, out var byId)) return byId;
        if (type.TypeParameterName is { } name)
            foreach (var candidate in scope.Values)
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    return candidate;
        return null;
    }

    private string Unresolved(KlibType owner, int typeParameterId)
    {
        Report.Sample("typeparameter.unresolved", $"{owner.FullName} -> #{typeParameterId}");
        // A companion object does not capture its outer class' type parameters in Kotlin, so a KLIB
        // companion whose members use them is not expressible in any representation. The upcoming kotc
        // emits companions as singletons rather than flattened statics, which should remove these.
        Report.UpstreamGap(
            "dll2klib gives a generic class' companion members the outer type parameters, which no " +
            "Kotlin companion can reference; those uses erase to Any");
        return ObjectDescriptor;
    }

    private static string? Primitive(string className) => className switch
    {
        "kotlin.Boolean" => "Z",
        "kotlin.Byte" => "B",
        "kotlin.Char" => "C",
        "kotlin.Double" => "D",
        "kotlin.Float" => "F",
        "kotlin.Int" => "I",
        "kotlin.Long" => "J",
        "kotlin.Short" => "S",
        _ => null,
    };

    private static string? Boxed(string className) => className switch
    {
        "kotlin.Boolean" => "Ljava/lang/Boolean;",
        "kotlin.Byte" => "Ljava/lang/Byte;",
        "kotlin.Char" => "Ljava/lang/Character;",
        "kotlin.Double" => "Ljava/lang/Double;",
        "kotlin.Float" => "Ljava/lang/Float;",
        "kotlin.Int" => "Ljava/lang/Integer;",
        "kotlin.Long" => "Ljava/lang/Long;",
        "kotlin.Short" => "Ljava/lang/Short;",
        _ => null,
    };

    /// <summary>True when erasure would lose information, i.e. a <c>Signature</c> attribute is required.</summary>
    public static bool NeedsSignature(KlibTypeRef? type) =>
        type is not null &&
        (type.TypeParameterId is not null ||
         type.TypeParameterName is not null ||
         type.Arguments.Count != 0 ||
         type.Arguments.Any(argument => NeedsSignature(argument.Type)));

    public string Signature(
        KlibTypeRef? type,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        KlibType owner,
        bool allowVoid,
        bool asTypeArgument = false,
        KlibType? extraScope = null)
    {
        if (type is null) return allowVoid ? "V" : ObjectDescriptor;
        if (Variable(type, scope) is { } variable) return "T" + variable.Name + ";";
        if (type.TypeParameterId is { } id) return Unresolved(owner, id);
        if (type.ClassName is null) return ObjectDescriptor;
        if (type.ClassName == "kotlin.Array")
            return "[" + (type.Arguments.FirstOrDefault()?.Type is { } element
                ? Signature(element, scope, owner, false, false, extraScope)
                : ObjectDescriptor);

        // A generic signature admits only reference types as type arguments, so `Span<Char>` has to be
        // written with the boxed element even though the erased descriptor keeps the primitive.
        if (asTypeArgument && Primitive(type.ClassName) is not null) return Boxed(type.ClassName)!;

        var descriptor = Descriptor(type, scope, owner, allowVoid, extraScope);
        if (type.Arguments.Count == 0 || descriptor.Length == 1 || descriptor[0] == '[') return descriptor;

        var arguments = string.Concat(type.Arguments.Select(argument => argument.Projection switch
        {
            KlibProjection.Star => "*",
            KlibProjection.In => "-" + Signature(argument.Type, scope, owner, false, true, extraScope),
            KlibProjection.Out => "+" + Signature(argument.Type, scope, owner, false, true, extraScope),
            _ => Signature(argument.Type, scope, owner, false, true, extraScope),
        }));
        return "L" + InternalName(type, owner, true, extraScope) + "<" + arguments + ">;";
    }

    /// <summary>Renders the <c>&lt;T:bound&gt;</c> prefix shared by class and method signatures.</summary>
    public string TypeParameterSignature(
        IReadOnlyList<KlibTypeParameter> parameters,
        IReadOnlyDictionary<int, JavaTypeVariable> scope,
        KlibType owner)
    {
        if (parameters.Count == 0) return "";
        var builder = new StringBuilder("<");
        foreach (var parameter in parameters)
        {
            // Java has no declaration-site variance, so `<in T>` flattens to `<T>` in the signature
            // attribute. That only widens what the editor accepts when no metadata restates it.
            if (parameter.Variance != KlibVariance.Invariant && !EmitKotlinMetadata)
                Report.Sample("class.variance-erased", $"{owner.FullName}<{parameter.Name}>");
            builder.Append(scope.TryGetValue(parameter.Id, out var variable) ? variable.Name : "T" + parameter.Id);
            var bounds = parameter.UpperBounds
                .Where(bound => bound.ClassName is not (null or "kotlin.Any") || bound.TypeParameterId is not null)
                .ToArray();
            if (bounds.Length == 0)
            {
                builder.Append(':').Append(ObjectDescriptor);
                continue;
            }
            // Java demands a class bound slot first; an interface-only bound leaves it empty.
            var first = bounds[0];
            var firstIsInterface = World.Find(first.ClassName)?.IsInterface == true;
            builder.Append(':');
            if (firstIsInterface) builder.Append(':');
            builder.Append(Signature(first, scope, owner, allowVoid: false));
            foreach (var bound in bounds.Skip(1))
                builder.Append(':').Append(Signature(bound, scope, owner, allowVoid: false));
        }
        return builder.Append('>').ToString();
    }
}
