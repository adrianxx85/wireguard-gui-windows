using System.Globalization;
using System.Net;
using System.Net.Sockets;

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
        if (colon <= 0 || ep.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        var portText = ep.AsSpan(colon + 1);
        if (portText.IsEmpty || portText.ContainsAnyExceptInRange('0', '9') ||
            !ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0) return false;

        var host = ep[..colon];
        if (!host.StartsWith('['))
            return !host.Contains(':') && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
        if (!host.EndsWith(']')) return false;
        var literal = host.AsSpan(1, host.Length - 2);
        var scope = literal.IndexOf('%');
        if (scope >= 0)
        {
            var zone = literal[(scope + 1)..];
            if (zone.IsEmpty || zone.ContainsAny('%', '[', ']') || zone.Contains('#')) return false;
            literal = literal[..scope];
        }
        return IPAddress.TryParse(literal, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
    }
}
