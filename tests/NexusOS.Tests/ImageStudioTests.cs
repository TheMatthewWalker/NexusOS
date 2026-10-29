using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// Local image generation (ImageStudio.cs): clear answers when it cannot run,
// a gallery read from ~/Pictures/Nexus, and deletes limited to its own files.
public sealed class ImageStudioTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-images-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Without_the_model_it_says_so_instead_of_failing()
    {
        using var factory = new CoreApiFactory(("NEXUS_SD_SERVER_BIN", "/bin/true"), ("NEXUS_SD_MODEL", Path.Combine(_dir, "missing.safetensors")),
            ("NEXUS_IMAGES_DIR", _dir));
        var client = factory.Authorized();
        var status = await client.GetFromJsonAsync<JsonElement>("/images/status");
        Assert.True(status.GetProperty("installed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("model").ValueKind);
        using var response = await client.PostAsJsonAsync("/images/generate", new { prompt = "a lighthouse" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("No image model yet", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_partial_download_reads_as_downloading()
    {
        File.WriteAllText(Path.Combine(_dir, "m.safetensors.part"), "x");
        using var factory = new CoreApiFactory(("NEXUS_SD_SERVER_BIN", "/bin/true"), ("NEXUS_SD_MODEL", Path.Combine(_dir, "m.safetensors")),
            ("NEXUS_IMAGES_DIR", _dir));
        var client = factory.Authorized();
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/images/status")).GetProperty("downloading").GetBoolean());
        using var response = await client.PostAsJsonAsync("/images/generate", new { prompt = "a lighthouse" });
        Assert.Contains("still downloading", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_gallery_lists_its_images_newest_first_and_deletes_only_its_own()
    {
        foreach (var (name, prompt) in new[] { ("20261001-120000-1.png", "older"), ("20261002-120000-2.png", "newer") })
        {
            File.WriteAllBytes(Path.Combine(_dir, name), [0x89, 0x50]);
            File.WriteAllText(Path.Combine(_dir, name + ".json"), JsonSerializer.Serialize(
                new ImageStudio.ImageInfo(name, "", prompt, null, 1, 512, 512, 4, "m", DateTimeOffset.Now)));
        }
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "keep");
        using var factory = new CoreApiFactory(("NEXUS_IMAGES_DIR", _dir));
        var client = factory.Authorized();
        var images = (await client.GetFromJsonAsync<JsonElement>("/images")).GetProperty("images").EnumerateArray().ToList();
        Assert.Equal(["newer", "older"], images.Select(i => i.GetProperty("prompt").GetString()));
        Assert.Equal(Path.Combine(_dir, "20261002-120000-2.png"), images[0].GetProperty("path").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/images/notes.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/images/..%2Fescape.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/images/20261001-120000-1.png")).StatusCode);
        Assert.False(File.Exists(Path.Combine(_dir, "20261001-120000-1.png.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
    }

    [Theory]
    [InlineData("", 400)]
    [InlineData(null, 400)]
    public async Task An_empty_description_is_refused(string? prompt, int status)
    {
        using var factory = new CoreApiFactory(("NEXUS_IMAGES_DIR", _dir));
        using var response = await factory.Authorized().PostAsJsonAsync("/images/generate", new { prompt });
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public void Styles_add_a_few_words_and_none_leaves_the_prompt_alone()
    {
        Assert.Equal("a cat", ImageStudio.Styled("a cat", null));
        Assert.StartsWith("a cat, painterly", ImageStudio.Styled("a cat", "painterly"));
    }
}
