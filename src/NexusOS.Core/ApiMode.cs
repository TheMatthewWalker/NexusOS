public enum ApiMode { Unix, Tcp }

public static class ApiModes
{
    // Linux defaults to the peer-authenticated Unix socket; TCP loopback is an
    // explicit development opt-in and the only mode on other platforms.
    public static ApiMode Resolve(string? configured, bool isLinux)
    {
        if (string.IsNullOrWhiteSpace(configured)) return isLinux ? ApiMode.Unix : ApiMode.Tcp;
        return configured.Trim().ToLowerInvariant() switch
        {
            "unix" when isLinux => ApiMode.Unix,
            "unix" => throw new InvalidOperationException("NEXUS_API_MODE=unix requires Linux."),
            "tcp" => ApiMode.Tcp,
            _ => throw new InvalidOperationException("NEXUS_API_MODE must be 'unix' or 'tcp'.")
        };
    }

    public static ApiMode Resolve(string? configured) => Resolve(configured, OperatingSystem.IsLinux());
}
