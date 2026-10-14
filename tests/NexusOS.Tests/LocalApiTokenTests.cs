using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class LocalApiTokenTests
{
    [Fact]
    public void TokenPersistsAndRejectsInvalidCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> { ["NEXUS_STATE_DIR"] = root }).Build();
            var token = new LocalApiToken(new NexusStatePaths(config));
            var credential = File.ReadAllText(token.TokenPath).Trim();
            Assert.Equal(64, credential.Length);
            Assert.True(token.Verify(credential));
            Assert.False(token.Verify(null));
            Assert.False(token.Verify("wrong"));
            Assert.False(token.Verify(new string('0', 64)));
            Assert.True(new LocalApiToken(new NexusStatePaths(config)).Verify(credential));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
