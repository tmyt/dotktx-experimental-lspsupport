using System.IO.Compression;
using DotKt.Klib.Metadata;
using ProtoClass = DotKt.Klib.Metadata.Class;
using ProtoConstructor = DotKt.Klib.Metadata.Constructor;
using ProtoFunction = DotKt.Klib.Metadata.Function;
using ProtoProperty = DotKt.Klib.Metadata.Property;
using ProtoType = DotKt.Klib.Metadata.Type;
using ProtoTypeParameter = DotKt.Klib.Metadata.TypeParameter;
using ProtoValueParameter = DotKt.Klib.Metadata.ValueParameter;

namespace DotKtx.Experimental.LspSupport;

/// <summary>Parses every referenced KLIB exactly once into the shared <see cref="KlibWorld"/> model.</summary>
internal static class KlibReader
{
    /// <summary>
    /// The classifiers a Kotlin analyzer takes from `*.kotlin_builtins` resources rather than from
    /// classfiles: `kotlin.Any`, `kotlin.Int`, `kotlin.collections.List` and the rest of the core set.
    /// The JVM maps them onto `java.lang.*` and primitives, so projecting a classfile of the same name
    /// would collide with the platform's own mapping. Reading the shipped resources is what makes the
    /// exclusion exact instead of a hand-kept list that drifts.
    /// </summary>
    public static IReadOnlySet<string> ReadBuiltinClassifiers(string builtinsJarPath)
    {
        var classifiers = new HashSet<string>(StringComparer.Ordinal);
        using var stream = File.OpenRead(builtinsJarPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries
            .Where(entry => entry.FullName.EndsWith(".kotlin_builtins", StringComparison.Ordinal)))
        {
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            var bytes = buffer.ToArray();
            // A builtins resource is its binary version - a big-endian count followed by that many
            // integers - and then the same PackageFragment message a KLIB stores in a `.knm`.
            if (bytes.Length < 4) continue;
            var versionCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes);
            var offset = 4 + (4 * versionCount);
            if (versionCount < 0 || offset > bytes.Length) continue;
            var fragment = PackageFragment.Parser.ParseFrom(bytes.AsSpan(offset).ToArray());
            var names = new NameResolver(fragment.Strings, fragment.QualifiedNames);
            foreach (var declaration in fragment.Class)
                classifiers.Add(names.ClassName(declaration.FqName));
        }
        return classifiers;
    }

    public static KlibWorld Read(
        IEnumerable<string> klibPaths,
        ProjectionReport report,
        IReadOnlySet<string>? builtinClassifiers = null)
    {
        var libraries = new List<KlibLibrary>();
        var typesByFullName = new Dictionary<string, KlibType>(StringComparer.Ordinal);

        foreach (var klibPath in klibPaths.Where(File.Exists).OrderBy(path => path, StringComparer.Ordinal))
        {
            var libraryName = Path.GetFileNameWithoutExtension(klibPath);
            var fragments = new List<KlibFragment>();
            using var stream = File.OpenRead(klibPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries
                .Where(entry => entry.FullName.Contains("/linkdata/", StringComparison.Ordinal) &&
                                entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                using var input = entry.Open();
                var fragment = PackageFragment.Parser.ParseFrom(input);
                if (fragment.IsEmpty) continue;
                fragments.Add(ReadFragment(
                    fragment, libraryName, typesByFullName, report, builtinClassifiers));
            }
            libraries.Add(new KlibLibrary(libraryName, klibPath, fragments));
        }

        CountTypeShapes(libraries, report);
        return new KlibWorld(
            libraries,
            typesByFullName,
            builtinClassifiers ?? new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Counts the type shapes that decide how much fidelity a representation can reach. A flexible type
    /// is the CLR platform type `String!`, which Kotlin source cannot spell but JVM metadata can. This
    /// walks the model rather than the protobuf: types reached through a type table are invisible to a
    /// traversal of the wire messages, and a counter that quietly skips a whole library is worse than
    /// no counter at all.
    /// </summary>
    private static void CountTypeShapes(IEnumerable<KlibLibrary> libraries, ProjectionReport report)
    {
        foreach (var fragment in libraries.SelectMany(library => library.Fragments))
        {
            foreach (var type in fragment.Types.SelectMany(type => type.SelfAndDescendants())
                .SelectMany(TypesOf)
                .Concat(fragment.Functions.SelectMany(TypesOf))
                .Concat(fragment.Properties.SelectMany(TypesOf)))
            {
                report.Count("klib.typeref.total");
                if (type.IsExtensionFunctionType) report.Count("klib.typeref.extension-function");
                if (type.IsSuspendFunctionType) report.Count("klib.typeref.suspend-function");
                if (type.IsFlexible) report.Count("klib.typeref.flexible");
                else if (type.IsNullable) report.Count("klib.typeref.nullable");
            }
        }
    }

    private static IEnumerable<KlibTypeRef> TypesOf(KlibType type) =>
        type.Supertypes.SelectMany(Expand)
            .Concat(type.Constructors.SelectMany(constructor => constructor.Parameters.SelectMany(TypesOf)))
            .Concat(type.Functions.SelectMany(TypesOf))
            .Concat(type.Properties.SelectMany(TypesOf));

    private static IEnumerable<KlibTypeRef> TypesOf(KlibFunction function) =>
        Expand(function.ReturnType).Concat(Expand(function.ReceiverType))
            .Concat(function.Parameters.SelectMany(TypesOf))
            .Concat(function.ContextParameters.SelectMany(TypesOf));

    private static IEnumerable<KlibTypeRef> TypesOf(KlibProperty property) =>
        Expand(property.ReturnType).Concat(Expand(property.ReceiverType));

    private static IEnumerable<KlibTypeRef> TypesOf(KlibValueParameter parameter) =>
        Expand(parameter.Type).Concat(Expand(parameter.VarargElementType));

    private static IEnumerable<KlibTypeRef> Expand(KlibTypeRef? type)
    {
        if (type is null) yield break;
        yield return type;
        foreach (var argument in type.Arguments)
            foreach (var nested in Expand(argument.Type))
                yield return nested;
        foreach (var nested in Expand(type.FlexibleUpperBound)) yield return nested;
    }

    private sealed class TypeTable
    {
        private readonly IReadOnlyList<ProtoType> _types;

        public static readonly TypeTable Empty = new(null);

        public TypeTable(DotKt.Klib.Metadata.TypeTable? table)
        {
            if (table is null) { _types = []; return; }
            var types = table.Type.ToList();
            if (table.HasFirstNullable)
                for (var index = table.FirstNullable; index < types.Count; index++)
                {
                    var nullable = types[index].Clone();
                    nullable.Nullable = true;
                    types[index] = nullable;
                }
            _types = types;
        }

        public ProtoType? Get(int id) => id >= 0 && id < _types.Count ? _types[id] : null;
    }

    private static KlibFragment ReadFragment(
        PackageFragment fragment,
        string libraryName,
        Dictionary<string, KlibType> typesByFullName,
        ProjectionReport report,
        IReadOnlySet<string>? builtinClassifiers)
    {
        var names = new NameResolver(fragment.Strings, fragment.QualifiedNames);
        var packageTable = new TypeTable(fragment.Package?.TypeTable);
        var packageName = fragment.HasFqName
            ? fragment.FqName
            : names.PackageName(fragment.Package?.PackageFqName ?? -1);

        // A fragment may repeat one class name across metadata parts. The first declaration is the
        // canonical shape, and nesting is resolved only against declarations of this same fragment
        // because a qualified-name parent that lives elsewhere is a package, not an outer class.
        var declarations = new Dictionary<int, ProtoClass>();
        foreach (var declaration in fragment.Class)
            // Distinct CLR types can collide here: dll2klib mangles a top-level generic's arity into the
            // name (`IComparable1`) but not a nested one, so `JSType.Function<T>` and `Function<T1, T2>`
            // arrive as one name. Emitting both would be a redeclaration, so the loss is recorded.
            if (!declarations.TryAdd(declaration.FqName, declaration))
            {
                report.Sample("klib.duplicate-declaration", names.ClassName(declaration.FqName));
                report.UpstreamGap(
                    "dll2klib mangles a top-level generic's arity into its name but not a nested one, so " +
                    "distinct nested generics collide and only the first can be emitted");
            }
        var childrenByParent = new Dictionary<int, List<ProtoClass>>();
        var roots = new List<ProtoClass>();
        foreach (var declaration in declarations.Values)
        {
            var parent = names.Parent(declaration.FqName);
            if (parent >= 0 && declarations.ContainsKey(parent))
                (childrenByParent.TryGetValue(parent, out var siblings)
                    ? siblings
                    : childrenByParent[parent] = []).Add(declaration);
            else roots.Add(declaration);
        }

        var types = roots
            .Select(root => ReadType(root, names, libraryName, declaringTypeFullName: null, childrenByParent))
            .ToArray();
        foreach (var type in types.SelectMany(type => type.SelfAndDescendants()))
        {
            typesByFullName.TryAdd(type.FullName, type);
            // A builtin classifier stays in the registry so references to it still resolve, but it is
            // accounted for separately: the analyzer gets its declaration from the builtins resource.
            if (builtinClassifiers?.Contains(type.FullName) == true)
            {
                report.Count("input.builtin-class");
                continue;
            }
            // The input side of the declaration accounting: whatever is counted here has to come out
            // the other end, or the difference is a loss the report must name.
            report.Count("input.class");
            report.Count("input.function", type.Functions.Count);
            report.Count("input.property", type.Properties.Count);
            report.Count("input.constructor", type.Constructors.Count);
            report.Count("input.enum-entry", type.EnumEntries.Count);
        }
        report.Count("input.top-level-function", fragment.Package?.Function.Count ?? 0);
        report.Count("input.top-level-property", fragment.Package?.Property.Count ?? 0);

        return new KlibFragment(
            packageName,
            types,
            (fragment.Package?.Function ?? Enumerable.Empty<ProtoFunction>())
                .Select(function => ReadFunction(function, names, packageTable)).ToArray(),
            (fragment.Package?.Property ?? Enumerable.Empty<ProtoProperty>())
                .Select(property => ReadProperty(property, names, packageTable)).ToArray());
    }

    private static KlibType ReadType(
        ProtoClass declaration,
        NameResolver names,
        string libraryName,
        string? declaringTypeFullName,
        IReadOnlyDictionary<int, List<ProtoClass>> childrenByParent)
    {
        var fullName = names.ClassName(declaration.FqName);
        var table = new TypeTable(declaration.TypeTable);
        var nested = childrenByParent.TryGetValue(declaration.FqName, out var children)
            ? children
                .OrderBy(child => names.ClassName(child.FqName), StringComparer.Ordinal)
                .Select(child => ReadType(child, names, libraryName, fullName, childrenByParent))
                .ToArray()
            : [];

        return new KlibType(
            fullName,
            names.SimpleClassName(declaration.FqName),
            names.PackageName(declaration.FqName),
            names.JvmInternalName(declaration.FqName),
            names.JvmClassId(declaration.FqName),
            declaringTypeFullName,
            KlibFlags.TypeKind(declaration.Flags),
            KlibFlags.Modality(declaration.Flags),
            KlibFlags.Visibility(declaration.Flags),
            KlibFlags.ClassIsInner(declaration.Flags),
            KlibFlags.ClassIsValue(declaration.Flags),
            declaration.TypeParameter.Select(parameter => ReadTypeParameter(parameter, names, table)).ToArray(),
            declaration.Supertype.Select(supertype => ReadTypeRef(supertype, names, table))
                .Concat(declaration.SupertypeId.Select(id => ReadTypeRefById(id, names, table)))
                .OfType<KlibTypeRef>().ToArray(),
            declaration.Constructor.Select(constructor => ReadConstructor(constructor, names, table)).ToArray(),
            declaration.Function.Select(function => ReadFunction(function, names, table)).ToArray(),
            declaration.Property.Select(property => ReadProperty(property, names, table)).ToArray(),
            declaration.EnumEntry.Select(entry => names.String(entry.Name)).ToArray(),
            nested,
            libraryName,
            new KlibRawFlags(declaration.Flags));
    }

    private static KlibConstructor ReadConstructor(
        ProtoConstructor constructor, NameResolver names, TypeTable table) =>
        new(KlibFlags.Visibility(constructor.Flags),
            KlibFlags.ConstructorIsSecondary(constructor.Flags),
            constructor.ValueParameter.Select(parameter => ReadValueParameter(parameter, names, table)).ToArray(),
            new KlibRawFlags(constructor.Flags));

    private static KlibFunction ReadFunction(ProtoFunction function, NameResolver names, TypeTable outer)
    {
        // A function may carry its own table; otherwise its types live in the enclosing one.
        var table = function.TypeTable is null ? outer : new TypeTable(function.TypeTable);
        return new KlibFunction(
            names.String(function.Name),
            ReadType(function.ReturnType, function.HasReturnTypeId, function.ReturnTypeId, names, table),
            ReadType(function.ReceiverType, function.HasReceiverTypeId, function.ReceiverTypeId, names, table),
            function.TypeParameter.Select(parameter => ReadTypeParameter(parameter, names, table)).ToArray(),
            function.ValueParameter.Select(parameter => ReadValueParameter(parameter, names, table)).ToArray(),
            function.ContextParameter.Select(parameter => ReadValueParameter(parameter, names, table)).ToArray(),
            KlibFlags.Visibility(function.Flags),
            KlibFlags.Modality(function.Flags),
            KlibFlags.MemberKind(function.Flags),
            KlibFlags.FunctionIsClrStatic(function.Flags),
            KlibFlags.FunctionIsSuspend(function.Flags),
            KlibFlags.FunctionIsOperator(function.Flags),
            KlibFlags.FunctionIsInfix(function.Flags),
            new KlibRawFlags(function.Flags));
    }

    private static KlibProperty ReadProperty(ProtoProperty property, NameResolver names, TypeTable table) =>
        new(names.String(property.Name),
            ReadType(property.ReturnType, property.HasReturnTypeId, property.ReturnTypeId, names, table),
            ReadType(property.ReceiverType, property.HasReceiverTypeId, property.ReceiverTypeId, names, table),
            property.TypeParameter.Select(parameter => ReadTypeParameter(parameter, names, table)).ToArray(),
            KlibFlags.Visibility(property.Flags),
            KlibFlags.Modality(property.Flags),
            KlibFlags.MemberKind(property.Flags),
            KlibFlags.PropertyIsClrStatic(property.Flags),
            KlibFlags.PropertyIsVar(property.Flags) || property.SetterValueParameter is not null,
            KlibFlags.PropertyIsConst(property.Flags),
            new KlibRawFlags(property.Flags),
            property.HasGetterFlags ? new KlibRawFlags(property.GetterFlags) : null,
            property.HasSetterFlags ? new KlibRawFlags(property.SetterFlags) : null);

    private static KlibTypeParameter ReadTypeParameter(
        ProtoTypeParameter parameter, NameResolver names, TypeTable table) =>
        new(parameter.Id,
            names.String(parameter.Name),
            (KlibVariance)(int)parameter.Variance,
            parameter.Reified,
            parameter.UpperBound.Select(bound => ReadTypeRef(bound, names, table))
                .Concat(parameter.UpperBoundId.Select(id => ReadTypeRefById(id, names, table)))
                .OfType<KlibTypeRef>().ToArray());

    private static KlibValueParameter ReadValueParameter(
        ProtoValueParameter parameter, NameResolver names, TypeTable table) =>
        new(names.String(parameter.Name),
            ReadType(parameter.Type, parameter.HasTypeId, parameter.TypeId, names, table),
            ReadType(
                parameter.VarargElementType,
                parameter.HasVarargElementTypeId,
                parameter.VarargElementTypeId,
                names,
                table),
            KlibFlags.ValueParameterHasDefault(parameter.Flags),
            new KlibRawFlags(parameter.Flags));

    /// <summary>Reads a type given inline, by table index, or not at all.</summary>
    private static KlibTypeRef? ReadType(
        ProtoType? inline, bool hasId, int id, NameResolver names, TypeTable table) =>
        inline is not null ? ReadTypeRef(inline, names, table)
            : hasId ? ReadTypeRefById(id, names, table)
            : null;

    private static KlibTypeRef? ReadTypeRefById(int id, NameResolver names, TypeTable table) =>
        table.Get(id) is { } type ? ReadTypeRef(type, names, table) : null;

    private static KlibTypeRef ReadTypeRef(ProtoType type, NameResolver names, TypeTable table) =>
        new(type.HasClassName ? names.ClassName(type.ClassName) : null,
            type.HasClassName ? names.JvmInternalName(type.ClassName) : null,
            type.HasClassName ? names.JvmClassId(type.ClassName) : null,
            type.HasTypeParameter ? type.TypeParameter : null,
            type.HasTypeParameterName ? names.String(type.TypeParameterName) : null,
            type.Argument
                .Select(argument => new KlibTypeArgument(
                    (KlibProjection)(int)argument.Projection,
                    ReadType(argument.Type, argument.HasTypeId, argument.TypeId, names, table)))
                .ToArray(),
            type.Nullable,
            ReadType(
                type.FlexibleUpperBound,
                type.HasFlexibleUpperBoundId,
                type.FlexibleUpperBoundId,
                names,
                table),
            type.TypeAnnotation.Any(annotation => names.ClassName(annotation.Id) == "kotlin.ExtensionFunctionType"),
            new KlibRawFlags(type.Flags));
}

internal sealed class NameResolver
{
    private readonly StringTable _strings;
    private readonly QualifiedNameTable _qualifiedNames;
    private readonly Dictionary<int, QualifiedNameParts> _cache = new();

    public NameResolver(StringTable? strings, QualifiedNameTable? qualifiedNames)
    {
        _strings = strings ?? new StringTable();
        _qualifiedNames = qualifiedNames ?? new QualifiedNameTable();
    }

    public string String(int index) =>
        index >= 0 && index < _strings.String.Count ? _strings.String[index] : "unknown";

    public int Parent(int qualifiedName) =>
        qualifiedName >= 0 && qualifiedName < _qualifiedNames.QualifiedName.Count
            ? _qualifiedNames.QualifiedName[qualifiedName].ParentQualifiedName
            : -1;

    public string PackageName(int qualifiedName) => Resolve(qualifiedName).PackageName;
    public string ClassName(int qualifiedName) => Resolve(qualifiedName).FullName;
    public string SimpleClassName(int qualifiedName) => Resolve(qualifiedName).ClassNames.LastOrDefault() ?? "Unknown";

    public string JvmInternalName(int qualifiedName) => JvmInternalName(Resolve(qualifiedName));

    /// <summary>
    /// The Kotlin class id a JVM metadata string table stores: package separated by <c>/</c> and the
    /// nesting chain by <c>.</c>, as in <c>System/Text/StringBuilder.ChunkEnumerator</c>.
    /// </summary>
    public string JvmClassId(int qualifiedName)
    {
        var parts = Resolve(qualifiedName);
        var classes = string.Join('.', parts.ClassNames);
        return parts.PackageName.Length == 0
            ? classes
            : parts.PackageName.Replace('.', '/') + "/" + classes;
    }

    private static string JvmInternalName(QualifiedNameParts parts)
    {
        var classes = string.Join('$', parts.ClassNames);
        return parts.PackageName.Length == 0
            ? classes
            : parts.PackageName.Replace('.', '/') + "/" + classes;
    }

    private QualifiedNameParts Resolve(int index)
    {
        if (_cache.TryGetValue(index, out var cached)) return cached;
        var packageNames = new Stack<string>();
        var classNames = new Stack<string>();
        var current = index;
        var seen = new HashSet<int>();
        while (current >= 0 && current < _qualifiedNames.QualifiedName.Count && seen.Add(current))
        {
            var item = _qualifiedNames.QualifiedName[current];
            if (item.Kind == QualifiedNameTable.Types.QualifiedName.Types.Kind.Package)
                packageNames.Push(String(item.ShortName));
            else
                classNames.Push(String(item.ShortName));
            current = item.ParentQualifiedName;
        }
        var package = string.Join('.', packageNames);
        var classes = classNames.ToArray();
        var fullName = string.Join('.', new[] { package }.Where(x => x.Length != 0).Concat(classes));
        var result = new QualifiedNameParts(package, classes, fullName);
        _cache[index] = result;
        return result;
    }

    private sealed record QualifiedNameParts(string PackageName, string[] ClassNames, string FullName);
}
