using System.Security.Cryptography;
using System.Text;

public sealed class LocalApiToken
{
    private readonly byte[] _expected;
    public string TokenPath { get; }

    public LocalApiToken(NexusStatePaths paths)
    {
        TokenPath = paths.PathFor("api-token");
        if (!File.Exists(TokenPath))
        {
            var candidate = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                using var file = new FileStream(TokenPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                NexusStatePaths.RestrictPermissions(TokenPath, false);
                var data = Encoding.ASCII.GetBytes(candidate);
                file.Write(data);
                file.Flush(true);
            }
            catch (IOException) when (File.Exists(TokenPath)) { /* Already created by another instance. */ }
        }
        NexusStatePaths.RestrictPermissions(TokenPath, false);
        var token = File.ReadAllText(TokenPath).Trim();
        try
        {
            if (token.Length != 64 || Convert.FromHexString(token).Length != 32)
                throw new FormatException("Invalid API token length");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Local API token file is invalid; inspect the state directory.");
        }
        _expected = Encoding.ASCII.GetBytes(token);
    }

    public bool Verify(string? provided)
    {
        if (provided is null || provided.Length != 64) return false;
        return CryptographicOperations.FixedTimeEquals(_expected, Encoding.ASCII.GetBytes(provided));
    }
}
