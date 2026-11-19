namespace WGClient.Core;

public enum RouteMode { FullTunnel, Split }

/// <summary>Benutzer-Einstellungen eines Tunnels. Werden beim Verbinden auf die Original-Konfiguration angewendet.</summary>
public sealed class TunnelOptions
{
    public string Dns { get; set; } = "";
    public RouteMode Mode { get; set; } = RouteMode.FullTunnel;

    // nur bei FullTunnel
    public bool KillSwitch { get; set; } = true;
    public bool AllowLan { get; set; }
    public string LanExceptions { get; set; } = "";
    public bool IncludeIPv6 { get; set; } = true;

    // nur bei Split
    public string SplitAllowedIps { get; set; } = "";

    public string Endpoint { get; set; } = "";
    public int? Mtu { get; set; }
    public int? PersistentKeepalive { get; set; }
    public bool Autostart { get; set; }

    /// <summary>Leitet sinnvolle Start-Optionen aus einer importierten Konfiguration ab.</summary>
    public static TunnelOptions FromConfig(WgConfig cfg)
    {
        var iface = cfg.Interface;
        var peer = cfg.Peers.FirstOrDefault();
        var allowed = ConfigBuilder.SplitList(peer?.Get("AllowedIPs"));

        var v4Default = allowed.Contains("0.0.0.0/0");
        var v6Default = allowed.Contains("::/0");
        var full = v4Default || (allowed.Contains("0.0.0.0/1") && allowed.Contains("128.0.0.0/1"));

        return new TunnelOptions
        {
            Dns = iface?.Get("DNS") ?? "",
            Mode = full ? RouteMode.FullTunnel : RouteMode.Split,
            KillSwitch = !full || v4Default || v6Default,
            IncludeIPv6 = true,
            SplitAllowedIps = full ? "" : string.Join(", ", allowed),
            Endpoint = peer?.Get("Endpoint") ?? "",
            Mtu = int.TryParse(iface?.Get("MTU"), out var mtu) ? mtu : null,
            PersistentKeepalive = int.TryParse(peer?.Get("PersistentKeepalive"), out var ka) ? ka : null,
        };
    }
}
