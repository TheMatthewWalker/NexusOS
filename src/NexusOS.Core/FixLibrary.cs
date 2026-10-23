using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// Known problems as data (src/NexusOS.Core/fixes/*.json, embedded at build
// time). An entry matches probe output with regexes or structured field
// conditions; its fixes are either an allow-listed personal action defined
// below in code, or a system change described for a Guardian proposal.
// The library is validated at startup, so a bad entry stops Core.
public sealed record FixCondition(
    string Field,
    [property: JsonPropertyName("equals")] string? Is,
    string? NotEquals,
    string[]? In,
    double? AtLeast,
    double? AtMost,
    string? Matches);

public sealed record FixClause(string Probe, string? Regex, string? Status, FixCondition[]? Where);

public sealed record FixBinding(string Name, string Probe, FixCondition[] Where, string Field, string Pattern);

public sealed record FixTemplate(string Id, string Kind, string Label, string? Action, string? Proposal);

public sealed record FixEntry(string Id, string Area, int Priority, FixClause[] Match, FixBinding[]? Bind, string Cause, FixTemplate[] Fixes);

public sealed record FixLibraryFile(int SchemaVersion, FixEntry[] Entries);

public static class FixKinds
{
    public const string Personal = "personal", System = "system";
}

public sealed record FixEvidence(string Probe, string Excerpt);

// A personal fix whose argv has been fully resolved and validated.
public sealed record ResolvedFix(string Id, string Kind, string Label, string[]? Argv, string? Proposal);

public sealed record FixMatch(FixEntry Entry, string Cause, IReadOnlyList<FixEvidence> Evidence, IReadOnlyList<ResolvedFix> Fixes);

public sealed record PersonalAction(string Id, string[] Argv, IReadOnlyDictionary<string, Regex> Parameters)
{
    public IEnumerable<string> ParameterNames => Parameters.Keys;
}

// The complete set of commands a personal fix can run. Library data can only
// name one of these and supply validated parameter values from probe output.
public static class PersonalActions
{
    private static Regex Exact(string pattern) =>
        new("^(?:" + pattern + ")$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static readonly IReadOnlyDictionary<string, PersonalAction> All = new[]
    {
        new PersonalAction("audio.set-default-sink", ["wpctl", "set-default", "{sinkId}"],
            new Dictionary<string, Regex> { ["sinkId"] = Exact("[0-9]{1,6}") }),
        new PersonalAction("audio.unmute-default-sink", ["wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "0"],
            new Dictionary<string, Regex>()),
        new PersonalAction("audio.restart-pipewire", ["systemctl", "--user", "restart", "pipewire.service", "wireplumber.service"],
            new Dictionary<string, Regex>()),
        new PersonalAction("network.wifi-radio-on", ["nmcli", "radio", "wifi", "on"], new Dictionary<string, Regex>()),
        new PersonalAction("network.connect-device", ["nmcli", "device", "connect", "{device}"],
            new Dictionary<string, Regex> { ["device"] = Exact("[A-Za-z0-9][A-Za-z0-9._-]{0,14}") }),
        new PersonalAction("power.power-saver", ["powerprofilesctl", "set", "power-saver"], new Dictionary<string, Regex>()),
        new PersonalAction("app.restart-user-unit", ["systemctl", "--user", "restart", "{unit}"],
            new Dictionary<string, Regex> { ["unit"] = Exact(@"[A-Za-z0-9][A-Za-z0-9@_.:\-]{0,120}\.(service|socket|timer)") }),
    }.ToDictionary(x => x.Id, StringComparer.Ordinal);

    public static string[]? Resolve(PersonalAction action, IReadOnlyDictionary<string, string> values)
    {
        var argv = new string[action.Argv.Length];
        for (var i = 0; i < argv.Length; i++)
        {
            var token = action.Argv[i];
            if (token.StartsWith('{') && token.EndsWith('}'))
            {
                var name = token[1..^1];
                if (!values.TryGetValue(name, out var value) || !action.Parameters[name].IsMatch(value)) return null;
                argv[i] = value;
            }
            else argv[i] = token;
        }
        return argv;
    }
}

public sealed class FixLibrary
{
    public const int MaxExcerpt = 160;
    private static readonly Regex Placeholder = new(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, Regex> _regexes = new(StringComparer.Ordinal);

    public IReadOnlyList<FixEntry> Entries { get; }

    public FixLibrary() : this(LoadEmbedded()) { }

    public FixLibrary(IEnumerable<FixLibraryFile> files)
    {
        var entries = new List<FixEntry>();
        foreach (var file in files)
        {
            if (file.SchemaVersion != 1) throw new InvalidOperationException("Unsupported fix library schema version.");
            entries.AddRange(file.Entries ?? throw new InvalidOperationException("Fix library file has no entries."));
        }
        foreach (var entry in entries) Validate(entry);
        if (entries.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidOperationException("Duplicate fix library entry id.");
        Entries = entries.OrderBy(x => x.Priority).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static FixLibraryFile ParseFile(string json) =>
        JsonSerializer.Deserialize<FixLibraryFile>(json, Json) ?? throw new InvalidOperationException("Empty fix library file.");

    private static IEnumerable<FixLibraryFile> LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames().Where(x => x.StartsWith("fixes/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Embedded fix library missing.");
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            yield return ParseFile(reader.ReadToEnd());
        }
    }

    // Probes an entry needs, in addition to its area's own probes.
    public IEnumerable<string> ProbesFor(string area) =>
        Entries.Where(x => x.Area == area).SelectMany(x => x.Match.Select(c => c.Probe).Concat((x.Bind ?? []).Select(b => b.Probe)));

    public IReadOnlyList<FixMatch> Match(IReadOnlyDictionary<string, ProbeResult> results, string? area = null)
    {
        var matches = new List<FixMatch>();
        foreach (var entry in Entries.Where(x => area is null || x.Area == area))
        {
            var evidence = new List<FixEvidence>();
            if (!entry.Match.All(clause => results.TryGetValue(clause.Probe, out var result) && Holds(clause, result, evidence)))
                continue;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var bound = true;
            foreach (var binding in entry.Bind ?? [])
            {
                var value = results.TryGetValue(binding.Probe, out var result) && result.Ran
                    ? result.Records.Where(r => binding.Where.All(c => Test(c, r)))
                        .Select(r => r.Fields.GetValueOrDefault(binding.Field))
                        .FirstOrDefault(v => v is not null && Compiled(Anchor(binding.Pattern)).IsMatch(v))
                    : null;
                if (value is null) { bound = false; break; }
                values[binding.Name] = value;
            }
            if (!bound) continue;
            var fixes = new List<ResolvedFix>();
            foreach (var fix in entry.Fixes)
            {
                var id = entry.Id + "/" + fix.Id;
                if (fix.Kind == FixKinds.System)
                {
                    fixes.Add(new ResolvedFix(id, fix.Kind, Fill(fix.Label, values), null, Fill(fix.Proposal!, values)));
                    continue;
                }
                var argv = PersonalActions.Resolve(PersonalActions.All[fix.Action!], values);
                if (argv is not null) fixes.Add(new ResolvedFix(id, fix.Kind, Fill(fix.Label, values), argv, null));
            }
            matches.Add(new FixMatch(entry, Fill(entry.Cause, values), evidence, fixes));
        }
        return matches;
    }

    private bool Holds(FixClause clause, ProbeResult result, List<FixEvidence> evidence)
    {
        if (clause.Status is not null)
        {
            if (result.Status != clause.Status) return false;
            evidence.Add(new FixEvidence(result.Id, "Probe result: " + result.Status));
            return true;
        }
        if (!result.Ran) return false;
        if (clause.Regex is not null)
        {
            var match = Compiled(clause.Regex).Match(result.Output);
            if (!match.Success) return false;
            evidence.Add(new FixEvidence(result.Id, Excerpt(LineAround(result.Output, match.Index))));
            return true;
        }
        var record = result.Records.FirstOrDefault(r => clause.Where!.All(c => Test(c, r)));
        if (record is null) return false;
        evidence.Add(new FixEvidence(result.Id, Excerpt(record.Line)));
        return true;
    }

    private bool Test(FixCondition condition, ProbeRecord record)
    {
        if (!record.Fields.TryGetValue(condition.Field, out var value)) return false;
        if (condition.Is is not null) return string.Equals(value, condition.Is, StringComparison.Ordinal);
        if (condition.NotEquals is not null) return !string.Equals(value, condition.NotEquals, StringComparison.Ordinal);
        if (condition.In is not null) return condition.In.Contains(value, StringComparer.Ordinal);
        if (condition.Matches is not null) return Compiled(condition.Matches).IsMatch(value);
        var number = LeadingNumber(value);
        if (number is null) return false;
        return condition.AtLeast is { } min ? number >= min : number <= condition.AtMost!.Value;
    }

    // "95%", "8.0%", "123456" -> the leading decimal number.
    public static double? LeadingNumber(string value)
    {
        var text = value.Trim();
        var end = 0;
        while (end < text.Length && end < 32 && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;
        return end > 0 && double.TryParse(text[..end], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string LineAround(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        if (index == 0) start = 0;
        var end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)];
    }

    // Probe output is untrusted: excerpts carry no control characters and are short.
    public static string Excerpt(string text)
    {
        var builder = new StringBuilder();
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) { space = builder.Length > 0; continue; }
            if (char.GetUnicodeCategory(ch) is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse) continue;
            if (space) { builder.Append(' '); space = false; }
            builder.Append(ch);
            if (builder.Length >= MaxExcerpt) break;
        }
        return builder.ToString();
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    private static string Anchor(string pattern) => "^(?:" + pattern + ")$";

    private Regex Compiled(string pattern)
    {
        lock (_regexes)
        {
            if (!_regexes.TryGetValue(pattern, out var regex))
                _regexes[pattern] = regex = new Regex(pattern,
                    RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.NonBacktracking);
            return regex;
        }
    }

    private void Validate(FixEntry entry)
    {
        void Require(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException($"Fix library entry '{entry?.Id}': {what}.");
        }
        Require(entry is not null && entry.Id is { Length: > 0 and <= 64 } && entry.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'),
            "invalid id");
        Require(ProbeAreas.All.Contains(entry!.Area), "unknown area");
        Require(entry.Cause is { Length: > 0 and <= 200 }, "cause must be 1-200 characters");
        Require(entry.Match is { Length: > 0 }, "needs at least one match clause");
        Require(entry.Fixes is { Length: > 0 and <= 2 }, "needs one or two fixes");
        var bindings = (entry.Bind ?? []).ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var clause in entry.Match)
        {
            Require(Probes.Find(clause.Probe) is not null, $"unknown probe '{clause.Probe}'");
            var kinds = (clause.Regex is null ? 0 : 1) + (clause.Status is null ? 0 : 1) + (clause.Where is null ? 0 : 1);
            Require(kinds == 1, "each clause needs exactly one of regex, status or where");
            Require(clause.Status is null or ProbeStatus.Failed or ProbeStatus.Ok, "status must be ok or failed");
            if (clause.Regex is not null) Compile(clause.Regex);
            ValidateConditions(clause.Where, Require);
        }
        foreach (var binding in entry.Bind ?? [])
        {
            Require(binding.Name is { Length: > 0 } && Placeholder.IsMatch("{" + binding.Name + "}"), "invalid binding name");
            Require(Probes.Find(binding.Probe) is not null, $"unknown probe '{binding.Probe}'");
            Require(binding.Field is { Length: > 0 } && binding.Pattern is { Length: > 0 }, "binding needs a field and a pattern");
            Require(binding.Where is not null, "binding needs where conditions");
            Compile(Anchor(binding.Pattern));
            ValidateConditions(binding.Where, Require);
        }
        Require(bindings.Count == (entry.Bind?.Length ?? 0), "duplicate binding");
        Require(Placeholders(entry.Cause).All(bindings.ContainsKey), "cause uses an unbound placeholder");
        var fixIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fix in entry.Fixes)
        {
            Require(fix.Id is { Length: > 0 and <= 32 } && fix.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && fixIds.Add(fix.Id),
                "invalid or duplicate fix id");
            Require(fix.Label is { Length: > 0 and <= 120 }, "fix label must be 1-120 characters");
            Require(Placeholders(fix.Label).All(bindings.ContainsKey), "fix label uses an unbound placeholder");
            if (fix.Kind == FixKinds.Personal)
            {
                Require(fix.Proposal is null, "personal fixes take an action, not a proposal");
                Require(fix.Action is not null && PersonalActions.All.ContainsKey(fix.Action), $"unknown personal action '{fix.Action}'");
                Require(PersonalActions.All[fix.Action!].ParameterNames.All(bindings.ContainsKey), "action parameter is not bound");
            }
            else
            {
                Require(fix.Kind == FixKinds.System, "fix kind must be personal or system");
                Require(fix.Action is null, "system fixes cannot name an action");
                Require(fix.Proposal is { Length: > 0 and <= 300 }, "system fixes need a proposal description");
                Require(Placeholders(fix.Proposal!).All(bindings.ContainsKey), "proposal uses an unbound placeholder");
            }
        }
    }

    private void ValidateConditions(FixCondition[]? conditions, Action<bool, string> require)
    {
        foreach (var condition in conditions ?? [])
        {
            require(condition is not null && condition.Field is { Length: > 0 }, "condition needs a field");
            var ops = new object?[] { condition!.Is, condition.NotEquals, condition.In, condition.AtLeast, condition.AtMost, condition.Matches }
                .Count(x => x is not null);
            require(ops == 1, "each condition needs exactly one operator");
            if (condition.Matches is not null) Compile(condition.Matches);
        }
    }

    private void Compile(string pattern)
    {
        try { _ = Compiled(pattern); }
        catch (ArgumentException) { throw new InvalidOperationException($"Fix library regex is invalid or unsupported: {pattern}"); }
        catch (NotSupportedException) { throw new InvalidOperationException($"Fix library regex is invalid or unsupported: {pattern}"); }
    }

    private static IEnumerable<string> Placeholders(string text) => Placeholder.Matches(text).Select(m => m.Groups[1].Value);
}
