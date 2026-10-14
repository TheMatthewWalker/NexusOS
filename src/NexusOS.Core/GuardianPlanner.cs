using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record GuardianChange(string Option, bool Enabled);
public sealed record GuardianPlan(string Id, string Description, IReadOnlyList<GuardianChange> Changes, string NixPreview, string Risk);

public static class GuardianPlanner
{
    // Only explicitly supported NixOS boolean options. No paths, commands or interpolated code.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "services.printing.enable",
        "services.openssh.enable",
        "services.flatpak.enable",
        "networking.networkmanager.enable"
    };

    public static GuardianPlan Preview(string? description, IReadOnlyList<GuardianChange>? changes)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 500 ||
            changes is null || changes.Count is < 1 or > 10)
            throw new ArgumentException("Invalid change plan.");

        if (changes.Any(x => !Allowed.Contains(x.Option)) ||
            changes.Select(x => x.Option).Distinct(StringComparer.Ordinal).Count() != changes.Count)
            throw new ArgumentException("Unsupported or duplicate NixOS option.");

        var sorted = changes.OrderBy(x => x.Option, StringComparer.Ordinal).ToArray();
        var preview = "{\n" + string.Join("\n", sorted.Select(x =>
            "  " + x.Option + " = " + (x.Enabled ? "true" : "false") + ";")) + "\n}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preview)))[..16].ToLowerInvariant();
        var risk = sorted.Any(x => x.Option == "services.openssh.enable" && x.Enabled)
            ? "Elevated — enables remote access; review firewall and authentication."
            : "Review required — configuration proposal only.";
        return new GuardianPlan(id, description.Trim(), sorted, preview, risk);
    }
}
