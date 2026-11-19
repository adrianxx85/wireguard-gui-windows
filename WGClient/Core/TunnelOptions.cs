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
    public bool? PreserveImportedRoutes { get; set; }

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
            KillSwitch = ConfigBuilder.HasKillSwitch(cfg),
            IncludeIPv6 = v6Default || (allowed.Contains("::/1") && allowed.Contains("8000::/1")),
            PreserveImportedRoutes = true,
            SplitAllowedIps = full ? "" : string.Join(", ", allowed),
            Endpoint = peer?.Get("Endpoint") ?? "",
            Mtu = int.TryParse(iface?.Get("MTU"), out var mtu) ? mtu : null,
            PersistentKeepalive = int.TryParse(peer?.Get("PersistentKeepalive"), out var ka) ? ka : null,
        };
    }

    public void UpdateImportedDefaults(WgConfig cfg)
    {
        var imported = FromConfig(cfg);
        if (PreserveImportedRoutes is null)
        {
            // In 1.0.0 IPv6 was selected by default, even for an IPv4-only profile.
            var allowed = ConfigBuilder.SplitList(cfg.Peers.FirstOrDefault()?.Get("AllowedIPs"));
            var oldKillSwitch = imported.Mode == RouteMode.Split || allowed.Contains("0.0.0.0/0") || allowed.Contains("::/0");
            PreserveImportedRoutes = Mode == imported.Mode && !AllowLan && IncludeIPv6 &&
                (Mode == RouteMode.Split || (KillSwitch == oldKillSwitch && string.IsNullOrWhiteSpace(LanExceptions))) &&
                ConfigBuilder.SplitList(SplitAllowedIps).SequenceEqual(ConfigBuilder.SplitList(imported.SplitAllowedIps), StringComparer.OrdinalIgnoreCase);
            if (PreserveImportedRoutes == true)
            {
                IncludeIPv6 = imported.IncludeIPv6;
                KillSwitch = imported.KillSwitch;
            }
        }
        if (!ConfigBuilder.SupportsKillSwitch(cfg)) KillSwitch = false;
    }
}
