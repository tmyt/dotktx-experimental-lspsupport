using System.Text;

namespace DotKtx.Experimental.LspSupport;

internal sealed record ProjectionResult(
    IReadOnlyDictionary<string, string> Files,
    int ClassCount,
    int CallableCount);

/// <summary>
/// Renders the declarations that are not carried by the Java classfile projection as analysis-only
/// Kotlin sources. Kotlin source cannot express a CLR flexible type, so this path is the fallback for
/// everything the classfile emitter deliberately leaves alone - most importantly top-level callables,
/// which have no faithful plain-Java shape.
/// </summary>
internal static class KlibSourceProjector
{
    public static ProjectionResult Project(
        KlibWorld world,
        IReadOnlySet<string> excludedClasses,
        bool excludeTopLevelDeclarations)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var knownClasses = new HashSet<string>(StringComparer.Ordinal);
        var classCount = 0;
        var callableCount = 0;

        for (var libraryIndex = 0; libraryIndex < world.Libraries.Count; libraryIndex++)
        {
            var library = world.Libraries[libraryIndex];
            var libraryName = SafePath(library.Name);
            var fragmentIndex = 0;
            foreach (var fragment in library.Fragments)
            {
                var renderer = new FragmentRenderer(
                    fragment, knownClasses, excludedClasses, excludeTopLevelDeclarations);
                var rendered = renderer.Render();
                if (rendered is null) continue;

                var packagePath = fragment.PackageName.Length == 0
                    ? "root"
                    : string.Join('/', fragment.PackageName.Split('.').Select(SafePath));
                var relative = $"{libraryIndex:D3}_{libraryName}/{packagePath}/{fragmentIndex:D3}_Declarations.kt";
                files[relative] = rendered;
                classCount += renderer.ClassCount;
                callableCount += renderer.CallableCount;
                fragmentIndex++;
            }
        }

        return new ProjectionResult(files, classCount, callableCount);
    }

    private static string SafePath(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        return builder.Length == 0 ? "_" : builder.ToString();
    }
}

internal sealed class FragmentRenderer
{
    private readonly KlibFragment _fragment;
    private readonly HashSet<string> _knownClasses;
    private readonly IReadOnlySet<string> _excludedClasses;
    private readonly bool _excludeTopLevelDeclarations;

    public int ClassCount { get; private set; }
    public int CallableCount { get; private set; }

    public FragmentRenderer(
        KlibFragment fragment,
        HashSet<string> knownClasses,
        IReadOnlySet<string> excludedClasses,
        bool excludeTopLevelDeclarations)
    {
        _fragment = fragment;
        _knownClasses = knownClasses;
        _excludedClasses = excludedClasses;
        _excludeTopLevelDeclarations = excludeTopLevelDeclarations;
    }

    public string? Render()
    {
        var roots = _fragment.Types
            .Where(type => !_excludedClasses.Contains(type.FullName))
            .Where(type => _knownClasses.Add(type.FullName))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        // Top-level callables have a JVM home only when file facades are emitted; otherwise this is the
        // only representation they get.
        var functions = _excludeTopLevelDeclarations ? [] : _fragment.Functions;
        var properties = _excludeTopLevelDeclarations ? [] : _fragment.Properties;
        if (roots.Length == 0 && functions.Count == 0 && properties.Count == 0) return null;

        var builder = new StringBuilder();
        builder.AppendLine("@file:Suppress(\"unused\", \"UNUSED_PARAMETER\", \"FunctionName\", \"PropertyName\", \"ClassName\", \"RedundantVisibilityModifier\")");
        if (_fragment.PackageName.Length != 0)
        {
            builder.Append("package ").AppendLine(KotlinNames.Package(_fragment.PackageName));
            builder.AppendLine();
        }
        builder.AppendLine("// Generated from DotKt frontend KLIB metadata for JetBrains Kotlin LSP analysis only.");
        builder.AppendLine();

        foreach (var property in properties)
        {
            RenderProperty(builder, property, 0, new Dictionary<int, string>(), inInterface: false);
            builder.AppendLine();
        }
        foreach (var function in functions)
        {
            RenderFunction(builder, function, 0, new Dictionary<int, string>(), inInterface: false);
            builder.AppendLine();
        }
        foreach (var type in roots)
        {
            RenderClass(builder, type, 0);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private void RenderClass(StringBuilder builder, KlibType type, int indent)
    {
        ClassCount++;
        var className = KotlinNames.Identifier(type.SimpleName);
        var typeParameters = TypeParameterNames(type.TypeParameters);
        var header = type.Kind switch
        {
            KlibTypeKind.Interface => "interface ",
            KlibTypeKind.EnumClass => "enum class ",
            KlibTypeKind.AnnotationClass => "annotation class ",
            KlibTypeKind.Object => "object ",
            KlibTypeKind.CompanionObject => "companion object ",
            _ => type.Modality switch
            {
                KlibModality.Open => "open class ",
                KlibModality.Abstract => "abstract class ",
                KlibModality.Sealed => "sealed class ",
                _ => "class ",
            },
        };
        var isObject = type.Kind is KlibTypeKind.Object or KlibTypeKind.CompanionObject;

        Indent(builder, indent).Append(header).Append(className);
        if (!isObject) AppendTypeParameters(builder, type.TypeParameters, typeParameters, allowVariance: true);
        if (HasConstructors(type) && type.Constructors.Count == 0)
            builder.Append(" private constructor()");

        if (type.Kind is KlibTypeKind.Class or KlibTypeKind.Interface or KlibTypeKind.EnumEntry)
        {
            var supertypes = type.Supertypes
                .Select(supertype => RenderType(supertype, typeParameters))
                .Where(supertype => supertype is not ("kotlin.Any" or "kotlin.Any?"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (supertypes.Length != 0) builder.Append(" : ").Append(string.Join(", ", supertypes));
        }
        builder.AppendLine(" {");

        if (type.Kind == KlibTypeKind.EnumClass && type.EnumEntries.Count != 0)
        {
            Indent(builder, indent + 1)
                .Append(string.Join(", ", type.EnumEntries.Select(KotlinNames.Identifier)))
                .AppendLine(";");
            builder.AppendLine();
        }

        if (HasConstructors(type))
        {
            foreach (var constructor in type.Constructors)
            {
                Indent(builder, indent + 1).Append("constructor(");
                AppendParameters(builder, constructor.Parameters, typeParameters);
                builder.AppendLine(")");
                CallableCount++;
            }
            if (type.Constructors.Count != 0) builder.AppendLine();
        }

        var inInterface = type.Kind == KlibTypeKind.Interface;
        foreach (var property in type.Properties.Where(property => !property.IsStatic))
        {
            RenderProperty(builder, property, indent + 1, typeParameters, inInterface);
            builder.AppendLine();
        }
        foreach (var function in type.Functions.Where(function => !function.IsStatic))
        {
            RenderFunction(builder, function, indent + 1, typeParameters, inInterface);
            builder.AppendLine();
        }

        var staticProperties = type.Properties.Where(property => property.IsStatic).ToArray();
        var staticFunctions = type.Functions.Where(function => function.IsStatic).ToArray();
        var hasDeclaredCompanion = type.NestedTypes.Any(nested => nested.Kind == KlibTypeKind.CompanionObject);
        if (!hasDeclaredCompanion && (staticProperties.Length != 0 || staticFunctions.Length != 0))
        {
            Indent(builder, indent + 1).AppendLine("companion object {");
            foreach (var property in staticProperties)
            {
                RenderProperty(builder, property, indent + 2, typeParameters, inInterface: false);
                builder.AppendLine();
            }
            foreach (var function in staticFunctions)
            {
                RenderFunction(builder, function, indent + 2, typeParameters, inInterface: false);
                builder.AppendLine();
            }
            Indent(builder, indent + 1).AppendLine("}");
            builder.AppendLine();
        }

        foreach (var nested in type.NestedTypes)
        {
            RenderClass(builder, nested, indent + 1);
            builder.AppendLine();
        }

        Indent(builder, indent).AppendLine("}");
    }

    /// <summary>Only these Kotlin class kinds may spell a constructor list in source.</summary>
    private static bool HasConstructors(KlibType type) =>
        type.Kind is KlibTypeKind.Class or KlibTypeKind.EnumEntry;

    private void RenderFunction(
        StringBuilder builder,
        KlibFunction function,
        int indent,
        IReadOnlyDictionary<int, string> ownerTypeParameters,
        bool inInterface)
    {
        CallableCount++;
        var parameters = new Dictionary<int, string>(ownerTypeParameters);
        foreach (var (id, name) in TypeParameterNames(function.TypeParameters)) parameters[id] = name;

        Indent(builder, indent);
        if (function.IsSuspend) builder.Append("suspend ");
        if (function.IsOperator) builder.Append("operator ");
        if (function.IsInfix) builder.Append("infix ");
        if (!inInterface && function.Modality == KlibModality.Abstract) builder.Append("abstract ");
        else if (!inInterface && function.Modality == KlibModality.Open) builder.Append("open ");
        builder.Append("fun ");
        AppendTypeParameters(builder, function.TypeParameters, parameters, allowVariance: false);
        if (function.TypeParameters.Count != 0) builder.Append(' ');
        if (function.ReceiverType is not null)
            builder.Append(RenderType(function.ReceiverType, parameters)).Append('.');
        builder.Append(KotlinNames.Identifier(function.Name)).Append('(');
        AppendParameters(builder, function.Parameters, parameters);
        builder.Append("): ").Append(RenderType(function.ReturnType, parameters));
        if (inInterface || function.Modality == KlibModality.Abstract) builder.AppendLine();
        else builder.AppendLine(" = throw kotlin.UnsupportedOperationException(\"DotKt LSP stub\")");
    }

    private void RenderProperty(
        StringBuilder builder,
        KlibProperty property,
        int indent,
        IReadOnlyDictionary<int, string> ownerTypeParameters,
        bool inInterface)
    {
        CallableCount++;
        Indent(builder, indent);
        if (!inInterface && property.Modality == KlibModality.Abstract) builder.Append("abstract ");
        else if (!inInterface && property.Modality == KlibModality.Open) builder.Append("open ");
        builder.Append(property.IsVar ? "var " : "val ");
        if (property.ReceiverType is not null)
            builder.Append(RenderType(property.ReceiverType, ownerTypeParameters)).Append('.');
        builder.Append(KotlinNames.Identifier(property.Name))
            .Append(": ")
            .Append(RenderType(property.ReturnType, ownerTypeParameters));
        if (inInterface || property.Modality == KlibModality.Abstract)
        {
            builder.AppendLine();
            return;
        }

        builder.AppendLine();
        Indent(builder, indent + 1).AppendLine("get() = throw kotlin.UnsupportedOperationException(\"DotKt LSP stub\")");
        if (property.IsVar)
            Indent(builder, indent + 1).AppendLine("set(value) { throw kotlin.UnsupportedOperationException(\"DotKt LSP stub\") }");
    }

    private void AppendParameters(
        StringBuilder builder,
        IEnumerable<KlibValueParameter> values,
        IReadOnlyDictionary<int, string> typeParameters)
    {
        var rendered = values.Select(value =>
        {
            var type = value.IsVararg ? value.VarargElementType : value.Type;
            var result = (value.IsVararg ? "vararg " : "") +
                KotlinNames.Identifier(value.Name) + ": " +
                RenderType(type, typeParameters);
            if (value.HasDefaultValue) result += " = throw kotlin.UnsupportedOperationException(\"DotKt LSP stub\")";
            return result;
        });
        builder.Append(string.Join(", ", rendered));
    }

    private void AppendTypeParameters(
        StringBuilder builder,
        IEnumerable<KlibTypeParameter> values,
        IReadOnlyDictionary<int, string> names,
        bool allowVariance)
    {
        var rendered = values.Select(parameter =>
        {
            var variance = allowVariance ? parameter.Variance switch
            {
                KlibVariance.In => "in ",
                KlibVariance.Out => "out ",
                _ => "",
            } : "";
            var name = names.GetValueOrDefault(parameter.Id) ?? "T" + parameter.Id;
            var upperBound = parameter.UpperBounds
                .Select(bound => RenderType(bound, names))
                .FirstOrDefault(bound => bound is not ("kotlin.Any" or "kotlin.Any?"));
            return variance + KotlinNames.Identifier(name) + (upperBound is null ? "" : " : " + upperBound);
        }).ToArray();
        if (rendered.Length != 0) builder.Append('<').Append(string.Join(", ", rendered)).Append('>');
    }

    private static Dictionary<int, string> TypeParameterNames(IEnumerable<KlibTypeParameter> values) =>
        values.ToDictionary(parameter => parameter.Id, parameter => parameter.Name);

    private string RenderType(KlibTypeRef? type, IReadOnlyDictionary<int, string> typeParameters)
    {
        if (type is null) return "kotlin.Any?";
        string result;
        if (type.ClassName is { } className)
            result = className;
        else if (type.TypeParameterId is { } typeParameterId)
            result = KotlinNames.Identifier(typeParameters.GetValueOrDefault(typeParameterId) ?? "T" + typeParameterId);
        else
            return "kotlin.Any?";

        if (type.Arguments.Count != 0)
        {
            var arguments = type.Arguments.Select(argument => argument.Projection switch
            {
                KlibProjection.Star => "*",
                KlibProjection.In => "in " + RenderType(argument.Type, typeParameters),
                KlibProjection.Out => "out " + RenderType(argument.Type, typeParameters),
                _ => RenderType(argument.Type, typeParameters),
            });
            result += "<" + string.Join(", ", arguments) + ">";
        }
        // A flexible CLR type cannot be written in Kotlin source. Its non-null lower bound gives completion the
        // least intrusive approximation; authoritative nullability remains the real DotKt compilation's job.
        if (type.IsNullable) result += "?";
        return result;
    }

    private static StringBuilder Indent(StringBuilder builder, int level) => builder.Append(' ', level * 4);
}

internal static class KotlinNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) {
        "as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in", "interface",
        "is", "null", "object", "package", "return", "super", "this", "throw", "true", "try", "typealias",
        "typeof", "val", "var", "when", "while", "by", "catch", "constructor", "delegate", "dynamic", "field",
        "file", "finally", "get", "import", "init", "param", "property", "receiver", "set", "setparam", "where",
        "actual", "abstract", "annotation", "companion", "const", "crossinline", "data", "enum", "expect", "external",
        "final", "infix", "inline", "inner", "internal", "lateinit", "noinline", "open", "operator", "out", "override",
        "private", "protected", "public", "reified", "sealed", "suspend", "tailrec", "vararg", "value",
    };

    public static string Package(string packageName) =>
        string.Join('.', packageName.Split('.').Select(Identifier));

    public static string Identifier(string value)
    {
        value = value.Replace('`', '_');
        if (value.Length != 0 && !Keywords.Contains(value) &&
            (char.IsLetter(value[0]) || value[0] == '_') &&
            value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_'))
            return value;
        return "`" + value + "`";
    }
}
