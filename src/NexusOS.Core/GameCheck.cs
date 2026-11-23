using System.Text.RegularExpressions;

// "Will this game work?" People moving from Windows most often give up over
// a game that will not start, and find out only after installing it. Most
// Windows games run through Steam's Proton; the ones that do not are nearly
// all multiplayer games whose anti-cheat refuses Linux. This is a short list
// of the best-known ones, as they stood in late 2025, so Nexus (check_game)
// and Windows programs (POST /windows-program) can say so honestly before
// anything is installed. Anything not listed is "probably works": ProtonDB
// has the current state of every game, and the answer always says so.
public static partial class GameCheck
{
    public enum Status { Blocked, Partly, Works }

    public sealed record Game(string Name, Status Status, string Why, string? Instead = null);
    public sealed record Answer(string Game, string Status, string Message, string ProtonDb);

    private static Game B(string name, string why, string? instead = null) => new(name, Status.Blocked, why, instead);
    private static Game W(string name, string why) => new(name, Status.Works, why);

    private sealed record Entry(Regex Pattern, Game Game);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static Entry E(string pattern, Game game) =>
        new(new Regex(@"(?<![\p{L}\p{N}])(?:" + pattern + @")(?![\p{L}\p{N}])", Options, TimeSpan.FromMilliseconds(50)), game);
    // Names that are also ordinary words ("Rust" the language): only the whole text.
    private static Entry Whole(string pattern, Game game) =>
        new(new Regex(@"\A\s*(?:" + pattern + @")\s*\z", Options, TimeSpan.FromMilliseconds(50)), game);

    private const string Dual = "Keep Windows beside NexusOS for it (the installer can), and use Restart into Windows in Settings, Power.";

    private static readonly Entry[] List =
    [
        E(@"valorant|riot ?vanguard", B("Valorant", "Riot's Vanguard anti-cheat runs only on Windows.", Dual)),
        E(@"league ?of ?legends|lol client|riot ?client", B("League of Legends", "Riot's Vanguard anti-cheat runs only on Windows.", Dual)),
        E(@"fortnite|epic ?games ?launcher.*fortnite", B("Fortnite", "Epic has not allowed its anti-cheat on Linux.", Dual + " Fortnite also streams through Xbox Cloud Gaming or GeForce NOW in the browser.")),
        E(@"apex ?legends", B("Apex Legends", "EA turned Linux players away in 2024.", Dual)),
        E(@"call ?of ?duty|warzone|black ?ops|modern ?warfare", B("Call of Duty", "Activision's Ricochet anti-cheat refuses Linux.", Dual)),
        E(@"rainbow ?six|r6 ?siege", B("Rainbow Six Siege", "Its BattlEye anti-cheat is not turned on for Linux.", Dual)),
        E(@"pubg|battlegrounds", B("PUBG: Battlegrounds", "Its anti-cheat refuses Linux.", Dual)),
        E(@"destiny ?2", B("Destiny 2", "Bungie does not allow Linux.", Dual)),
        E(@"battlefield ?(2042|6|vi)", B("Battlefield", "EA's Javelin anti-cheat runs only on Windows.", Dual)),
        E(@"ea ?(sports)? ?fc|fifa", B("EA Sports FC", "EA's anti-cheat runs only on Windows.", Dual)),
        E(@"escape ?from ?tarkov|tarkov", B("Escape from Tarkov", "Its anti-cheat refuses Linux.", Dual)),
        Whole(@"rust( ?game)?", B("Rust", "Its anti-cheat is not turned on for Linux.", Dual)),
        E(@"rust ?client|facepunch", B("Rust", "Its anti-cheat is not turned on for Linux.", Dual)),
        E(@"roblox", B("Roblox", "Roblox blocks running under Wine.",
            "Sober, an unofficial Roblox player for Linux, is on Flathub (org.vinegarhq.Sober); or keep Windows beside NexusOS.")),
        E(@"gta ?(v|5|online)|grand ?theft ?auto", new("Grand Theft Auto V", Status.Partly,
            "Story mode works through Steam; GTA Online's anti-cheat refuses Linux.", Dual)),
        E(@"counter-?strike|cs ?2|csgo", W("Counter-Strike 2", "Valve's own game: runs on Linux.")),
        E(@"dota ?2", W("Dota 2", "Valve's own game: runs on Linux.")),
        E(@"elden ?ring", W("Elden Ring", "Runs through Steam's Proton, online too.")),
        E(@"helldivers ?2", W("Helldivers 2", "Runs through Steam's Proton.")),
        E(@"overwatch", W("Overwatch 2", "Runs through Steam's Proton.")),
        E(@"marvel ?rivals", W("Marvel Rivals", "Runs through Steam's Proton.")),
        E(@"baldur'?s ?gate ?3|bg3", W("Baldur's Gate 3", "Runs through Steam's Proton.")),
        E(@"cyberpunk ?2077", W("Cyberpunk 2077", "Runs through Steam's Proton.")),
    ];

    public static Game? Find(string text)
    {
        foreach (var e in List)
        {
            try { if (e.Pattern.IsMatch(text ?? "")) return e.Game; }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }

    public static string ProtonDbLink(string name) => "https://www.protondb.com/search?q=" + Uri.EscapeDataString(name.Trim());

    public static Answer Check(string text)
    {
        var name = (text ?? "").Trim();
        var game = Find(name);
        if (game is null)
            return new(name, "unknown", $"{name} is not on NexusOS's list of games known not to work, so it probably runs through Steam's Proton " +
                "(most Windows games do). ProtonDB shows how well it runs for other people.", ProtonDbLink(name));
        var status = game.Status switch { Status.Blocked => "blocked", Status.Partly => "partly", _ => "works" };
        var lead = game.Status switch
        {
            Status.Blocked => $"{game.Name} does not run on Linux: {game.Why}",
            Status.Partly => $"{game.Name} partly runs: {game.Why}",
            _ => $"{game.Name} works: {game.Why}",
        };
        var message = lead + (game.Instead is null ? "" : " " + game.Instead) + " (As of late 2025; ProtonDB has the latest.)";
        return new(game.Name, status, message, ProtonDbLink(game.Name));
    }
}
