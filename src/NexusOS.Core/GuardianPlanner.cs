using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record GuardianChange(string Option, bool Enabled);
public sealed record GuardianPlan(string Id, string Description, IReadOnlyList<GuardianChange> Changes, string NixPreview,
    string Risk, string RiskLevel);

public sealed record GuardianOptionSpec(string Type, string Risk, bool? ElevatedWhen, string Summary, string? RiskNote);

// The option allowlist lives only in schema/guardian-options.json (embedded at
// build time) and is shared with the Python broker; see ADR-002.
public static class GuardianSchema
{
    public static int MaxChanges { get; }
    public static int MaxDescriptionLength { get; }
    public static IReadOnlyDictionary<string, GuardianOptionSpec> Options { get; }

    static GuardianSchema()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("guardian-options.json")
            ?? throw new InvalidOperationException("Embedded Guardian schema missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidOperationException("Unsupported Guardian schema version.");
        MaxChanges = root.GetProperty("maxChanges").GetInt32();
        MaxDescriptionLength = root.GetProperty("maxDescriptionLength").GetInt32();
        var options = new Dictionary<string, GuardianOptionSpec>(StringComparer.Ordinal);
        foreach (var option in root.GetProperty("options").EnumerateObject())
        {
            var value = option.Value;
            options[option.Name] = new GuardianOptionSpec(value.GetProperty("type").GetString()!,
                value.GetProperty("risk").GetString()!,
                value.TryGetProperty("elevatedWhen", out var when) ? when.GetBoolean() : null,
                value.GetProperty("summary").GetString()!,
                value.TryGetProperty("riskNote", out var note) ? note.GetString() : null);
        }
        Options = options;
    }
}

public static class GuardianPlanner
{
    public static string RenderModule(IEnumerable<GuardianChange> sorted) =>
        "{\n" + string.Concat(sorted.Select(x => "  " + x.Option + " = " + (x.Enabled ? "true" : "false") + ";\n")) + "}\n";

    public static string PlanId(string moduleText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("nexus-guardian-plan-v1\n" + moduleText)))[..24].ToLowerInvariant();

    public static GuardianPlan Preview(string? description, IReadOnlyList<GuardianChange>? changes)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > GuardianSchema.MaxDescriptionLength ||
            changes is null || changes.Count < 1 || changes.Count > GuardianSchema.MaxChanges)
            throw new ArgumentException("Invalid change plan.");
        if (changes.Any(x => x is null || x.Option is null || !GuardianSchema.Options.TryGetValue(x.Option, out var spec) || spec.Type != "bool") ||
            changes.Select(x => x.Option).Distinct(StringComparer.Ordinal).Count() != changes.Count)
            throw new ArgumentException("Unsupported or duplicate NixOS option.");

        var sorted = changes.OrderBy(x => x.Option, StringComparer.Ordinal).ToArray();
        var module = RenderModule(sorted);
        var notes = sorted.Where(x => GuardianSchema.Options[x.Option] is { Risk: "elevated" } spec && spec.ElevatedWhen == x.Enabled)
            .Select(x => GuardianSchema.Options[x.Option].RiskNote).ToArray();
        var risk = notes.Length > 0
            ? "Elevated — " + string.Join(" ", notes)
            : "Review required — configuration proposal only.";
        return new GuardianPlan(PlanId(module), description.Trim(), sorted, module, risk, notes.Length > 0 ? "elevated" : "review");
    }
}
