using System.Net.Http.Json;
using System.Text.Json;

// Nexus's offers ("Do it" cards) from a terminal:
// nexus actions [list]         the offers waiting, with their ids
// nexus actions confirm <id>   the person's "Do it"; then Nexus says what happened
// nexus actions dismiss <id>   "Not now"
// A desktop action (open an app, a setting...) is handed to the desktop, which
// does it; without a running desktop Core says so and the offer keeps waiting.
static class ActionsCommand
{
    public const string Usage = "nexus actions [list | confirm <id> | dismiss <id>]";

    public static async Task<int> RunAsync(HttpClient http, string[] args, TextWriter output, TextWriter error)
    {
        switch (args)
        {
            case [] or ["list"]:
                return await List(http, output, error);
            case ["confirm" or "do", var id]:
                return await Confirm(http, id, output, error);
            case ["dismiss" or "decline", var id]:
            {
                using var response = await http.PostAsync($"/actions/{Uri.EscapeDataString(id)}/decline", null);
                if (!await Ok(response, error)) return 1;
                output.WriteLine("Dismissed. Nothing was done.");
                return 0;
            }
            default:
                output.WriteLine(Usage);
                return 2;
        }
    }

    private static async Task<int> List(HttpClient http, TextWriter output, TextWriter error)
    {
        using var response = await http.GetAsync("/actions/pending");
        if (!await Ok(response, error)) return 1;
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (items.GetArrayLength() == 0)
        {
            output.WriteLine("Nothing is waiting. Ask Nexus for something (nexus ask --stream ...).");
            return 0;
        }
        foreach (var item in items.EnumerateArray())
        {
            var system = Text(item, "safety") == "system";
            output.WriteLine($"{Text(item, "id")}  {Text(item, "summary")}{(system ? "  (system change: goes to Guardian for approval)" : "")}");
            if (Text(item, "detail") is { } detail)
                foreach (var line in detail.Split('\n')) output.WriteLine("    " + line);
        }
        output.WriteLine("Do one with: nexus actions confirm <id>, or nexus actions dismiss <id>");
        return 0;
    }

    private static async Task<int> Confirm(HttpClient http, string id, TextWriter output, TextWriter error)
    {
        using var response = await http.PostAsync($"/actions/{Uri.EscapeDataString(id)}/confirm?from=terminal", null);
        if (!await Ok(response, error)) return 1;
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var status = Text(result, "status");
        output.WriteLine(Text(result, "message") ?? status);
        // Nexus says what happened and what could come next: one short turn.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/actions/{Uri.EscapeDataString(id)}/followup")
            { Content = JsonContent.Create(new { }) };
            using var follow = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (follow.IsSuccessStatusCode)
            {
                using var reader = new StreamReader(await follow.Content.ReadAsStreamAsync());
                var any = false;
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0) continue;
                    using var doc = JsonDocument.Parse(line);
                    if (Text(doc.RootElement, "type") == "delta") { output.Write(Text(doc.RootElement, "text")); any = true; }
                }
                if (any) output.WriteLine();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException) { }
        return status == "failed" ? 1 : 0;
    }

    private static async Task<bool> Ok(HttpResponseMessage response, TextWriter error)
    {
        if (response.IsSuccessStatusCode) return true;
        var raw = await response.Content.ReadAsStringAsync();
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            message = Text(doc.RootElement, "error") ?? Text(doc.RootElement, "detail");
        }
        catch (JsonException) { }
        error.WriteLine($"nexus: {message ?? "Request failed."} (HTTP {(int)response.StatusCode})");
        return false;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? new string(value.GetString()!.Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray()) : null;
}
