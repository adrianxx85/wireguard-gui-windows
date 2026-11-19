namespace WGClient.Core;

public static class WgValidation
{
    /// <summary>WireGuard-Schlüssel: 32 Byte, Base64 (44 Zeichen inkl. "=").</summary>
    public static bool IsKey(string? key)
    {
        key = key?.Trim();
        if (key is not { Length: 44 }) return false;
        Span<byte> buffer = stackalloc byte[32];
        return Convert.TryFromBase64String(key, buffer, out var written) && written == 32;
    }

    /// <summary>host:port bzw. [IPv6]:port</summary>
    public static bool IsEndpoint(string? endpoint)
    {
        var ep = endpoint?.Trim() ?? "";
        var colon = ep.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(ep[(colon + 1)..], out var port) || port is < 1 or > 65535) return false;
        var host = ep[..colon];
        return host.StartsWith('[') ? host.EndsWith(']') && host.Length > 2 : !host.Contains(':');
    }
}
