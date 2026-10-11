using System.Diagnostics;
using System.Text;

namespace DotKtx.Experimental.LspSupport;

/// <summary>
/// Records what the projection kept, approximated and dropped. Silence is the dangerous failure mode
/// here: a member that vanishes or a descriptor that resolves nowhere still produces a JAR that looks
/// healthy in the editor, so every lossy decision has to leave a counted trace.
/// </summary>
internal sealed class ProjectionReport
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];
    private readonly SortedDictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, SortedSet<string>> _samples = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, SortedDictionary<string, int>> _tallies = new(StringComparer.Ordinal);
    private readonly List<(string Phase, TimeSpan Elapsed)> _timings = [];
    private TimeSpan _lastPhaseEnd = TimeSpan.Zero;

    public IReadOnlyList<string> Errors => _errors;

    public void CompletePhase(string phase)
    {
        var now = _clock.Elapsed;
        _timings.Add((phase, now - _lastPhaseEnd));
        _lastPhaseEnd = now;
    }

    public void Count(string key, int amount = 1) =>
        _counters[key] = _counters.GetValueOrDefault(key) + amount;

    /// <summary>Counts an event and keeps a bounded, deterministic sample so the report stays readable.</summary>
    public void Sample(string key, string value, int limit = 20)
    {
        Count(key);
        var bucket = _samples.TryGetValue(key, out var existing) ? existing : _samples[key] = new(StringComparer.Ordinal);
        if (bucket.Count < limit) bucket.Add(value);
    }

    public void PolicyMiss(string kind, string value, bool isError)
    {
        var message = $"Java projection policy {kind} '{value}' matched no declaration in the referenced KLIBs";
        if (isError) _errors.Add(message);
        else _warnings.Add(message);
    }

    /// <summary>
    /// Counts one occurrence per distinct value. Unlike <see cref="Sample"/> this keeps the full
    /// distribution, which is what turns "1587 unresolved supertypes" into an actionable list.
    /// </summary>
    public void Tally(string key, string value)
    {
        Count(key);
        var bucket = _tallies.TryGetValue(key, out var existing)
            ? existing
            : _tallies[key] = new SortedDictionary<string, int>(StringComparer.Ordinal);
        bucket[value] = bucket.GetValueOrDefault(value) + 1;
    }

    public void UnknownSupertype(string owner, string supertype)
    {
        Tally("supertype.unresolved", supertype);
        Sample("supertype.unresolved.owners", $"{owner} : {supertype}");
    }

    /// <summary>
    /// A reference dll2klib wrote as a bare nested name with no parent link, where the surrounding
    /// declaration names no candidate owner either. The KLIB does not carry enough to place it, so the
    /// descriptor points at a name that exists nowhere - an upstream gap, not an approximation choice.
    /// </summary>
    public void UnresolvedReference(string owner, string reference)
    {
        Tally("reference.unresolved", reference);
        Sample("reference.unresolved.owners", $"{owner} -> {reference}");
        _upstreamGaps.Add(
            "dll2klib recorded a nested type reference without its parent, and nothing in the " +
            "declaration names a candidate owner");
    }

    private readonly SortedSet<string> _upstreamGaps = new(StringComparer.Ordinal);

    public void UpstreamGap(string description) => _upstreamGaps.Add(description);

    private readonly SortedSet<string> _representationLimits = new(StringComparer.Ordinal);

    /// <summary>
    /// A loss that no representation can avoid because Kotlin itself has no shape for the CLR construct.
    /// Distinct from an upstream gap: changing dll2klib would not help.
    /// </summary>
    public void RepresentationLimit(string description) => _representationLimits.Add(description);

    public void DroppedMember(string reason, string owner, string member) =>
        Sample("member.dropped." + reason, $"{owner}.{member}");

    public string Render(string projectName, ProjectionPolicy policy)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DotKtx.Experimental.LspSupport projection report");
        builder.AppendLine();
        builder.Append("project: ").AppendLine(projectName);
        builder.Append("java projection policy: ").AppendLine(policy.Describe());
        builder.Append("supertype closure: ").AppendLine(policy.CloseOverSupertypes ? "on" : "off");
        builder.AppendLine();

        builder.AppendLine("## timings");
        builder.AppendLine();
        foreach (var (phase, elapsed) in _timings)
            builder.Append("- ").Append(phase).Append(": ").Append(elapsed.TotalMilliseconds.ToString("F1")).AppendLine(" ms");
        builder.Append("- total: ").Append(_lastPhaseEnd.TotalMilliseconds.ToString("F1")).AppendLine(" ms");
        builder.AppendLine();

        builder.AppendLine("## declaration accounting");
        builder.AppendLine();
        builder.AppendLine("What the KLIBs declared against what reached a classfile. A negative delta is");
        builder.AppendLine("a declaration the projection could not carry; a positive one is something it added.");
        builder.AppendLine();
        builder.AppendLine("| declaration | KLIB | JAR | delta |");
        builder.AppendLine("|---|---:|---:|---:|");
        foreach (var (label, inputKey, outputKeys) in new[]
        {
            ("class", "input.class", new[] { "output.class" }),
            ("function", "input.function", ["output.function"]),
            ("property", "input.property", ["output.property"]),
            ("constructor", "input.constructor", ["output.constructor"]),
            ("enum entry", "input.enum-entry", ["output.enum-entry"]),
            ("top-level function", "input.top-level-function", ["output.top-level-function"]),
            ("top-level property", "input.top-level-property", ["output.top-level-property"]),
        })
        {
            var input = _counters.GetValueOrDefault(inputKey);
            var output = outputKeys.Sum(key => _counters.GetValueOrDefault(key));
            builder.Append("| ").Append(label)
                .Append(" | ").Append(input)
                .Append(" | ").Append(output)
                .Append(" | ").Append((output - input).ToString("+#;-#;0"))
                .AppendLine(" |");
        }
        builder.AppendLine();

        builder.AppendLine("## counters");
        builder.AppendLine();
        foreach (var (key, value) in _counters)
            builder.Append("- ").Append(key).Append(": ").Append(value).AppendLine();
        builder.AppendLine();

        if (_tallies.Count != 0)
        {
            builder.AppendLine("## distributions");
            builder.AppendLine();
            foreach (var (key, values) in _tallies)
            {
                builder.Append("### ").AppendLine(key);
                builder.AppendLine();
                foreach (var (value, count) in values
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(25))
                    builder.Append("- ").Append(count).Append("  ").AppendLine(value);
                if (values.Count > 25)
                    builder.Append("- (").Append(values.Count - 25).AppendLine(" more distinct values)");
                builder.AppendLine();
            }
        }

        if (_samples.Count != 0)
        {
            builder.AppendLine("## samples");
            builder.AppendLine();
            foreach (var (key, values) in _samples)
            {
                builder.Append("### ").AppendLine(key);
                builder.AppendLine();
                foreach (var value in values) builder.Append("- ").AppendLine(value);
                builder.AppendLine();
            }
        }

        if (_upstreamGaps.Count != 0)
        {
            builder.AppendLine("## upstream gaps");
            builder.AppendLine();
            builder.AppendLine("Losses this projection cannot repair because the information is absent from the");
            builder.AppendLine("KLIB itself. Fixing them means changing what dll2klib writes.");
            builder.AppendLine();
            foreach (var gap in _upstreamGaps) builder.Append("- ").AppendLine(gap);
            builder.AppendLine();
        }

        if (_representationLimits.Count != 0)
        {
            builder.AppendLine("## representation limits");
            builder.AppendLine();
            builder.AppendLine("Losses inherent to expressing a CLR construct in Kotlin. No change upstream or here");
            builder.AppendLine("removes them.");
            builder.AppendLine();
            foreach (var limit in _representationLimits) builder.Append("- ").AppendLine(limit);
            builder.AppendLine();
        }

        if (_warnings.Count != 0)
        {
            builder.AppendLine("## warnings");
            builder.AppendLine();
            foreach (var warning in _warnings) builder.Append("- ").AppendLine(warning);
            builder.AppendLine();
        }

        if (_errors.Count != 0)
        {
            builder.AppendLine("## errors");
            builder.AppendLine();
            foreach (var error in _errors) builder.Append("- ").AppendLine(error);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
