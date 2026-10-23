using System.Net.Http.Json;
using System.Text.Json;

// nexus rules                      list rules as sentences
// nexus rules add "sentence" [--yes] show the draft, ask, then save
// nexus rules pause|resume|rm ID
// nexus rules history ID
static class RulesCommand
{
    public const string Usage = "nexus rules [add \"<sentence>\" [--yes] | pause <id> | resume <id> | rm <id> | history <id>]";

    public static async Task<int> RunAsync(HttpClient http, string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        var yes = args.Any(a => a is "--yes" or "-y");
        var rest = args.Where(a => a is not ("--yes" or "-y")).ToArray();
        switch (rest.Length == 0 ? "list" : rest[0].ToLowerInvariant())
        {
            case "list" when rest.Length <= 1:
                return await List(http, output, error);
            case "add" when rest.Length > 1:
                return await Add(http, string.Join(' ', rest.Skip(1)), yes, input, output, error);
            case "pause" when rest.Length == 2:
                return await Toggle(http, rest[1], "pause", "Paused", output, error);
            case "resume" when rest.Length == 2:
                return await Toggle(http, rest[1], "resume", "Resumed", output, error);
            case "rm" or "remove" or "delete" when rest.Length == 2:
            {
                using var response = await http.DeleteAsync($"/rules/{Uri.EscapeDataString(rest[1])}");
                if (!await Ok(response, error)) return 1;
                output.WriteLine($"Removed rule {rest[1]} and its history.");
                return 0;
            }
            case "history" when rest.Length == 2:
                return await History(http, rest[1], output, error);
            default:
                output.WriteLine(Usage);
                return 2;
        }
    }

    private static async Task<int> List(HttpClient http, TextWriter output, TextWriter error)
    {
        using var response = await http.GetAsync("/rules");
        if (!await Ok(response, error)) return 1;
        var rules = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (rules.GetArrayLength() == 0)
        {
            output.WriteLine("No rules yet. Add one with: nexus rules add \"When I plug in my headphones, open Spotify\"");
            return 0;
        }
        foreach (var rule in rules.EnumerateArray())
        {
            var history = rule.GetProperty("history");
            var last = history.GetProperty("lastRun").ValueKind == JsonValueKind.String
                ? $", last {history.GetProperty("lastRun").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm}" : "";
            output.WriteLine($"{rule.GetProperty("id").GetString()}  {(rule.GetProperty("enabled").GetBoolean() ? "active" : "paused")}  " +
                             $"{rule.GetProperty("sentence").GetString()}");
            output.WriteLine($"                  ran {history.GetProperty("runs").GetInt32()} time(s){last}");
        }
        return 0;
    }

    private static async Task<int> Add(HttpClient http, string sentence, bool yes, TextReader input, TextWriter output, TextWriter error)
    {
        using var parsed = await http.PostAsJsonAsync("/rules/parse", new { sentence });
        if (!await Ok(parsed, error)) return 1;
        var body = await parsed.Content.ReadFromJsonAsync<JsonElement>();
        if (!body.GetProperty("parsed").GetBoolean())
        {
            error.WriteLine($"nexus: {body.GetProperty("message").GetString()}");
            if (body.TryGetProperty("suggestions", out var suggestions) && suggestions.GetArrayLength() > 0)
            {
                error.WriteLine("Try something like:");
                foreach (var suggestion in suggestions.EnumerateArray()) error.WriteLine($"  {suggestion.GetString()}");
            }
            return 1;
        }
        var rule = body.GetProperty("rule");
        output.WriteLine("Draft rule (not saved):");
        output.WriteLine($"  {rule.GetProperty("sentence").GetString()}");
        output.WriteLine($"  Understood by: {(body.GetProperty("source").GetString() == "model" ? "the model" : "keyword patterns")}");
        output.WriteLine(JsonSerializer.Serialize(new
        {
            trigger = rule.GetProperty("trigger"), conditions = rule.GetProperty("conditions"), actions = rule.GetProperty("actions")
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!yes)
        {
            output.Write("Save this rule? [y/N] ");
            var answer = input.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                output.WriteLine("Not saved.");
                return 1;
            }
        }
        using var saved = await http.PostAsJsonAsync("/rules", new
        {
            name = rule.GetProperty("name").GetString(), createdFrom = rule.GetProperty("createdFrom").GetString(),
            trigger = rule.GetProperty("trigger"), conditions = rule.GetProperty("conditions"), actions = rule.GetProperty("actions")
        });
        if (!await Ok(saved, error)) return 1;
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        output.WriteLine($"Saved rule {result.GetProperty("id").GetString()}.");
        return 0;
    }

    private static async Task<int> Toggle(HttpClient http, string id, string verb, string done, TextWriter output, TextWriter error)
    {
        using var response = await http.PostAsync($"/rules/{Uri.EscapeDataString(id)}/{verb}", null);
        if (!await Ok(response, error)) return 1;
        var rule = await response.Content.ReadFromJsonAsync<JsonElement>();
        output.WriteLine($"{done}: {rule.GetProperty("sentence").GetString()}");
        return 0;
    }

    private static async Task<int> History(HttpClient http, string id, TextWriter output, TextWriter error)
    {
        using var response = await http.GetAsync($"/rules/{Uri.EscapeDataString(id)}/history");
        if (!await Ok(response, error)) return 1;
        var runs = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (runs.GetArrayLength() == 0) output.WriteLine("This rule has not run yet.");
        foreach (var run in runs.EnumerateArray())
            output.WriteLine($"{run.GetProperty("time").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm:ss}  " +
                             $"{run.GetProperty("trigger").GetString()}  ({run.GetProperty("actions").GetArrayLength()} action(s))");
        return 0;
    }

    private static async Task<bool> Ok(HttpResponseMessage response, TextWriter error)
    {
        if (response.IsSuccessStatusCode) return true;
        var raw = await response.Content.ReadAsStringAsync();
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            foreach (var key in new[] { "detail", "error", "title" })
                if (message is null && document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    message = value.GetString();
        }
        catch (JsonException) { }
        message ??= response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Authentication failed; restart Core or check the api-token file.",
            System.Net.HttpStatusCode.TooManyRequests => "Nexus Core is busy or rate limiting requests; try again shortly.",
            System.Net.HttpStatusCode.NotFound => "No such rule.",
            _ => "Request failed."
        };
        error.WriteLine($"nexus: {message} (HTTP {(int)response.StatusCode})");
        return false;
    }
}
