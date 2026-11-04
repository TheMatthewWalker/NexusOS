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
// Pictures are kept for a while (ImageSettings: 90 days by default, and
// optionally up to so many GB), then removed oldest first; a starred picture,
// or one moved out of the folder, is never removed. The model's download
// (nexus_image_model.py, a root service) reports how it is going in
// download.json beside the model, which Status() passes on.
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
    private readonly Timer _tidy;
    private readonly string _settingsPath;
    private readonly string? _downloadState;
    private readonly TimeProvider _time;
    private Process? _process;
    private int _port;

    public string? Server { get; }
    public string? Model { get; }
    public int DefaultSteps { get; }
    public double DefaultCfg { get; }
    public string Folder { get; }
    public string ModelName => Model is null ? "" : Path.GetFileNameWithoutExtension(Model);

    public ImageStudio(IHttpClientFactory factory, IConfiguration config, ModelRouter router, ActivityJournal journal, ILogger<ImageStudio> log,
        TimeProvider? time = null)
    {
        _factory = factory; _router = router; _journal = journal; _log = log; _time = time ?? TimeProvider.System;
        Server = config["NEXUS_SD_SERVER_BIN"];
        Model = config["NEXUS_SD_MODEL"];
        DefaultSteps = int.TryParse(config["NEXUS_SD_STEPS"], out var s) && s is >= 1 and <= 60 ? s : 4;
        DefaultCfg = double.TryParse(config["NEXUS_SD_CFG"], NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 20 ? c : 1.0;
        var pictures = Environment.GetEnvironmentVariable("XDG_PICTURES_DIR");
        Folder = config["NEXUS_IMAGES_DIR"] ?? Path.Combine(string.IsNullOrWhiteSpace(pictures)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures") : pictures, "Nexus");
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _settingsPath = config["NEXUS_IMAGES_SETTINGS"] ?? Path.Combine(configHome, "nexusos", "images.json");
        _downloadState = config["NEXUS_SD_DOWNLOAD_STATE"] ?? (Model is null ? null : Path.Combine(Path.GetDirectoryName(Model) ?? "/", "download.json"));
        _idle = new Timer(_ => Stop(), null, Timeout.Infinite, Timeout.Infinite);
        // Old pictures are tidied a little after start and then daily.
        _tidy = new Timer(_ => _ = TidyAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
    }

    public object Status() => new
    {
        installed = Server is not null && File.Exists(Server),
        model = Model is not null && File.Exists(Model) ? ModelName : null,
        downloading = Model is not null && File.Exists(Model + ".part"),
        download = Download(),
        running = _process is { HasExited: false },
        folder = Folder
    };

    // What the download service last wrote (state, a plain message, when it tries
    // again), with the progress read from the partial file. Only known fields
    // of simple types are passed on.
    public const long MaxStateBytes = 16384;
    public static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(10);
    public object? Download()
    {
        if (_downloadState is null || Model is null) return null;
        try
        {
            if (new FileInfo(_downloadState) is not { Exists: true, Length: <= MaxStateBytes }) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_downloadState));
            var root = doc.RootElement;
            string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? NexusActions.Clean(v.GetString()!, 300) : null;
            long? Number(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
            var part = new FileInfo(Model + ".part");
            var bytes = part.Exists ? part.Length : File.Exists(Model) ? new FileInfo(Model).Length : 0;
            var total = Number("total");
            var state = Text("state") ?? "waiting";
            var message = Text("message") ?? "";
            // A download that stopped with the computer (a restart) resumes on its own.
            if (state == "downloading" && (!part.Exists || _time.GetUtcNow() - part.LastWriteTimeUtc > StalledAfter))
            {
                state = "waiting";
                message = "The download is paused. It resumes by itself in a few minutes.";
            }
            var next = Number("nextTry") is > 0 and var n ? DateTimeOffset.FromUnixTimeSeconds(n.Value) : (DateTimeOffset?)null;
            return new
            {
                state, reason = Text("reason") ?? "", message, bytes, total,
                percent = total is > 0 ? Math.Clamp((int)(bytes * 100 / total.Value), 0, 100) : (int?)null,
                nextTry = next is { } t && t > _time.GetUtcNow() ? t : (DateTimeOffset?)null,
                free = Number("free"), needed = Number("needed")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

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
                var info = new ImageInfo(name, path, request.Prompt.Trim(), request.Style, seed + i, width, height, steps, ModelName, _time.GetLocalNow());
                await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(info), ct);
                made.Add(info);
            }
            await _journal.AppendAsync(new JournalRecord(JournalKinds.App, "nexus",
                Summary($"Made {count} image{(count == 1 ? "" : "s")} on this computer: {request.Prompt.Trim()}")), ct);
            _ = TidyAsync();
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
        int Steps, string Model, DateTimeOffset Made, bool Starred = false);

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

    // A starred picture is kept however old it is.
    public ImageInfo Star(string name, bool starred)
    {
        if (!SafeName().IsMatch(name)) throw new ImageException("Not an image made by Nexus.");
        var path = Path.Combine(Folder, name);
        if (Read(path + ".json") is not { } info || !File.Exists(path)) throw new ImageException("No such image.", 404);
        info = info with { Path = path, Starred = starred };
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(info));
        return info;
    }

    private static ImageInfo? Read(string json)
    {
        try
        {
            return new FileInfo(json) is { Exists: true, Length: <= MaxStateBytes } && !new FileInfo(json).Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? JsonSerializer.Deserialize<ImageInfo>(File.ReadAllText(json)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- keeping pictures for a while ----
    // KeepDays: pictures older than this go (1-3650). KeepGb: if set, the
    // oldest go until the folder's Nexus pictures fit (starred ones count, but stay).
    public sealed record ImageSettings(int KeepDays = ImageSettings.DefaultKeepDays, double? KeepGb = null)
    {
        public const int DefaultKeepDays = 90, MaxKeepDays = 3650;
        public const double MaxKeepGb = 10000;
    }

    public ImageSettings Settings()
    {
        try
        {
            if (new FileInfo(_settingsPath) is { Exists: true, Length: <= MaxStateBytes } &&
                JsonSerializer.Deserialize<ImageSettings>(File.ReadAllText(_settingsPath), Json) is { } saved)
                return Checked(saved.KeepDays, saved.KeepGb);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ImageException) { }
        return new ImageSettings();
    }

    public static ImageSettings Checked(int keepDays, double? keepGb)
    {
        if (keepDays is < 1 or > ImageSettings.MaxKeepDays) throw new ImageException($"Keep pictures for 1-{ImageSettings.MaxKeepDays} days.");
        if (keepGb is { } gb && (double.IsNaN(gb) || gb <= 0 || gb > ImageSettings.MaxKeepGb))
            throw new ImageException($"Keep up to 0.1-{ImageSettings.MaxKeepGb} GB, or no size limit.");
        return new ImageSettings(keepDays, keepGb is { } g ? Math.Round(g, 1) : null);
    }

    public ImageSettings SaveSettings(int keepDays, double? keepGb)
    {
        var settings = Checked(keepDays, keepGb);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporary = _settingsPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
        File.Move(temporary, _settingsPath, true);
        return settings;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Kept(string Name, string Path, long Bytes, DateTimeOffset Made, bool Starred);

    // Only pictures Nexus made (its file names, with the JSON beside them) directly in the folder.
    private List<Kept> Pictures()
    {
        var list = new List<Kept>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var png in Directory.EnumerateFiles(Folder, "*.png"))
        {
            var name = Path.GetFileName(png);
            if (!SafeName().IsMatch(name)) continue;
            var file = new FileInfo(png);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || Read(png + ".json") is not { } info) continue;
            list.Add(new Kept(name, png, file.Length + new FileInfo(png + ".json").Length, info.Made, info.Starred));
        }
        return list;
    }

    // Oldest first: past the age limit, then, while the total is over the size limit.
    private List<Kept> Due(List<Kept> all, ImageSettings settings)
    {
        var cutoff = _time.GetUtcNow() - TimeSpan.FromDays(settings.KeepDays);
        var due = new List<Kept>();
        var total = all.Sum(p => p.Bytes);
        foreach (var p in all.Where(p => !p.Starred).OrderBy(p => p.Made).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var overSize = settings.KeepGb is { } gb && total > (long)(gb * 1e9);
            if (p.Made >= cutoff && !overSize) break;
            due.Add(p);
            total -= p.Bytes;
        }
        return due;
    }

    public object Usage()
    {
        var all = Pictures();
        var settings = Settings();
        var due = Due(all, settings);
        return new
        {
            folder = Folder, count = all.Count, bytes = all.Sum(p => p.Bytes), starred = all.Count(p => p.Starred),
            oldest = all.Count == 0 ? (DateTimeOffset?)null : all.Min(p => p.Made),
            keepDays = settings.KeepDays, keepGb = settings.KeepGb,
            due = new { count = due.Count, bytes = due.Sum(p => p.Bytes) }
        };
    }

    public sealed record CleanupResult(int Removed, long FreedBytes);

    private readonly SemaphoreSlim _tidyGate = new(1, 1);
    public async Task<CleanupResult> CleanupAsync(CancellationToken ct)
    {
        await _tidyGate.WaitAsync(ct);
        try
        {
            var removed = 0;
            long freed = 0;
            foreach (var p in Due(Pictures(), Settings()))
            {
                try { File.Delete(p.Path); File.Delete(p.Path + ".json"); removed++; freed += p.Bytes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogInformation("Kept {Name}: {Message}", p.Name, ex.Message); }
            }
            if (removed > 0)
                await _journal.AppendAsync(new JournalRecord(JournalKinds.App, "nexus",
                    Summary($"Removed {removed} old picture{(removed == 1 ? "" : "s")} from {Folder} ({freed / 1e6:0.#} MB)")), ct);
            return new CleanupResult(removed, freed);
        }
        finally { _tidyGate.Release(); }
    }

    private async Task TidyAsync()
    {
        try { await CleanupAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogInformation("Pictures not tidied: {Message}", ex.Message); }
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

    public void Dispose() { _idle.Dispose(); _tidy.Dispose(); Stop(); }
}

public sealed record StarRequest(bool Starred);

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
        app.MapPost("/images/{name}/star", (string name, StarRequest request, ImageStudio studio) =>
        {
            try { return Results.Ok(studio.Star(name, request.Starred)); }
            catch (ImageException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
        // How much room the pictures take, what the settings would remove; and the clean-up itself.
        app.MapGet("/images/usage", (ImageStudio studio) => Results.Ok(studio.Usage()));
        app.MapPost("/images/cleanup", async (ImageStudio studio, CancellationToken ct) => Results.Ok(await studio.CleanupAsync(ct)));
        app.MapGet("/images/settings", (ImageStudio studio) => Results.Ok(studio.Settings()));
        app.MapPost("/images/settings", (ImageStudio.ImageSettings request, ImageStudio studio) =>
        {
            try { return Results.Ok(studio.SaveSettings(request.KeepDays, request.KeepGb)); }
            catch (ImageException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
    }
}
