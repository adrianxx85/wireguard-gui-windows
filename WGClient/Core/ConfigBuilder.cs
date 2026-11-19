using System.Net;
using System.Net.Sockets;

namespace WGClient.Core;

/// <summary>Erzeugt aus Original-Konfiguration + Optionen die Konfiguration, mit der der Tunnel gestartet wird.</summary>
public static class ConfigBuilder
{
    static readonly char[] Separators = [',', ' ', ';', '\n', '\r', '\t'];

    public static List<string> SplitList(string? s) =>
        (s ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static WgConfig Build(WgConfig original, TunnelOptions o)
    {
        var cfg = original.Clone();
        var iface = cfg.Interface ?? throw new InvalidOperationException("Sektion [Interface] fehlt.");

        iface.Set("DNS", string.Join(", ", SplitList(o.Dns)));
        iface.Set("MTU", o.Mtu?.ToString());

        var peers = cfg.Peers.ToList();
        if (peers.Count > 0)
        {
            var first = peers[0];
            first.Set("AllowedIPs", string.Join(", ", ComputeAllowedIps(o)));
            if (!string.IsNullOrWhiteSpace(o.Endpoint)) first.Set("Endpoint", o.Endpoint.Trim());
        }
        foreach (var p in peers)
            p.Set("PersistentKeepalive", o.PersistentKeepalive is > 0 ? o.PersistentKeepalive.ToString() : null);

        return cfg;
    }

    /// <summary>
    /// WireGuard für Windows aktiviert den Kill-Switch automatisch, wenn (bei genau einem Peer)
    /// eine /0-Route in AllowedIPs steht. Ohne Kill-Switch wird daher 0.0.0.0/1 + 128.0.0.0/1 verwendet.
    /// </summary>
    public static List<string> ComputeAllowedIps(TunnelOptions o)
    {
        var result = new List<string>();

        if (o.Mode == RouteMode.Split)
        {
            result.AddRange(SplitList(o.SplitAllowedIps));
        }
        else if (o.AllowLan)
        {
            result.AddRange(Cidr.SubtractV4("0.0.0.0/0", Cidr.LocalV4Ranges));
            result.AddRange(SplitList(o.LanExceptions));
            if (o.IncludeIPv6) result.AddRange(["::/1", "8000::/1"]);
        }
        else if (o.KillSwitch)
        {
            result.Add("0.0.0.0/0");
            if (o.IncludeIPv6) result.Add("::/0");
        }
        else
        {
            result.AddRange(["0.0.0.0/1", "128.0.0.0/1"]);
            if (o.IncludeIPv6) result.AddRange(["::/1", "8000::/1"]);
        }

        // DNS-Server immer durch den Tunnel erreichbar machen (z. B. 192.168.x.x im entfernten Netz)
        foreach (var token in SplitList(o.Dns))
        {
            if (!IPAddress.TryParse(token, out var ip)) continue; // Suchdomäne
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (!Cidr.ContainsV4(result, ip)) result.Add($"{ip}/32");
            }
            else if (!(result.Contains("::/0") || (result.Contains("::/1") && result.Contains("8000::/1"))))
            {
                result.Add($"{ip}/128");
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
