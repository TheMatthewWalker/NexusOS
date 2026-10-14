public static class ProviderEndpointGuard
{
    public static void RequireLoopback(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Local inference endpoint must be HTTP on loopback.");
    }

    public static void RequireCloudEndpoint(string raw, string? extraHosts)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port != 443)
            throw new InvalidOperationException("Cloud provider endpoint must use HTTPS on port 443.");
        var allowed = new[] { "api.openai.com" }
            .Concat((extraHosts ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (!allowed.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cloud provider host is not allowlisted.");
    }
}
