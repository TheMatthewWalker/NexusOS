using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

// Workspaces remember more than apps: files open in them (from window titles
// and recent files), Firefox's tabs (its session store, read only) and the
// Nexus conversation (by its history entry); Restore says what it could not bring back.
public sealed class WorkspaceContentsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("nexus-ws-home-").FullName;
    public void Dispose() => Directory.Delete(_home, recursive: true);

    // A valid LZ4 block of literals only, as the smallest encoder can write it.
    internal static byte[] MozLz4File(string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var block = new List<byte>();
        if (data.Length < 15) block.Add((byte)(data.Length << 4));
        else
        {
            block.Add(0xF0);
            var rest = data.Length - 15;
            while (rest >= 255) { block.Add(255); rest -= 255; }
            block.Add((byte)rest);
        }
        block.AddRange(data);
        return [.. "mozLz40\0"u8.ToArray(), .. BitConverter.GetBytes(data.Length), .. block];
    }

    [Fact]
    public void DecodesLz4LiteralsAndOverlappingMatches()
    {
        Assert.Equal("{\"windows\":[]}", Encoding.UTF8.GetString(MozLz4.Decode(MozLz4File("{\"windows\":[]}"))));
        var long_ = new string('x', 1000) + "end";
        Assert.Equal(long_, Encoding.UTF8.GetString(MozLz4.Decode(MozLz4File(long_))));
        // "abc", then a match 3 back for 9 bytes: abcabcabcabc.
        byte[] block = [0x35, (byte)'a', (byte)'b', (byte)'c', 3, 0];
        Assert.Equal("abcabcabcabc", Encoding.ASCII.GetString(MozLz4.DecodeBlock(block, 12)));
    }

    [Theory]
    [InlineData(new byte[] { 0x35, (byte)'a', (byte)'b', (byte)'c', 9, 0 }, 12)]   // offset before the start
    [InlineData(new byte[] { 0x35, (byte)'a', (byte)'b', (byte)'c', 3, 0 }, 8)]    // longer than it says
    [InlineData(new byte[] { 0xF0 }, 20)]                                          // length cut short
    public void RefusesBrokenLz4(byte[] block, int size) =>
        Assert.Throws<InvalidDataException>(() => MozLz4.DecodeBlock(block, size));

    [Fact]
    public void RefusesFilesThatAreNotMozLz4() =>
        Assert.Throws<InvalidDataException>(() => MozLz4.Decode(Encoding.UTF8.GetBytes("{\"windows\":[]}")));

    [Theory]
    [InlineData("Chapter 3.odt - LibreOffice Writer", "Chapter 3.odt")]
    [InlineData("*notes.txt (~/Documents) - gedit", "notes.txt")]
    [InlineData("report.pdf — Document Viewer", "report.pdf")]
    [InlineData("~/src/app.py - Mousepad", "~/src/app.py")]
    [InlineData("Program.cs - NexusOS - Visual Studio Code", "Program.cs")]
    [InlineData("Sources — Mozilla Firefox", null)]
    [InlineData("Terminal", null)]
    [InlineData("", null)]
    public void FindsFileNamesInWindowTitles(string title, string? expected) =>
        Assert.Equal(expected, WorkspaceContents.FileNameFromTitle(title));

    [Fact]
    public void ReadsFirefoxSessionWindowsWithTheirCurrentPages()
    {
        var windows = WorkspaceContents.ParseSession(Encoding.UTF8.GetBytes(Session));
        Assert.Equal(2, windows.Count);
        Assert.Equal("Sources", windows[0].SelectedTitle);
        // The tab's current page (index), web pages only.
        Assert.Equal(["https://example.org/b", "https://scholar.example/"], windows[0].Tabs.Select(t => t.Url));
        Assert.Equal("Sources", WorkspaceContents.FirefoxPageTitle("Sources — Mozilla Firefox"));
    }

    private const string Session = """
        {"windows":[
          {"selected":2,"tabs":[
            {"index":2,"entries":[{"url":"https://example.org/a","title":"A"},{"url":"https://example.org/b","title":"B"}]},
            {"index":1,"entries":[{"url":"https://scholar.example/","title":"Sources"}]},
            {"index":1,"entries":[{"url":"about:preferences","title":"Settings"}]}]},
          {"selected":1,"tabs":[{"index":1,"entries":[{"url":"https://mail.example/","title":"Mail"}]}]}
        ]}
        """;

    private void MakeHome(bool firefoxRunning)
    {
        var docs = Directory.CreateDirectory(Path.Combine(_home, "Documents")).FullName;
        File.WriteAllText(Path.Combine(docs, "Chapter 3.odt"), "draft");
        var share = Directory.CreateDirectory(Path.Combine(_home, ".local/share")).FullName;
        var chapter = new Uri(Path.Combine(docs, "Chapter 3.odt")).AbsoluteUri;
        File.WriteAllText(Path.Combine(share, "recently-used.xbel"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xbel version="1.0" xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks">
              <bookmark href="file:///etc/passwd" visited="2026-11-01T10:00:00Z"/>
              <bookmark href="{chapter}" visited="2026-11-01T09:00:00Z"/>
            </xbel>
            """);
        var ff = Directory.CreateDirectory(Path.Combine(_home, ".mozilla/firefox")).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(ff, "abcd.default-release")).FullName;
        File.WriteAllText(Path.Combine(ff, "profiles.ini"), "[Profile0]\nName=default-release\nIsRelative=1\nPath=abcd.default-release\nDefault=1\n\n[Install4F96D1932A9F858E]\nDefault=abcd.default-release\n");
        // Closed: sessionstore.jsonlz4. Running: the recovery file is newer, and the lock is a dangling link.
        File.WriteAllBytes(Path.Combine(profile, "sessionstore.jsonlz4"), MozLz4File("""{"windows":[{"selected":1,"tabs":[{"index":1,"entries":[{"url":"https://old.example/","title":"Sources"}]}]}]}"""));
        if (firefoxRunning)
        {
            Directory.CreateDirectory(Path.Combine(profile, "sessionstore-backups"));
            File.WriteAllBytes(Path.Combine(profile, "sessionstore-backups/recovery.jsonlz4"), MozLz4File(Session));
            File.CreateSymbolicLink(Path.Combine(profile, "lock"), "127.0.0.1:+4242");
        }
    }

    [Fact]
    public async Task RemembersFilesTabsAndTheConversationAndRestoreSaysWhatIsGone()
    {
        MakeHome(firefoxRunning: true);
        using var factory = new CoreApiFactory(("NEXUS_WORKSPACE_HOME", _home));
        var client = factory.Authorized();
        using (var chat = await client.PostAsJsonAsync("/chat", new { prompt = "Plan chapter 3" }))
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);

        using var saved = await client.PostAsJsonAsync("/workspaces", new
        {
            name = "Dissertation", workspace = 2, conversation = "Plan chapter 3",
            apps = new object[]
            {
                new { appId = "libreoffice-writer", desktopId = "writer", title = "Chapter 3.odt - LibreOffice Writer" },
                new { appId = "org.mozilla.firefox", desktopId = "firefox", title = "Sources — Mozilla Firefox" },
                new { appId = "org.gnome.gedit", desktopId = (string?)null, title = "mystery.txt - gedit" },
                new { appId = "kitty", desktopId = "kitty", title = "~" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var ws = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspace");
        var chapter = Path.Combine(_home, "Documents", "Chapter 3.odt");
        Assert.Equal(chapter, ws.GetProperty("apps")[0].GetProperty("files")[0].GetString());
        Assert.Equal(["https://example.org/b", "https://scholar.example/"],
            ws.GetProperty("tabs").EnumerateArray().Select(t => t.GetProperty("url").GetString()));
        Assert.Equal("Plan chapter 3", ws.GetProperty("conversation").GetProperty("prompt").GetString());
        Assert.Contains(ws.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("mystery.txt"));

        var plan = await client.GetFromJsonAsync<JsonElement>("/workspaces/restore?name=dissertation");
        Assert.Equal(chapter, plan.GetProperty("apps")[0].GetProperty("files")[0].GetString());
        Assert.Equal(2, plan.GetProperty("tabs").GetArrayLength());
        Assert.Equal("stub answer", plan.GetProperty("conversation").GetProperty("answer").GetString());

        // The file moves away and history is erased: Restore says so instead of pretending.
        File.Delete(chapter);
        using (var erased = await client.DeleteAsync("/history")) Assert.Equal(HttpStatusCode.NoContent, erased.StatusCode);
        plan = await client.GetFromJsonAsync<JsonElement>("/workspaces/restore?name=Dissertation");
        Assert.Equal(0, plan.GetProperty("apps")[0].GetProperty("files").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("conversation").ValueKind);
        var notes = plan.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToArray();
        Assert.Contains(notes, n => n.Contains("no longer at") && n.Contains("Chapter 3.odt"));
        Assert.Contains(notes, n => n.Contains("no longer in history"));
        Assert.Contains(notes, n => n.Contains("mystery.txt"));
    }

    [Fact]
    public async Task UsesTheClosedSessionAndSaysWhenAWindowDoesNotMatch()
    {
        MakeHome(firefoxRunning: false);
        using var factory = new CoreApiFactory(("NEXUS_WORKSPACE_HOME", _home));
        var client = factory.Authorized();
        using var saved = await client.PostAsJsonAsync("/workspaces", new
        {
            name = "Reading", workspace = 3,
            apps = new object[]
            {
                new { appId = "firefox", desktopId = "firefox", title = "Sources — Mozilla Firefox" },
                new { appId = "firefox", desktopId = "firefox", title = "Something else — Mozilla Firefox" }
            }
        });
        var ws = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspace");
        Assert.Equal("https://old.example/", Assert.Single(ws.GetProperty("tabs").EnumerateArray()).GetProperty("url").GetString());
        Assert.Contains(ws.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("did not match"));
        // The session store is only read.
        Assert.True(File.Exists(Path.Combine(_home, ".mozilla/firefox/abcd.default-release/sessionstore.jsonlz4")));
    }

    [Fact]
    public async Task WithoutFirefoxOrHistoryItSaysWhatItCouldNotKeep()
    {
        using var factory = new CoreApiFactory(("NEXUS_WORKSPACE_HOME", _home));
        var client = factory.Authorized();
        using var saved = await client.PostAsJsonAsync("/workspaces", new
        {
            name = "Bare", workspace = 1, conversation = "never asked",
            apps = new[] { new { appId = "firefox", desktopId = "firefox", title = "Mozilla Firefox" } }
        });
        var notes = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspace").GetProperty("notes")
            .EnumerateArray().Select(n => n.GetString()!).ToArray();
        Assert.Contains(notes, n => n.Contains("Firefox's tabs could not be read"));
        Assert.Contains(notes, n => n.Contains("not in Nexus's history"));
        using var missing = await client.GetAsync("/workspaces/restore?name=Nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void RecentFilesOutsideHomeAreIgnored()
    {
        MakeHome(firefoxRunning: false);
        var contents = new WorkspaceContents(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NEXUS_WORKSPACE_HOME"] = _home }).Build());
        Assert.DoesNotContain("/etc/passwd", contents.RecentFiles());
        Assert.Null(contents.ResolveFile("passwd"));
        Assert.Null(contents.ResolveFile("/etc/passwd"));
    }
}
