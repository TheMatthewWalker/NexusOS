using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// Workspaces that remember (feature 7): Core keeps named records of the apps
// on a workspace; the shell does the relaunching.
public sealed class WorkspacesApiTests
{
    private static object Dissertation(int workspace = 2) => new
    {
        name = "Dissertation", workspace,
        apps = new object[]
        {
            new { appId = "org.mozilla.firefox", desktopId = "firefox", title = "Sources – Mozilla Firefox" },
            new { appId = "libreoffice-writer", desktopId = (string?)null, title = "Chapter 3.odt\u0007" }
        }
    };

    [Fact]
    public async Task SaveListReplaceAndForgetRoundTrip()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/workspaces")).GetProperty("workspaces").GetArrayLength());

        using (var saved = await client.PostAsJsonAsync("/workspaces", Dissertation()))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("replaced").GetBoolean());
            var apps = body.GetProperty("workspace").GetProperty("apps");
            Assert.Equal(2, apps.GetArrayLength());
            // Control characters from window titles are dropped.
            Assert.Equal("Chapter 3.odt", apps[1].GetProperty("title").GetString());
        }
        // Same name in another case replaces it, on another workspace.
        using (var again = await client.PostAsJsonAsync("/workspaces", new { name = "dissertation", workspace = 4, apps = Array.Empty<object>() }))
            Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replaced").GetBoolean());
        var list = (await client.GetFromJsonAsync<JsonElement>("/workspaces")).GetProperty("workspaces");
        var only = Assert.Single(list.EnumerateArray());
        Assert.Equal(4, only.GetProperty("workspace").GetInt32());

        using (var forgotten = await client.DeleteAsync("/workspaces?name=DISSERTATION"))
            Assert.Equal(HttpStatusCode.OK, forgotten.StatusCode);
        using (var missing = await client.DeleteAsync("/workspaces?name=Dissertation"))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("", 1, "firefox")]
    [InlineData("a;dispatch exec rm", 1, "firefox")]
    [InlineData("Work", 0, "firefox")]
    [InlineData("Work", 100, "firefox")]
    [InlineData("Work", 1, "fire fox")]
    [InlineData("Work", 1, "$(touch x)")]
    public async Task RejectsNamesAndIdsThatCouldNotBeUsedSafely(string name, int workspace, string appId)
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/workspaces",
            new { name, workspace, apps = new[] { new { appId, desktopId = (string?)null, title = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/workspaces")).GetProperty("workspaces").GetArrayLength());
    }

    [Fact]
    public async Task KeepsAtMostThirtyWorkspaces()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        for (var i = 0; i < WorkspaceStore.MaxWorkspaces; i++)
            using (var ok = await client.PostAsJsonAsync("/workspaces", new { name = $"Space {i}", workspace = 1, apps = Array.Empty<object>() }))
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var full = await client.PostAsJsonAsync("/workspaces", new { name = "One more", workspace = 1, apps = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
    }

    [Fact]
    public async Task NeedsTheApiToken()
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.CreateClient().GetAsync("/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
