using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Voice control (Settings, Accessibility): push-to-talk and spoken replies,
// both on this computer. The desktop records only while Super+V is held or
// the microphone button is pressed (pw-record, into its private runtime
// folder) and shows that the microphone is open; Core turns the recording
// into text with whisper.cpp and deletes it, and the text goes to Nexus as
// if it had been typed. Spoken replies, when turned on, are read by Piper
// into a WAV file in the same folder for the desktop to play. Nothing here
// listens by itself, and no sound or text leaves the computer.
//   NEXUS_WHISPER_BIN    whisper-cli
//   NEXUS_WHISPER_MODEL  the ggml model (base.en)
//   NEXUS_PIPER_BIN      piper
//   NEXUS_PIPER_VOICE    the voice (.onnx, with its .onnx.json beside it)
//   NEXUS_VOICE_THREADS  processor threads for speech to text (default: up to 4)
public sealed class VoiceControl : IDisposable
{
    public const int MaxRecordingBytes = 8 * 1024 * 1024, MaxSpeechLength = 3000;
    public const string SpeechFile = "speech.wav";
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly ILogger<VoiceControl> _log;
    private readonly SemaphoreSlim _listen = new(1, 1), _speak = new(1, 1);
    private readonly ConcurrentDictionary<int, Process> _running = new();

    public string? WhisperBin { get; }
    public string? WhisperModel { get; }
    public string? PiperBin { get; }
    public string? PiperVoice { get; }
    public int Threads { get; }
    public string? CaptureDir { get; }

    public VoiceControl(IConfiguration config, ILogger<VoiceControl> log)
    {
        _log = log;
        WhisperBin = Configured(config["NEXUS_WHISPER_BIN"]);
        WhisperModel = Configured(config["NEXUS_WHISPER_MODEL"]);
        PiperBin = Configured(config["NEXUS_PIPER_BIN"]);
        PiperVoice = Configured(config["NEXUS_PIPER_VOICE"]);
        Threads = int.TryParse(config["NEXUS_VOICE_THREADS"], out var t) && t is >= 1 and <= 64 ? t : Math.Clamp(Environment.ProcessorCount, 1, 4);
        CaptureDir = CaptureFolder.Resolve(config);
    }

    private static string? Configured(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (bool Installed, bool Ready, bool Downloading) Parts(string? bin, string? model) =>
        (bin is not null && File.Exists(bin), model is not null && File.Exists(model), model is not null && File.Exists(model + ".part"));

    public object Status()
    {
        var listen = Parts(WhisperBin, WhisperModel);
        var speak = Parts(PiperBin, PiperVoice);
        return new
        {
            listen = new { installed = listen.Installed, ready = listen.Installed && listen.Ready, downloading = listen.Downloading },
            speak = new { installed = speak.Installed, ready = speak.Installed && speak.Ready && File.Exists(PiperVoice + ".json"),
                          downloading = speak.Downloading },
            local = true
        };
    }

    // audio: a WAV file name in the capture folder, such as "voice.wav". It is deleted afterwards.
    public sealed record Recording(string? Audio);

    public async Task<object> TranscribeAsync(Recording request, CancellationToken ct)
    {
        var path = CaptureFolder.PathFor(CaptureDir, request.Audio, ".wav");
        try
        {
            var (installed, ready, downloading) = Parts(WhisperBin, WhisperModel);
            if (!installed) throw new CaptureException("Speech to text is not installed on this computer.", 503);
            if (!ready)
                throw new CaptureException(downloading ? "The speech model is still downloading. Try again when it has finished."
                    : "No speech model yet. It downloads once this computer is online.", 503);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) throw new CaptureException("That recording is not in the capture folder.", 404);
            if (info.Length > MaxRecordingBytes) throw new CaptureException("The recording is too long. Hold the key for under a minute.");
            if (!IsWav(path)) throw new CaptureException("The recording is not a WAV file.");
            if (!await _listen.WaitAsync(TimeSpan.Zero, ct)) throw new CaptureException("Still turning the last recording into text.", 429);
            try
            {
                var (code, output, error) = await RunAsync(WhisperBin!,
                    ["-m", WhisperModel!, "-f", path, "-l", "en", "-nt", "-np", "-t", Threads.ToString(CultureInfo.InvariantCulture)], null, ct);
                if (code != 0)
                {
                    _log.LogInformation("whisper-cli exited with {Code}: {Error}", code, Last(error));
                    throw new CaptureException("Speech to text failed. Try again.", 502);
                }
                var text = Transcript(output);
                return new { text, heard = text.Length > 0 };
            }
            finally { _listen.Release(); }
        }
        finally { CaptureFolder.TryDelete(path); }
    }

    // whisper prints the text, with markers for silence and sounds in brackets: keep the words.
    public static string Transcript(string output)
    {
        var text = Regex.Replace(output, @"\[[^\]]{0,40}\]|\([^)]{0,40}\)", " ");
        text = new string(text.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 2000 ? text : text[..2000];
    }

    private static bool IsWav(string path)
    {
        var head = new byte[12];
        using var file = File.OpenRead(path);
        return file.Read(head, 0, 12) == 12 && Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "WAVE";
    }

    public sealed record Speech(string? Text);

    // Reads text aloud into speech.wav in the capture folder; the desktop plays and deletes it.
    public async Task<object> SpeakAsync(Speech request, CancellationToken ct)
    {
        var text = SpeakableText(request.Text ?? "");
        if (text.Length == 0) throw new CaptureException("Nothing to say.");
        var (installed, ready, downloading) = Parts(PiperBin, PiperVoice);
        if (!installed) throw new CaptureException("Spoken replies are not installed on this computer.", 503);
        if (!ready || !File.Exists(PiperVoice + ".json"))
            throw new CaptureException(downloading ? "The voice is still downloading." : "No voice yet. It downloads once this computer is online.", 503);
        var output = CaptureFolder.PathFor(CaptureDir, SpeechFile, ".wav");
        await _speak.WaitAsync(ct);
        try
        {
            CaptureFolder.TryDelete(output);
            var (code, _, error) = await RunAsync(PiperBin!, ["-m", PiperVoice!, "-c", PiperVoice + ".json", "-f", output], text, ct);
            if (code != 0 || !File.Exists(output))
            {
                _log.LogInformation("piper exited with {Code}: {Error}", code, Last(error));
                CaptureFolder.TryDelete(output);
                throw new CaptureException("The voice could not read that out.", 502);
            }
            return new { audio = SpeechFile, path = output };
        }
        finally { _speak.Release(); }
    }

    // Piper reads one line per sentence group; Markdown marks and links are not read out.
    public static string SpeakableText(string value)
    {
        var text = new string(value.Select(ch => ch == '\n' ? '\n' : char.IsControl(ch) ? ' ' : ch).ToArray());
        text = Regex.Replace(text, @"https?://\S+", "a link");
        text = Regex.Replace(text, @"[*_`#>|]+", " ");
        var lines = text.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 0);
        text = string.Join("\n", lines);
        return text.Length <= MaxSpeechLength ? text : text[..MaxSpeechLength];
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(string file, string[] args, string? input, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = file, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new CaptureException("Could not start " + Path.GetFileName(file) + ": " + ex.Message, 503); }
        _running[process.Id] = process;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), ct);
                process.StandardInput.Close();
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                Kill(process);
                if (ct.IsCancellationRequested) throw;
                throw new CaptureException(Path.GetFileName(file) + " took too long.", 504);
            }
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { _running.TryRemove(process.Id, out _); }
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
    }

    private static string Last(string error) => error.Trim().Split('\n').LastOrDefault() ?? "";

    // Game mode: nothing of voice stays running.
    public void Stop()
    {
        foreach (var p in _running.Values) Kill(p);
    }

    public void Dispose() => Stop();
}

public static class VoiceEndpoints
{
    public static void MapVoice(this WebApplication app)
    {
        app.MapGet("/voice/status", (VoiceControl voice) => Results.Ok(voice.Status()));
        app.MapPost("/voice/transcribe", async (VoiceControl.Recording request, VoiceControl voice, CancellationToken ct) =>
        {
            try { return Results.Ok(await voice.TranscribeAsync(request, ct)); }
            catch (CaptureException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        }).RequireRateLimiting("ai");
        app.MapPost("/voice/speak", async (VoiceControl.Speech request, VoiceControl voice, CancellationToken ct) =>
        {
            try { return Results.Ok(await voice.SpeakAsync(request, ct)); }
            catch (CaptureException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        }).RequireRateLimiting("ai");
    }
}
