using System.Text.Json;
using Xunit;

// The C# planner must reproduce schema/guardian-vectors.json byte for byte,
// exactly as the Python broker does (tests/guardian/test_schema.py).
public sealed class GuardianSchemaVectorTests
{
    private static JsonElement Vectors => JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schema", "guardian-vectors.json")))).RootElement;

    private static GuardianChange[] Changes(JsonElement changes) =>
        changes.EnumerateArray().Where(x => !x.TryGetProperty("package", out _))
            .Select(x => new GuardianChange(x.GetProperty("option").GetString()!, x.GetProperty("enabled").GetBoolean())).ToArray();

    private static string[] Packages(JsonElement changes) =>
        changes.EnumerateArray().Where(x => x.TryGetProperty("package", out _))
            .Select(x => x.GetProperty("package").GetString()!).ToArray();

    // Package changes are exactly {"package": <string>}; anything else cannot bind to the C# request.
    private static bool Expressible(JsonElement change) => change.TryGetProperty("package", out var package)
        ? change.EnumerateObject().Count() == 1 && package.ValueKind == JsonValueKind.String
        : change.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is (JsonValueKind.True or JsonValueKind.False) &&
          change.EnumerateObject().Count() == 2;

    [Fact]
    public void ReproducesModuleTextPlanIdAndRisk()
    {
        foreach (var vector in Vectors.GetProperty("cases").EnumerateArray())
        {
            var plan = GuardianPlanner.Preview("vector", Changes(vector.GetProperty("changes")), Packages(vector.GetProperty("changes")));
            Assert.Equal(vector.GetProperty("moduleText").GetString(), plan.NixPreview);
            Assert.Equal(vector.GetProperty("planId").GetString(), plan.Id);
            Assert.Equal(vector.GetProperty("risk").GetString(), plan.RiskLevel);
        }
    }

    [Fact]
    public void RejectsRejectedVectorsThatTypeCheckInCSharp()
    {
        foreach (var vector in Vectors.GetProperty("rejected").EnumerateArray())
        {
            // Non-boolean values and extra fields cannot be expressed as GuardianChange; JSON binding rejects them.
            if (!vector.EnumerateArray().All(Expressible))
                continue;
            Assert.Throws<ArgumentException>(() => GuardianPlanner.Preview("vector", Changes(vector), Packages(vector)));
        }
    }

    [Fact]
    public void SchemaLoadsAllOptions() =>
        Assert.Equal(6, GuardianSchema.Options.Count);
}
