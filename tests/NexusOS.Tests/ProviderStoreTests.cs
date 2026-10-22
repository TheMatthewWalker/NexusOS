using Xunit;

public sealed class ProviderStoreTests
{
    [Fact]
    public void StartsLocal()
    {
        using var state = new TempState();
        Assert.Equal("local", new ProviderStore(state.Paths()).Selected);
    }

    [Fact]
    public void RejectsUnknownProvider()
    {
        using var state = new TempState();
        var store = new ProviderStore(state.Paths());
        Assert.False(store.Select("untrusted"));
        Assert.False(store.Select(null));
        Assert.Equal("local", store.Selected);
    }

    [Fact]
    public void SwitchesCaseInsensitivelyAndPersists()
    {
        using var state = new TempState();
        Assert.True(new ProviderStore(state.Paths()).Select("OPENAI-COMPATIBLE"));
        Assert.Equal("openai-compatible", new ProviderStore(state.Paths()).Selected);
        Assert.False(new ProviderStore(state.Paths()).Select("invalid"));
        Assert.Equal("openai-compatible", new ProviderStore(state.Paths()).Selected);
    }

    [Fact]
    public void NamesComeFromTheSingleProviderList()
    {
        using var state = new TempState();
        Assert.Equal(ProviderNames.All, new ProviderStore(state.Paths()).Names);
    }
}
