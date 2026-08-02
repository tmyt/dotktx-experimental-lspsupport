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
        var klibPaths = ReadResponseFile(options.Required("klibs"));
        var stdlib = options.Optional("stdlib");
        var overlay = options.Optional("overlay");
        var projectName = options.Optional("name") ?? Path.GetFileName(project);

        var projection = KlibSourceProjector.Project(klibPaths);
        WorkspaceWriter.Generate(
            Path.GetFullPath(project),
            projectName,
            Path.GetFullPath(output),
            sourcePaths,
            stdlib,
            overlay,
            projection);

        Console.WriteLine(
            $"DotKtx.Experimental.LspSupport: projected {projection.ClassCount} class(es), " +
            $"{projection.CallableCount} callable(s) into {projection.Files.Count} source file(s)");
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
            "  DotKtx.Experimental.LspSupport generate --project <dir> --name <name> --output <dir> --sources <rsp> --klibs <rsp> [--stdlib <jar>] [--overlay <jar>]\n" +
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
