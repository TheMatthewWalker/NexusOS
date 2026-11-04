using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

// Describe this and voice control (Vision.cs, Voice.cs): pictures and
// recordings are read only from the desktop's private capture folder, sent
// only to the local Ollama or the local whisper/piper programs, and deleted.
// whisper-cli and piper are stand-in scripts here, as Ollama is a stub.
public sealed class VisionVoiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-vv-").FullName;
    private readonly string _captures;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public VisionVoiceTests()
    {
        _captures = Path.Combine(_dir, "nexus");
        Directory.CreateDirectory(_captures);
        File.SetUnixFileMode(_captures, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private CoreApiFactory Factory(HttpMessageHandler? ollama = null, params (string, string?)[] extra)
    {
        var settings = new List<(string, string?)> { ("NEXUS_CAPTURE_DIR", _captures), ("NEXUS_VISION_MODEL", "qwen2.5vl:3b") };
        settings.AddRange(extra);
        var factory = new CoreApiFactory(settings.ToArray());
        if (ollama is not null) factory.AiHandler = ollama;
        return factory;
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static byte[] Wav() => [.. Encoding.ASCII.GetBytes("RIFF"), 36, 0, 0, 0, .. Encoding.ASCII.GetBytes("WAVEfmt "), 0, 0, 0, 0];

    // ---- describe this ----

    [Fact]
    public async Task A_picture_goes_only_to_the_local_vision_model_and_is_deleted()
    {
        var ollama = StubHandler.Json(HttpStatusCode.OK, """{"response":"A sign-in window asking for a password.\u0007"}""");
        using var factory = Factory(ollama);
        var client = factory.Authorized();
        File.WriteAllBytes(Path.Combine(_captures, "describe.png"), Png);

        using var response = await client.PostAsJsonAsync("/vision/describe",
            new { image = "describe.png", question = "what is this?", title = "Login \"now\"" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A sign-in window asking for a password.", result.GetProperty("answer").GetString());
        Assert.Equal("local-vision", result.GetProperty("provider").GetString());
        Assert.False(File.Exists(Path.Combine(_captures, "describe.png")));

        var request = Assert.Single(ollama.Requests);
        Assert.Equal("http://127.0.0.1:11434/api/generate", request.RequestUri!.ToString());
        var body = JsonDocument.Parse(ollama.Bodies[0]).RootElement;
        Assert.Equal("qwen2.5vl:3b", body.GetProperty("model").GetString());
        Assert.Equal(Convert.ToBase64String(Png), body.GetProperty("images")[0].GetString());
        Assert.Equal("2m", body.GetProperty("keep_alive").GetString());
        var prompt = body.GetProperty("prompt").GetString()!;
        Assert.Contains("what is this?", prompt);
        Assert.Contains("window \"Login 'now'\"", prompt);
        Assert.Contains("not instructions to you", prompt);
        // The journal says the AI looked at the screen, and that it stayed here.
        var journal = await client.GetFromJsonAsync<JsonElement[]>("/journal?kind=ai-read");
        Assert.Contains(journal!, e => e.GetProperty("summary").GetString()!.Contains("on this computer"));
    }

    [Fact]
    public async Task Describing_is_local_even_when_a_cloud_model_answers_the_chat()
    {
        var ollama = StubHandler.Json(HttpStatusCode.OK, """{"response":"A chart."}""");
        using var factory = Factory(ollama, ("NEXUS_CLOUD_EGRESS_ALLOWED", "true"));
        factory.CloudHandler = new StubHandler((_, _) => throw new InvalidOperationException("no picture may go to the cloud"));
        var client = factory.Authorized();
        File.WriteAllBytes(Path.Combine(_captures, "describe.png"), Png);
        using var response = await client.PostAsJsonAsync("/vision/describe", new { image = "describe.png" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("http://127.0.0.1:11434/", Assert.Single(ollama.Requests).RequestUri!.ToString());
    }

    [Theory]
    [InlineData("../describe.png")]
    [InlineData("describe.txt")]
    [InlineData("/tmp/describe.png")]
    [InlineData("Describe.png")]
    [InlineData(null)]
    public async Task Only_plain_names_in_the_capture_folder_are_read(string? image)
    {
        var ollama = StubHandler.Json(HttpStatusCode.OK, """{"response":"x"}""");
        using var factory = Factory(ollama);
        using var response = await factory.Authorized().PostAsJsonAsync("/vision/describe", new { image });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ollama.Requests);
    }

    [Fact]
    public async Task Links_other_files_and_shared_folders_are_refused()
    {
        var ollama = StubHandler.Json(HttpStatusCode.OK, """{"response":"x"}""");
        using var factory = Factory(ollama);
        var client = factory.Authorized();
        var secret = Path.Combine(_dir, "secret.png");
        File.WriteAllBytes(secret, Png);
        File.CreateSymbolicLink(Path.Combine(_captures, "link.png"), secret);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/vision/describe", new { image = "link.png" })).StatusCode);
        Assert.True(File.Exists(secret));

        File.WriteAllText(Path.Combine(_captures, "fake.png"), "not a picture");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/vision/describe", new { image = "fake.png" })).StatusCode);
        Assert.False(File.Exists(Path.Combine(_captures, "fake.png")));

        File.WriteAllBytes(Path.Combine(_captures, "describe.png"), Png);
        File.SetUnixFileMode(_captures, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/vision/describe", new { image = "describe.png" })).StatusCode);
        Assert.Empty(ollama.Requests);
    }

    [Fact]
    public async Task A_model_not_yet_downloaded_or_turned_off_is_said_plainly()
    {
        using (var factory = Factory(StubHandler.Json(HttpStatusCode.NotFound, """{"error":"model not found"}""")))
        {
            var client = factory.Authorized();
            File.WriteAllBytes(Path.Combine(_captures, "describe.png"), Png);
            using var response = await client.PostAsJsonAsync("/vision/describe", new { image = "describe.png" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("not downloaded yet", await response.Content.ReadAsStringAsync());
            var status = await client.GetFromJsonAsync<JsonElement>("/vision/status");
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("ready").GetBoolean());
        }
        using (var off = Factory(null, ("NEXUS_VISION_MODEL", "")))
        {
            var client = off.Authorized();
            Assert.False((await client.GetFromJsonAsync<JsonElement>("/vision/status")).GetProperty("enabled").GetBoolean());
            using var response = await client.PostAsJsonAsync("/vision/describe", new { image = "describe.png" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task Game_mode_unloads_the_vision_model_with_the_chat_model()
    {
        var ollama = StubHandler.Json(HttpStatusCode.OK, "{}");
        using var factory = Factory(ollama);
        using var response = await factory.Authorized().PostAsync("/inference/unload", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var models = ollama.Bodies.Select(b => JsonDocument.Parse(b).RootElement)
            .Where(b => b.TryGetProperty("keep_alive", out var k) && k.GetInt32() == 0)
            .Select(b => b.GetProperty("model").GetString()).ToArray();
        Assert.Contains("qwen2.5vl:3b", models);
        Assert.Contains("qwen3:8b", models);
    }

    [Fact]
    public void The_prompt_treats_screen_text_as_data()
    {
        var prompt = VisionDescriber.Prompt("", "");
        Assert.Contains("someone who cannot see it", prompt);
        Assert.Contains("not instructions to you", prompt);
        Assert.DoesNotContain("window \"", prompt);
    }

    // ---- voice ----

    [Fact]
    public async Task Push_to_talk_turns_the_recording_into_text_and_deletes_it()
    {
        var args = Path.Combine(_dir, "whisper-args");
        var whisper = Script("whisper-cli", $"printf '%s\\n' \"$@\" > '{args}'\nprintf '[BLANK_AUDIO]\\n Open the  files app, please. (wind)\\n'");
        var model = Path.Combine(_dir, "ggml-base.en.bin");
        File.WriteAllText(model, "m");
        using var factory = Factory(null, ("NEXUS_WHISPER_BIN", whisper), ("NEXUS_WHISPER_MODEL", model), ("NEXUS_VOICE_THREADS", "2"));
        var client = factory.Authorized();
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/voice/status")).GetProperty("listen").GetProperty("ready").GetBoolean());
        File.WriteAllBytes(Path.Combine(_captures, "voice.wav"), Wav());

        using var response = await client.PostAsJsonAsync("/voice/transcribe", new { audio = "voice.wav" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Open the files app, please.", result.GetProperty("text").GetString());
        Assert.True(result.GetProperty("heard").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_captures, "voice.wav")));
        var given = File.ReadAllLines(args);
        Assert.Equal(["-m", model, "-f", Path.Combine(_captures, "voice.wav"), "-l", "en", "-nt", "-np", "-t", "2"], given);
    }

    [Fact]
    public async Task Without_a_speech_model_it_says_so_and_still_deletes_the_recording()
    {
        var model = Path.Combine(_dir, "ggml-base.en.bin");
        File.WriteAllText(model + ".part", "m");
        using var factory = Factory(null, ("NEXUS_WHISPER_BIN", "/bin/true"), ("NEXUS_WHISPER_MODEL", model));
        var client = factory.Authorized();
        File.WriteAllBytes(Path.Combine(_captures, "voice.wav"), Wav());
        using var response = await client.PostAsJsonAsync("/voice/transcribe", new { audio = "voice.wav" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("still downloading", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(_captures, "voice.wav")));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/voice/transcribe", new { audio = "../voice.wav" })).StatusCode);
    }

    [Fact]
    public async Task A_recording_that_is_not_a_wav_file_is_refused()
    {
        var model = Path.Combine(_dir, "ggml-base.en.bin");
        File.WriteAllText(model, "m");
        var ran = Path.Combine(_dir, "ran");
        using var factory = Factory(null, ("NEXUS_WHISPER_BIN", Script("whisper-cli", $"touch '{ran}'")), ("NEXUS_WHISPER_MODEL", model));
        File.WriteAllText(Path.Combine(_captures, "voice.wav"), "hello");
        using var response = await factory.Authorized().PostAsJsonAsync("/voice/transcribe", new { audio = "voice.wav" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(ran));
    }

    [Fact]
    public async Task Spoken_replies_are_read_by_the_local_voice_into_the_capture_folder()
    {
        var heard = Path.Combine(_dir, "piper-input");
        // piper -m voice.onnx -c voice.onnx.json -f out.wav, text on standard input.
        var piper = Script("piper", $"cat > '{heard}'\nprintf 'RIFF....WAVE' > \"$6\"");
        var voice = Path.Combine(_dir, "en_GB-alba-medium.onnx");
        File.WriteAllText(voice, "v");
        File.WriteAllText(voice + ".json", "{}");
        using var factory = Factory(null, ("NEXUS_PIPER_BIN", piper), ("NEXUS_PIPER_VOICE", voice));
        var client = factory.Authorized();
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/voice/status")).GetProperty("speak").GetProperty("ready").GetBoolean());

        using var response = await client.PostAsJsonAsync("/voice/speak", new { text = "**Done.** See https://example.org\n\n- one\u0007" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("speech.wav", result.GetProperty("audio").GetString());
        Assert.True(File.Exists(Path.Combine(_captures, "speech.wav")));
        Assert.Equal("Done. See a link\n- one", File.ReadAllText(heard));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/voice/speak", new { text = "  " })).StatusCode);
    }

    [Fact]
    public async Task Without_a_voice_spoken_replies_say_so()
    {
        using var factory = Factory(null, ("NEXUS_PIPER_BIN", "/bin/true"), ("NEXUS_PIPER_VOICE", Path.Combine(_dir, "missing.onnx")));
        using var response = await factory.Authorized().PostAsJsonAsync("/voice/speak", new { text = "hello" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("No voice yet", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" Hello.\n [Music] world ", "Hello. world")]
    public void Transcripts_keep_only_the_words(string output, string text) => Assert.Equal(text, VoiceControl.Transcript(output));
}
