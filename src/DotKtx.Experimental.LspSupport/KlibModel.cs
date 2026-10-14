namespace DotKtx.Experimental.LspSupport;

// A projection-independent view of the DotKt frontend KLIBs. Both the Kotlin source emitter and the
// Java classfile emitter read this model instead of the protobuf tables, so a KLIB is parsed once and
// every cross-library question (is this supertype an interface? what is this nested type's binary
// name?) is answered by lookup rather than by guessing from the locally selected declarations.

internal enum KlibTypeKind
{
    Class = 0,
    Interface = 1,
    EnumClass = 2,
    EnumEntry = 3,
    AnnotationClass = 4,
    Object = 5,
    CompanionObject = 6,
}

internal enum KlibModality
{
    Final = 0,
    Open = 1,
    Abstract = 2,
    Sealed = 3,
}

internal enum KlibVisibility
{
    Internal = 0,
    Private = 1,
    Protected = 2,
    Public = 3,
    PrivateToThis = 4,
    Local = 5,
}

internal enum KlibMemberKind
{
    Declaration = 0,
    FakeOverride = 1,
    Delegation = 2,
    Synthesized = 3,
}

internal enum KlibVariance
{
    In = 0,
    Out = 1,
    Invariant = 2,
}

internal enum KlibProjection
{
    In = 0,
    Out = 1,
    Invariant = 2,
    Star = 3,
}

/// <summary>
/// A resolved reference to a class, to a type parameter, or to neither when the KLIB omitted both.
/// <paramref name="ClassJvmInternalName"/> is derived from the qualified-name table, which records for
/// every segment whether it is a package or an outer class - the only place that distinction survives,
/// and therefore the only reliable source of a nested binary name such as <c>Outer$Inner</c>.
/// </summary>
internal sealed record KlibTypeRef(
    string? ClassName,
    string? ClassJvmInternalName,
    string? ClassJvmClassId,
    int? TypeParameterId,
    // A type variable referenced by name rather than by id, which is how the Kotlin compiler writes it.
    string? TypeParameterName,
    IReadOnlyList<KlibTypeArgument> Arguments,
    bool IsNullable,
    KlibTypeRef? FlexibleUpperBound,
    // Function1<T, Unit> alone means a lambda with an `it` parameter. This type-use marker makes
    // its first argument the implicit receiver instead: T.() -> Unit.
    bool IsExtensionFunctionType = false,
    // Type flags are separate from declaration flags. In particular, SUSPEND_TYPE is what turns
    // FunctionN<..., Continuation<R>, Any?> back into a suspend function type on deserialization.
    KlibRawFlags RawFlags = default)
{
    public bool IsFlexible => FlexibleUpperBound is not null;
    public bool IsSuspendFunctionType => KlibFlags.TypeIsSuspend(RawFlags);
}

internal sealed record KlibTypeArgument(KlibProjection Projection, KlibTypeRef? Type);

internal sealed record KlibTypeParameter(
    int Id,
    string Name,
    KlibVariance Variance,
    bool IsReified,
    IReadOnlyList<KlibTypeRef> UpperBounds);

/// <summary>
/// The Kotlin metadata flag words, kept verbatim. Re-encoding them from the decoded properties would
/// silently drop every bit this model does not name, so the metadata writer passes them straight
/// through and only the projection logic reads the decoded form.
/// </summary>
internal readonly record struct KlibRawFlags(int Value)
{
    public static implicit operator int(KlibRawFlags flags) => flags.Value;
}

internal sealed record KlibValueParameter(
    string Name,
    KlibTypeRef? Type,
    KlibTypeRef? VarargElementType,
    bool HasDefaultValue,
    KlibRawFlags RawFlags)
{
    public bool IsVararg => VarargElementType is not null;
}

internal sealed record KlibConstructor(
    KlibVisibility Visibility,
    bool IsSecondary,
    IReadOnlyList<KlibValueParameter> Parameters,
    KlibRawFlags RawFlags);

internal sealed record KlibFunction(
    string Name,
    KlibTypeRef? ReturnType,
    KlibTypeRef? ReceiverType,
    IReadOnlyList<KlibTypeParameter> TypeParameters,
    IReadOnlyList<KlibValueParameter> Parameters,
    IReadOnlyList<KlibValueParameter> ContextParameters,
    KlibVisibility Visibility,
    KlibModality Modality,
    KlibMemberKind MemberKind,
    bool IsStatic,
    bool IsSuspend,
    bool IsOperator,
    bool IsInfix,
    KlibRawFlags RawFlags);

internal sealed record KlibProperty(
    string Name,
    KlibTypeRef? ReturnType,
    KlibTypeRef? ReceiverType,
    IReadOnlyList<KlibTypeParameter> TypeParameters,
    KlibVisibility Visibility,
    KlibModality Modality,
    KlibMemberKind MemberKind,
    bool IsStatic,
    bool IsVar,
    bool IsConst,
    KlibRawFlags RawFlags,
    // Absent accessor flags mean "same as the property"; writing a zero word instead would silently
    // demote the accessor to internal visibility and hide the property from the editor.
    KlibRawFlags? GetterFlags,
    KlibRawFlags? SetterFlags);

internal sealed record KlibType(
    string FullName,
    string SimpleName,
    string PackageName,
    string JvmInternalName,
    string JvmClassId,
    string? DeclaringTypeFullName,
    KlibTypeKind Kind,
    KlibModality Modality,
    KlibVisibility Visibility,
    bool IsInner,
    bool IsValue,
    IReadOnlyList<KlibTypeParameter> TypeParameters,
    IReadOnlyList<KlibTypeRef> Supertypes,
    IReadOnlyList<KlibConstructor> Constructors,
    IReadOnlyList<KlibFunction> Functions,
    IReadOnlyList<KlibProperty> Properties,
    IReadOnlyList<string> EnumEntries,
    IReadOnlyList<KlibType> NestedTypes,
    string LibraryName,
    KlibRawFlags RawFlags)
{
    public bool IsInterface => Kind == KlibTypeKind.Interface;

    public IEnumerable<KlibType> SelfAndDescendants()
    {
        yield return this;
        foreach (var nested in NestedTypes)
            foreach (var descendant in nested.SelfAndDescendants())
                yield return descendant;
    }
}

internal sealed record KlibFragment(
    string PackageName,
    IReadOnlyList<KlibType> Types,
    IReadOnlyList<KlibFunction> Functions,
    IReadOnlyList<KlibProperty> Properties);

internal sealed record KlibLibrary(
    string Name,
    string Path,
    IReadOnlyList<KlibFragment> Fragments);

/// <summary>Every referenced KLIB, plus a whole-world index used to classify supertypes and nested names.</summary>
internal sealed record KlibWorld(
    IReadOnlyList<KlibLibrary> Libraries,
    IReadOnlyDictionary<string, KlibType> TypesByFullName,
    IReadOnlySet<string> BuiltinClassifiers)
{
    /// <summary>
    /// True when the analyzer takes this classifier from a `*.kotlin_builtins` resource. Such a type
    /// must not also be projected as a classfile: on the JVM `kotlin.Int` is a primitive and
    /// `kotlin.collections.List` is `java.util.List`, and a same-named classfile would fight that.
    /// </summary>
    public bool IsBuiltinClassifier(string fullName) => BuiltinClassifiers.Contains(fullName);

    private Dictionary<string, List<KlibType>>? _byPackageAndSimpleName;

    public IEnumerable<KlibType> AllTypes =>
        Libraries.SelectMany(library => library.Fragments)
            .SelectMany(fragment => fragment.Types)
            .SelectMany(type => type.SelfAndDescendants());

    public KlibType? Find(string? fullName) =>
        fullName is not null && TypesByFullName.TryGetValue(fullName, out var type) ? type : null;

    /// <summary>
    /// Resolves a type reference that may be written relative to <paramref name="owner"/>. The KLIB
    /// qualified-name table sometimes records only a nested type's short name, and dll2klib mangles a
    /// generic's arity into the outer name (<c>ImmutableArray1.Builder</c>) while the reference still
    /// says <c>Builder</c>, so the plain lookup has to be backed by a scope walk.
    /// </summary>
    public KlibType? Resolve(
        KlibTypeRef reference,
        KlibType owner,
        ProjectionReport? report = null,
        KlibType? extraScope = null)
    {
        if (reference.ClassName is not { } className) return null;
        if (Find(className) is { } direct) return direct;
        // A reference the qualified-name table spelled with a package is absolute. Letting the relative
        // fallbacks run on it would let `kotlin.clr.Span` be captured by a same-named `System.Span`.
        if (reference.ClassJvmInternalName?.Contains('/') == true) return null;

        var simpleName = SimpleName(reference);
        foreach (var scope in OuterChain(owner))
            if (Find(scope.FullName + "." + simpleName) is { } nested) return nested;
        // An extension's receiver says which type a bare nested name belongs to: `ImmutableArray<T>
        // .ToBuilder(): Builder` means that array's Builder, not any of the five others in the package.
        if (extraScope is not null)
        {
            foreach (var scope in OuterChain(extraScope))
                if (Find(scope.FullName + "." + simpleName) is { } nested) return nested;
            if (InheritedNested(extraScope, simpleName) is { } inheritedFromReceiver) return inheritedFromReceiver;
        }
        if (owner.PackageName.Length != 0 && Find(owner.PackageName + "." + simpleName) is { } sibling)
            return sibling;
        // A nested type is often inherited rather than declared, as with `TypeConverter.StandardValues-
        // Collection` referenced from a converter subclass.
        if (InheritedNested(owner, simpleName) is { } inherited) return inherited;

        // Everything exact has failed, so what follows are inferences. Each one is only accepted when
        // it leaves a single candidate, and each records which inference was used - a reference that
        // resolves to the wrong type is worse than one the report admits it could not place.
        var candidates = BySimpleName().GetValueOrDefault(simpleName, []);
        if (candidates.Count == 0) return null;

        var samePackage = candidates
            .Where(candidate => string.Equals(candidate.PackageName, owner.PackageName, StringComparison.Ordinal))
            .ToArray();
        if (samePackage.Length == 1)
        {
            report?.Tally("reference.resolved-in-package", samePackage[0].FullName);
            return samePackage[0];
        }

        // The declaring type is often named by the context: an outer type, the package's last segment
        // (dll2klib puts a static class' extensions in a package of that name), the receiver or another
        // type from the same signature, or the owner with a conventional `...Extensions` suffix removed.
        var contextNames = OuterChain(owner).Select(scope => scope.SimpleName)
            .Concat(owner.PackageName.Length == 0 ? [] : [owner.PackageName.Split('.')[^1]])
            .Concat(extraScope is null
                ? []
                : OuterChain(extraScope).Select(scope => scope.SimpleName)
                    .Concat(extraScope.PackageName.Length == 0 ? [] : [extraScope.PackageName.Split('.')[^1]]))
            .SelectMany(name => new[] { name, StripConventionalSuffix(name) })
            .Where(name => name.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var byContext = candidates
            .Where(candidate => Find(candidate.DeclaringTypeFullName) is { } declaring &&
                                contextNames.Any(name => MatchesIgnoringArity(declaring.SimpleName, name)))
            .Distinct()
            .ToArray();
        if (byContext.Length == 1)
        {
            report?.Tally("reference.resolved-by-context", byContext[0].FullName);
            return byContext[0];
        }
        return null;
    }

    private static readonly string[] ConventionalSuffixes = ["Extensions", "Marshal", "Helpers", "Helper"];

    private static string StripConventionalSuffix(string name)
    {
        foreach (var suffix in ConventionalSuffixes)
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                return name[..^suffix.Length];
        return name;
    }

    /// <summary>Finds a nested type of that name on any transitive supertype of the owner.</summary>
    private KlibType? InheritedNested(KlibType owner, string simpleName)
    {
        var pending = new Queue<KlibType>([owner]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { owner.FullName };
        while (pending.Count != 0 && seen.Count < 64)
        {
            foreach (var supertype in pending.Dequeue().Supertypes)
            {
                if (supertype.ClassName is not { } name || Find(name) is not { } resolved) continue;
                if (!seen.Add(resolved.FullName)) continue;
                if (Find(resolved.FullName + "." + simpleName) is { } nested) return nested;
                pending.Enqueue(resolved);
            }
        }
        return null;
    }

    private IEnumerable<KlibType> OuterChain(KlibType owner)
    {
        for (var scope = owner; scope is not null; scope = Find(scope.DeclaringTypeFullName)) yield return scope;
    }

    /// <summary>
    /// Compares two names ignoring the arity suffix dll2klib appends to a generic type, in either
    /// direction: a reference may say <c>Dictionary2</c> where the declaration is <c>Dictionary</c>.
    /// </summary>
    private static bool MatchesIgnoringArity(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal) ||
        IsAritySuffixed(left, right) ||
        IsAritySuffixed(right, left);

    private static bool IsAritySuffixed(string candidate, string bare) =>
        candidate.Length > bare.Length &&
        candidate.StartsWith(bare, StringComparison.Ordinal) &&
        candidate[bare.Length..].All(char.IsAsciiDigit);

    private static string SimpleName(KlibTypeRef reference)
    {
        var name = reference.ClassName ?? reference.ClassJvmInternalName ?? "";
        var separator = name.LastIndexOfAny(['.', '/', '$']);
        return separator < 0 ? name : name[(separator + 1)..];
    }

    private static string Key(string packageName, string simpleName) => packageName + "|" + simpleName;

    private Dictionary<string, List<KlibType>>? _bySimpleName;

    private Dictionary<string, List<KlibType>> BySimpleName()
    {
        if (_bySimpleName is not null) return _bySimpleName;
        var index = new Dictionary<string, List<KlibType>>(StringComparer.Ordinal);
        foreach (var type in TypesByFullName.Values.Where(type => type.DeclaringTypeFullName is not null))
            (index.TryGetValue(type.SimpleName, out var bucket) ? bucket : index[type.SimpleName] = []).Add(type);
        return _bySimpleName = index;
    }

    private Dictionary<string, List<KlibType>> ByPackageAndSimpleName()
    {
        if (_byPackageAndSimpleName is not null) return _byPackageAndSimpleName;
        var index = new Dictionary<string, List<KlibType>>(StringComparer.Ordinal);
        foreach (var type in TypesByFullName.Values)
        {
            var key = Key(type.PackageName, type.SimpleName);
            (index.TryGetValue(key, out var bucket) ? bucket : index[key] = []).Add(type);
        }
        return _byPackageAndSimpleName = index;
    }
}

/// <summary>
/// Decodes the Kotlin metadata flag words. Bits 0-8 follow Kotlin's own <c>Flags</c> layout; the two
/// static bits above them are how DotKt's dll2klib records a CLR <c>static</c> member, which has no
/// Kotlin equivalent.
/// </summary>
internal static class KlibFlags
{
    private const int VisibilityShift = 1;
    private const int ModalityShift = 4;
    private const int KindShift = 6;
    private const int ClassIsInnerBit = 9;
    private const int ClassIsValueBit = 13;
    private const int PropertyIsVarBit = 8;
    private const int PropertyHasSetterBit = 10;
    private const int PropertyIsConstBit = 11;
    private const int FunctionIsOperatorBit = 8;
    private const int FunctionIsInfixBit = 9;
    private const int FunctionIsSuspendBit = 13;
    private const int TypeIsSuspendBit = 0;
    private const int ConstructorIsSecondaryBit = 4;
    private const int ValueParameterHasDefaultBit = 1;
    private const int FunctionIsClrStaticBit = 18;
    private const int PropertyIsClrStaticBit = 19;

    public static KlibVisibility Visibility(int flags) => (KlibVisibility)((flags >> VisibilityShift) & 0x7);
    public static KlibModality Modality(int flags) => (KlibModality)((flags >> ModalityShift) & 0x3);
    public static KlibTypeKind TypeKind(int flags) => (KlibTypeKind)((flags >> KindShift) & 0x7);
    public static KlibMemberKind MemberKind(int flags) => (KlibMemberKind)((flags >> KindShift) & 0x3);
    public static bool ClassIsInner(int flags) => Bit(flags, ClassIsInnerBit);
    public static bool ClassIsValue(int flags) => Bit(flags, ClassIsValueBit);
    public static bool PropertyIsVar(int flags) => Bit(flags, PropertyIsVarBit);
    public static bool PropertyHasSetter(int flags) => Bit(flags, PropertyHasSetterBit);
    public static bool PropertyIsConst(int flags) => Bit(flags, PropertyIsConstBit);
    public static bool FunctionIsOperator(int flags) => Bit(flags, FunctionIsOperatorBit);
    public static bool FunctionIsInfix(int flags) => Bit(flags, FunctionIsInfixBit);
    public static bool FunctionIsSuspend(int flags) => Bit(flags, FunctionIsSuspendBit);
    public static bool TypeIsSuspend(int flags) => Bit(flags, TypeIsSuspendBit);
    public static bool ConstructorIsSecondary(int flags) => Bit(flags, ConstructorIsSecondaryBit);
    public static bool ValueParameterHasDefault(int flags) => Bit(flags, ValueParameterHasDefaultBit);
    public static bool FunctionIsClrStatic(int flags) => Bit(flags, FunctionIsClrStaticBit);
    public static bool PropertyIsClrStatic(int flags) => Bit(flags, PropertyIsClrStaticBit);

    private static bool Bit(int flags, int index) => (flags & (1 << index)) != 0;
}
