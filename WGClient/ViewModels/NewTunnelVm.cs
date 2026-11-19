using System.Windows.Media;
using WGClient.Core;

namespace WGClient.ViewModels;

/// <summary>Assistent "Neuen Tunnel erstellen": Grunddaten → Server → Zusammenfassung.</summary>
public sealed class NewTunnelVm(Func<string, bool> nameExists) : ObservableObject
{
    public const int LastStep = 2;
    const string FullTunnelRoutes = "0.0.0.0/0, ::/0";

    static readonly Brush Done = Freeze(Color.FromRgb(0x4F, 0x8C, 0xFF));
    static readonly Brush Open = Freeze(Color.FromRgb(0x2A, 0x2F, 0x3A));
    static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    string? _privateKey;   // aktuell verwendeter privater Schlüssel
    string? _generatedKey; // automatisch erzeugter Schlüssel (bleibt beim Hin- und Herblättern gleich)

    // ---------- Navigation ----------

    int _step;
    public int Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;
            OnPropertyChanged(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(StepCaption), nameof(StepTitle),
                nameof(NextText), nameof(CanGoBack), nameof(Bar2), nameof(Bar3));
        }
    }

    public bool IsStep1 => Step == 0;
    public bool IsStep2 => Step == 1;
    public bool IsStep3 => Step == 2;
    public string StepCaption => $"Schritt {Step + 1} von {LastStep + 1}";
    public string StepTitle => Step switch { 0 => "Dieses Gerät", 1 => "Server (Peer)", _ => "Zusammenfassung" };
    public string NextText => Step == LastStep ? "Tunnel erstellen" : "Weiter";
    public Brush Bar1 => Done;
    public Brush Bar2 => Step >= 1 ? Done : Open;
    public Brush Bar3 => Step >= 2 ? Done : Open;

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

    public string Address { get; set; } = "";
    public string Dns { get; set; } = "";

    bool _useOwnKey;
    public bool UseOwnKey { get => _useOwnKey; set => Set(ref _useOwnKey, value); }
    public string OwnPrivateKey { get; set; } = "";

    // ---------- Schritt 2: Server ----------

    public string ServerPublicKey { get; set; } = "";
    public string Endpoint { get; set; } = "";

    string _presharedKey = "";
    public string PresharedKey { get => _presharedKey; set => Set(ref _presharedKey, value); }

    bool _isFullTunnel = true;
    public bool IsFullTunnel
    {
        get => _isFullTunnel;
        set { if (Set(ref _isFullTunnel, value)) OnPropertyChanged(nameof(IsSplit)); }
    }
    public bool IsSplit { get => !_isFullTunnel; set => IsFullTunnel = !value; }

    public string SplitAllowedIps { get; set; } = "";
    public string Keepalive { get; set; } = "25";

    // ---------- Schritt 3: Ergebnis ----------

    string _publicKey = "";
    public string PublicKey { get => _publicKey; private set => Set(ref _publicKey, value); }

    string _serverSnippet = "";
    public string ServerSnippet { get => _serverSnippet; private set => Set(ref _serverSnippet, value); }

    string _configPreview = "";
    public string ConfigPreview { get => _configPreview; private set => Set(ref _configPreview, value); }

    /// <summary>Name und vollständige Konfiguration (inkl. privatem Schlüssel) nach dem letzten Schritt.</summary>
    public (string Name, string Config)? Result { get; private set; }

    // ---------- Aktionen ----------

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
                    if (Validate1() is { } e1) { Error = e1; return false; }
                    await EnsureKeysAsync();
                    break;
                case 1:
                    if (Validate2() is { } e2) { Error = e2; return false; }
                    var cfg = BuildConfig();
                    ConfigPreview = cfg.Serialize(maskSecrets: true);
                    ServerSnippet = BuildServerSnippet();
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
            Info = "Pre-shared Key erzeugt – er steht später auch im Block für den Server.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsWorking = false; }
    }

    // ---------- Prüfung ----------

    string? Validate1()
    {
        if (Name.Trim().Length == 0) return "Bitte einen Namen für den Tunnel eingeben.";
        if (nameExists(Name)) return $"Ein Tunnel „{TunnelStore.SanitizeName(Name)}“ existiert bereits.";

        var addresses = ConfigBuilder.SplitList(Address);
        if (addresses.Count == 0) return "Bitte die Tunnel-Adresse dieses Geräts angeben (z. B. 10.8.0.2/32).";
        if (addresses.FirstOrDefault(a => !Cidr.IsValid(a)) is { } bad) return $"Ungültige Adresse: {bad}";

        if (ConfigBuilder.SplitList(Dns).FirstOrDefault(d => d.Contains('/') || d.Contains('=')) is { } badDns)
            return $"Ungültiger DNS-Eintrag: {badDns}";

        if (UseOwnKey && !WgValidation.IsKey(OwnPrivateKey))
            return "Der private Schlüssel ist ungültig (44 Zeichen Base64, endet auf „=“).";
        return null;
    }

    string? Validate2()
    {
        if (!WgValidation.IsKey(ServerPublicKey))
            return "Der öffentliche Schlüssel des Servers ist ungültig (44 Zeichen Base64, endet auf „=“).";
        if (_privateKey is not null && ServerPublicKey.Trim() == PublicKey)
            return "Das ist der öffentliche Schlüssel dieses Geräts – hier gehört der des Servers hin.";
        if (!WgValidation.IsEndpoint(Endpoint))
            return "Endpoint muss das Format host:port haben, z. B. vpn.example.com:51820 (IPv6 als [Adresse]:port).";
        if (PresharedKey.Trim().Length > 0 && !WgValidation.IsKey(PresharedKey))
            return "Der Pre-shared Key ist ungültig (44 Zeichen Base64). Leer lassen, wenn der Server keinen verwendet.";
        if (IsSplit)
        {
            var nets = ConfigBuilder.SplitList(SplitAllowedIps);
            if (nets.Count == 0) return "Für den Split-Tunnel mindestens ein Netz angeben (z. B. 10.8.0.0/24).";
            if (nets.FirstOrDefault(n => !Cidr.IsValid(n)) is { } bad) return $"Ungültiges Netz: {bad}";
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

    WgConfig BuildConfig()
    {
        var iface = new WgSection("Interface");
        iface.Entries.Add(new WgEntry("PrivateKey", _privateKey ?? throw new InvalidOperationException("Kein Schlüssel vorhanden.")));
        iface.Entries.Add(new WgEntry("Address", string.Join(", ", ConfigBuilder.SplitList(Address))));
        if (ConfigBuilder.SplitList(Dns) is { Count: > 0 } dns) iface.Entries.Add(new WgEntry("DNS", string.Join(", ", dns)));

        var peer = new WgSection("Peer");
        peer.Entries.Add(new WgEntry("PublicKey", ServerPublicKey.Trim()));
        if (PresharedKey.Trim().Length > 0) peer.Entries.Add(new WgEntry("PresharedKey", PresharedKey.Trim()));
        peer.Entries.Add(new WgEntry("AllowedIPs", IsFullTunnel ? FullTunnelRoutes : string.Join(", ", ConfigBuilder.SplitList(SplitAllowedIps))));
        peer.Entries.Add(new WgEntry("Endpoint", Endpoint.Trim()));
        if (int.TryParse(Keepalive.Trim(), out var k) && k > 0) peer.Entries.Add(new WgEntry("PersistentKeepalive", k.ToString()));

        var cfg = new WgConfig();
        cfg.Sections.Add(iface);
        cfg.Sections.Add(peer);
        return cfg;
    }

    string BuildServerSnippet() => ServerPeer.Snippet(TunnelStore.SanitizeName(Name), PublicKey, PresharedKey, Address);
}
