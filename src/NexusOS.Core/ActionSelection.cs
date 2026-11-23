using System.Text.Json;
using System.Text.RegularExpressions;

// Which actions a question is offered. Sending every action's definition with
// every question cost about 1,700 tokens before the model read a word, which
// on a laptop GPU is seconds of prompt processing. A cheap keyword pre-pass
// picks the groups a question is about; the model only sees those:
//  - "this computer" (get_system_state) is always offered;
//  - a short follow-up ("yes", "do that", "the second one") keeps the groups
//    of the question before it, so "install it" after "is there a Spotify app?"
//    still has install_app;
//  - an action in no group (a new one not given keywords yet) is always offered,
//    so nothing silently disappears; NexusActionsTests keep the budget honest.
// The model can still call any action it knows of (the guide names some); the
// call is validated exactly as before, so offering fewer changes nothing about
// what is safe, only what it costs.
public static class ActionSelection
{
    // Rough characters per token for the wire JSON, as AssistantContextBuilder uses for text.
    public const double CharsPerToken = 3.5;
    public const int FollowUpWords = 6;

    // "This computer" and the look at the running system: how Nexus works out what a request needs.
    public static readonly string[] Always = ["get_system_state", "inspect_system"];

    private sealed record Group(string Name, Regex Words, string[] Actions);

    private static Group G(string name, string words, params string[] actions) =>
        new(name, new Regex(@"(?<![\p{L}\p{N}])(?:" + words + @")(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), actions);

    private static readonly Group[] Groups =
    [
        G("open", @"open|launch|start|run|apps?|applications?|programs?|browser|firefox|chrom\w*|terminal|editor|player",
            "list_installed_apps", "open_app"),
        G("install", @"install\w*|software|packages?|flathub|flatpak|apt|download|get me|tools?|spotify|discord|steam|gimp|obs|vlc|" +
                     @"libreoffice|office|games?",
            "install_app", "apt_install", "install_system_software", "get_app_details"),
        G("web", @"web ?apps?|websites?|sites?|pwas?|outlook|teams|gmail|whatsapp|microsoft 365|office 365|google docs|web page",
            "add_web_app"),
        G("remove", @"uninstall\w*|remove|delete|get rid of|undo|put back|reinstall", "remove_app", "list_installed_apps", "undo_app_change", "get_today_summary"),
        G("try", @"try|trial|without installing|sandbox\w*", "try_app"),
        G("settings", @"settings?|preferences?|volume|loud\w*|quiet\w*|mute\w*|sound|audio|speakers?|headphones?|night ?light|" +
                      @"scale|scaling|bigger|smaller|larger|zoom|text size|blur|animations?|motion|contrast|tile|tiled|tiling|" +
                      @"float\w*|windows|wi-?fi|network|internet|bluetooth|display|screen|monitor|brightness|power|battery|" +
                      @"privacy|accessib\w*|dark|theme|magnif\w*|zoom\w*|colou?r ?blind\w*|colou?r filters?|grey ?scale|gray ?scale|invert\w*",
            "get_settings", "open_settings", "change_setting"),
        G("fix", @"fix\w*|broken|break\w*|not working|doesn'?t work|isn'?t working|won'?t|can'?t|cannot|problems?|issues?|wrong|" +
                 @"errors?|crash\w*|freez\w*|frozen|no sound",
            "diagnose_problem", "open_fix_it"),
        G("health", @"health|healthy|check ?up|slow|disk|space|full|lag\w*|overheat\w*", "get_health_report"),
        G("files", @"find|search\w*|files?|documents?|docs?|folders?|where|photos?|pictures?|pdfs?|notes?|essays?|" +
                   @"spreadsheets?|look for|letters?|invoices?|receipts?",
            "search_files"),
        G("automations", @"automat\w*|every|each|whenever|when i|daily|weekly|nightly|at \d+|schedul\w*|remind\w*|rules?|routines?",
            "list_automations", "create_automation"),
        G("today", @"today|happened|journal|activity|timeline|changed|changes|recent\w*|history|what did|yesterday",
            "get_today_summary"),
        G("updates", @"updates?|updat\w*|upgrad\w*|security|patch\w*|kernel|up to date|outdated",
            "get_system_updates", "check_for_updates"),
        G("describe", @"describe|screen|screenshot|what(?:'s| is) (?:on|in) (?:my|the|this)|see|look(?:ing)? at|read (?:this|it)|blind|vision",
            "describe_screen"),
        G("images", @"images?|pictures?|photos?|clean ?up|draw\w*|paint\w*|illustrat\w*|art|artwork|logo|wallpaper|generate|sketch",
            "make_image", "get_picture_usage", "clean_up_pictures"),
        G("code", @"code|coding|coder|programm\w*|bugs?|debug\w*|projects?|repo\w*|functions?|scripts?|refactor\w*|tests?|" +
                  @"failing|compile\w*|python|javascript|typescript|rust|java|c#|website|develop\w*",
            "start_coding_task"),
        G("workspaces", @"workspaces?|remember|bring back|restore|layout|desktops?",
            "remember_workspace", "restore_workspace"),
        G("moving", @"move in|moving|migrat\w*|old (?:drive|disk|computer|laptop|pc|mac)|windows|mac ?os|macbook|transfer\w*|" +
                    @"copy|bring (?:over|across)|welcome|set ?up|get started|getting started|new here|first time",
            "open_move_in", "open_welcome", "get_move_in_report", "copy_old_files", "import_old_bookmarks", "show_old_apps"),
        G("system", @"system|everyone|every (?:account|user)|all (?:users|accounts)|services?|ssh|remote|printers?|printing|print|" +
                    @"cups|flatpak|networkmanager|drivers?|guardian|server",
            "install_system_software", "change_system_option"),
        G("model", @"models?|large|bigger|smarter|cleverer|standard|gpt-?oss|qwen|experts?|\bai\b|llm|brain",
            "use_large_model", "use_very_large_model", "get_settings"),
    ];

    // Offered once a look-up has run, because only then are they useful: a fix
    // diagnose_problem found, the app list's ids.
    public static readonly IReadOnlyDictionary<string, string[]> FollowOn = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["diagnose_problem"] = ["apply_fix", "open_fix_it"],
        ["get_health_report"] = ["diagnose_problem", "open_fix_it"],
        ["list_installed_apps"] = ["open_app", "remove_app"],
        ["get_system_updates"] = ["check_for_updates"],
        ["list_automations"] = ["create_automation"],
    };

    private static readonly HashSet<string> Grouped =
        Groups.SelectMany(g => g.Actions).Concat(Always).Concat(FollowOn.Values.SelectMany(x => x)).ToHashSet(StringComparer.Ordinal);

    // Paths and app ids ("~/projects/app", "org.mozilla.firefox") are not words about what to do.
    private static readonly Regex Paths = new(@"(?:~|\.{0,2}/)\S*|\S+\.\S+\.\S+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static string Words(string text) => Paths.Replace(text, " ");

    // The groups a piece of text is about.
    public static IReadOnlyList<string> GroupsFor(string text) =>
        Groups.Where(g => g.Words.IsMatch(Words(text))).Select(g => g.Name).ToArray();

    // The action names to offer for this question (earlier: the person's previous question, if any).
    public static IReadOnlySet<string> NamesFor(string prompt, string? earlier = null)
    {
        var words = Words(prompt);
        var groups = Groups.Where(g => g.Words.IsMatch(words)).ToList();
        if (earlier is { Length: > 0 } && IsFollowUp(prompt))
            groups.AddRange(Groups.Where(g => g.Words.IsMatch(Words(earlier)) && !groups.Contains(g)));
        var names = new HashSet<string>(Always, StringComparer.Ordinal);
        foreach (var g in groups) names.UnionWith(g.Actions);
        foreach (var action in NexusActions.All) if (!Grouped.Contains(action.Name)) names.Add(action.Name);
        return names;
    }

    public static bool IsFollowUp(string prompt) =>
        prompt.Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).Length <= FollowUpWords;

    // What the tools add to a request, in rough tokens (the same estimate the context budget uses).
    public static int Tokens(IReadOnlyList<ToolSpec> tools) =>
        tools.Count == 0 ? 0 : (int)Math.Ceiling(JsonSerializer.Serialize(ModelTools.Wire(tools)).Length / CharsPerToken);
}
