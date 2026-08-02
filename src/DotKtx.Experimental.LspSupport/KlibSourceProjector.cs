using System.IO.Compression;
using System.Text;
using DotKt.Klib.Metadata;
using KlibClass = DotKt.Klib.Metadata.Class;
using KlibFunction = DotKt.Klib.Metadata.Function;
using KlibProperty = DotKt.Klib.Metadata.Property;
using KlibType = DotKt.Klib.Metadata.Type;

namespace DotKtx.Experimental.LspSupport;

internal sealed record ProjectionResult(
    IReadOnlyDictionary<string, string> Files,
    int ClassCount,
    int CallableCount);

internal static class KlibSourceProjector
{
    public static ProjectionResult Project(IEnumerable<string> klibPaths)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var knownClasses = new HashSet<string>(StringComparer.Ordinal);
        var classCount = 0;
        var callableCount = 0;
        var libraryIndex = 0;

        foreach (var klibPath in klibPaths.Where(File.Exists).OrderBy(path => path, StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(klibPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var libraryName = SafePath(Path.GetFileNameWithoutExtension(klibPath));
            var fragmentIndex = 0;
            foreach (var entry in archive.Entries
                .Where(entry => entry.FullName.Contains("/linkdata/", StringComparison.Ordinal) &&
                                entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                using var input = entry.Open();
                var fragment = PackageFragment.Parser.ParseFrom(input);
                if (fragment.IsEmpty) continue;
                var names = new NameResolver(fragment.Strings, fragment.QualifiedNames);
                var packageName = fragment.HasFqName
                    ? fragment.FqName
                    : names.PackageName(fragment.Package?.PackageFqName ?? -1);
                var renderer = new FragmentRenderer(packageName, fragment, names, knownClasses);
                var rendered = renderer.Render();
                if (rendered is null) continue;

                var packagePath = packageName.Length == 0
                    ? "root"
                    : string.Join('/', packageName.Split('.').Select(SafePath));
                var relative = $"{libraryIndex:D3}_{libraryName}/{packagePath}/{fragmentIndex:D3}_Declarations.kt";
                files[relative] = rendered;
                classCount += renderer.ClassCount;
                callableCount += renderer.CallableCount;
                fragmentIndex++;
            }
            libraryIndex++;
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
    private const int ClassKindShift = 6;
    private const int ClassModalityShift = 4;
    private readonly string _packageName;
    private readonly PackageFragment _fragment;
    private readonly NameResolver _names;
    private readonly HashSet<string> _knownClasses;
    private readonly Dictionary<int, KlibClass> _classesByName;
    private readonly Dictionary<int, List<KlibClass>> _childrenByName;

    public int ClassCount { get; private set; }
    public int CallableCount { get; private set; }

    public FragmentRenderer(
        string packageName,
        PackageFragment fragment,
        NameResolver names,
        HashSet<string> knownClasses)
    {
        _packageName = packageName;
        _fragment = fragment;
        _names = names;
        _knownClasses = knownClasses;
        // KLIB fragments may repeat one class name for metadata parts. The first declaration is the
        // canonical shape for an analysis stub; indexing must not assume the table is unique.
        _classesByName = fragment.Class
            .GroupBy(declaration => declaration.FqName)
            .ToDictionary(group => group.Key, group => group.First());
        _childrenByName = fragment.Class
            .Where(declaration => _classesByName.ContainsKey(_names.Parent(declaration.FqName)))
            .GroupBy(declaration => _names.Parent(declaration.FqName))
            .ToDictionary(group => group.Key, group => group.OrderBy(x => _names.ClassName(x.FqName), StringComparer.Ordinal).ToList());
    }

    public string? Render()
    {
        var roots = _fragment.Class
            .Where(declaration => !_classesByName.ContainsKey(_names.Parent(declaration.FqName)))
            .Where(declaration => _knownClasses.Add(_names.ClassName(declaration.FqName)))
            .OrderBy(declaration => _names.ClassName(declaration.FqName), StringComparer.Ordinal)
            .ToArray();
        IEnumerable<KlibFunction> topLevelFunctions = _fragment.Package?.Function ?? Enumerable.Empty<KlibFunction>();
        IEnumerable<KlibProperty> topLevelProperties = _fragment.Package?.Property ?? Enumerable.Empty<KlibProperty>();
        var topLevelFunctionArray = topLevelFunctions.ToArray();
        var topLevelPropertyArray = topLevelProperties.ToArray();
        if (roots.Length == 0 && topLevelFunctionArray.Length == 0 && topLevelPropertyArray.Length == 0) return null;

        var builder = new StringBuilder();
        builder.AppendLine("@file:Suppress(\"unused\", \"UNUSED_PARAMETER\", \"FunctionName\", \"PropertyName\", \"ClassName\", \"RedundantVisibilityModifier\")");
        if (_packageName.Length != 0)
        {
            builder.Append("package ").AppendLine(KotlinNames.Package(_packageName));
            builder.AppendLine();
        }
        builder.AppendLine("// Generated from DotKt frontend KLIB metadata for JetBrains Kotlin LSP analysis only.");
        builder.AppendLine();

        foreach (var property in topLevelPropertyArray)
        {
            RenderProperty(builder, property, 0, new Dictionary<int, string>(), inInterface: false);
            builder.AppendLine();
        }
        foreach (var function in topLevelFunctionArray)
        {
            RenderFunction(builder, function, 0, new Dictionary<int, string>(), inInterface: false);
            builder.AppendLine();
        }
        foreach (var declaration in roots)
        {
            RenderClass(builder, declaration, 0);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private void RenderClass(StringBuilder builder, KlibClass declaration, int indent)
    {
        ClassCount++;
        var kind = (declaration.Flags >> ClassKindShift) & 0x7;
        var modality = (declaration.Flags >> ClassModalityShift) & 0x3;
        var className = KotlinNames.Identifier(_names.SimpleClassName(declaration.FqName));
        var typeParameters = TypeParameterNames(declaration.TypeParameter);
        var header = kind switch
        {
            1 => "interface ",
            2 => "enum class ",
            4 => "annotation class ",
            5 => "object ",
            6 => "companion object ",
            _ => modality switch
            {
                1 => "open class ",
                2 => "abstract class ",
                3 => "sealed class ",
                _ => "class ",
            },
        };

        Indent(builder, indent).Append(header).Append(className);
        if (kind is not (5 or 6)) AppendTypeParameters(builder, declaration.TypeParameter, typeParameters, allowVariance: true);
        if (kind is 0 or 3 && declaration.Constructor.Count == 0)
            builder.Append(" private constructor()");

        if (kind is 0 or 1 or 3)
        {
            var supertypes = declaration.Supertype
                .Select(type => RenderType(type, typeParameters))
                .Where(type => type is not ("kotlin.Any" or "kotlin.Any?"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (supertypes.Length != 0) builder.Append(" : ").Append(string.Join(", ", supertypes));
        }
        builder.AppendLine(" {");

        if (kind == 2 && declaration.EnumEntry.Count != 0)
        {
            Indent(builder, indent + 1)
                .Append(string.Join(", ", declaration.EnumEntry.Select(entry => KotlinNames.Identifier(_names.String(entry.Name)))))
                .AppendLine(";");
            builder.AppendLine();
        }

        if (kind is 0 or 3)
        {
            foreach (var constructor in declaration.Constructor)
            {
                Indent(builder, indent + 1).Append("constructor(");
                AppendParameters(builder, constructor.ValueParameter, typeParameters);
                builder.AppendLine(")");
                CallableCount++;
            }
            if (declaration.Constructor.Count != 0) builder.AppendLine();
        }

        var instanceProperties = declaration.Property.Where(property => !IsStaticProperty(property)).ToArray();
        var staticProperties = declaration.Property.Where(IsStaticProperty).ToArray();
        var instanceFunctions = declaration.Function.Where(function => !IsStaticFunction(function)).ToArray();
        var staticFunctions = declaration.Function.Where(IsStaticFunction).ToArray();
        var inInterface = kind == 1;

        foreach (var property in instanceProperties)
        {
            RenderProperty(builder, property, indent + 1, typeParameters, inInterface);
            builder.AppendLine();
        }
        foreach (var function in instanceFunctions)
        {
            RenderFunction(builder, function, indent + 1, typeParameters, inInterface);
            builder.AppendLine();
        }

        var hasDeclaredCompanion = _childrenByName.TryGetValue(declaration.FqName, out var declaredChildren) &&
            declaredChildren.Any(child => ClassKind(child) == 6);
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

        if (_childrenByName.TryGetValue(declaration.FqName, out var children))
            foreach (var child in children)
            {
                RenderClass(builder, child, indent + 1);
                builder.AppendLine();
            }

        Indent(builder, indent).AppendLine("}");
    }

    private void RenderFunction(
        StringBuilder builder,
        KlibFunction function,
        int indent,
        IReadOnlyDictionary<int, string> ownerTypeParameters,
        bool inInterface)
    {
        CallableCount++;
        var modality = (function.Flags >> 4) & 0x3;
        var parameters = new Dictionary<int, string>(ownerTypeParameters);
        foreach (var (id, name) in TypeParameterNames(function.TypeParameter)) parameters[id] = name;

        Indent(builder, indent);
        if ((function.Flags & (1 << 13)) != 0) builder.Append("suspend ");
        if ((function.Flags & (1 << 8)) != 0) builder.Append("operator ");
        if ((function.Flags & (1 << 9)) != 0) builder.Append("infix ");
        if (!inInterface && modality == 2) builder.Append("abstract ");
        else if (!inInterface && modality == 1) builder.Append("open ");
        builder.Append("fun ");
        AppendTypeParameters(builder, function.TypeParameter, parameters, allowVariance: false);
        if (function.TypeParameter.Count != 0) builder.Append(' ');
        if (function.ReceiverType is not null)
            builder.Append(RenderType(function.ReceiverType, parameters)).Append('.');
        builder.Append(KotlinNames.Identifier(_names.String(function.Name))).Append('(');
        AppendParameters(builder, function.ValueParameter, parameters);
        builder.Append("): ").Append(RenderType(function.ReturnType, parameters));
        if (inInterface || modality == 2) builder.AppendLine();
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
        var mutable = (property.Flags & (1 << 8)) != 0 || property.SetterValueParameter is not null;
        var modality = (property.Flags >> 4) & 0x3;
        Indent(builder, indent);
        if (!inInterface && modality == 2) builder.Append("abstract ");
        else if (!inInterface && modality == 1) builder.Append("open ");
        builder.Append(mutable ? "var " : "val ");
        if (property.ReceiverType is not null)
            builder.Append(RenderType(property.ReceiverType, ownerTypeParameters)).Append('.');
        builder.Append(KotlinNames.Identifier(_names.String(property.Name)))
            .Append(": ")
            .Append(RenderType(property.ReturnType, ownerTypeParameters));
        if (inInterface || modality == 2)
        {
            builder.AppendLine();
            return;
        }

        builder.AppendLine();
        Indent(builder, indent + 1).AppendLine("get() = throw kotlin.UnsupportedOperationException(\"DotKt LSP stub\")");
        if (mutable)
            Indent(builder, indent + 1).AppendLine("set(value) { throw kotlin.UnsupportedOperationException(\"DotKt LSP stub\") }");
    }

    private void AppendParameters(
        StringBuilder builder,
        IEnumerable<ValueParameter> values,
        IReadOnlyDictionary<int, string> typeParameters)
    {
        var rendered = values.Select(value =>
        {
            var vararg = value.VarargElementType is not null;
            var type = vararg ? value.VarargElementType : value.Type;
            var result = (vararg ? "vararg " : "") +
                KotlinNames.Identifier(_names.String(value.Name)) + ": " +
                RenderType(type, typeParameters);
            if ((value.Flags & (1 << 1)) != 0) result += " = throw kotlin.UnsupportedOperationException(\"DotKt LSP stub\")";
            return result;
        });
        builder.Append(string.Join(", ", rendered));
    }

    private void AppendTypeParameters(
        StringBuilder builder,
        IEnumerable<TypeParameter> values,
        IReadOnlyDictionary<int, string> names,
        bool allowVariance)
    {
        var rendered = values.Select(parameter =>
        {
            var variance = allowVariance ? parameter.Variance switch
            {
                TypeParameter.Types.Variance.In => "in ",
                TypeParameter.Types.Variance.Out => "out ",
                _ => "",
            } : "";
            var name = names.GetValueOrDefault(parameter.Id) ?? "T" + parameter.Id;
            var upperBound = parameter.UpperBound
                .Select(type => RenderType(type, names))
                .FirstOrDefault(type => type is not ("kotlin.Any" or "kotlin.Any?"));
            return variance + KotlinNames.Identifier(name) + (upperBound is null ? "" : " : " + upperBound);
        }).ToArray();
        if (rendered.Length != 0) builder.Append('<').Append(string.Join(", ", rendered)).Append('>');
    }

    private Dictionary<int, string> TypeParameterNames(IEnumerable<TypeParameter> values) =>
        values.ToDictionary(parameter => parameter.Id, parameter => _names.String(parameter.Name));

    private string RenderType(KlibType? type, IReadOnlyDictionary<int, string> typeParameters)
    {
        if (type is null) return "kotlin.Any?";
        string result;
        if (type.HasClassName)
            result = _names.ClassName(type.ClassName);
        else if (type.HasTypeParameter)
            result = KotlinNames.Identifier(typeParameters.GetValueOrDefault(type.TypeParameter) ?? "T" + type.TypeParameter);
        else
            return "kotlin.Any?";

        if (type.Argument.Count != 0)
        {
            var arguments = type.Argument.Select(argument => argument.Projection switch
            {
                KlibType.Types.Argument.Types.Projection.Star => "*",
                KlibType.Types.Argument.Types.Projection.In => "in " + RenderType(argument.Type, typeParameters),
                KlibType.Types.Argument.Types.Projection.Out => "out " + RenderType(argument.Type, typeParameters),
                _ => RenderType(argument.Type, typeParameters),
            });
            result += "<" + string.Join(", ", arguments) + ">";
        }
        // A flexible CLR type cannot be written in Kotlin source. Its non-null lower bound gives completion the
        // least intrusive approximation; authoritative nullability remains the real DotKt compilation's job.
        if (type.Nullable) result += "?";
        return result;
    }

    private static bool IsStaticFunction(KlibFunction function) => (function.Flags & (1 << 18)) != 0;
    private static bool IsStaticProperty(KlibProperty property) => (property.Flags & (1 << 19)) != 0;
    private static int ClassKind(KlibClass declaration) => (declaration.Flags >> ClassKindShift) & 0x7;
    private static StringBuilder Indent(StringBuilder builder, int level) => builder.Append(' ', level * 4);
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
