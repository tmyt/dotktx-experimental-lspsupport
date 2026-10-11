namespace DotKtx.Experimental.LspSupport;

/// <summary>
/// Chooses which KLIB declarations become analysis-only JVM classfiles instead of Kotlin source stubs.
/// The classfile path is what preserves CLR platform types, statics, generics and inheritance, so the
/// policy is the single place that decides how far that representation is trusted for a given build.
/// </summary>
internal sealed record ProjectionPolicy(
    IReadOnlyList<string> Types,
    IReadOnlyList<string> PackagePrefixes,
    IReadOnlyList<string> Libraries,
    bool AllTypes,
    bool CloseOverSupertypes,
    bool Strict,
    bool KotlinMetadata,
    bool TopLevelFacades)
{
    /// <summary>
    /// Every referenced declaration, carrying its own Kotlin metadata. A metadata-bearing classfile is
    /// the declaration rather than an approximation of it, and measurement says the whole surface indexes
    /// faster this way than as generated Kotlin sources, so there is no reason to project a subset.
    /// </summary>
    public static ProjectionPolicy Default { get; } = new(
        Types: [],
        PackagePrefixes: [],
        Libraries: [],
        AllTypes: true,
        CloseOverSupertypes: true,
        Strict: true,
        KotlinMetadata: true,
        TopLevelFacades: true);

    /// <summary>
    /// Parses <c>type:Name;package:Prefix;library:Name;all;no-supertype-closure;strict;no-metadata</c>.
    /// An empty specification keeps <see cref="Default"/> so an ordinary consumer build never depends on
    /// syntax. Metadata is on unless <c>no-metadata</c> asks for the plain-Java approximation instead.
    /// </summary>
    public static ProjectionPolicy Parse(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification)) return Default;

        var types = new List<string>();
        var packagePrefixes = new List<string>();
        var libraries = new List<string>();
        var allTypes = false;
        var closeOverSupertypes = true;
        var strict = false;
        var kotlinMetadata = true;
        var topLevelFacades = true;

        foreach (var raw in specification.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            var separator = entry.IndexOf(':');
            var kind = (separator < 0 ? entry : entry[..separator]).Trim().ToLowerInvariant();
            var value = separator < 0 ? "" : entry[(separator + 1)..].Trim();
            switch (kind)
            {
                case "type" when value.Length != 0: types.Add(value); break;
                case "package" when value.Length != 0: packagePrefixes.Add(value); break;
                case "library" when value.Length != 0: libraries.Add(value); break;
                case "all": allTypes = true; break;
                case "none": break;
                case "no-supertype-closure": closeOverSupertypes = false; break;
                case "strict": strict = true; break;
                case "metadata": kotlinMetadata = true; break;
                case "no-metadata": kotlinMetadata = false; break;
                case "no-facades": topLevelFacades = false; break;
                default: throw new ArgumentException($"unknown Java projection policy entry '{entry}'");
            }
        }

        return new ProjectionPolicy(
            types, packagePrefixes, libraries, allTypes, closeOverSupertypes, strict, kotlinMetadata,
            topLevelFacades);
    }

    public bool SelectsNothing =>
        !AllTypes && Types.Count == 0 && PackagePrefixes.Count == 0 && Libraries.Count == 0;

    public string Describe() =>
        (KotlinMetadata ? "kotlin metadata; " : "plain java; ") +
        (AllTypes
            ? "all"
            : string.Join(
                ", ",
                Types.Select(type => "type:" + type)
                    .Concat(PackagePrefixes.Select(prefix => "package:" + prefix))
                    .Concat(Libraries.Select(library => "library:" + library))
                    .DefaultIfEmpty("none")));

    /// <summary>
    /// Resolves the policy against the whole KLIB world. Nested types travel with their outer type, and
    /// - unless disabled - supertypes are pulled in transitively so a projected class never extends a
    /// binary name that exists nowhere in the archive.
    /// </summary>
    public IReadOnlyList<KlibType> Select(KlibWorld world, ProjectionReport report)
    {
        var selected = new Dictionary<string, KlibType>(StringComparer.Ordinal);
        var matchedTypes = new HashSet<string>(StringComparer.Ordinal);
        var matchedPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var matchedLibraries = new HashSet<string>(StringComparer.Ordinal);
        var exactTypes = Types.ToHashSet(StringComparer.Ordinal);
        var libraries = Libraries.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var type in world.AllTypes)
        {
            var matched = AllTypes;
            if (exactTypes.Contains(type.FullName))
            {
                matchedTypes.Add(type.FullName);
                matched = true;
            }
            foreach (var prefix in PackagePrefixes.Where(prefix => MatchesPackage(type.PackageName, prefix)))
            {
                matchedPrefixes.Add(prefix);
                matched = true;
            }
            if (libraries.Contains(type.LibraryName))
            {
                matchedLibraries.Add(type.LibraryName);
                matched = true;
            }
            if (matched) AddFamily(type, selected, world);
        }

        if (CloseOverSupertypes) CloseSupertypes(selected, world, report);

        foreach (var missing in exactTypes.Except(matchedTypes).Order(StringComparer.Ordinal))
            report.PolicyMiss("type", missing, Strict);
        foreach (var missing in PackagePrefixes.Except(matchedPrefixes).Order(StringComparer.Ordinal))
            report.PolicyMiss("package", missing, isError: false);
        foreach (var missing in Libraries.Except(matchedLibraries, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal))
            report.PolicyMiss("library", missing, isError: false);

        return selected.Values.OrderBy(type => type.JvmInternalName, StringComparer.Ordinal).ToArray();
    }

    private static bool MatchesPackage(string packageName, string prefix) =>
        string.Equals(packageName, prefix, StringComparison.Ordinal) ||
        packageName.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>Adds a type together with the nested subtree it owns and the outer chain that names it.</summary>
    private static void AddFamily(KlibType type, Dictionary<string, KlibType> selected, KlibWorld world)
    {
        foreach (var member in type.SelfAndDescendants()) selected.TryAdd(member.FullName, member);
        AddOuterChain(type, selected, world);
    }

    private static void AddOuterChain(
        KlibType type,
        Dictionary<string, KlibType> selected,
        KlibWorld world,
        Queue<KlibType>? pending = null)
    {
        var current = world.Find(type.DeclaringTypeFullName);
        while (current is not null && selected.TryAdd(current.FullName, current))
        {
            pending?.Enqueue(current);
            current = world.Find(current.DeclaringTypeFullName);
        }
    }

    private static void CloseSupertypes(
        Dictionary<string, KlibType> selected,
        KlibWorld world,
        ProjectionReport report)
    {
        var pending = new Queue<KlibType>(selected.Values);
        while (pending.Count != 0)
        {
            var type = pending.Dequeue();
            foreach (var supertype in type.Supertypes)
            {
                if (supertype.ClassName is not { } name || name is "kotlin.Any") continue;
                if (world.Resolve(supertype, type) is not { } resolved)
                {
                    // Kotlin built-ins and unresolvable names have no KLIB declaration to pull in; the
                    // classfile emitter reports them once it knows whether they actually matter.
                    continue;
                }
                if (!selected.TryAdd(resolved.FullName, resolved)) continue;
                AddOuterChain(resolved, selected, world, pending);
                pending.Enqueue(resolved);
            }
        }
    }
}
