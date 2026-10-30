using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record GuardianChange(string Option, bool Enabled);
// Packages are nixpkgs attribute names added to environment.systemPackages;
// on the broker wire each is a {"package": attr} change.
public sealed record GuardianPlan(string Id, string Description, IReadOnlyList<GuardianChange> Changes, string NixPreview,
    string Risk, string RiskLevel, IReadOnlyList<string>? Packages = null);

// AlsoSetWhenEnabled: settings written with the option whenever it is turned
// on, as Nix text (for example key-only SSH with services.openssh.enable).
// They are never options of their own, so they cannot be proposed by themselves.
public sealed record GuardianOptionSpec(string Type, string Risk, bool? ElevatedWhen, string Summary, string? RiskNote,
    IReadOnlyDictionary<string, string>? AlsoSetWhenEnabled = null);

// The option allowlist lives only in schema/guardian-options.json (embedded at
// build time) and is shared with the Python broker; see ADR-002.
public static class GuardianSchema
{
    public static int MaxChanges { get; }
    public static int MaxDescriptionLength { get; }
    public static IReadOnlyDictionary<string, GuardianOptionSpec> Options { get; }
    private static readonly Regex PackagePattern;
    private static readonly HashSet<string> ReservedWords;
    // As guardian_schema.FIXED_PATH and FIXED_WORD: plain paths and words, rendered without quoting.
    private static readonly Regex FixedPath = new(@"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){1,7}\z", RegexOptions.CultureInvariant);
    private static readonly Regex FixedWord = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);

    // A plain nixpkgs attribute path ("hello", "python3Packages.requests"): no
    // flake references, store paths, quoting or Nix syntax.
    public static bool IsPackageAttr(string? attr) =>
        attr is not null && PackagePattern.IsMatch(attr) && !attr.Split('.').Any(ReservedWords.Contains);

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
        var names = root.GetProperty("options").EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var fixedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in root.GetProperty("options").EnumerateObject())
        {
            var value = option.Value;
            Dictionary<string, string>? also = null;
            if (value.TryGetProperty("alsoSetWhenEnabled", out var fixedSettings))
            {
                also = new(StringComparer.Ordinal);
                foreach (var setting in fixedSettings.EnumerateObject())
                {
                    if (!FixedPath.IsMatch(setting.Name) || names.Contains(setting.Name) || !fixedKeys.Add(setting.Name))
                        throw new InvalidOperationException("Invalid fixed setting in Guardian schema.");
                    also[setting.Name] = setting.Value.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.String when FixedWord.IsMatch(setting.Value.GetString()!) => "\"" + setting.Value.GetString() + "\"",
                        _ => throw new InvalidOperationException("Invalid fixed setting in Guardian schema.")
                    };
                }
            }
            options[option.Name] = new GuardianOptionSpec(value.GetProperty("type").GetString()!,
                value.GetProperty("risk").GetString()!,
                value.TryGetProperty("elevatedWhen", out var when) ? when.GetBoolean() : null,
                value.GetProperty("summary").GetString()!,
                value.TryGetProperty("riskNote", out var note) ? note.GetString() : null,
                also);
        }
        Options = options;
        var packages = root.GetProperty("packages");
        PackagePattern = new Regex(@"\A(?:" + packages.GetProperty("attrPattern").GetString() + @")\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        ReservedWords = packages.GetProperty("reservedWords").EnumerateArray().Select(x => x.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}

public static class GuardianPlanner
{
    public static string RenderModule(IEnumerable<GuardianChange> sorted) => RenderModule(sorted, []);

    // Byte-identical to guardian_schema.render (schema/guardian-vectors.json).
    public static string RenderModule(IEnumerable<GuardianChange> sorted, IReadOnlyList<string> sortedPackages)
    {
        var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in sorted)
        {
            lines[change.Option] = change.Enabled ? "true" : "false";
            if (change.Enabled && GuardianSchema.Options.TryGetValue(change.Option, out var spec) && spec.AlsoSetWhenEnabled is { } also)
                foreach (var (key, nix) in also) lines[key] = nix;
        }
        var options = string.Concat(lines.Select(x => "  " + x.Key + " = " + x.Value + ";\n"));
        if (sortedPackages.Count == 0) return "{\n" + options + "}\n";
        return "{ pkgs, ... }:\n{\n  environment.systemPackages = [ " +
               string.Join(' ', sortedPackages.Select(x => "pkgs." + x)) + " ];\n" + options + "}\n";
    }

    public static string PlanId(string moduleText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("nexus-guardian-plan-v1\n" + moduleText)))[..24].ToLowerInvariant();

    // The typed plan for "install <attr>": one package, nothing else.
    public static GuardianPlan Install(string? attr, string? description = null)
    {
        if (!GuardianSchema.IsPackageAttr(attr)) throw new ArgumentException("Invalid package attribute.");
        return Preview(description ?? "Install " + attr, [], [attr!]);
    }

    // The broker's change list: options as {option, enabled}, packages as {package}.
    public static object[] WireChanges(GuardianPlan plan) =>
        plan.Changes.Select(x => (object)new { option = x.Option, enabled = x.Enabled })
            .Concat((plan.Packages ?? []).Select(x => (object)new { package = x })).ToArray();

    public static GuardianPlan Preview(string? description, IReadOnlyList<GuardianChange>? changes,
        IReadOnlyList<string>? packages = null)
    {
        packages ??= [];
        changes ??= packages.Count > 0 ? [] : null;
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > GuardianSchema.MaxDescriptionLength ||
            changes is null || changes.Count + packages.Count < 1 || changes.Count + packages.Count > GuardianSchema.MaxChanges)
            throw new ArgumentException("Invalid change plan.");
        if (changes.Any(x => x is null || x.Option is null || !GuardianSchema.Options.TryGetValue(x.Option, out var spec) || spec.Type != "bool") ||
            changes.Select(x => x.Option).Distinct(StringComparer.Ordinal).Count() != changes.Count)
            throw new ArgumentException("Unsupported or duplicate NixOS option.");
        if (packages.Any(x => !GuardianSchema.IsPackageAttr(x)) ||
            packages.Distinct(StringComparer.Ordinal).Count() != packages.Count)
            throw new ArgumentException("Invalid or duplicate package attribute.");

        var sorted = changes.OrderBy(x => x.Option, StringComparer.Ordinal).ToArray();
        var sortedPackages = packages.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var module = RenderModule(sorted, sortedPackages);
        var notes = sorted.Where(x => GuardianSchema.Options[x.Option] is { Risk: "elevated" } spec && spec.ElevatedWhen == x.Enabled)
            .Select(x => GuardianSchema.Options[x.Option].RiskNote).ToArray();
        var risk = notes.Length > 0
            ? "Elevated — " + string.Join(" ", notes)
            : "Review required — configuration proposal only.";
        if (sortedPackages.Length > 0)
            risk += " Installs " + string.Join(", ", sortedPackages) + " system-wide from the host's pinned nixpkgs.";
        return new GuardianPlan(PlanId(module), description.Trim(), sorted, module, risk, notes.Length > 0 ? "elevated" : "review",
            sortedPackages.Length > 0 ? sortedPackages : null);
    }
}
