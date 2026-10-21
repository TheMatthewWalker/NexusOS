using System.Text;
using Xunit;

public sealed class ModelResponseGuardTests
{
    [Fact]
    public async Task ParsesSmallProviderResponse()
    {
        using var content = new StringContent("{\"response\":\"hello\"}");
        using var doc = await ModelResponseGuard.ReadAsync(content, CancellationToken.None);
        Assert.Equal("hello", doc.RootElement.GetProperty("response").GetString());
    }

    [Fact]
    public async Task RejectsOversizedResponse()
    {
        using var content = new ByteArrayContent(new byte[ModelResponseGuard.MaxResponseBytes + 1]);
        await Assert.ThrowsAsync<ResponseTooLargeException>(() => ModelResponseGuard.ReadAsync(content, CancellationToken.None));
    }
}
