using System.Text;
using System.Text.Json;

// Tool calling on the wire, for the chat's actions (NexusActions.cs). The
// model only ever *asks* for an action by name with JSON arguments; those
// arguments stay raw text here and are untrusted until the action registry
// validates them. Two dialects:
//  - Ollama /api/chat: "tools" in the request; each streamed line may carry
//    message.tool_calls with arguments as an object;
//  - OpenAI-style /v1/chat/completions (llama.cpp, cloud): "tools" in the
//    request; delta.tool_calls arrive in pieces, keyed by index, with the
//    arguments as a JSON string built up across lines.
public sealed record ToolSpec(string Name, string Description, JsonElement Parameters);
// Malformed: the model sent a call that could not be read (arguments that are
// not JSON text, an entry of the wrong shape). It is answered with one error
// (ActionRunner.Malformed) instead of breaking the stream.
public sealed record ModelToolCall(string Id, string Name, string Arguments, bool Malformed = false);
// Calls: what an assistant turn asked for. Answers: the call a "tool" message answers.
public sealed record TurnMessage(string Role, string Content, IReadOnlyList<ModelToolCall>? Calls = null,
    ModelToolCall? Answers = null);
public sealed record TurnPiece(string? Text, ModelToolCall? Call);

// The backend turned the tools down (HTTP 400/500 before answering); the turn is retried without them.
public sealed class ToolsRefusedException() : Exception("The model backend does not accept tools.");

public interface ITurnParser
{
    (string? Text, bool Done) Feed(string line);
    IReadOnlyList<ModelToolCall> Calls { get; }
}

public static class ModelTools
{
    public const int MaxCallsPerTurn = 8;
    public const int MaxArgumentChars = 8192;
    public const int MaxNameChars = 64;

    public static object[] Wire(IReadOnlyList<ToolSpec> tools) =>
        tools.Select(t => (object)new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.Parameters } })
            .ToArray();

    public static object[] OllamaMessages(IReadOnlyList<TurnMessage> messages) => messages.Select(m => m switch
    {
        { Role: "assistant", Calls.Count: > 0 } => (object)new
        {
            role = "assistant", content = m.Content,
            tool_calls = m.Calls.Select(c => new { function = new { name = c.Name, arguments = ArgumentsObject(c.Arguments) } }).ToArray()
        },
        { Role: "tool" } => new { role = "tool", content = m.Content, tool_name = m.Answers?.Name ?? "" },
        _ => new { role = m.Role, content = m.Content }
    }).ToArray();

    public static object[] OpenAiMessages(IReadOnlyList<TurnMessage> messages) => messages.Select(m => m switch
    {
        { Role: "assistant", Calls.Count: > 0 } => (object)new
        {
            role = "assistant", content = m.Content,
            // llama-server (--jinja) parses the arguments of earlier calls again: a malformed one would fail the whole request.
            tool_calls = m.Calls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name,
                arguments = ArgumentsObject(c.Arguments).GetRawText() } }).ToArray()
        },
        { Role: "tool" } => new { role = "tool", tool_call_id = m.Answers?.Id ?? "", content = m.Content },
        _ => new { role = m.Role, content = m.Content }
    }).ToArray();

    private static JsonElement ArgumentsObject(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
        }
        // ArgumentException: text that is not valid UTF-16 (a lone surrogate) cannot even be parsed.
        catch (Exception ex) when (ex is JsonException or ArgumentException) { }
        return JsonSerializer.SerializeToElement(new { });
    }

    // Names are kept as the model sent them (bounded); the registry decides whether they mean anything.
    internal static string Name(string? name)
    {
        var text = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length <= MaxNameChars ? text : text[..MaxNameChars];
    }
}

// Ollama: {"message":{"content":"..","tool_calls":[{"function":{"name":"x","arguments":{..}}}]},"done":false}
public sealed class OllamaTurnParser : ITurnParser
{
    private readonly List<ModelToolCall> _calls = [];
    public IReadOnlyList<ModelToolCall> Calls => _calls;

    public (string? Text, bool Done) Feed(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return (null, done);
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            foreach (var call in calls.EnumerateArray())
            {
                if (_calls.Count >= ModelTools.MaxCallsPerTurn) break;
                if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("function", out var function) ||
                    function.ValueKind != JsonValueKind.Object) continue;
                string? name;
                string arguments;
                var malformed = false;
                // GetString throws on text that is not valid Unicode; such a call is answered, not fatal.
                try
                {
                    name = function.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    arguments = function.TryGetProperty("arguments", out var a) ? a.ValueKind switch
                    {
                        JsonValueKind.Object => a.GetRawText(),
                        JsonValueKind.String => a.GetString()!,
                        JsonValueKind.Null or JsonValueKind.Undefined => "{}",
                        _ => ""
                    } : "{}";
                }
                catch (InvalidOperationException) { name = "unreadable"; arguments = ""; malformed = true; }
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (arguments.Length > ModelTools.MaxArgumentChars || arguments.Length == 0) { arguments = ""; malformed = true; }
                _calls.Add(new ModelToolCall("call_" + (_calls.Count + 1), ModelTools.Name(name), arguments, malformed));
            }
        var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return (text, done);
    }
}

// OpenAI-style SSE: "data: {choices:[{delta:{content, tool_calls:[{index,id,function:{name,arguments}}]}}]}".
public sealed class OpenAiTurnParser : ITurnParser
{
    private readonly SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Arguments, bool Malformed)> _parts = [];

    public IReadOnlyList<ModelToolCall> Calls => _parts
        .Where(p => p.Value.Name.Length > 0)
        .Take(ModelTools.MaxCallsPerTurn)
        .Select(p =>
        {
            var tooLong = p.Value.Arguments.Length > ModelTools.MaxArgumentChars;
            return new ModelToolCall(p.Value.Id ?? "call_" + (p.Key + 1), ModelTools.Name(p.Value.Name.ToString()),
                tooLong || p.Value.Malformed ? "" : p.Value.Arguments.Length == 0 ? "{}" : p.Value.Arguments.ToString(),
                tooLong || p.Value.Malformed);
        })
        .ToArray();

    public (string? Text, bool Done) Feed(string line)
    {
        if (line.StartsWith(':') || !line.StartsWith("data:", StringComparison.Ordinal)) return (null, false);
        var data = line[5..].Trim();
        if (data == "[DONE]") return (null, true);
        using var doc = JsonDocument.Parse(data);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return (null, false);
        var choice = choices[0];
        string? text = null;
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) text = c.GetString();
            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                foreach (var call in calls.EnumerateArray()) if (call.ValueKind == JsonValueKind.Object) Add(call);
        }
        var done = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String;
        return (text, done);
    }

    private void Add(JsonElement call)
    {
        var index = call.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : _parts.Count;
        if (index is < 0 or > 64) return;
        if (!_parts.TryGetValue(index, out var part))
        {
            if (_parts.Count >= ModelTools.MaxCallsPerTurn) return;
            part = (null, new StringBuilder(), new StringBuilder(), false);
        }
        try { Fill(call, ref part); }
        // Text that is not valid Unicode: the call is kept, marked, and answered with an error.
        catch (InvalidOperationException) { part.Malformed = true; }
        _parts[index] = part;
    }

    private static void Fill(JsonElement call, ref (string? Id, StringBuilder Name, StringBuilder Arguments, bool Malformed) part)
    {
        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 64 } value)
            part.Id = value;
        if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && part.Name.Length < 256)
                part.Name.Append(name.GetString());
            if (function.TryGetProperty("arguments", out var args) && part.Arguments.Length <= ModelTools.MaxArgumentChars)
            {
                // Some servers send the arguments as an object rather than a string.
                if (args.ValueKind == JsonValueKind.String) part.Arguments.Append(args.GetString());
                else if (args.ValueKind == JsonValueKind.Object) part.Arguments.Append(args.GetRawText());
                else if (args.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) part.Malformed = true;
            }
        }
    }
}
