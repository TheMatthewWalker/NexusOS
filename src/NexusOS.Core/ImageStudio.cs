using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

// Local image generation (design: "Image generation A · Gallery and
// inspector"). Core starts stable-diffusion.cpp's sd-server on demand, bound
// to loopback, with the image model NexusOS downloaded (nexusos.imageModel);
// before it does, it unloads the chat model, because both share the GPU. The
// server stops again after a few idle minutes, so the next question to Nexus
// gets the GPU back. Images are written to ~/Pictures/Nexus with a small JSON
// file beside each (prompt, seed, size, steps, model); nothing is uploaded.
//   NEXUS_SD_SERVER_BIN   absolute path to sd-server
//   NEXUS_SD_MODEL        absolute path to the model (.safetensors or .gguf)
//   NEXUS_SD_STEPS        default steps (4: SDXL-Turbo needs few)
//   NEXUS_SD_CFG          default guidance scale (1 for turbo models)
public sealed partial class ImageStudio : IDisposable
{
    public const int MaxCount = 4, MaxPromptLength = 1000, MaxGallery = 300;
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _factory;
    private readonly ModelRouter _router;
    private readonly ActivityJournal _journal;
    private readonly ILogger<ImageStudio> _log;
    private readonly Timer _idle;
    private Process? _process;
    private int _port;

    public string? Server { get; }
    public string? Model { get; }
    public int DefaultSteps { get; }
    public double DefaultCfg { get; }
    public string Folder { get; }
    public string ModelName => Model is null ? "" : Path.GetFileNameWithoutExtension(Model);

    public ImageStudio(IHttpClientFactory factory, IConfiguration config, ModelRouter router, ActivityJournal journal, ILogger<ImageStudio> log)
    {
        _factory = factory; _router = router; _journal = journal; _log = log;
        Server = config["NEXUS_SD_SERVER_BIN"];
        Model = config["NEXUS_SD_MODEL"];
        DefaultSteps = int.TryParse(config["NEXUS_SD_STEPS"], out var s) && s is >= 1 and <= 60 ? s : 4;
        DefaultCfg = double.TryParse(config["NEXUS_SD_CFG"], NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 20 ? c : 1.0;
        var pictures = Environment.GetEnvironmentVariable("XDG_PICTURES_DIR");
        Folder = config["NEXUS_IMAGES_DIR"] ?? Path.Combine(string.IsNullOrWhiteSpace(pictures)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures") : pictures, "Nexus");
        _idle = new Timer(_ => Stop(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public object Status() => new
    {
        installed = Server is not null && File.Exists(Server),
        model = Model is not null && File.Exists(Model) ? ModelName : null,
        downloading = Model is not null && File.Exists(Model + ".part"),
        running = _process is { HasExited: false },
        folder = Folder
    };

    public static readonly Dictionary<string, (int W, int H)> Shapes = new()
    {
        ["square"] = (1024, 1024), ["landscape"] = (1344, 768), ["portrait"] = (768, 1344), ["small"] = (512, 512)
    };

    public sealed record Request(string Prompt, string? Negative, string? Shape, int? Count, long? Seed, int? Steps, string? Style);

    public async Task<object> GenerateAsync(Request request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > MaxPromptLength)
            throw new ImageException($"Describe the image in 1-{MaxPromptLength} characters.");
        if (Server is null || !File.Exists(Server)) throw new ImageException("Image generation is not installed on this computer.", 503);
        if (Model is null || !File.Exists(Model))
            throw new ImageException(Model is not null && File.Exists(Model + ".part")
                ? "The image model is still downloading. Try again when it has finished."
                : "No image model yet. It downloads once this computer is online (Settings, Privacy and AI).", 503);
        var (width, height) = Shapes.GetValueOrDefault(request.Shape ?? "square", Shapes["square"]);
        var count = Math.Clamp(request.Count ?? 1, 1, MaxCount);
        var steps = Math.Clamp(request.Steps ?? DefaultSteps, 1, 60);
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(0, int.MaxValue);
        var prompt = Styled(request.Prompt.Trim(), request.Style);

        await _gate.WaitAsync(ct);
        try
        {
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            await EnsureRunningAsync(ct);
            Directory.CreateDirectory(Folder);
            var made = new List<object>();
            // One image at a time: each gets its own seed, and 8 GB of video memory is enough for one.
            for (var i = 0; i < count; i++)
            {
                using var response = await _factory.CreateClient("images").PostAsJsonAsync($"http://127.0.0.1:{_port}/sdapi/v1/txt2img", new
                {
                    prompt, negative_prompt = request.Negative ?? "", width, height, steps, cfg_scale = DefaultCfg,
                    seed = seed + i, batch_size = 1, sampler_name = "euler_a"
                }, ct);
                if (!response.IsSuccessStatusCode)
                    throw new ImageException("The image model could not make that image (" + (int)response.StatusCode + ").", 502);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var b64 = doc.RootElement.GetProperty("images")[0].GetString()!;
                var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{seed + i}.png";
                var path = Path.Combine(Folder, name);
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(b64), ct);
                var info = new ImageInfo(name, path, request.Prompt.Trim(), request.Style, seed + i, width, height, steps, ModelName, DateTimeOffset.Now);
                await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(info), ct);
                made.Add(info);
            }
            await _journal.AppendAsync(new JournalRecord(JournalKinds.App, "nexus",
                Summary($"Made {count} image{(count == 1 ? "" : "s")} on this computer: {request.Prompt.Trim()}")), ct);
            return new { images = made };
        }
        catch (HttpRequestException ex) { throw new ImageException("The image model stopped: " + ex.Message, 502); }
        finally
        {
            _idle.Change(IdleStop, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    public sealed record ImageInfo(string Name, string Path, string Prompt, string? Style, long Seed, int Width, int Height,
        int Steps, string Model, DateTimeOffset Made);

    public IReadOnlyList<ImageInfo> Gallery()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<ImageInfo>();
        foreach (var json in Directory.EnumerateFiles(Folder, "*.png.json").OrderDescending(StringComparer.Ordinal).Take(MaxGallery))
        {
            try
            {
                var info = JsonSerializer.Deserialize<ImageInfo>(File.ReadAllText(json));
                var png = json[..^5];
                if (info is not null && File.Exists(png)) list.Add(info with { Path = png });
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list;
    }

    public void Delete(string name)
    {
        if (!SafeName().IsMatch(name)) throw new ImageException("Not an image made by Nexus.");
        var path = Path.Combine(Folder, name);
        File.Delete(path);
        File.Delete(path + ".json");
    }

    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}-[0-9]+\.png$")]
    private static partial Regex SafeName();

    // A few words that steer the look, as the mock's Style chip does.
    public static string Styled(string prompt, string? style) => style switch
    {
        "photo" => prompt + ", photograph, natural light, detailed",
        "painterly" => prompt + ", painterly, oil painting, visible brush strokes",
        "illustration" => prompt + ", clean digital illustration, flat colours",
        "3d" => prompt + ", 3d render, soft studio lighting",
        _ => prompt
    };

    private async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return;
        // The chat model and the image model share the GPU: free it first.
        try { if (_router.Current.SupportsUnload) await _router.Current.UnloadAsync(ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException) { _log.LogInformation("Chat model not unloaded: {Message}", ex.Message); }
        _port = FreePort();
        var start = new ProcessStartInfo { FileName = Server!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--model", Model!, "--listen-ip", "127.0.0.1", "--listen-port", _port.ToString(CultureInfo.InvariantCulture),
                                  "--diffusion-fa", "--vae-tiling" })
            start.ArgumentList.Add(a);
        var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new ImageException("Could not start the image model: " + ex.Message, 503); }
        process.OutputDataReceived += (_, e) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) _log.LogDebug("sd-server: {Line}", line); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        // Loading the model takes a while the first time (it is read from disk into the GPU).
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new ImageException("The image model stopped while loading (is there enough video memory?).", 503);
            try
            {
                using var probe = await _factory.CreateClient("images").GetAsync($"http://127.0.0.1:{_port}/sdapi/v1/samplers", ct);
                if (probe.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500, ct);
        }
        Stop();
        throw new ImageException("The image model took too long to load.", 504);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch (InvalidOperationException) { }
        p.Dispose();
    }

    private static string Summary(string s) => s.Length <= ActivityJournal.MaxSummaryLength ? s : s[..(ActivityJournal.MaxSummaryLength - 1)] + "…";

    public void Dispose() { _idle.Dispose(); Stop(); }
}

public sealed class ImageException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public static class ImageEndpoints
{
    public static void MapImages(this WebApplication app)
    {
        app.MapGet("/images/status", (ImageStudio studio) => Results.Ok(studio.Status()));
        app.MapGet("/images", (ImageStudio studio) => Results.Ok(new { images = studio.Gallery() }));
        app.MapPost("/images/generate", async (ImageStudio.Request request, ImageStudio studio, InferenceActivity activity, CancellationToken ct) =>
        {
            activity.BeginRequest();
            try { return Results.Ok(await studio.GenerateAsync(request, ct)); }
            catch (ImageException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
            finally { activity.EndRequest(); }
        }).RequireRateLimiting("ai");
        app.MapDelete("/images/{name}", (string name, ImageStudio studio) =>
        {
            try { studio.Delete(name); return Results.NoContent(); }
            catch (ImageException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
    }
}
