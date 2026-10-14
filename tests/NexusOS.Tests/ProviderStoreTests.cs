using Xunit;

public sealed class ProviderStoreTests
{
    [Fact] public void StartsLocal() => Assert.Equal("local", new ProviderStore().Selected);
    [Fact] public void RejectsUnknownProvider()
    {
        var store = new ProviderStore();
        Assert.False(store.Select("untrusted"));
        Assert.Equal("local", store.Selected);
    }
    [Fact] public void SwitchesToCompatibleProvider()
    {
        var store = new ProviderStore();
        Assert.True(store.Select("OPENAI-COMPATIBLE"));
        Assert.Equal("openai-compatible", store.Selected);
    }
}
