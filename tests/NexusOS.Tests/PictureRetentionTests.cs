using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// ~/Pictures/Nexus does not grow without bound (ImageStudio.cs): pictures are
// kept for a number of days and optionally up to so many GB, removed oldest
// first, never a starred one or one the person moved elsewhere; and the model's
// download reports its progress and why it is waiting.
public sealed class PictureRetentionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-pictures-").FullName;
    private string Pictures => Path.Combine(_dir, "Nexus");
    private string SettingsFile => Path.Combine(_dir, "images.json");

    public void Dispose() => Directory.Delete(_dir, true);

    private CoreApiFactory Factory(params (string, string?)[] extra) =>
        new([("NEXUS_IMAGES_DIR", Pictures), ("NEXUS_IMAGES_SETTINGS", SettingsFile), .. extra]);

    private string Picture(int daysOld, int index, int bytes = 1000, bool starred = false, string? folder = null)
    {
        var made = DateTimeOffset.Now.AddDays(-daysOld);
        var name = $"{made:yyyyMMdd-HHmmss}-{index}.png";
        var dir = Directory.CreateDirectory(folder ?? Pictures).FullName;
        File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(
            new ImageStudio.ImageInfo(name, "", "p" + index, null, index, 512, 512, 4, "m", made, starred)));
        return name;
    }

    [Fact]
    public async Task By_default_pictures_older_than_90_days_go_except_starred_and_moved_ones()
    {
        var old = Picture(120, 1);
        var starred = Picture(200, 2, starred: true);
        var recent = Picture(10, 3);
        var moved = Picture(300, 4, folder: Path.Combine(Pictures, "Keep"));     // moved into a folder of its own
        File.WriteAllBytes(Path.Combine(Pictures, "20200101-000000-9.png"), new byte[10]);  // no JSON: not known to be Nexus's
        using var factory = Factory();
        var client = factory.Authorized();

        var usage = await client.GetFromJsonAsync<JsonElement>("/images/usage");
        Assert.Equal(90, usage.GetProperty("keepDays").GetInt32());
        Assert.Equal(3, usage.GetProperty("count").GetInt32());
        Assert.Equal(1, usage.GetProperty("starred").GetInt32());
        Assert.Equal(1, usage.GetProperty("due").GetProperty("count").GetInt32());

        var result = await (await client.PostAsync("/images/cleanup", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("removed").GetInt32());
        Assert.False(File.Exists(Path.Combine(Pictures, old)));
        Assert.False(File.Exists(Path.Combine(Pictures, old + ".json")));
        foreach (var kept in new[] { Path.Combine(Pictures, starred), Path.Combine(Pictures, recent), Path.Combine(Pictures, "Keep", moved),
                                     Path.Combine(Pictures, "20200101-000000-9.png") })
            Assert.True(File.Exists(kept), kept);
        var journal = await client.GetFromJsonAsync<JsonElement[]>("/journal?limit=10");
        Assert.Contains(journal!, e => e.GetProperty("summary").GetString()!.StartsWith("Removed 1 old picture"));
    }

    [Fact]
    public async Task A_size_limit_removes_the_oldest_first_until_it_fits()
    {
        var oldest = Picture(5, 1, bytes: 400_000_000);
        var middle = Picture(4, 2, bytes: 400_000_000);
        var newest = Picture(3, 3, bytes: 400_000_000);
        var starred = Picture(9, 4, bytes: 400_000_000, starred: true);
        using var factory = Factory();
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/images/settings", new { keepDays = 30, keepGb = 1.0 })).EnsureSuccessStatusCode();
        Assert.Equal(30, JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.GetProperty("keepDays").GetInt32());

        var result = await (await client.PostAsync("/images/cleanup", null)).Content.ReadFromJsonAsync<JsonElement>();
        // 1.6 GB with the starred one (which counts but stays): the two oldest unstarred go.
        Assert.Equal(2, result.GetProperty("removed").GetInt32());
        Assert.False(File.Exists(Path.Combine(Pictures, oldest)));
        Assert.False(File.Exists(Path.Combine(Pictures, middle)));
        Assert.True(File.Exists(Path.Combine(Pictures, newest)));
        Assert.True(File.Exists(Path.Combine(Pictures, starred)));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(4000, null)]
    [InlineData(30, -1.0)]
    public async Task Settings_out_of_range_are_refused(int days, double? gb)
    {
        using var factory = Factory();
        using var response = await factory.Authorized().PostAsJsonAsync("/images/settings", new { keepDays = days, keepGb = gb });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Starring_keeps_a_picture_and_shows_in_the_gallery()
    {
        var name = Picture(400, 1);
        using var factory = Factory();
        var client = factory.Authorized();
        var starred = await (await client.PostAsJsonAsync($"/images/{name}/star", new { starred = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(starred.GetProperty("starred").GetBoolean());
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/images")).GetProperty("images")[0].GetProperty("starred").GetBoolean());
        Assert.Equal(0, (await (await client.PostAsync("/images/cleanup", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/images/notes.txt/star", new { starred = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/images/20200101-000000-1.png/star", new { starred = true })).StatusCode);
    }

    [Fact]
    public async Task Nexus_offers_the_clean_up_as_a_card_and_runs_it_on_Do_it()
    {
        Picture(120, 1);
        Picture(20, 2);
        static string Line(object value) => JsonSerializer.Serialize(value) + "\n";
        var call = Line(new { message = new { content = "", tool_calls = new[] { new { function = new { name = "clean_up_pictures", arguments = new { keep_days = 15 } } } } }, done = false })
                   + Line(new { message = new { content = "" }, done = true });
        using var factory = new CoreApiFactory(("NEXUS_IMAGES_DIR", Pictures), ("NEXUS_IMAGES_SETTINGS", SettingsFile),
            ("NEXUS_ASSISTANT_NOTES", Path.Combine(_dir, "none")), ("NEXUS_FIXIT_MODEL", "false"))
        { AiHandler = StubHandler.Json(HttpStatusCode.OK, call) };
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/chat/stream", new { prompt = "free some room from my pictures" });
        var events = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        var card = events.Single(e => e.GetProperty("type").GetString() == "action");
        Assert.Equal("Keep pictures for 15 days, and remove older ones now", card.GetProperty("summary").GetString());
        Assert.Equal(2, Directory.GetFiles(Pictures, "*.png").Length);     // nothing until Do it

        var done = await (await client.PostAsync($"/actions/{card.GetProperty("id").GetString()}/confirm", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Contains("Removed 2 pictures", done.GetProperty("message").GetString());
        Assert.Equal(15, (await client.GetFromJsonAsync<JsonElement>("/images/settings")).GetProperty("keepDays").GetInt32());
    }

    [Fact]
    public async Task The_download_reports_progress_and_why_it_is_waiting()
    {
        var model = Path.Combine(_dir, "m.safetensors");
        File.WriteAllBytes(model + ".part", new byte[250]);
        var next = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(_dir, "download.json"), JsonSerializer.Serialize(new
        {
            state = "waiting", reason = "space", message = "Not enough free disk space for the image model: 3.0 GB free, 9.1 GB needed.",
            total = 1000, nextTry = next, free = 3_000_000_000L, needed = 9_100_000_000L, extra = new { ignored = true }
        }));
        using var factory = Factory(("NEXUS_SD_SERVER_BIN", "/bin/true"), ("NEXUS_SD_MODEL", model));
        var download = (await factory.Authorized().GetFromJsonAsync<JsonElement>("/images/status")).GetProperty("download");
        Assert.Equal("space", download.GetProperty("reason").GetString());
        Assert.Contains("Not enough free disk space", download.GetProperty("message").GetString());
        Assert.Equal(25, download.GetProperty("percent").GetInt32());
        Assert.Equal(9_100_000_000L, download.GetProperty("needed").GetInt64());
        Assert.Equal(next, download.GetProperty("nextTry").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.False(download.TryGetProperty("extra", out _));
    }

    [Fact]
    public async Task A_download_that_stopped_with_the_computer_reads_as_paused()
    {
        var model = Path.Combine(_dir, "m.safetensors");
        File.WriteAllBytes(model + ".part", new byte[10]);
        File.SetLastWriteTimeUtc(model + ".part", DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(_dir, "download.json"), """{"state":"downloading","message":"Downloading the image model.","total":100}""");
        using var factory = Factory(("NEXUS_SD_MODEL", model));
        var download = (await factory.Authorized().GetFromJsonAsync<JsonElement>("/images/status")).GetProperty("download");
        Assert.Equal("waiting", download.GetProperty("state").GetString());
        Assert.Contains("paused", download.GetProperty("message").GetString());
    }
}
