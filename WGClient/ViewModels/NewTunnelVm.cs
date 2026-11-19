using System.Net;
using System.Net.Sockets;
using System.Windows.Media;
using WGClient.Core;

namespace WGClient.ViewModels;

public enum TunnelScenario { Internet, Site, PointToPoint }

/// <summary>Assistent "Neuen Tunnel erstellen": Art → dieses Gerät → Gegenstelle → Zusammenfassung.</summary>
public sealed class NewTunnelVm : ObservableObject
{
    public const int LastStep = 3;
    const string FullTunnelRoutes = "0.0.0.0/0, ::/0";
    const string DefaultInternetDns = "1.1.1.1";

    static readonly Brush Done = Freeze(Color.FromRgb(0x4F, 0x8C, 0xFF));
    static readonly Brush Open = Freeze(Color.FromRgb(0x2A, 0x2F, 0x3A));
    static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    readonly Func<string, bool> _nameExists;
    readonly List<string> _localNetworks; // Netze der Netzwerkadapter dieses PCs
    readonly List<string> _usedNetworks;  // lokale Netze + Adressen bestehender Tunnel

    string? _privateKey;   // aktuell verwendeter privater Schlüssel
    string? _generatedKey; // automatisch erzeugter Schlüssel (bleibt beim Hin- und Herblättern gleich)

    // Zuletzt automatisch eingetragene Werte – nur diese werden bei Änderungen nachgeführt.
    string? _suggestedAddress, _suggestedSubnet;
    string _autoDns = "", _autoVpnSubnet = "", _autoPeerAddress = "";

    public NewTunnelVm(Func<string, bool> nameExists, IEnumerable<string> tunnelAddresses, IEnumerable<string>? localNetworks = null)
    {
        _nameExists = nameExists;
        _localNetworks = (localNetworks ?? LocalNetworks.IPv4Subnets()).ToList();
        _usedNetworks = _localNetworks.Concat(tunnelAddresses).ToList();
        SuggestAddress();
        ApplyScenarioDefaults();
    }

    // ---------- Navigation ----------

    int _step;
    public int Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;
            OnPropertyChanged(nameof(IsStep0), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(StepCaption),
                nameof(StepTitle), nameof(NextText), nameof(CanGoBack), nameof(Bar2), nameof(Bar3), nameof(Bar4));
        }
    }

    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public string StepCaption => $"Schritt {Step + 1} von {LastStep + 1}";
    public string StepTitle => Step switch
    {
        0 => "Wofür ist der Tunnel?",
        1 => "Dieses Gerät",
        2 => PeerTitle,
        _ => "Zusammenfassung",
    };
    public string NextText => Step == LastStep ? "Tunnel erstellen" : "Weiter";
    public Brush Bar1 => Done;
    public Brush Bar2 => Step >= 1 ? Done : Open;
    public Brush Bar3 => Step >= 2 ? Done : Open;
    public Brush Bar4 => Step >= 3 ? Done : Open;

    bool _isWorking;
    public bool IsWorking
    {
        get => _isWorking;
        private set { if (Set(ref _isWorking, value)) OnPropertyChanged(nameof(IsIdle), nameof(CanGoBack)); }
    }
    public bool IsIdle => !IsWorking;
    public bool CanGoBack => Step > 0 && !IsWorking;

    string? _error;
    public string? Error { get => _error; set { if (Set(ref _error, value) && value is not null) Info = null; } }

    string? _info;
    public string? Info { get => _info; set => Set(ref _info, value); }

    // ---------- Schritt 0: Art der Verbindung ----------

    TunnelScenario _scenario = TunnelScenario.Internet;
    public TunnelScenario Scenario
    {
        get => _scenario;
        set
        {
            if (!Set(ref _scenario, value)) return;
            OnPropertyChanged(nameof(IsInternet), nameof(IsSite), nameof(IsPointToPoint), nameof(PeerTitle), nameof(StepTitle),
                nameof(PeerKeyLabel), nameof(EndpointLabel), nameof(DnsHint));
        }
    }

    public bool IsInternet { get => Scenario == TunnelScenario.Internet; set { if (value) Scenario = TunnelScenario.Internet; } }
    public bool IsSite { get => Scenario == TunnelScenario.Site; set { if (value) Scenario = TunnelScenario.Site; } }
    public bool IsPointToPoint { get => Scenario == TunnelScenario.PointToPoint; set { if (value) Scenario = TunnelScenario.PointToPoint; } }

    public string PeerTitle => IsPointToPoint ? "Gegenstelle" : "Server";
    public string PeerKeyLabel => IsPointToPoint ? "Öffentlicher Schlüssel der Gegenstelle" : "Öffentlicher Schlüssel des Servers";
    public string EndpointLabel => IsPointToPoint ? "Endpoint der Gegenstelle" : "Endpoint des Servers";

    // ---------- Schritt 1: dieses Gerät ----------

    string _name = "";
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(NameHint)); } }

    public string NameHint
    {
        get
        {
            var sanitized = TunnelStore.SanitizeName(Name);
            return Name.Trim().Length > 0 && sanitized != Name.Trim()
                ? $"Wird gespeichert als „{sanitized}“ (erlaubt: Buchstaben, Ziffern und _ = + . -, max. 32 Zeichen)."
                : "Erscheint in der Tunnel-Liste und als Name des Windows-Dienstes.";
        }
    }

    string _address = "";
    public string Address { get => _address; set { if (Set(ref _address, value)) OnPropertyChanged(nameof(AddressHint)); } }

    public string AddressHint => IsSuggestedAddress
        ? $"Vorschlag aus einem freien VPN-Netz ({_suggestedSubnet}). " +
          "Hat der Server schon ein VPN-Netz, trage stattdessen die Adresse ein, die er dir zugewiesen hat."
        : "Die IP-Adresse dieses Geräts im VPN, z. B. 10.8.0.2/32. Mehrere mit Komma trennen (z. B. zusätzlich IPv6).";

    bool IsSuggestedAddress => _suggestedAddress is not null && ConfigBuilder.SplitList(Address).SequenceEqual([_suggestedAddress]);

    string _dns = "";
    public string Dns { get => _dns; set => Set(ref _dns, value); }

    public string DnsHint => IsInternet
        ? "Wird benötigt: Der Kill-Switch blockiert den DNS deines lokalen Routers. 1.1.1.1 (Cloudflare) funktioniert, wenn der Server ins Internet weiterleitet."
        : "Optional, z. B. der Router im entfernten Netz (192.168.178.1). Leer lassen = DNS von Windows verwenden.";

    bool _useOwnKey;
    public bool UseOwnKey { get => _useOwnKey; set => Set(ref _useOwnKey, value); }
    public string OwnPrivateKey { get; set; } = "";

    // ---------- Schritt 2: Server / Gegenstelle ----------

    public string ServerPublicKey { get; set; } = "";
    public string Endpoint { get; set; } = "";

    string _presharedKey = "";
    public string PresharedKey { get => _presharedKey; set => Set(ref _presharedKey, value); }

    // Point-to-Site
    string _vpnSubnet = "";
    public string VpnSubnet { get => _vpnSubnet; set => Set(ref _vpnSubnet, value); }
    public string RemoteNetworks { get; set; } = "";

    // Point-to-Point
    string _peerAddress = "";
    public string PeerAddress { get => _peerAddress; set => Set(ref _peerAddress, value); }

    public string Keepalive { get; set; } = "25";

    // ---------- Schritt 3: Ergebnis ----------

    string _publicKey = "";
    public string PublicKey { get => _publicKey; private set => Set(ref _publicKey, value); }

    string _serverSnippet = "";
    public string ServerSnippet { get => _serverSnippet; private set => Set(ref _serverSnippet, value); }

    string _interfaceSuggestion = "";
    /// <summary>Vorschlag für das [Interface] der Gegenstelle, wenn ein neues VPN-Netz vorgeschlagen wurde.</summary>
    public string InterfaceSuggestion
    {
        get => _interfaceSuggestion;
        private set { if (Set(ref _interfaceSuggestion, value)) OnPropertyChanged(nameof(HasInterfaceSuggestion)); }
    }
    public bool HasInterfaceSuggestion => InterfaceSuggestion.Length > 0;

    string _serverHint = "";
    public string ServerHint { get => _serverHint; private set => Set(ref _serverHint, value); }

    string _warning = "";
    public string Warning { get => _warning; private set { if (Set(ref _warning, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => Warning.Length > 0;

    string _configPreview = "";
    public string ConfigPreview { get => _configPreview; private set => Set(ref _configPreview, value); }

    /// <summary>Name und vollständige Konfiguration (inkl. privatem Schlüssel) nach dem letzten Schritt.</summary>
    public (string Name, string Config)? Result { get; private set; }

    // ---------- Aktionen ----------

    /// <summary>Schlägt eine Adresse aus einem freien 10.x.y.0/24 vor (.2 für dieses Gerät, .1 für die Gegenstelle).</summary>
    public void SuggestAddress()
    {
        var subnet = Cidr.SuggestSubnetV4(_usedNetworks);
        if (subnet is null)
        {
            Info = "Kein freies privates IPv4-Netz gefunden. Bitte die Tunnel-Adresse manuell eingeben.";
            return;
        }
        Info = null;
        _suggestedSubnet = subnet;
        _suggestedAddress = Cidr.HostV4(subnet, 2) + "/32";
        Address = _suggestedAddress;
        OnPropertyChanged(nameof(AddressHint));
    }

    public void Back()
    {
        if (!CanGoBack) return;
        Error = Info = null;
        Step--;
    }

    /// <returns>true, wenn der Assistent abgeschlossen ist (<see cref="Result"/> gesetzt).</returns>
    public async Task<bool> NextAsync()
    {
        if (IsWorking) return false;
        Error = Info = null;
        IsWorking = true;
        try
        {
            switch (Step)
            {
                case 0:
                    ApplyScenarioDefaults();
                    break;
                case 1:
                    if (ValidateDevice() is { } e1) { Error = e1; return false; }
                    await EnsureKeysAsync();
                    ApplyPeerDefaults();
                    break;
                case 2:
                    if (ValidatePeer() is { } e2) { Error = e2; return false; }
                    BuildSummary();
                    break;
                default:
                    Result = (TunnelStore.SanitizeName(Name), BuildConfig().Serialize());
                    return true;
            }
            Step++;
            return false;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally { IsWorking = false; }
    }

    public async Task GeneratePresharedKeyAsync()
    {
        if (IsWorking) return;
        Error = Info = null;
        IsWorking = true;
        try
        {
            PresharedKey = await WireGuardService.GenerateKeyAsync(preshared: true);
            Info = "Pre-shared Key erzeugt – er steht später auch im Block für die Gegenstelle.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsWorking = false; }
    }

    // ---------- Vorbelegung ----------

    void ApplyScenarioDefaults()
    {
        var dns = IsInternet ? DefaultInternetDns : "";
        if (Dns.Trim().Length == 0 || Dns == _autoDns) Dns = dns;
        _autoDns = dns;
    }

    /// <summary>VPN-Netz und Adresse der Gegenstelle aus der Adresse dieses Geräts ableiten (Konvention: /24, Gegenstelle .1).</summary>
    void ApplyPeerDefaults()
    {
        if (DeviceAddresses().FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) is not { } ip) return;
        var subnet = Cidr.NetworkV4(ip, 24);
        if (VpnSubnet.Trim().Length == 0 || VpnSubnet == _autoVpnSubnet) VpnSubnet = subnet;
        _autoVpnSubnet = subnet;

        var peer = Cidr.HostV4(subnet, ip.ToString() == Cidr.HostV4(subnet, 1) ? 2u : 1u) + "/32";
        if (PeerAddress.Trim().Length == 0 || PeerAddress == _autoPeerAddress) PeerAddress = peer;
        _autoPeerAddress = peer;
    }

    IEnumerable<IPAddress> DeviceAddresses() => ConfigBuilder.SplitList(Address)
        .Select(a => IPAddress.TryParse(a.Split('/')[0], out var ip) ? ip : null).OfType<IPAddress>();

    // ---------- Prüfung ----------

    string? ValidateDevice()
    {
        if (Name.Trim().Length == 0) return "Bitte einen Namen für den Tunnel eingeben.";
        if (_nameExists(Name)) return $"Ein Tunnel „{TunnelStore.SanitizeName(Name)}“ existiert bereits.";

        var addresses = ConfigBuilder.SplitList(Address);
        if (addresses.Count == 0) return "Bitte die Tunnel-Adresse dieses Geräts angeben (z. B. 10.8.0.2/32).";
        if (addresses.FirstOrDefault(a => !Cidr.IsValid(a)) is { } bad) return $"Ungültige Adresse: {bad}";

        var dns = ConfigBuilder.SplitList(Dns);
        if (dns.FirstOrDefault(d => d.Contains('/') || d.Contains('=')) is { } badDns) return $"Ungültiger DNS-Eintrag: {badDns}";
        if (IsInternet && !dns.Any(d => IPAddress.TryParse(d, out _)))
            return "Für „gesamten Internetverkehr“ wird ein DNS-Server benötigt (z. B. 1.1.1.1) – sonst funktioniert die Namensauflösung nicht.";

        if (UseOwnKey && !WgValidation.IsKey(OwnPrivateKey))
            return "Der private Schlüssel ist ungültig (44 Zeichen Base64, endet auf „=“).";
        return null;
    }

    string? ValidatePeer()
    {
        if (!WgValidation.IsKey(ServerPublicKey))
            return $"{PeerKeyLabel} ist ungültig (44 Zeichen Base64, endet auf „=“).";
        if (_privateKey is not null && ServerPublicKey.Trim() == PublicKey)
            return $"Das ist der öffentliche Schlüssel dieses Geräts – hier gehört der {(IsPointToPoint ? "der Gegenstelle" : "des Servers")} hin.";
        if (!WgValidation.IsEndpoint(Endpoint))
            return "Endpoint muss das Format host:port haben, z. B. vpn.example.com:51820 (IPv6 als [Adresse]:port).";
        if (PresharedKey.Trim().Length > 0 && !WgValidation.IsKey(PresharedKey))
            return "Der Pre-shared Key ist ungültig (44 Zeichen Base64). Leer lassen, wenn die Gegenstelle keinen verwendet.";

        if (IsSite)
        {
            var nets = ConfigBuilder.SplitList(VpnSubnet).Concat(ConfigBuilder.SplitList(RemoteNetworks)).ToList();
            if (nets.Count == 0) return "Bitte mindestens das Netz hinter dem Server angeben (z. B. 192.168.178.0/24).";
            if (nets.FirstOrDefault(n => !Cidr.IsValid(n)) is { } bad) return $"Ungültiges Netz: {bad}";
        }
        if (IsPointToPoint)
        {
            var peer = PeerAddress.Trim();
            if (peer.Length == 0) return "Bitte die Tunnel-Adresse der Gegenstelle angeben (z. B. 10.8.0.1).";
            if (!TryPeerHost(peer, out var ip))
                return "Für Point-to-Point eine einzelne IP-Adresse der Gegenstelle eingeben. Erlaubte Präfixe sind /32 für IPv4 und /128 für IPv6.";
            var devices = DeviceAddresses().ToList();
            if (devices.Contains(ip))
                return "Die Gegenstelle braucht eine andere Tunnel-Adresse als dieses Gerät.";
            if (!devices.Any(device => device.AddressFamily == ip.AddressFamily))
                return "Dieses Gerät braucht eine Tunnel-Adresse derselben Adressfamilie wie die Gegenstelle (IPv4 oder IPv6).";
        }

        if (Keepalive.Trim().Length > 0 && (!int.TryParse(Keepalive.Trim(), out var k) || k is < 0 or > 65535))
            return "Keepalive muss zwischen 0 und 65535 Sekunden liegen (leer = aus).";
        return null;
    }

    async Task EnsureKeysAsync()
    {
        string key;
        if (UseOwnKey) key = OwnPrivateKey.Trim();
        else key = _generatedKey ??= await WireGuardService.GenerateKeyAsync();

        if (key == _privateKey) return;
        PublicKey = await WireGuardService.PublicKeyAsync(key);
        _privateKey = key;
    }

    // ---------- Erzeugen ----------

    List<string> AllowedIps() => Scenario switch
    {
        TunnelScenario.Internet => ConfigBuilder.SplitList(FullTunnelRoutes),
        TunnelScenario.Site => ConfigBuilder.SplitList(VpnSubnet).Concat(ConfigBuilder.SplitList(RemoteNetworks))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        _ => [HostRoute(PeerAddress.Trim())],
    };

    static bool TryPeerHost(string address, out IPAddress ip)
    {
        ip = IPAddress.None;
        var parts = address.Split('/');
        if (parts.Length > 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var parsed)) return false;
        if (parts.Length == 2 && parts[1] != (parsed.AddressFamily == AddressFamily.InterNetwork ? "32" : "128")) return false;
        ip = parsed;
        return true;
    }

    static string HostRoute(string address) => TryPeerHost(address, out var ip)
        ? $"{ip}/{(ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}"
        : throw new InvalidOperationException("Die Tunnel-Adresse der Gegenstelle ist ungültig.");

    WgConfig BuildConfig()
    {
        var iface = new WgSection("Interface");
        iface.Entries.Add(new WgEntry("PrivateKey", _privateKey ?? throw new InvalidOperationException("Kein Schlüssel vorhanden.")));
        iface.Entries.Add(new WgEntry("Address", string.Join(", ", ConfigBuilder.SplitList(Address))));
        if (ConfigBuilder.SplitList(Dns) is { Count: > 0 } dns) iface.Entries.Add(new WgEntry("DNS", string.Join(", ", dns)));

        var peer = new WgSection("Peer");
        peer.Entries.Add(new WgEntry("PublicKey", ServerPublicKey.Trim()));
        if (PresharedKey.Trim().Length > 0) peer.Entries.Add(new WgEntry("PresharedKey", PresharedKey.Trim()));
        peer.Entries.Add(new WgEntry("AllowedIPs", string.Join(", ", AllowedIps())));
        peer.Entries.Add(new WgEntry("Endpoint", Endpoint.Trim()));
        if (int.TryParse(Keepalive.Trim(), out var k) && k > 0) peer.Entries.Add(new WgEntry("PersistentKeepalive", k.ToString()));

        var cfg = new WgConfig();
        cfg.Sections.Add(iface);
        cfg.Sections.Add(peer);
        return cfg;
    }

    void BuildSummary()
    {
        ConfigPreview = BuildConfig().Serialize(maskSecrets: true);
        ServerSnippet = ServerPeer.Snippet(TunnelStore.SanitizeName(Name), PublicKey, PresharedKey, Address, maskPresharedKey: true);
        InterfaceSuggestion = BuildInterfaceSuggestion();
        ServerHint = Scenario switch
        {
            TunnelScenario.Internet =>
                "Auf dem Server müssen IP-Weiterleitung und NAT (Masquerading) ins Internet aktiv sein – sonst steht der Tunnel, aber Webseiten laden nicht.",
            TunnelScenario.Site =>
                "Auf dem Server muss IP-Weiterleitung aktiv sein. Geräte im entfernten Netz brauchen eine Route zurück ins VPN-Netz – oder der Server macht NAT. " +
                "Router mit eingebautem WireGuard (z. B. FRITZ!Box) erledigen das selbst.",
            _ =>
                "Die Gegenstelle braucht den Block oben als [Peer]. Ihr UDP-Port (siehe Endpoint) muss erreichbar sein – ggf. am Router weiterleiten und in der Firewall freigeben.",
        };
        Warning = BuildOverlapWarning();
    }

    public string ServerSnippetForCopy() => ServerPeer.Snippet(TunnelStore.SanitizeName(Name), PublicKey, PresharedKey, Address);

    /// <summary>Nur bei vorgeschlagenem (neuem) VPN-Netz: So könnte das [Interface] der Gegenstelle aussehen.</summary>
    string BuildInterfaceSuggestion()
    {
        if (!IsSuggestedAddress || _suggestedSubnet is not { } subnet) return "";
        if (IsSite && !ConfigBuilder.SplitList(VpnSubnet).Contains(subnet)) return "";
        var peerAddress = IsPointToPoint ? HostRoute(PeerAddress.Trim()) : Cidr.HostV4(subnet, 1) + "/24";
        var port = Endpoint.Trim()[(Endpoint.Trim().LastIndexOf(':') + 1)..];
        return string.Join(Environment.NewLine,
            "[Interface]",
            "PrivateKey = <privater Schlüssel der Gegenstelle>",
            $"Address = {peerAddress}",
            $"ListenPort = {port}") + Environment.NewLine;
    }

    /// <summary>Warnt, wenn ein Tunnel-Netz mit einem Netz kollidiert, in dem dieser PC gerade hängt.</summary>
    string BuildOverlapWarning()
    {
        var tunnelNets = ConfigBuilder.SplitList(Address).Concat(IsInternet ? [] : AllowedIps());
        var conflicts = tunnelNets
            .SelectMany(t => _localNetworks.Where(l => Cidr.OverlapsV4(t, l)).Select(l => (Tunnel: t, Local: l)))
            .Distinct()
            .Select(c => $"{c.Tunnel} überschneidet sich mit deinem lokalen Netz {c.Local}")
            .ToList();
        if (conflicts.Count == 0) return "";
        return "Achtung: " + string.Join("; ", conflicts) + ". " +
               "Solange du in diesem Netz bist, ist eins von beiden nicht erreichbar. Am besten nutzt eine Seite ein anderes Netz " +
               "(z. B. den Router zuhause auf 192.168.77.0/24 umstellen).";
    }
}
