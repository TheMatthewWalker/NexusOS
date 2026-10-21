using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

// M2-4 (#53): DNS answer handling, address fallback, rebinding and redirects.
public sealed class CloudConnectorTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("2606:4700:4700::1111");
    private static readonly IPAddress V4 = IPAddress.Parse("1.1.1.1");

    private sealed class Recorder
    {
        public List<IPEndPoint> Attempts { get; } = [];
    }

    private static Func<IPEndPoint, CancellationToken, ValueTask<Stream>> Connect(Recorder recorder,
        Func<IPEndPoint, CancellationToken, Task<Stream>> behaviour) =>
        async (endpoint, ct) => { recorder.Attempts.Add(endpoint); return await behaviour(endpoint, ct); };

    private static Func<string, CancellationToken, Task<IPAddress[]>> Answers(params IPAddress[][] answers)
    {
        var call = 0;
        return (_, _) => Task.FromResult(answers[Math.Min(call++, answers.Length - 1)]);
    }

    [Fact]
    public async Task FallsBackToNextValidatedAddressWhenFirstIsUnreachable()
    {
        var recorder = new Recorder();
        var connector = new CloudConnector(Answers([V6, V4]), Connect(recorder, (endpoint, _) =>
            endpoint.Address.Equals(V6)
                ? throw new SocketException((int)SocketError.NetworkUnreachable)
                : Task.FromResult<Stream>(new MemoryStream())), TimeSpan.FromSeconds(1));
        await using var stream = await connector.ConnectAsync("api.openai.com", 443, CancellationToken.None);
        Assert.Equal([new IPEndPoint(V6, 443), new IPEndPoint(V4, 443)], recorder.Attempts);
    }

    [Fact]
    public async Task HangingAddressTimesOutAndNextIsTried()
    {
        var recorder = new Recorder();
        var connector = new CloudConnector(Answers([V6, V4]), Connect(recorder, async (endpoint, ct) =>
        {
            if (endpoint.Address.Equals(V6)) await Task.Delay(Timeout.Infinite, ct);
            return new MemoryStream();
        }), TimeSpan.FromMilliseconds(200));
        await using var stream = await connector.ConnectAsync("api.openai.com", 443, CancellationToken.None);
        Assert.Equal(2, recorder.Attempts.Count);
    }

    [Fact]
    public async Task MixedAnswerConnectsToNothing()
    {
        var recorder = new Recorder();
        var connector = new CloudConnector(Answers([V4, IPAddress.Parse("10.0.0.7")]),
            Connect(recorder, (_, _) => Task.FromResult<Stream>(new MemoryStream())), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ConnectAsync("api.openai.com", 443, CancellationToken.None).AsTask());
        Assert.Empty(recorder.Attempts);
    }

    [Fact]
    public async Task RebindingToPrivateAddressOnLaterConnectionIsRejected()
    {
        var recorder = new Recorder();
        var connector = new CloudConnector(Answers([V4], [IPAddress.Parse("169.254.169.254")]),
            Connect(recorder, (_, _) => Task.FromResult<Stream>(new MemoryStream())), TimeSpan.FromSeconds(1));
        await using (await connector.ConnectAsync("api.openai.com", 443, CancellationToken.None)) { }
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ConnectAsync("api.openai.com", 443, CancellationToken.None).AsTask());
        Assert.Equal([new IPEndPoint(V4, 443)], recorder.Attempts);
    }

    [Fact]
    public async Task AllAddressesFailingIsAnHttpRequestException()
    {
        var connector = new CloudConnector(Answers([V6, V4]), Connect(new Recorder(), (_, _) =>
            throw new SocketException((int)SocketError.ConnectionRefused)), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<HttpRequestException>(() => connector.ConnectAsync("api.openai.com", 443, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CallerCancellationIsNotTreatedAsFallback()
    {
        var recorder = new Recorder();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var connector = new CloudConnector(Answers([V6, V4]), Connect(recorder, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new MemoryStream();
        }), TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ConnectAsync("api.openai.com", 443, cancel.Token).AsTask());
        Assert.Single(recorder.Attempts);
    }

    [Fact]
    public async Task LiteralIpHostIsRejectedBeforeResolution()
    {
        var resolved = false;
        var connector = new CloudConnector((_, _) => { resolved = true; return Task.FromResult(new[] { V4 }); },
            Connect(new Recorder(), (_, _) => Task.FromResult<Stream>(new MemoryStream())), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ConnectAsync("1.1.1.1", 443, CancellationToken.None).AsTask());
        Assert.False(resolved);
    }

    [Fact]
    public void CloudHandlerRefusesRedirectsAndProxies()
    {
        using var handler = CloudHttp.CreateHandler(CloudConnector.Default);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task RedirectResponseIsReturnedNotFollowed()
    {
        // A local socket stands in for the validated public address; the
        // redirect target must never be contacted.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = 0;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            served++;
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        var connector = new CloudConnector(Answers([V4]), async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }, TimeSpan.FromSeconds(5));
        using var http = new HttpClient(CloudHttp.CreateHandler(connector));
        using var response = await http.GetAsync("http://api.openai.com/v1/models");
        await server;
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, served);
    }
}
