using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

// The very large model: colibri (https://github.com/JustVugg/colibri,
// Apache-2.0) runs mixture-of-experts models bigger than the computer's
// memory. The part every word uses stays in memory and the experts are read
// from the disk when the model asks for them, so a 16 GB laptop can run a
// 35B model comfortably, and a 284B one slowly.
//
// Nothing is downloaded until the person turns it on (Settings, Privacy and
// AI, or Nexus's use_very_large_model card): the models are 19 GB to hundreds
// of GB. The download runs as the person, into their own folder (no system
// change, nothing for Guardian), through nexus-colibri-model, which uses
// colibri's own resumable, checksummed downloader. It goes on after a restart
// until the model is whole (ColibriKeeper).
//
// Once downloaded and chosen, Core starts colibri's server on demand (loopback,
// argument list, never a shell) and talks to it like the llama.cpp server
// (LlamaCppProvider.Colibri). One model in memory at a time: the others are
// unloaded first. Until it can answer (downloading, not set up, this
// processor too old, failed to start), the standard model answers.
public sealed class Colibri
{
    private readonly IConfiguration _config;
    private readonly ILogger<Colibri>? _log;
    private readonly object _gate = new();
    private Process? _download;
    private (DateTimeOffset At, string Reason)? _failure;
    private (DateTimeOffset At, JsonElement Rows)? _offered;
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);
    public const string DefaultModel = "qwen36-35b";

    public string? Bin { get; }
    public string? Fetch { get; }
    public string Dir { get; }
    public int Port { get; }
    public bool Configured { get; }

    public Colibri(IConfiguration config, ILogger<Colibri>? log = null)
    {
        _config = config;
        _log = log;
        Bin = config["NEXUS_COLIBRI_BIN"];
        Fetch = config["NEXUS_COLIBRI_FETCH"];
        var data = config["XDG_DATA_HOME"] is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        Dir = config["NEXUS_COLIBRI_DIR"] is { Length: > 0 } d ? d : Path.Combine(data, "nexusos", "colibri");
        Port = int.TryParse(config["NEXUS_COLIBRI_PORT"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1024 and <= 65535 ? p : 18436;
        Configured = Usable(Bin) && Usable(Fetch);
    }

    private static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);

    // colibri is built for x86-64-v3: AVX2, FMA and BMI2 (Intel 2013 on, AMD 2015 on).
    public bool CpuSupported()
    {
        if (_config["NEXUS_COLIBRI_CPU_OK"] is { } forced) return forced == "1";
        try
        {
            var flags = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("flags", StringComparison.Ordinal));
            if (flags is null) return false;
            var set = flags[(flags.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            return set.Contains("avx2") && set.Contains("fma") && set.Contains("bmi2");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // The person's choice: which model, and whether they want it (download and keep it).
    private sealed record Choice(string Model, bool Wanted);
    private string ChoiceFile => Path.Combine(Dir, "choice.json");

    private Choice ReadChoice()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ChoiceFile));
            var model = doc.RootElement.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } s ? s : DefaultModelFor();
            return new(model, doc.RootElement.TryGetProperty("wanted", out var w) && w.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(DefaultModelFor(), false);
        }
    }

    private string DefaultModelFor() => _config["NEXUS_COLIBRI_MODEL"] is { Length: > 0 } m ? m : DefaultModel;

    private void WriteChoice(Choice choice)
    {
        Directory.CreateDirectory(Dir);
        var tmp = ChoiceFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { model = choice.Model, wanted = choice.Wanted }));
        File.Move(tmp, ChoiceFile, overwrite: true);
    }

    public string Model => ReadChoice().Model;
    public bool Wanted => ReadChoice().Wanted;
    public string ModelDir => Path.Combine(Dir, Model);

    // colibri's downloader marks the folder complete in its manifest once every file checked out.
    public bool Downloaded
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, ".colibri-download.json")));
                return doc.RootElement.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
        }
    }

    public bool Downloading { get { lock (_gate) return _download is { HasExited: false }; } }

    // What nexus-colibri-model last wrote (state, message, done, total, percent).
    public object? Status()
    {
        var file = Path.Combine(Dir, Model + ".status.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long Number(string key) => root.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;
            var state = Text("state");
            // A download that was going when Core stopped is not going now.
            if (state is "downloading" or "checking" && !Downloading) state = "paused";
            return new { state, message = Text("message"), done = Number("done"), total = Number("total"), percent = (int)Math.Clamp(Number("percent"), 0, 100) };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // The models on offer and whether each fits this computer (nexus-colibri-model --list), kept a minute.
    public async Task<JsonElement?> OfferedAsync(CancellationToken ct)
    {
        if (!Configured) return null;
        lock (_gate) if (_offered is { } o && DateTimeOffset.UtcNow - o.At < TimeSpan.FromMinutes(1)) return o.Rows;
        var start = new ProcessStartInfo { FileName = Fetch!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--dir", Dir, "--list" }) start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(output);
            var rows = doc.RootElement.Clone();
            lock (_gate) _offered = (DateTimeOffset.UtcNow, rows);
            return rows;
        }
        catch (Exception ex) when (ex is Win32Exception or JsonException or OperationCanceledException or InvalidOperationException) { return null; }
    }

    // Turn it on: remember the choice and start (or go on with) the download.
    public void Want(string? model)
    {
        var choice = ReadChoice();
        var next = new Choice(string.IsNullOrWhiteSpace(model) ? choice.Model : model.Trim(), true);
        if (next.Model != choice.Model) StopDownload();
        WriteChoice(next);
        StartDownload();
    }

    // Turn it off: the download stops (what is there stays, to go on from later).
    public void Unwant()
    {
        WriteChoice(ReadChoice() with { Wanted = false });
        StopDownload();
    }

    // Deletes the downloaded model (Settings, "Remove the download").
    public void Remove()
    {
        Unwant();
        var dir = ModelDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        File.Delete(Path.Combine(Dir, Model + ".status.json"));
    }

    public void StartDownload()
    {
        if (!Configured || Downloaded) return;
        lock (_gate)
        {
            if (_download is { HasExited: false }) return;
            _download?.Dispose();
            Directory.CreateDirectory(Dir);
            var start = new ProcessStartInfo { FileName = Fetch!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--dir", Dir, "--model", Model }) start.ArgumentList.Add(a);
            try
            {
                var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.Start();
                process.OutputDataReceived += static (_, _) => { };
                process.ErrorDataReceived += static (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _download = process;
                _log?.LogInformation("Downloading the very large model {Model}", Model);
            }
            catch (Win32Exception ex) { _log?.LogWarning(ex, "The very large model's download could not start"); }
        }
    }

    public void StopDownload()
    {
        lock (_gate)
        {
            if (_download is { HasExited: false } p)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.WaitForExit(5000);
            }
            _download?.Dispose();
            _download = null;
        }
    }

    public void Failed(string reason)
    {
        lock (_gate) _failure = (DateTimeOffset.UtcNow, reason);
        _log?.LogWarning("The very large model could not be used: {Reason}", reason);
    }
    public void Started() { lock (_gate) _failure = null; }

    // Why it cannot answer right now; null when it can. `running`: its server is already up.
    public string? Blocked(bool running)
    {
        if (!Configured) return "This computer has no very large model set up.";
        if (!CpuSupported()) return "This processor is too old for the very large model (it needs AVX2, from about 2013 on).";
        if (!Downloaded) return Downloading ? "The very large model is still downloading." : "The very large model is not downloaded yet.";
        if (running) return null;
        lock (_gate)
            if (_failure is { } f && DateTimeOffset.UtcNow - f.At < FailureCooldown) return f.Reason;
        return null;
    }
}

// colibri's server (`coli serve`), started on demand and stopped to free memory.
public sealed class ColibriServer(Colibri colibri, IHttpClientFactory factory, IConfiguration config) : IManagedModelServer, IDisposable
{
    public const string ModelId = "nexus-colibri";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(
        int.TryParse(config["NEXUS_COLIBRI_START_TIMEOUT_SECONDS"], NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 5 and <= 1800 ? s : 600);
    private Process? _process;

    public bool IsConfigured => colibri.Configured;
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }
    public bool ToolCalling => true;
    public string Endpoint => $"http://127.0.0.1:{colibri.Port}";
    public string HealthPath => "/v1/models";

    public IReadOnlyList<string> Arguments() =>
        ["serve", "--model", colibri.ModelDir, "--host", "127.0.0.1", "--port", colibri.Port.ToString(CultureInfo.InvariantCulture),
         "--model-id", ModelId];

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false }) return;
            _process?.Dispose();
            _process = null;
            if (colibri.Blocked(false) is { } blocked)
            {
                colibri.Failed(blocked);
                throw new ProviderException(blocked + " Nexus is using the standard model for now.", 503);
            }
            var start = new ProcessStartInfo
            {
                FileName = colibri.Bin!, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            foreach (var a in Arguments()) start.ArgumentList.Add(a);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try { process.Start(); }
            catch (Win32Exception)
            {
                process.Dispose();
                colibri.Failed("The very large model could not be started.");
                throw new ProviderException("The very large model could not be started.", 503);
            }
            // Drained, never kept: the server's log can echo what was asked.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            var client = factory.CreateClient("ai");
            var deadline = DateTime.UtcNow + _startTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    _process = null;
                    process.Dispose();
                    colibri.Failed("The very large model stopped while starting (too little memory, or the download is damaged).");
                    throw new ProviderException("The very large model stopped while starting. Nexus is using the standard model for now.", 503);
                }
                if (await ProviderHttp.ProbeAsync(client, Endpoint + HealthPath, ct)) { colibri.Started(); return; }
                await Task.Delay(500, ct);
            }
            Kill(process);
            _process = null;
            colibri.Failed("The very large model took too long to start.");
            throw new ProviderException("The very large model took too long to start.", 504);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is null) return;
            Kill(_process);
            await _process.WaitForExitAsync(ct);
            _process.Dispose();
            _process = null;
        }
        finally { _gate.Release(); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    public void Dispose()
    {
        if (_process is not null) { Kill(_process); _process.WaitForExit(5000); _process.Dispose(); }
        _gate.Dispose();
    }
}

// Goes on with a wanted download after Core restarts, and after the network
// comes back: every 15 minutes, while the model is wanted and not whole.
public sealed class ColibriKeeper(Colibri colibri) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!colibri.Configured) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (colibri.Wanted && !colibri.Downloaded) colibri.StartDownload();
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { colibri.StopDownload(); }
    }
}

public sealed record ColibriWant(bool On, string? Model = null);
public sealed record ColibriUse(bool On);

public static class ColibriEndpoints
{
    public static async Task<object> ViewAsync(Colibri colibri, ProviderStore store, ColibriServer server, CancellationToken ct) => new
    {
        configured = colibri.Configured, cpuSupported = colibri.Configured && colibri.CpuSupported(),
        model = colibri.Model, wanted = colibri.Wanted, downloaded = colibri.Downloaded, downloading = colibri.Downloading,
        selected = store.Selected == ProviderNames.Colibri, running = server.IsRunning,
        blocked = colibri.Configured ? colibri.Blocked(server.IsRunning) : null,
        download = colibri.Status(), models = await colibri.OfferedAsync(ct)
    };

    // Use it (or stop): the person's choice, from Settings or Nexus's card.
    public static async Task<string?> UseAsync(bool on, Colibri colibri, ProviderStore store, ColibriServer server,
        IEnumerable<IModelProvider> providers, ActivityJournal journal, CancellationToken ct)
    {
        if (!colibri.Configured) return "This computer has no very large model set up.";
        if (on && colibri.Blocked(false) is { } blocked) return blocked;
        store.Select(on ? ProviderNames.Colibri : ProviderNames.Local);
        try
        {
            if (on)
            {
                // One model in memory at a time.
                foreach (var p in providers.Where(p => p.Name is ProviderNames.Local or ProviderNames.LlamaCpp && p.SupportsUnload))
                    try { await p.UnloadAsync(ct); } catch (ProviderException) { }
            }
            else await server.StopAsync(ct);
        }
        catch (HttpRequestException) { }
        await journal.AppendAsync(new JournalRecord(JournalKinds.Setting, "user",
            on ? $"Nexus answers with the very large model ({colibri.Model})" : "Nexus answers with the standard model", Ref: "model:colibri"), ct);
        return null;
    }

    public static void MapColibri(this WebApplication app)
    {
        app.MapGet("/model/colibri", async (Colibri colibri, ProviderStore store, ColibriServer server, CancellationToken ct) =>
            Results.Ok(await ViewAsync(colibri, store, server, ct)));
        // Download it (on) or stop the download (off); a model id switches to another one.
        app.MapPost("/model/colibri/want", async (ColibriWant request, Colibri colibri, ProviderStore store, ColibriServer server,
            ActivityJournal journal, CancellationToken ct) =>
        {
            if (!colibri.Configured) return Results.Problem("This computer has no very large model set up.", statusCode: 409);
            if (request.On && !colibri.CpuSupported()) return Results.Problem(colibri.Blocked(false), statusCode: 409);
            if (request.Model is { } m && !System.Text.RegularExpressions.Regex.IsMatch(m, "^[a-z0-9][a-z0-9.-]{0,63}$"))
                return Results.BadRequest(new { error = "Unknown model." });
            if (request.On) colibri.Want(request.Model);
            else
            {
                colibri.Unwant();
                if (store.Selected == ProviderNames.Colibri) { store.Select(ProviderNames.Local); await server.StopAsync(ct); }
            }
            await journal.AppendAsync(new JournalRecord(JournalKinds.Setting, "user",
                request.On ? $"The very large model ({colibri.Model}) is downloading" : "The very large model's download stopped", Ref: "model:colibri"), ct);
            return Results.Ok(await ViewAsync(colibri, store, server, ct));
        }).RequireRateLimiting("journal");
        app.MapPost("/model/colibri/use", async (ColibriUse request, Colibri colibri, ProviderStore store, ColibriServer server,
            IEnumerable<IModelProvider> providers, ActivityJournal journal, CancellationToken ct) =>
        {
            if (await UseAsync(request.On, colibri, store, server, providers, journal, ct) is { } refused)
                return Results.Problem(refused, statusCode: 409);
            return Results.Ok(await ViewAsync(colibri, store, server, ct));
        }).RequireRateLimiting("journal");
        app.MapDelete("/model/colibri", async (Colibri colibri, ProviderStore store, ColibriServer server, ActivityJournal journal, CancellationToken ct) =>
        {
            if (store.Selected == ProviderNames.Colibri) store.Select(ProviderNames.Local);
            await server.StopAsync(ct);
            colibri.Remove();
            await journal.AppendAsync(new JournalRecord(JournalKinds.Setting, "user", "The very large model was removed", Ref: "model:colibri"), ct);
            return Results.Ok(await ViewAsync(colibri, store, server, ct));
        }).RequireRateLimiting("journal");
    }
}
