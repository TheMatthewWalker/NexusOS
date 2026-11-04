using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

// Move in's Core side (MoveIn.cs): bookmarks and programs found on a crafted
// old drive. The drive is never written; only http(s) bookmarks with clean
// titles come out; oversized, special and symlinked files are not read; the
// bookmarks file never replaces anything; app ids only come from the list.
public sealed class MoveInTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-movein-").FullName;
    private string Drive => Path.Combine(_dir, "drive");
    private string Home => Path.Combine(_dir, "home");
    private string Outside => Path.Combine(_dir, "outside");

    public MoveInTests()
    {
        Directory.CreateDirectory(Drive);
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Outside);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string Make(string rel, string text = "x")
    {
        var path = Path.Combine(Drive, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Dir(string rel) => Directory.CreateDirectory(Path.Combine(Drive, rel)).FullName;

    private static string Chrome(params object[] bar) => JsonSerializer.Serialize(new
    {
        roots = new
        {
            bookmark_bar = new { type = "folder", name = "Bookmarks bar", children = bar },
            other = new { type = "folder", name = "Other", children = Array.Empty<object>() }
        },
        version = 1
    });

    private static object Url(string name, string url) => new { type = "url", name, url };

    // A small Firefox places.sqlite: roots, a folder, bookmarks and a tag.
    private static void Places(string path, bool view = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = (view
            ? "CREATE TABLE real_bookmarks(id INTEGER PRIMARY KEY, type INT, fk INT, parent INT, position INT, title TEXT, guid TEXT);" +
              "CREATE VIEW moz_bookmarks AS SELECT * FROM real_bookmarks;"
            : "CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, type INT, fk INT, parent INT, position INT, title TEXT, guid TEXT);") +
            """
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT);
            INSERT INTO moz_places VALUES (1,'https://bbc.co.uk/news','x'),(2,'javascript:alert(document.cookie)','x'),
              (3,'https://tagged.example/','x'),(4,'file:///etc/passwd','x'),(5,'https://example.org/a"b<c','x');
            """ + (view ? "INSERT INTO real_bookmarks" : "INSERT INTO moz_bookmarks") +
            """
             VALUES (1,2,NULL,0,0,'','root________'),(2,2,NULL,1,0,'menu','menu________'),(3,2,NULL,1,1,'toolbar','toolbar_____'),
              (4,2,NULL,1,2,'tags','tags________'),(5,2,NULL,3,0,'News <b>','abcdefghijkl'),
              (10,1,1,5,0,'BBC News','b1'),(11,1,2,3,1,'Steal','b2'),(12,2,NULL,4,0,'mytag','t1'),(13,1,3,12,0,'Tagged','b3'),
              (14,1,4,2,0,'Passwords','b4'),(15,1,5,2,1,'Quote "test" <img src=x onerror=alert(1)>','b5');
            """;
        cmd.ExecuteNonQuery();
    }

    private void WindowsDrive()
    {
        Dir("Windows/System32");
        Dir("ProgramData");
        Make("Users/bob/Documents/a.txt");
        Make(Path.Combine("Users/bob/AppData/Local/Google/Chrome/User Data/Default/Bookmarks"), Chrome(
            Url("Mail", "https://mail.example.com/"),
            Url("<script>alert(1)</script>", "https://evil.example/"),
            Url("Run me", "javascript:alert(1)"),
            Url("Data", "data:text/html,<script>alert(1)</script>"),
            Url("Local", "file:///C:/Windows"),
            Url("spaced", "https://a.example/ b"),
            Url("\u202eevil\u202c title\u0007", "https://bidi.example/"),
            Url(new string('A', 5000), "https://long.example/"),
            new { type = "folder", name = "Work", children = new object[] { Url("Wiki", "https://wiki.example/") } }));
        Make("Users/bob/AppData/Local/Microsoft/Edge/User Data/Profile 1/Bookmarks", Chrome(Url("Edge one", "http://edge.example/")));
        Places(Path.Combine(Drive, "Users/bob/AppData/Roaming/Mozilla/Firefox/Profiles/abc.default-release/places.sqlite"));
        Dir("Program Files/Adobe Photoshop 2024");
        Dir("Program Files/Microsoft Office");
        Dir("Program Files (x86)/Steam");
        Dir("Program Files/Common Files");
        Make("ProgramData/Microsoft/Windows/Start Menu/Programs/Spotify.lnk");
        Make("ProgramData/Microsoft/Windows/Start Menu/Programs/VLC/VLC media player.lnk");
        Make("ProgramData/Microsoft/Windows/Start Menu/Programs/Uninstall Foo.lnk");
        Make("Users/bob/AppData/Roaming/Microsoft/Windows/Start Menu/Programs/Discord Inc/Discord.lnk");
    }

    private CoreApiFactory Factory(StubHandler? model = null)
    {
        var factory = new CoreApiFactory(("NEXUS_MOVEIN_HOME", Home), ("NEXUS_ASSISTANT_NOTES", Path.Combine(_dir, "none")),
            ("NEXUS_FIXIT_MODEL", "false"), ("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"));
        if (model is not null) factory.AiHandler = model;
        return factory;
    }

    private static async Task<JsonElement> Look(HttpClient client, string root, object[]? folders = null)
    {
        using var response = await client.PostAsJsonAsync("/movein/look", new { root, os = "Windows", folders = folders ?? [] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task A_windows_drive_gives_bookmarks_and_app_suggestions()
    {
        WindowsDrive();
        var places = Path.Combine(Drive, "Users/bob/AppData/Roaming/Mozilla/Firefox/Profiles/abc.default-release/places.sqlite");
        var before = Hash(places);
        using var factory = Factory();
        var client = factory.Authorized();
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/movein")).GetProperty("opened").GetBoolean());

        var report = await Look(client, Drive, [new { user = "bob", folder = "Documents", bytes = 1, on = true },
                                                new { user = "../x", folder = "Documents", bytes = 1, on = true },
                                                new { user = "bob", folder = ".ssh", bytes = 1, on = true }]);
        Assert.Equal(["bob"], report.GetProperty("users").EnumerateArray().Select(u => u.GetString()));
        Assert.Single(report.GetProperty("folders").EnumerateArray());
        var marks = report.GetProperty("bookmarks").EnumerateArray()
            .ToDictionary(b => b.GetProperty("browser").GetString()!, b => b.GetProperty("count").GetInt32());
        // Chrome: mail, evil (title only is odd), bidi, long, wiki; Firefox: BBC, the quote one; Edge: one.
        Assert.Equal(5, marks["Chrome"]);
        Assert.Equal(2, marks["Firefox"]);
        Assert.Equal(1, marks["Edge"]);

        var apps = report.GetProperty("apps").EnumerateArray().ToArray();
        string? Id(string name) => apps.Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetString();
        Assert.Equal("org.gimp.GIMP", Id("GIMP"));
        Assert.Equal("com.spotify.Client", Id("Spotify"));
        Assert.Equal("com.valvesoftware.Steam", Id("Steam"));
        Assert.Equal("com.discordapp.Discord", Id("Discord"));
        Assert.Null(Id("LibreOffice"));
        Assert.Null(Id("VLC"));
        Assert.DoesNotContain(report.GetProperty("oldApps").EnumerateArray(), a => a.GetString()!.StartsWith("Uninstall"));
        Assert.All(apps.Where(a => a.GetProperty("id").ValueKind == JsonValueKind.String),
            a => Assert.Contains(a.GetProperty("id").GetString(), MoveInApps.Ids));

        // The old drive was not touched: same bytes, no journal files next to it.
        Assert.Equal(before, Hash(places));
        Assert.False(File.Exists(places + "-wal"));
        Assert.False(File.Exists(places + "-journal"));
        Assert.Empty(Directory.GetDirectories(factory.StateRoot, "movein-*"));
    }

    [Fact]
    public async Task The_bookmarks_file_holds_only_web_addresses_escaped_and_never_replaces_anything()
    {
        WindowsDrive();
        using var factory = Factory();
        var client = factory.Authorized();
        await Look(client, Drive);
        // Something already there under the first name, and a planted link under the second.
        Directory.CreateDirectory(Path.Combine(Home, "Documents"));
        File.WriteAllText(Path.Combine(Home, "Documents/Bookmarks from the old computer.html"), "mine");
        File.CreateSymbolicLink(Path.Combine(Home, "Documents/Bookmarks from the old computer 2.html"), Path.Combine(Outside, "target"));

        using var response = await client.PostAsJsonAsync("/movein/bookmarks", new { user = "bob" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(8, saved.GetProperty("count").GetInt32());
        var path = saved.GetProperty("path").GetString()!;
        Assert.Equal(Path.Combine(Home, "Documents/Bookmarks from the old computer 3.html"), path);
        Assert.Equal("~/Documents/Bookmarks from the old computer 3.html", saved.GetProperty("shown").GetString());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Home, "Documents/Bookmarks from the old computer.html")));
        Assert.False(File.Exists(Path.Combine(Outside, "target")));

        var html = File.ReadAllText(path);
        Assert.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>", html);
        Assert.Contains("script-src 'none'", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text", html);
        Assert.DoesNotContain("file:", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("tagged.example", html);
        Assert.DoesNotContain("\u202e", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("HREF=\"https://example.org/a%22b%3Cc\"", html);
        Assert.Contains("<DT><H3>News &lt;b&gt;</H3>", html);
        Assert.Contains("<DT><H3>Work</H3>", html);
        Assert.DoesNotContain(new string('A', MoveInService.MaxTitle + 1), html);
        // Every link is an http(s) address in quotes.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "HREF=\"([^\"]*)\""))
            Assert.Matches("^https?://", m.Groups[1].Value);
        Assert.Contains(await client.GetFromJsonAsync<JsonElement[]>("/journal?limit=20") ?? [],
            e => e.GetProperty("summary").GetString()!.Contains("saved 8 bookmarks"));
    }

    [Fact]
    public async Task Oversized_special_symlinked_and_disguised_files_are_not_read()
    {
        Dir("Users/bob/Documents");
        // Too large: a sparse Bookmarks file over the cap.
        var big = Make("Users/bob/AppData/Local/Google/Chrome/User Data/Default/Bookmarks", "");
        using (var f = new FileStream(big, FileMode.Open)) f.SetLength(MoveInService.MaxChromeBytes + 1);
        // A FIFO where Edge's file would be: reading it would hang.
        var edge = Dir("Users/bob/AppData/Local/Microsoft/Edge/User Data/Default");
        Process.Start("mkfifo", Path.Combine(edge, "Bookmarks"))!.WaitForExit();
        // A profile folder that is a symlink to somewhere else.
        var brave = Dir("Users/bob/AppData/Local/BraveSoftware/Brave-Browser/User Data");
        Directory.CreateDirectory(Path.Combine(Outside, "Default"));
        File.WriteAllText(Path.Combine(Outside, "Default/Bookmarks"), Chrome(Url("Outside", "https://outside.example/")));
        Directory.CreateSymbolicLink(Path.Combine(brave, "Default"), Path.Combine(Outside, "Default"));
        // A places.sqlite whose tables are views.
        Places(Path.Combine(Drive, "Users/bob/AppData/Roaming/Mozilla/Firefox/Profiles/x/places.sqlite"), view: true);
        // A symlinked Program Files.
        Directory.CreateDirectory(Path.Combine(Outside, "Spotify"));
        Directory.CreateSymbolicLink(Path.Combine(Drive, "Program Files"), Outside);
        // Not JSON at all.
        Make("Users/bob/AppData/Local/Google/Chrome/User Data/Profile 2/Bookmarks", "{\"roots\": [[[[");

        using var factory = Factory();
        var report = await Look(factory.Authorized(), Drive);
        Assert.Empty(report.GetProperty("bookmarks").EnumerateArray());
        Assert.Empty(report.GetProperty("apps").EnumerateArray());
        var notes = report.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToArray();
        Assert.Contains(notes, n => n.Contains("too large"));
        Assert.Contains(notes, n => n.Contains("Firefox") && n.Contains("could not be read"));
        using var save = await factory.Authorized().PostAsJsonAsync("/movein/bookmarks", new { user = "bob" });
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Home, "Documents")));
    }

    [Fact]
    public async Task A_mac_data_volume_and_an_apfs_fuse_mount_are_read()
    {
        var volume = Path.Combine(Drive, "root");
        Directory.CreateDirectory(Path.Combine(Drive, "private-dir"));
        Places(Path.Combine(volume, "Users/alice/Library/Application Support/Firefox/Profiles/q.default/places.sqlite"));
        Directory.CreateDirectory(Path.Combine(volume, "Users/alice/Library/Safari"));
        File.WriteAllText(Path.Combine(volume, "Users/alice/Library/Safari/Bookmarks.plist"), "bplist00");
        Directory.CreateDirectory(Path.Combine(volume, "Users/Shared"));
        foreach (var app in new[] { "Spotify.app", "Keynote.app", "Final Cut Pro.app", "Utilities/Terminal.app" })
            Directory.CreateDirectory(Path.Combine(volume, "Applications", app));
        Directory.CreateDirectory(Path.Combine(volume, "Users/alice/Applications/zoom.us.app"));

        using var factory = Factory();
        var report = await Look(factory.Authorized(), Drive);
        Assert.Equal(["alice"], report.GetProperty("users").EnumerateArray().Select(u => u.GetString()));
        var marks = Assert.Single(report.GetProperty("bookmarks").EnumerateArray());
        Assert.Equal(("alice", "Firefox", 2), (marks.GetProperty("user").GetString(), marks.GetProperty("browser").GetString(), marks.GetProperty("count").GetInt32()));
        Assert.Contains(report.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("Safari"));
        var names = report.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToArray();
        Assert.Contains("Spotify", names);
        Assert.Contains("Kdenlive", names);
        Assert.Contains("Zoom", names);
        Assert.Contains("LibreOffice", names);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/no/such/place")]
    [InlineData("/tmp/../etc")]
    [InlineData("/tmp/a\nb")]
    public async Task Bad_roots_are_refused(string root)
    {
        using var factory = Factory();
        using var response = await factory.Authorized().PostAsJsonAsync("/movein/look", new { root });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Matching_ignores_lookalikes()
    {
        var apps = MoveInApps.Match(["WordPad", "Steamworks Common Redistributables", "Steam", "Microsoft Word", "Notepad"]);
        Assert.Equal(["Steam", "LibreOffice"], apps.Select(a => a.Name).Order().Reverse());
        Assert.Equal("Microsoft Word", apps.Single(a => a.Name == "LibreOffice").Was);
    }

    // ---- Nexus ----

    private static string Line(object value) => JsonSerializer.Serialize(value) + "\n";
    private static readonly string End = Line(new { message = new { content = "" }, done = true });
    private static string Says(string text) => Line(new { message = new { content = text }, done = false }) + End;
    private static string Calls(string name, object args) =>
        Line(new { message = new { content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } }, done = false }) + End;

    private static StubHandler Model(params string[] turns)
    {
        var next = 0;
        return new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(turns[Math.Min(next++, turns.Length - 1)]) }));
    }

    private static async Task<JsonElement[]> Ask(HttpClient client, string prompt)
    {
        using var response = await client.PostAsJsonAsync("/chat/stream", new { prompt });
        var text = await response.Content.ReadAsStringAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
    }

    private static JsonElement Card(JsonElement[] events) => events.Single(e => e.GetProperty("type").GetString() == "action");

    [Fact]
    public async Task Nexus_reports_what_was_found_and_offers_cards()
    {
        WindowsDrive();
        var stub = Model(Calls("get_move_in_report", new { }), Says("Found it."));
        using var factory = Factory(stub);
        var client = factory.Authorized();

        // Nothing open yet: Nexus is told to offer Move in.
        await Ask(client, "what is on my old drive?");
        Assert.Contains("open_move_in", stub.Bodies[1]);
        await Look(client, Drive, [new { user = "bob", folder = "Documents", bytes = 2_500_000_000L, on = true }]);
        factory.AiHandler = stub = Model(Calls("get_move_in_report", new { }), Says("Found it."));
        await Ask(client, "and now?");
        Assert.Contains("com.spotify.Client", stub.Bodies[1]);
        Assert.Contains("Chrome", stub.Bodies[1]);

        factory.AiHandler = Model(Calls("import_old_bookmarks", new { }));
        var card = Card(await Ask(client, "bring my bookmarks"));
        Assert.Equal("Save 8 bookmarks from bob's Firefox and Chrome and Edge as a file for Firefox", card.GetProperty("summary").GetString());
        using (var confirm = await client.PostAsync($"/actions/{card.GetProperty("id").GetString()}/confirm", null))
        {
            var result = await confirm.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("done", result.GetProperty("status").GetString());
            Assert.Contains("Import Bookmarks from HTML", result.GetProperty("message").GetString());
        }
        Assert.True(File.Exists(Path.Combine(Home, "Documents/Bookmarks from the old computer.html")));

        factory.AiHandler = Model(Calls("show_old_apps", new { }));
        card = Card(await Ask(client, "which apps?"));
        using (var confirm = await client.PostAsync($"/actions/{card.GetProperty("id").GetString()}/confirm", null))
        {
            var perform = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("perform");
            Assert.Equal("apps.suggest", perform.GetProperty("op").GetString());
            var ids = perform.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToArray();
            Assert.Contains("org.gimp.GIMP", ids);
            Assert.All(ids, id => Assert.Contains(id, MoveInApps.Ids));
        }

        factory.AiHandler = Model(Calls("copy_old_files", new { }));
        card = Card(await Ask(client, "copy my files"));
        Assert.Equal("Copy Documents (2.5 GB) from the old drive into your home folder", card.GetProperty("summary").GetString());

        // A user name that is a path is refused by the schema.
        factory.AiHandler = Model(Calls("import_old_bookmarks", new { user = "../root" }), Says("ok"));
        Assert.DoesNotContain(await Ask(client, "bookmarks of ../root"), e => e.GetProperty("type").GetString() == "action");
    }
}
