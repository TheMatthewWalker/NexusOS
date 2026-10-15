public static class CloudEgressPolicy
{
    public static bool IsExplicitlyAllowed(IConfiguration config) =>
        string.Equals(config["NEXUS_CLOUD_EGRESS_ALLOWED"], "true", StringComparison.OrdinalIgnoreCase);
    public static void RequireConsent(IConfiguration config)
    {
        if (!IsExplicitlyAllowed(config))
            throw new InvalidOperationException("Cloud prompt transmission is disabled. Set NEXUS_CLOUD_EGRESS_ALLOWED=true explicitly to enable.");
    }
}
