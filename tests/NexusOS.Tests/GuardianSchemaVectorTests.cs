using System.Text.Json;
using Xunit;

// The C# planner must reproduce schema/guardian-vectors.json byte for byte,
// exactly as the Python broker does (tests/guardian/test_schema.py).
public sealed class GuardianSchemaVectorTests
{
    private static JsonElement Vectors => JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schema", "guardian-vectors.json")))).RootElement;

    private static GuardianChange[] Changes(JsonElement changes) =>
        changes.EnumerateArray().Select(x => new GuardianChange(x.GetProperty("option").GetString()!, x.GetProperty("enabled").GetBoolean())).ToArray();

    [Fact]
    public void ReproducesModuleTextPlanIdAndRisk()
    {
        foreach (var vector in Vectors.GetProperty("cases").EnumerateArray())
        {
            var plan = GuardianPlanner.Preview("vector", Changes(vector.GetProperty("changes")));
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
            if (vector.EnumerateArray().Any(x => x.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                                                 x.EnumerateObject().Count() != 2))
                continue;
            Assert.Throws<ArgumentException>(() => GuardianPlanner.Preview("vector", Changes(vector)));
        }
    }

    [Fact]
    public void SchemaLoadsAllOptions() =>
        Assert.Equal(4, GuardianSchema.Options.Count);
}
