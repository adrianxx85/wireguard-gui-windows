using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WGClient.Core;

namespace WGClient.ViewModels;

public sealed record ServerPeerChoice(WgSection Peer, string Label);

public sealed class TunnelVm : ObservableObject
{
    static readonly Brush Green = Freeze(Color.FromRgb(0x2E, 0xCC, 0x71));
    static readonly Brush Amber = Freeze(Color.FromRgb(0xF5, 0xA5, 0x24));
    static readonly Brush Grey = Freeze(Color.FromRgb(0x5A, 0x62, 0x73));
    static readonly Brush ConnectBrush = Freeze(Color.FromRgb(0x2E, 0xA0, 0x5C));
    static readonly Brush DisconnectBrush = Freeze(Color.FromRgb(0xC9, 0x3C, 0x41));

    static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public TunnelVm(string name, WgConfig original, TunnelOptions options)
    {
        Name = name;
        Original = original;
        Saved = options;
        LoadServerPeers();
        LoadEdit(options);
    }

    public string Name { get; }
    public WgConfig Original { get; private set; }
    public TunnelOptions Saved { get; private set; }

    public IReadOnlyList<ServerPeerChoice> ServerPeers { get; private set; } = [];
    public bool HasMultipleServerPeers => ServerPeers.Count > 1;
    public bool CanCopyServerPeer => SelectedServerPeer is not null;

    ServerPeerChoice? _selectedServerPeer;
    public ServerPeerChoice? SelectedServerPeer
    {
        get => _selectedServerPeer;
        set { if (Set(ref _selectedServerPeer, value)) OnPropertyChanged(nameof(CanCopyServerPeer)); }
    }

    void LoadServerPeers()
    {
        ServerPeers = Original.Peers.Select((peer, index) =>
        {
            var endpoint = peer.Get("Endpoint");
            var label = $"Peer {index + 1} · {peer.Get("PublicKey")}";
            return new ServerPeerChoice(peer, endpoint is null ? label : $"{label} · {endpoint}");
        }).ToList();
        OnPropertyChanged(nameof(ServerPeers), nameof(HasMultipleServerPeers));
        SelectedServerPeer = ServerPeers.Count == 1 ? ServerPeers[0] : null;
    }

    // ---------- Laufzeitstatus ----------

    TunnelState _state;
    public TunnelState State
    {
        get => _state;
        set { if (Set(ref _state, value)) RaiseStatus(); }
    }

    TunnelStats? _stats;
    public TunnelStats? Stats
    {
        get => _stats;
        set { _stats = value; RaiseStatus(); }
    }

    bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) RaiseStatus(); }
    }

    public bool IsActive => State is TunnelState.Running or TunnelState.Starting;
    public bool CanToggle => !IsBusy && State is not TunnelState.Stopping;
    public bool CanRemove => !IsBusy;
    public bool CanSave => IsDirty && !IsBusy;
    bool HasRecentHandshake => Stats?.LastHandshake is { } h && DateTimeOffset.UtcNow - h < TimeSpan.FromMinutes(3);

    public string StateText => IsBusy ? "Bitte warten …" : State switch
    {
        TunnelState.Running => HasRecentHandshake ? "Verbunden" : "Aktiv – warte auf Handshake",
        TunnelState.Starting => "Verbinde …",
        TunnelState.Stopping => "Trenne …",
        _ => "Getrennt",
    };

    public Brush StateBrush => IsBusy || State is TunnelState.Starting or TunnelState.Stopping ? Amber
        : State == TunnelState.Running ? (HasRecentHandshake ? Green : Amber)
        : Grey;

    public string ConnectButtonText => IsActive ? "Trennen" : "Verbinden";
    public Brush ConnectButtonBrush => IsActive ? DisconnectBrush : ConnectBrush;

    public string HandshakeText => Stats?.LastHandshake is { } h ? FormatAgo(h) : "—";
    public string RxText => Stats is null ? "—" : FormatBytes(Stats.RxBytes);
    public string TxText => Stats is null ? "—" : FormatBytes(Stats.TxBytes);
    public string EndpointText => Stats?.Endpoint ?? (string.IsNullOrWhiteSpace(Saved.Endpoint) ? "—" : Saved.Endpoint);
    public string AddressText => Original.Interface?.Get("Address") ?? "—";
    public string DnsText => string.IsNullOrWhiteSpace(Saved.Dns) ? "System-DNS" : Saved.Dns;
    public string Subtitle => string.IsNullOrWhiteSpace(Saved.Endpoint) ? AddressText : Saved.Endpoint;
    public string PeerCountText => $"{Original.Peers.Count()} Peer(s)";
    public bool UsesManualRoutes => ConfigBuilder.UsesManualRoutes(Original);

    public string RoutingSummary
    {
        get
        {
            if (UsesManualRoutes) return "Manuelle Routen (Table = off)";
            var summary = Saved.Mode == RouteMode.Split
                ? "Split-Tunnel: " + (string.IsNullOrWhiteSpace(Saved.SplitAllowedIps) ? "—" : Saved.SplitAllowedIps)
                : "Gesamter Traffic" + (Saved.AllowLan ? " · LAN erreichbar" : "")
                  + (Saved.IncludeIPv6 ? " · inkl. IPv6" : "");
            return ConfigBuilder.HasKillSwitch(ConfigBuilder.Build(Original, Saved)) ? summary + " · Kill-Switch" : summary;
        }
    }

    void RaiseStatus() => OnPropertyChanged(
        nameof(IsActive), nameof(CanToggle), nameof(CanRemove), nameof(CanSave), nameof(StateText), nameof(StateBrush),
        nameof(ConnectButtonText), nameof(ConnectButtonBrush), nameof(HandshakeText),
        nameof(RxText), nameof(TxText), nameof(EndpointText), nameof(SaveButtonText));

    // ---------- Bearbeitbare Optionen ----------

    bool _loading;
    bool SetEdit<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        if (!_loading) { IsDirty = true; UpdatePreview(); }
        return true;
    }

    string _dns = "";
    public string Dns { get => _dns; set => SetEdit(ref _dns, value); }

    bool _isFullTunnel;
    public bool IsFullTunnel
    {
        get => _isFullTunnel;
        set { if (SetEdit(ref _isFullTunnel, value)) OnPropertyChanged(nameof(IsSplit)); }
    }
    public bool IsSplit
    {
        get => !_isFullTunnel;
        set => IsFullTunnel = !value;
    }

    bool _killSwitch;
    public bool KillSwitch { get => _killSwitch; set => SetEdit(ref _killSwitch, value); }

    bool _allowLan;
    public bool AllowLan
    {
        get => _allowLan;
        set { if (SetEdit(ref _allowLan, value)) OnPropertyChanged(nameof(KillSwitchEnabled)); }
    }
    public bool KillSwitchEnabled => !AllowLan && ConfigBuilder.SupportsKillSwitch(Original);

    string _lanExceptions = "";
    public string LanExceptions { get => _lanExceptions; set => SetEdit(ref _lanExceptions, value); }

    bool _includeIPv6;
    public bool IncludeIPv6 { get => _includeIPv6; set => SetEdit(ref _includeIPv6, value); }

    string _splitAllowedIps = "";
    public string SplitAllowedIps { get => _splitAllowedIps; set => SetEdit(ref _splitAllowedIps, value); }

    string _endpoint = "";
    public string Endpoint { get => _endpoint; set => SetEdit(ref _endpoint, value); }

    string _mtuText = "";
    public string MtuText { get => _mtuText; set => SetEdit(ref _mtuText, value); }

    string _keepaliveText = "";
    public string KeepaliveText { get => _keepaliveText; set => SetEdit(ref _keepaliveText, value); }

    bool _autostart;
    public bool Autostart { get => _autostart; set => SetEdit(ref _autostart, value); }

    bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set { if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(CanSave)); }
    }

    public string SaveButtonText => IsActive ? "Speichern & neu verbinden" : "Speichern";

    string? _validationError;
    public string? ValidationError
    {
        get => _validationError;
        private set { if (Set(ref _validationError, value)) OnPropertyChanged(nameof(HasValidationError)); }
    }
    public bool HasValidationError => ValidationError is not null;

    string _effectivePreview = "";
    public string EffectivePreview { get => _effectivePreview; private set => Set(ref _effectivePreview, value); }

    string _allowedIpsPreview = "";
    public string AllowedIpsPreview { get => _allowedIpsPreview; private set => Set(ref _allowedIpsPreview, value); }

    public void LoadEdit(TunnelOptions o)
    {
        _loading = true;
        Dns = o.Dns;
        IsFullTunnel = o.Mode == RouteMode.FullTunnel;
        KillSwitch = o.KillSwitch;
        AllowLan = o.AllowLan;
        LanExceptions = o.LanExceptions;
        IncludeIPv6 = o.IncludeIPv6;
        SplitAllowedIps = o.SplitAllowedIps;
        Endpoint = o.Endpoint;
        MtuText = o.Mtu?.ToString(CultureInfo.InvariantCulture) ?? "";
        KeepaliveText = o.PersistentKeepalive?.ToString(CultureInfo.InvariantCulture) ?? "";
        Autostart = o.Autostart;
        _loading = false;
        IsDirty = false;
        UpdatePreview();
    }

    public bool TryGetOptions(out TunnelOptions options, out string? error)
    {
        options = new TunnelOptions();
        error = null;

        var dnsEntries = ConfigBuilder.SplitList(Dns);
        if (dnsEntries.FirstOrDefault(d => !Cidr.IsValidDnsEntry(d)) is { } badDns)
        { error = $"Ungültiger DNS-Eintrag: {badDns}"; return false; }

        int? mtu = null, keepalive = null;
        if (!string.IsNullOrWhiteSpace(MtuText))
        {
            if (!int.TryParse(MtuText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m is < 576 or > 9000)
            { error = "MTU muss zwischen 576 und 9000 liegen."; return false; }
            mtu = m;
        }
        if (!string.IsNullOrWhiteSpace(KeepaliveText))
        {
            if (!int.TryParse(KeepaliveText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k is < 0 or > 65535)
            { error = "Keepalive muss zwischen 0 und 65535 Sekunden liegen."; return false; }
            keepalive = k;
        }

        var ep = Endpoint.Trim();
        if (ep.Length > 0 && !WgValidation.IsEndpoint(ep))
        { error = "Endpoint muss das Format host:port haben (IPv6 als [Adresse]:port)."; return false; }

        var splitRoutes = ConfigBuilder.SplitList(SplitAllowedIps);
        var exceptions = string.Join(", ", ConfigBuilder.SplitList(LanExceptions));
        var mode = IsFullTunnel ? RouteMode.FullTunnel : RouteMode.Split;
        var preserveRoutes = Saved.PreserveImportedRoutes == true && mode == Saved.Mode && (IsSplit
            ? splitRoutes.SequenceEqual(ConfigBuilder.SplitList(Saved.SplitAllowedIps), StringComparer.OrdinalIgnoreCase)
            : KillSwitch == Saved.KillSwitch && AllowLan == Saved.AllowLan && IncludeIPv6 == Saved.IncludeIPv6 &&
              string.Equals(exceptions, Saved.LanExceptions, StringComparison.OrdinalIgnoreCase));

        if (IsSplit)
        {
            if (splitRoutes.Count == 0 && !preserveRoutes) { error = "Für den Split-Tunnel mindestens ein Netz angeben (z. B. 192.168.0.0/24)."; return false; }
            var bad = splitRoutes.FirstOrDefault(n => !Cidr.IsValid(n));
            if (bad is not null) { error = $"Ungültiges Netz: {bad}"; return false; }
        }
        if (IsFullTunnel && AllowLan)
        {
            var bad = ConfigBuilder.SplitList(LanExceptions).FirstOrDefault(n => !Cidr.IsValid(n));
            if (bad is not null) { error = $"Ungültiges Netz: {bad}"; return false; }
        }

        options = new TunnelOptions
        {
            Dns = string.Join(", ", dnsEntries),
            Mode = mode,
            KillSwitch = KillSwitch,
            AllowLan = AllowLan,
            LanExceptions = exceptions,
            IncludeIPv6 = IncludeIPv6,
            PreserveImportedRoutes = preserveRoutes,
            SplitAllowedIps = string.Join(", ", splitRoutes),
            Endpoint = ep,
            Mtu = mtu,
            PersistentKeepalive = keepalive,
            Autostart = Autostart,
        };
        return true;
    }

    void UpdatePreview()
    {
        if (TryGetOptions(out var o, out var err))
        {
            ValidationError = null;
            var config = ConfigBuilder.Build(Original, o);
            AllowedIpsPreview = config.Peers.FirstOrDefault()?.Get("AllowedIPs") ?? "";
            EffectivePreview = config.Serialize(maskSecrets: true);
        }
        else
        {
            ValidationError = err;
        }
    }

    public void ApplySaved(TunnelOptions saved)
    {
        Saved = saved;
        IsDirty = false;
        OnPropertyChanged(nameof(Saved), nameof(Subtitle), nameof(RoutingSummary), nameof(DnsText), nameof(EndpointText));
    }

    public void ReplaceOriginal(WgConfig cfg, TunnelOptions options)
    {
        Original = cfg;
        LoadServerPeers();
        ApplySaved(options);
        LoadEdit(options);
        OnPropertyChanged(nameof(AddressText), nameof(PeerCountText), nameof(KillSwitchEnabled), nameof(UsesManualRoutes));
    }

    // ---------- Formatierung ----------

    static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : v.ToString("0.0", CultureInfo.CurrentCulture) + " " + units[i];
    }

    static string FormatAgo(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 0) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"vor {(int)d.TotalSeconds} s";
        if (d.TotalMinutes < 60) return $"vor {(int)d.TotalMinutes} min";
        if (d.TotalHours < 48) return $"vor {(int)d.TotalHours} h";
        return t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    }
}
