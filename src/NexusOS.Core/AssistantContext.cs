using System.Reflection;
using System.Text;
using System.Text.Json;

// What the chat model is told before each question (POST /chat and
// /chat/stream; the JSON-only prompts for suggestions and plans carry their
// own instructions):
//  - who it is and what NexusOS can do: the built-in guide (assistant/nexus-guide.md);
//  - the person's own notes: Markdown or text files in ~/.config/nexusos/assistant
//    (NEXUS_ASSISTANT_NOTES), read on every question, so editing one takes effect at once;
//  - this computer: date, time, machine, memory, the apps set up for each role;
//  - the last few exchanges, so follow-up questions make sense.
// The model is only told things; it gets no tools and runs nothing.
public sealed record ChatMessage(string Role, string Content);

public sealed record ChatContext(string System, IReadOnlyList<ChatMessage> Earlier)
{
    public IReadOnlyList<ChatMessage> Messages(string prompt) =>
        [new("system", System), .. Earlier, new("user", prompt)];
}

public sealed class AssistantContextBuilder
{
    // Rough characters per token for English text; only used to stay inside the budget.
    private const double CharsPerToken = 3.5;
    public const int MaxNoteFiles = 20;
    public const int MaxNotesChars = 8000;
    public const int MaxEarlierTurns = 3;
    public const int MaxEarlierAnswerChars = 1500;
    public static readonly TimeSpan EarlierWindow = TimeSpan.FromMinutes(30);

    private static readonly Lazy<string> BuiltInGuide = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("assistant/nexus-guide.md")
            ?? throw new InvalidOperationException("The assistant guide is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    });

    private readonly ConversationStore _history;
    private readonly InferenceTokenSettings _tokens;
    private readonly string _notesDir;
    private readonly string _desktopApps;
    private readonly TimeProvider _time;

    public AssistantContextBuilder(ConversationStore history, InferenceTokenSettings tokens, IConfiguration config,
        TimeProvider? time = null)
    {
        _history = history;
        _tokens = tokens;
        _time = time ?? TimeProvider.System;
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _notesDir = config["NEXUS_ASSISTANT_NOTES"] ?? Path.Combine(configHome, "nexusos", "assistant");
        _desktopApps = config["NEXUS_DESKTOP_APPS"] ?? "/etc/nexus/desktop-apps.json";
    }

    public static string Guide => BuiltInGuide.Value;

    public async Task<ChatContext> BuildAsync(string prompt, CancellationToken ct)
    {
        var system = new StringBuilder(Guide);
        system.Append("\n\nThis computer\n").Append(Facts());
        var notes = Notes();
        if (notes.Length > 0)
            system.Append("\n\nNotes from the person (follow them unless they ask for something unsafe)\n").Append(notes);

        // Whatever is left of the input budget after the guide and the question
        // goes to earlier exchanges, newest first; the oldest are dropped first.
        var budget = (int)(_tokens.Budget.MaxInputTokens * CharsPerToken) - system.Length - prompt.Length;
        var earlier = new List<ChatMessage>();
        var recent = await _history.RecentAsync(MaxEarlierTurns, ct);
        var cutoff = _time.GetUtcNow() - EarlierWindow;
        foreach (var entry in recent.Where(e => e.Timestamp >= cutoff))
        {
            var answer = entry.Answer.Length <= MaxEarlierAnswerChars ? entry.Answer : entry.Answer[..MaxEarlierAnswerChars] + "…";
            budget -= entry.Prompt.Length + answer.Length;
            if (budget < 0) break;
            earlier.InsertRange(0, [new ChatMessage("user", entry.Prompt), new ChatMessage("assistant", answer)]);
        }
        return new ChatContext(system.ToString(), earlier);
    }

    private string Facts()
    {
        var now = _time.GetLocalNow();
        var lines = new List<string>
        {
            $"- Now: {now:dddd d MMMM yyyy, HH:mm} ({TimeZoneInfo.Local.Id})",
            $"- Computer name: {Environment.MachineName}; user: {Environment.UserName}",
            $"- System: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
                $"{Environment.ProcessorCount} CPU threads, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB memory",
        };
        var apps = DesktopApps();
        if (apps.Length > 0) lines.Add("- Apps set up: " + apps);
        return string.Join("\n", lines);
    }

    // /etc/nexus/desktop-apps.json maps orbit roles to desktop entries,
    // for example {"terminal":"kitty","files":"thunar"}.
    private string DesktopApps()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_desktopApps));
            return string.Join(", ", doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => $"{p.Name}: {Clean(p.Value.GetString()!, 80)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return "";
        }
    }

    private string Notes()
    {
        if (!Directory.Exists(_notesDir)) return "";
        var text = new StringBuilder();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_notesDir)
                         .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.Ordinal).Take(MaxNoteFiles))
            {
                if (new FileInfo(file).LinkTarget is not null) continue;
                var body = File.ReadAllText(file).Trim();
                if (body.Length == 0) continue;
                text.Append(text.Length > 0 ? "\n\n" : "").Append(body);
                if (text.Length >= MaxNotesChars) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return text.Length <= MaxNotesChars ? text.ToString() : text.ToString(0, MaxNotesChars) + "…";
    }

    private static string Clean(string value, int max)
    {
        var text = new string(value.Where(ch => !char.IsControl(ch)).ToArray());
        return text.Length <= max ? text : text[..max];
    }
}
