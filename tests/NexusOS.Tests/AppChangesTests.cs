using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

// Installing software (AppChanges.cs, FlathubDetails.cs): what a Flathub app
// may reach in plain words, and every install and removal journalled with an
// Undo that does the opposite in the same place, at the same version.
public sealed class AppChangesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-apps-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private const string Commit = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // ---- permissions in plain words ----

    [Fact]
    public void Home_access_is_said_plainly_and_warned_about()
    {
        var permissions = FlatpakPermissions.Summarise("""
            [Application]
            name=org.example.Editor

            [Context]
            shared=network;ipc;
            sockets=x11;wayland;pulseaudio;
            devices=dri;
            filesystems=home;xdg-download:ro;!host;
            """);
        Assert.Equal("Can read and change all your files", permissions[0].Text);
        Assert.True(permissions[0].Broad);
        Assert.Contains(permissions, p => p.Text == "Can use the internet" && !p.Broad);
        Assert.Contains(permissions, p => p.Text == "Can play sound and use the microphone");
        Assert.Contains(permissions, p => p.Text.StartsWith("Uses the older X11 display") && p.Broad);
        Assert.Contains(permissions, p => p.Text == "Can read your Downloads folder" && !p.Broad);
        // Negated entries are not permissions; dri, ipc and wayland are what every app needs.
        Assert.DoesNotContain(permissions, p => p.Text.Contains("all files on this computer"));
        Assert.Equal(5, permissions.Count);
        Assert.StartsWith("Wide access: can read and change all your files.", FlathubDetails.Warning(permissions));
    }

    [Theory]
    [InlineData("filesystems=host", "Can read and change all files on this computer, yours included", true)]
    [InlineData("filesystems=home:ro", "Can read all your files", true)]
    [InlineData("filesystems=~/.ssh:ro", "Can read ~/.ssh, which holds keys or passwords", true)]
    [InlineData("filesystems=~/Music", "Can read and change ~/Music", false)]
    [InlineData("filesystems=xdg-pictures/Screenshots:create", "Can read and change a folder in your Pictures folder", false)]
    [InlineData("filesystems=/run/media", "Can read and change USB drives and other disks", false)]
    [InlineData("devices=all", "Can use all devices, including the camera, microphones and USB devices", true)]
    [InlineData("sockets=session-bus", "Can talk to every app and service in your session", true)]
    [InlineData("features=bluetooth", "Can use Bluetooth", false)]
    public void Each_permission_has_a_plain_sentence(string context, string text, bool broad)
    {
        var permission = Assert.Single(FlatpakPermissions.Summarise("[Context]\n" + context + "\n"));
        Assert.Equal((text, broad), (permission.Text, permission.Broad));
    }

    [Fact]
    public void Talking_to_flatpak_itself_means_leaving_the_sandbox()
    {
        var permissions = FlatpakPermissions.Summarise("[Session Bus Policy]\norg.freedesktop.Flatpak=talk\norg.freedesktop.Notifications=talk\n");
        var only = Assert.Single(permissions);
        Assert.Equal(("Can run commands outside its sandbox", true), (only.Text, only.Broad));
    }

    [Fact]
    public void An_app_with_no_holes_is_said_to_stay_in_its_sandbox()
    {
        var lines = FlathubDetails.Lines(new FlathubAppDetails("org.example.Game", true, "Game", null, "Example Ltd", false,
            "LicenseRef-proprietary", false, "12 MB", null, null, true, [], null, []));
        Assert.Equal("By Example Ltd, not verified: Flathub has not confirmed who publishes it", lines[0]);
        Assert.Equal("Licence: proprietary (not open source)", lines[1]);
        Assert.StartsWith("Download: 12 MB", lines[2]);
        Assert.StartsWith("Stays in its sandbox", lines[3]);
    }

    // ---- the details, read the cheap way ----

    private string Catalogue()
    {
        var path = Path.Combine(_dir, "appstream.xml.gz");
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Fastest);
        gz.Write(Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <components version="0.8" origin="flathub">
              <component type="desktop-application">
                <id>org.gimp.GIMP</id>
                <name>GNU Image Manipulation Program</name>
                <name xml:lang="de">GNU-Bildbearbeitung</name>
                <summary>Create images and edit photographs</summary>
                <developer id="org.gimp"><name>The GIMP team</name></developer>
                <project_license>GPL-3.0-or-later</project_license>
                <custom><value key="flathub::verification::verified">true</value></custom>
              </component>
              <component type="desktop-application">
                <id>com.example.Unverified.desktop</id>
                <name>Unverified</name>
                <developer_name>Somebody</developer_name>
                <project_license>LicenseRef-proprietary</project_license>
              </component>
            </components>
            """));
        return path;
    }

    private (CoreApiFactory Factory, FakeCommandRunner Runner) Core(params (string, string?)[] extra)
    {
        var runner = new FakeCommandRunner();
        runner.Installed.Add("flatpak");
        runner.ReturnsForArgv(["flatpak", "remote-info", "--cached", "flathub", "org.gimp.GIMP"], new CommandResult(ProbeStatus.Ok, 0, """
            GNU Image Manipulation Program - Create images and edit photographs

                    ID: org.gimp.GIMP
                   Ref: app/org.gimp.GIMP/x86_64/stable
               Version: 2.10.38
               License: GPL-3.0+
              Download: 120.5 MB
             Installed: 380.2 MB
            """, false));
        runner.ReturnsForArgv(["flatpak", "remote-info", "--show-metadata", "--cached", "flathub", "org.gimp.GIMP"], new CommandResult(ProbeStatus.Ok, 0,
            "[Application]\nname=org.gimp.GIMP\n\n[Context]\nshared=network;ipc;\nsockets=x11;\nfilesystems=host;\n", false));
        runner.Default = new CommandResult(ProbeStatus.Failed, 1, "error: Nothing matches", false);
        var settings = new List<(string, string?)> { ("NEXUS_FLATPAK_APPSTREAM", Catalogue()), ("NEXUS_FIXIT_MODEL", "false"),
            ("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"), ("NEXUS_ASSISTANT_NOTES", Path.Combine(_dir, "none")) };
        settings.AddRange(extra);
        return (new CoreApiFactory(settings.ToArray()) { Commands = runner }, runner);
    }

    [Fact]
    public async Task Details_come_from_the_catalogue_on_disk_and_the_cached_summary()
    {
        var (factory, runner) = Core();
        using var _ = factory;
        var details = await factory.Authorized().GetFromJsonAsync<JsonElement>("/apps/flathub/org.gimp.GIMP");
        Assert.True(details.GetProperty("known").GetBoolean());
        Assert.Equal("GNU Image Manipulation Program", details.GetProperty("name").GetString());
        Assert.Equal("The GIMP team", details.GetProperty("developer").GetString());
        Assert.True(details.GetProperty("verified").GetBoolean());
        Assert.Equal("GPL-3.0-or-later", details.GetProperty("license").GetString());
        Assert.Equal("120.5 MB", details.GetProperty("downloadSize").GetString());
        var lines = details.GetProperty("lines").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal("By The GIMP team, verified by Flathub", lines[0]);
        Assert.Contains("Can read and change all files on this computer, yours included", lines);
        Assert.Contains("all files on this computer", details.GetProperty("warning").GetString());
        // Only the cached summary was used: no network look-up was needed.
        Assert.All(runner.Executed, argv => Assert.Contains("--cached", argv));
    }

    [Fact]
    public async Task An_unverified_app_says_so_and_offline_permissions_are_unknown()
    {
        var (factory, runner) = Core();
        using var _ = factory;
        var details = await factory.Authorized().GetFromJsonAsync<JsonElement>("/apps/flathub/com.example.Unverified");
        Assert.False(details.GetProperty("verified").GetBoolean());
        Assert.False(details.GetProperty("openSource").GetBoolean());
        Assert.False(details.GetProperty("permissionsKnown").GetBoolean());
        var lines = details.GetProperty("lines").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(lines, l => l!.Contains("not verified"));
        Assert.Contains(lines, l => l!.Contains("could not be read"));
        // After the cache, it tried Flathub itself.
        Assert.Contains(runner.Executed, argv => !argv.Contains("--cached"));
    }

    [Fact]
    public async Task A_bad_app_id_is_refused()
    {
        var (factory, _) = Core();
        using var __ = factory;
        using var response = await factory.Authorized().GetAsync("/apps/flathub/" + Uri.EscapeDataString("org.gimp;rm -rf"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- journalling and Undo ----

    [Theory]
    [InlineData("flathub", "install", "org.gimp.GIMP", null, null)]           // no scope
    [InlineData("flathub", "install", "../../etc", "user", null)]
    [InlineData("apt", "install", "htop && reboot", null, "ubuntu")]
    [InlineData("apt", "install", "htop", null, null)]                         // no container
    [InlineData("apt", "delete-data", "htop", null, "ubuntu")]
    [InlineData("snap", "install", "htop", null, "ubuntu")]
    public async Task Reports_that_do_not_fit_are_refused(string source, string op, string id, string? scope, string? box)
    {
        var (factory, _) = Core();
        using var __ = factory;
        using var response = await factory.Authorized().PostAsJsonAsync("/apps/changes", new { source, op, id, scope, box });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_apt_version_that_could_reach_a_shell_is_refused()
    {
        Assert.NotNull(AppChanges.Validate(new AppChange("apt", "remove", "htop", Version: "1.0; reboot", Box: "ubuntu")));
        Assert.Null(AppChanges.Validate(new AppChange("apt", "remove", "htop", Version: "1:3.3.0-4ubuntu1~24.04", Box: "ubuntu")));
        Assert.NotNull(AppChanges.Validate(new AppChange("flathub", "remove", "org.gimp.GIMP", Scope: "user", Commit: "abc")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Undoing_a_flathub_removal_reinstalls_it_in_the_same_place_at_the_same_commit()
    {
        var (factory, _) = Core();
        using var __ = factory;
        var client = factory.Authorized();
        var removed = await (await client.PostAsJsonAsync("/apps/changes", new
        {
            source = "flathub", op = "remove", id = "org.gimp.GIMP", name = "GIMP", scope = "system", origin = "flathub",
            branch = "stable", commit = Commit, version = "2.10.38"
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = removed.GetProperty("id").GetInt64();
        Assert.Equal("Removed GIMP (Flathub, for everyone)", removed.GetProperty("summary").GetString());

        var entry = Assert.Single(await client.GetFromJsonAsync<JsonElement[]>("/journal?kind=app") ?? []);
        Assert.True(entry.GetProperty("reversible").GetBoolean());
        Assert.Equal("flathub:system:org.gimp.GIMP", entry.GetProperty("ref").GetString());

        var undo = await (await client.PostAsync($"/journal/{id}/undo", null)).Content.ReadFromJsonAsync<JsonElement>();
        var perform = undo.GetProperty("perform");
        Assert.Equal("flathub.install", perform.GetProperty("op").GetString());
        Assert.Equal("system", perform.GetProperty("scope").GetString());
        Assert.Equal(Commit, perform.GetProperty("commit").GetString());
        Assert.Equal("stable", perform.GetProperty("branch").GetString());
        Assert.Equal(id, perform.GetProperty("undoes").GetInt64());
        Assert.False(undo.GetProperty("undone").GetBoolean());       // not until the reinstall is reported

        var back = await (await client.PostAsJsonAsync("/apps/changes", new
        {
            source = "flathub", op = "install", id = "org.gimp.GIMP", name = "GIMP", scope = "system", commit = Commit, undoes = id
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(back.GetProperty("undid").GetBoolean());
        Assert.EndsWith("(undo)", back.GetProperty("summary").GetString());
        var journal = await client.GetFromJsonAsync<JsonElement[]>("/journal?kind=app") ?? [];
        Assert.NotEqual(JsonValueKind.Null, journal.Single(e => e.GetProperty("id").GetInt64() == id).GetProperty("undoneAt").ValueKind);
        using var again = await client.PostAsync($"/journal/{id}/undo", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Undoing_an_apt_install_removes_it_from_the_same_container_and_a_later_change_wins()
    {
        var (factory, _) = Core();
        using var __ = factory;
        var client = factory.Authorized();
        async Task<long> Report(object body) =>
            (await (await client.PostAsJsonAsync("/apps/changes", body)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var installed = await Report(new { source = "apt", op = "install", id = "htop", version = "3.3.0-4", box = "ubuntu-isolated", isolated = true });
        var undo = await (await client.PostAsync($"/journal/{installed}/undo", null)).Content.ReadFromJsonAsync<JsonElement>();
        var perform = undo.GetProperty("perform");
        Assert.Equal(("apt.remove", "htop", "ubuntu-isolated", true),
            (perform.GetProperty("op").GetString(), perform.GetProperty("package").GetString(), perform.GetProperty("box").GetString(),
             perform.GetProperty("isolated").GetBoolean()));

        // Removed by hand afterwards: undoing the install would do nothing, so the later entry is the one to undo.
        var removed = await Report(new { source = "apt", op = "remove", id = "htop", version = "3.3.0-4", box = "ubuntu-isolated", isolated = true });
        using (var stale = await client.PostAsync($"/journal/{installed}/undo", null))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var reinstall = (await (await client.PostAsync($"/journal/{removed}/undo", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("perform");
        Assert.Equal(("apt.install", "3.3.0-4"), (reinstall.GetProperty("op").GetString(), reinstall.GetProperty("version").GetString()));

        // A report claiming to undo the wrong thing records the change but marks nothing undone.
        var other = await (await client.PostAsJsonAsync("/apps/changes",
            new { source = "apt", op = "install", id = "curl", box = "ubuntu-isolated", isolated = true, undoes = removed }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(other.GetProperty("undid").GetBoolean());
    }

    [Fact]
    public async Task Deleting_leftover_data_is_journalled_without_an_undo()
    {
        var (factory, _) = Core();
        using var __ = factory;
        var client = factory.Authorized();
        var id = (await (await client.PostAsJsonAsync("/apps/changes",
            new { source = "flathub", op = "delete-data", id = "org.gimp.GIMP", name = "GIMP", scope = "user" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var entry = Assert.Single(await client.GetFromJsonAsync<JsonElement[]>("/journal?kind=app") ?? []);
        Assert.Equal("Deleted GIMP's data and settings (~/.var/app/org.gimp.GIMP)", entry.GetProperty("summary").GetString());
        Assert.False(entry.GetProperty("reversible").GetBoolean());
        using var undo = await client.PostAsync($"/journal/{id}/undo", null);
        Assert.Equal(HttpStatusCode.Conflict, undo.StatusCode);
    }

    // ---- Nexus ----

    private static string Line(object value) => JsonSerializer.Serialize(value) + "\n";
    private static string Calls(string name, object args) =>
        Line(new { message = new { content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } }, done = false }) +
        Line(new { message = new { content = "" }, done = true });

    private static async Task<JsonElement> Offer(HttpClient client, string prompt)
    {
        using var response = await client.PostAsJsonAsync("/chat/stream", new { prompt });
        var text = await response.Content.ReadAsStringAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Single(e => e.GetProperty("type").GetString() == "action");
    }

    [Fact]
    public async Task Nexus_shows_the_publisher_and_permissions_on_the_install_card()
    {
        var (factory, _) = Core();
        using var __ = factory;
        factory.AiHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Calls("install_app", new { app_id = "org.gimp.GIMP" })) }));
        var action = await Offer(factory.Authorized(), "install gimp");
        Assert.Equal("Install GNU Image Manipulation Program (org.gimp.GIMP) from Flathub, just for you", action.GetProperty("summary").GetString());
        var details = action.GetProperty("details");
        Assert.Contains("all files on this computer", details.GetProperty("warning").GetString());
        Assert.Contains("By The GIMP team, verified by Flathub", details.GetProperty("lines").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Nexus_says_apt_is_not_a_sandbox_and_can_offer_the_isolated_container()
    {
        var (factory, _) = Core();
        using var __ = factory;
        factory.AiHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Calls("apt_install", new { package = "htop" })) }));
        var plain = await Offer(factory.Authorized(), "install htop");
        Assert.Contains("can read your home folder", plain.GetProperty("details").GetProperty("lines")[0].GetString());
        factory.AiHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Calls("apt_install", new { package = "htop", isolated = true })) }));
        var isolated = await Offer(factory.Authorized(), "install htop isolated");
        Assert.Equal("Install htop with apt, in the isolated Ubuntu container", isolated.GetProperty("summary").GetString());
        var confirmed = await (await factory.Authorized().PostAsync($"/actions/{isolated.GetProperty("id").GetString()}/confirm", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(confirmed.GetProperty("perform").GetProperty("isolated").GetBoolean());
    }

    [Fact]
    public async Task Nexus_can_undo_an_install_from_the_journal_after_the_person_says_do_it()
    {
        var (factory, _) = Core();
        using var __ = factory;
        var client = factory.Authorized();
        var id = (await (await client.PostAsJsonAsync("/apps/changes",
            new { source = "flathub", op = "install", id = "org.gimp.GIMP", name = "GIMP", scope = "user" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        factory.AiHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Calls("undo_app_change", new { entry = id })) }));
        var action = await Offer(client, "undo installing gimp");
        Assert.Equal("Undo: Remove GIMP again. Its data and settings stay unless you delete them", action.GetProperty("summary").GetString());
        var confirmed = await (await client.PostAsync($"/actions/{action.GetProperty("id").GetString()}/confirm", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var perform = confirmed.GetProperty("perform");
        Assert.Equal(("flathub.remove", "user", id), (perform.GetProperty("op").GetString(), perform.GetProperty("scope").GetString(),
            perform.GetProperty("undoes").GetInt64()));
    }
}
