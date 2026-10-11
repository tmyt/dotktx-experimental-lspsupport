using System.Text.Json;

namespace DotKtx.Experimental.LspSupport;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            var command = args[0];
            var options = Arguments.Parse(args[1..]);
            return command switch
            {
                "generate" => Generate(options),
                "clean" => Clean(options),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DotKtx.Experimental.LspSupport: {ex.Message}");
            return 1;
        }
    }

    private static int Generate(Arguments options)
    {
        var project = options.Required("project");
        var output = options.Required("output");
        var sourcePaths = ReadResponseFile(options.Required("sources"));
        var klibPaths = ReadResponseFile(options.Required("klibs")).ToList();
        var builtins = options.Optional("builtins");
        // DotKt's own CLR standard library, published by the SDK as the StandardLibrary frontend KLIB.
        // Projecting it is what keeps JVM-only stdlib APIs out of completion.
        if (options.Optional("stdlib-klib") is { } stdlibKlib) klibPaths.Add(Path.GetFullPath(stdlibKlib));
        var projectName = options.Optional("name") ?? Path.GetFileName(project);
        var policy = ProjectionPolicy.Parse(options.Optional("java-projection"));
        var report = new ProjectionReport();

        // One pass over the KLIBs feeds both emitters, so a cross-library question such as "is this
        // supertype an interface" is answered by lookup instead of by guessing from what was selected.
        var builtinClassifiers = builtins is null
            ? null
            : KlibReader.ReadBuiltinClassifiers(Path.GetFullPath(builtins));
        report.Count("klib.builtin-classifiers", builtinClassifiers?.Count ?? 0);
        var world = KlibReader.Read(klibPaths, report, builtinClassifiers);
        report.Count("klib.libraries", world.Libraries.Count);
        report.Count("klib.types", world.TypesByFullName.Count);
        report.CompletePhase("klib read");

        var javaProjection = KlibJavaStubProjector.Project(world, policy, report);
        var projection = KlibSourceProjector.Project(
            world, javaProjection.ProjectedRoots, javaProjection.ProjectsTopLevelDeclarations);
        report.Count("source.classes", projection.ClassCount);
        report.Count("source.callables", projection.CallableCount);
        report.Count("source.files", projection.Files.Count);
        report.CompletePhase("source projection");

        WorkspaceWriter.Generate(
            Path.GetFullPath(project),
            projectName,
            Path.GetFullPath(output),
            sourcePaths,
            builtins,
            projection,
            javaProjection,
            report.Render(projectName, policy));
        report.CompletePhase("workspace write");

        if (report.Errors.Count != 0)
        {
            foreach (var error in report.Errors) Console.Error.WriteLine("DotKtx.Experimental.LspSupport: " + error);
            return 1;
        }

        Console.WriteLine(
            $"DotKtx.Experimental.LspSupport: projected {projection.ClassCount} class(es), " +
            $"{projection.CallableCount} callable(s) into {projection.Files.Count} source file(s), plus " +
            $"{javaProjection.ClassCount} Java class stub(s)");
        return 0;
    }

    private static int Clean(Arguments options)
    {
        WorkspaceWriter.Clean(
            Path.GetFullPath(options.Required("project")),
            Path.GetFullPath(options.Required("output")));
        return 0;
    }

    private static IReadOnlyList<string> ReadResponseFile(string path) =>
        File.Exists(path)
            ? File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length != 0)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : throw new FileNotFoundException("response file does not exist", path);

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage:\n" +
            "  DotKtx.Experimental.LspSupport generate --project <dir> --name <name> --output <dir> --sources <rsp> --klibs <rsp> [--stdlib-klib <klib>] [--builtins <jar>] [--java-projection <policy>]\n" +
            "    policy entries, ';' separated: type:<fqn> | package:<prefix> | library:<name> | all | no-supertype-closure | strict\n" +
            "  DotKtx.Experimental.LspSupport clean --project <dir> --output <dir>");
        return 2;
    }
}

internal sealed class Arguments
{
    private readonly Dictionary<string, string> _values;

    private Arguments(Dictionary<string, string> values) => _values = values;

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count || !args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"invalid argument near '{args[i]}'");
            values[args[i][2..]] = args[i + 1];
        }
        return new Arguments(values);
    }

    public string Required(string name) =>
        _values.TryGetValue(name, out var value) && value.Length != 0
            ? value
            : throw new ArgumentException($"missing --{name}");

    public string? Optional(string name) => _values.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
}
