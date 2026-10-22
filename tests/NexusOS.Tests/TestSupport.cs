using System.Net;
using Microsoft.Extensions.Configuration;

public sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        return await respond(request, ct);
    }

    public static StubHandler Json(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
}

public sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public sealed class FakeCredentials(string? value) : IProviderCredentialStore
{
    public int Calls { get; private set; }
    public Task<string?> GetAsync(string provider, CancellationToken ct) { Calls++; return Task.FromResult(value); }
}

public sealed class TempState : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
    public IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var data = new Dictionary<string, string?> { ["NEXUS_STATE_DIR"] = Root };
        foreach (var (key, value) in values) data[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }
    public NexusStatePaths Paths(params (string Key, string? Value)[] values) => new(Config(values));
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

public static class TestConfig
{
    public static IConfiguration Of(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value)).Build();
}
