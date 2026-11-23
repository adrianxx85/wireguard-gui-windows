using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WGClient.Core;

/// <summary>Erzeugt aus Original-Konfiguration + Optionen die Konfiguration, mit der der Tunnel gestartet wird.</summary>
public static class ConfigBuilder
{
    static readonly char[] Separators = [',', ' ', ';', '\n', '\r', '\t'];

    public static List<string> SplitList(string? s) =>
        (s ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static bool UsesManualRoutes(WgConfig cfg) =>
        string.Equals(cfg.Interface?.Get("Table"), "off", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsKillSwitch(WgConfig cfg) => cfg.Peers.Count() == 1 && !UsesManualRoutes(cfg);

    public static bool HasKillSwitch(WgConfig cfg) => SupportsKillSwitch(cfg) &&
        SplitList(cfg.Peers.Single().Get("AllowedIPs")).Any(route =>
            IPNetwork.TryParse(route, out var network) && network.PrefixLength == 0);

    public static WgConfig Build(WgConfig original, TunnelOptions o)
    {
        var cfg = original.Clone();
        var iface = cfg.Interface ?? throw new InvalidOperationException("Sektion [Interface] fehlt.");

        iface.Set("DNS", string.Join(", ", SplitList(o.Dns)));
        iface.Set("MTU", o.Mtu?.ToString(CultureInfo.InvariantCulture));

        if (cfg.Peers.FirstOrDefault() is { } first)
        {
            var imported = SplitList(first.Get("AllowedIPs"));
            var routes = o.PreserveImportedRoutes == true ? new List<string>(imported) : ComputeAllowedIps(o);
            if (!UsesManualRoutes(cfg) && (o.PreserveImportedRoutes != true ||
                !SplitList(original.Interface?.Get("DNS")).SequenceEqual(SplitList(o.Dns), StringComparer.OrdinalIgnoreCase)))
                AddDnsRoutes(routes, cfg.Peers.Skip(1), o.Dns);
            if (!routes.SequenceEqual(imported, StringComparer.OrdinalIgnoreCase))
                first.Set("AllowedIPs", string.Join(", ", routes));
            first.Set("Endpoint", string.IsNullOrWhiteSpace(o.Endpoint) ? null : o.Endpoint.Trim());
            first.Set("PersistentKeepalive", o.PersistentKeepalive is > 0 ? o.PersistentKeepalive.Value.ToString(CultureInfo.InvariantCulture) : null);
        }

        return cfg;
    }

    /// <summary>
    /// WireGuard für Windows aktiviert den Kill-Switch automatisch, wenn (bei genau einem Peer)
    /// eine /0-Route in AllowedIPs steht. Ohne Kill-Switch wird daher 0.0.0.0/1 + 128.0.0.0/1 verwendet.
    /// </summary>
    static List<string> ComputeAllowedIps(TunnelOptions o)
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
            // Nur Global Unicast (2000::/3), damit Link-Local (fe80::/10), ULA (fc00::/7) und Multicast (ff00::/8) im LAN bleiben.
            if (o.IncludeIPv6) result.Add("2000::/3");
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

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void AddDnsRoutes(List<string> routes, IEnumerable<WgSection> otherPeers, string dns)
    {
        var otherRoutes = otherPeers.SelectMany(peer => SplitList(peer.Get("AllowedIPs"))).ToList();
        foreach (var token in SplitList(dns))
        {
            if (!IPAddress.TryParse(token, out var ip)) continue; // Suchdomäne
            if (routes.Concat(otherRoutes).Any(route => CoversAddress(route, ip))) continue;
            routes.Add($"{ip}/{(ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}");
        }
    }

    static bool CoversAddress(string route, IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return Cidr.ContainsV4([route], address);
        return IPNetwork.TryParse(route, out var network) ? network.Contains(address) :
            IPAddress.TryParse(route, out var host) && address.Equals(host);
    }
}
