using System.Collections.ObjectModel;
using WGClient.Core;

namespace WGClient.ViewModels;

public sealed class MainVm : ObservableObject
{
    readonly TunnelStore _store = new();
    bool _refreshing;
    int _logRequestVersion;

    public MainVm() : this(App.IsAdmin) { }

    internal MainVm(bool isAdmin)
    {
        IsAdmin = isAdmin;
        Tunnels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTunnels));
    }

    public bool IsAdmin { get; }
    public bool IsStandardUser => !IsAdmin;

    bool _managerUnavailable;
    public bool ManagerUnavailable { get => _managerUnavailable; private set => Set(ref _managerUnavailable, value); }

    public ObservableCollection<TunnelVm> Tunnels { get; } = [];

    TunnelVm? _selected;
    public TunnelVm? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection), nameof(NoSelection));
            _logRequestVersion++;
            LogText = "";
        }
    }

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
    public bool HasNoTunnels => Tunnels.Count == 0;
    public bool WireGuardMissing => !WireGuardService.IsInstalled;
    public bool AnyActive => Tunnels.Any(t => t.IsActive);

    string _statusMessage = "Bereit";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    string _logText = "";
    public string LogText { get => _logText; set => Set(ref _logText, value); }

    bool _logOnlySelected = true;
    public bool LogOnlySelected { get => _logOnlySelected; set => Set(ref _logOnlySelected, value); }

    /// <summary>Wird ausgelöst, wenn sich der Verbindungsstatus irgendeines Tunnels ändert (Tray-Icon).</summary>
    public event Action? StatesChanged;

    public async Task LoadAsync()
    {
        if (IsAdmin)
        {
            LoadDirect();
            return;
        }
        await LoadViaManagerAsync();
    }

    void LoadDirect()
    {
        Tunnels.Clear();
        foreach (var name in _store.ListNames())
        {
            try
            {
                var profile = _store.ReadProfile(name);
                var cfg = WgConfig.Parse(profile.Config);
                var opts = profile.Options ?? TunnelOptions.FromConfig(cfg);
                opts.UpdateImportedDefaults(cfg);
                Tunnels.Add(new TunnelVm(name, cfg, opts));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Tunnel \"{name}\" konnte nicht geladen werden: {ex.Message}";
            }
        }
        Selected = Tunnels.FirstOrDefault();
        foreach (var t in Tunnels) t.State = WireGuardService.GetState(t.Name);
    }

    async Task LoadViaManagerAsync()
    {
        var previousName = Selected?.Name;
        try
        {
            var snapshots = await ManagerClient.ListAsync();
            ManagerUnavailable = false;
            Tunnels.Clear();
            foreach (var snapshot in snapshots)
                Tunnels.Add(new TunnelVm(snapshot));
            Selected = previousName is not null
                ? Tunnels.FirstOrDefault(t => t.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase)) ?? Tunnels.FirstOrDefault()
                : Tunnels.FirstOrDefault();
            StatusMessage = Tunnels.Count == 0 ? "Keine Tunnel eingerichtet." : "Bereit (Standardbenutzer)";
        }
        catch (Exception ex)
        {
            ManagerUnavailable = true;
            StatusMessage = ex.Message;
        }
    }

    /// <returns>Den importierten Tunnel oder null, wenn der Benutzer abgebrochen hat.</returns>
    public TunnelVm? Import(string path, Func<string, bool> confirmOverwrite)
    {
        EnsureAdmin();
        return Add(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path), confirmOverwrite);
    }

    public bool NameExists(string name)
    {
        var sanitized = TunnelStore.SanitizeName(name);
        return Tunnels.Any(t => t.Name.Equals(sanitized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tunnel-Adressen aller bekannten Tunnel – der Assistent schlägt nur freie Netze vor.</summary>
    public List<string> TunnelAddresses() =>
        Tunnels.SelectMany(t => ConfigBuilder.SplitList(t.Original?.Interface?.Get("Address") ?? t.AddressText)).ToList();

    /// <summary>Legt einen Tunnel aus Konfigurationstext an (Import oder Assistent).</summary>
    /// <returns>Den Tunnel oder null, wenn der Benutzer das Überschreiben abgelehnt hat.</returns>
    public TunnelVm? Add(string requestedName, string text, Func<string, bool> confirmOverwrite)
    {
        EnsureAdmin();
        var cfg = WgConfig.Parse(text);
        var errors = cfg.Validate();
        if (errors.Count > 0) throw new FormatException(string.Join(Environment.NewLine, errors));

        var name = TunnelStore.SanitizeName(requestedName);
        var existing = Tunnels.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            EnsureIdle(existing);
            if (!confirmOverwrite(name)) return null;
            EnsureIdle(existing);
        }

        var opts = TunnelOptions.FromConfig(cfg);
        var vm = existing ?? new TunnelVm(name, cfg, opts) { State = WireGuardService.GetState(name) };
        _store.SaveProfile(name, text, opts);

        if (existing is not null)
        {
            existing.ReplaceOriginal(cfg, opts);
            StatusMessage = existing.IsActive
                ? $"\"{name}\" ersetzt – wird beim nächsten Verbinden aktiv."
                : $"\"{name}\" ersetzt.";
            return existing;
        }

        var idx = 0;
        while (idx < Tunnels.Count && string.Compare(Tunnels[idx].Name, name, StringComparison.OrdinalIgnoreCase) < 0) idx++;
        Tunnels.Insert(idx, vm);
        StatusMessage = $"\"{name}\" importiert.";
        return vm;
    }

    public async Task ToggleAsync(TunnelVm t)
    {
        if (t.IsActive) await DisconnectAsync(t);
        else await ConnectAsync(t);
    }

    public async Task ConnectAsync(TunnelVm t)
    {
        var running = false;
        if (IsAdmin)
        {
            var original = t.Original ?? throw new InvalidOperationException("Konfiguration ist nicht geladen.");
            var cfg = ConfigBuilder.Build(original, t.Saved);
            var errors = cfg.Validate();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

            await RunBusy(t, $"Verbinde \"{t.Name}\" …", async () =>
                running = await WireGuardService.ConnectAsync(t.Name, t.Saved.Autostart, () => _store.WriteActive(t.Name, cfg.Serialize())));
        }
        else
        {
            await RunBusy(t, $"Verbinde \"{t.Name}\" …", async () =>
                running = await ManagerClient.ConnectAsync(t.Name));
        }

        StatusMessage = running
            ? $"\"{t.Name}\" gestartet."
            : $"\"{t.Name}\" startet noch (WireGuard wartet z. B. auf Netzwerk/DNS). Details im Log.";
    }

    public async Task DisconnectAsync(TunnelVm t)
    {
        if (IsAdmin)
            await RunBusy(t, $"Trenne \"{t.Name}\" …", () => WireGuardService.DisconnectAsync(t.Name));
        else
            await RunBusy(t, $"Trenne \"{t.Name}\" …", () => ManagerClient.DisconnectAsync(t.Name));

        StatusMessage = $"\"{t.Name}\" getrennt.";
    }

    public async Task SaveAsync(TunnelVm t, bool reconnectIfActive = true)
    {
        EnsureAdmin();
        EnsureIdle(t);
        if (!t.TryGetOptions(out var opts, out var error))
            throw new InvalidOperationException(error);

        _store.SaveProfile(t.Name, _store.ReadProfile(t.Name).Config, opts);
        t.ApplySaved(opts);
        StatusMessage = $"Einstellungen für \"{t.Name}\" gespeichert.";

        if (reconnectIfActive && t.IsActive)
        {
            try { await ConnectAsync(t); } // neu verbinden, damit die Änderungen greifen
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Einstellungen wurden gespeichert, aber das erneute Verbinden ist fehlgeschlagen: {ex.Message}", ex);
            }
        }
    }

    public async Task DeleteAsync(TunnelVm t)
    {
        EnsureAdmin();
        await RunBusy(t, $"Entferne \"{t.Name}\" …", async () =>
        {
            await WireGuardService.DisconnectAsync(t.Name);
            _store.Delete(t.Name);
            var idx = Tunnels.IndexOf(t);
            Tunnels.Remove(t);
            Selected = Tunnels.Count == 0 ? null : Tunnels[Math.Min(idx, Tunnels.Count - 1)];
        });
        StatusMessage = $"\"{t.Name}\" entfernt.";
    }

    public void Export(TunnelVm t, string path)
    {
        EnsureAdmin();
        var original = t.Original ?? throw new InvalidOperationException("Konfiguration ist nicht geladen.");
        if (!t.TryGetOptions(out var opts, out var error))
            throw new InvalidOperationException(error ?? "Ungültige Optionen können nicht exportiert werden.");
        File.WriteAllText(path, ConfigBuilder.Build(original, opts).Serialize());
        StatusMessage = $"Konfiguration exportiert nach {path}";
    }

    public async Task LoadLogAsync()
    {
        var version = ++_logRequestVersion;
        var filter = LogOnlySelected ? Selected?.Name : null;
        LogText = "Lade Log …";
        var text = IsAdmin
            ? await WireGuardService.DumpLogAsync(filter)
            : await ManagerClient.DumpLogAsync(filter);
        if (version != _logRequestVersion) return;
        LogText = string.IsNullOrWhiteSpace(text) ? "(keine Einträge)" : text;
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var before = AnyActive;
            if (IsAdmin)
            {
                foreach (var t in Tunnels.ToList())
                {
                    if (t.IsBusy || !Tunnels.Contains(t)) continue;
                    var state = await Task.Run(() => WireGuardService.GetState(t.Name));
                    if (t.IsBusy || !Tunnels.Contains(t)) continue;
                    t.State = state;
                    if (state == TunnelState.Running)
                    {
                        var stats = await WireGuardService.GetStatsAsync(t.Name);
                        if (!t.IsBusy && t.State == TunnelState.Running && Tunnels.Contains(t))
                            t.Stats = stats;
                    }
                    else
                    {
                        t.Stats = null;
                    }
                }
            }
            else
            {
                try
                {
                    var states = await ManagerClient.GetStatesAsync();
                    var wasUnavailable = ManagerUnavailable;
                    ManagerUnavailable = false;
                    var knownNames = Tunnels.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var remoteNames = states.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (wasUnavailable || !knownNames.SetEquals(remoteNames))
                    {
                        await LoadViaManagerAsync();
                    }
                    else
                    {
                        var byName = states.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
                        foreach (var t in Tunnels.ToList())
                        {
                            if (t.IsBusy || !byName.TryGetValue(t.Name, out var s)) continue;
                            t.State = s.State;
                            t.Stats = s.State == TunnelState.Running ? s.Stats : null;
                        }
                    }
                }
                catch
                {
                    ManagerUnavailable = true;
                }
            }
            if (before != AnyActive) OnPropertyChanged(nameof(AnyActive));
            StatesChanged?.Invoke();
        }
        finally { _refreshing = false; }
    }

    async Task RunBusy(TunnelVm t, string message, Func<Task> action)
    {
        EnsureIdle(t);
        t.IsBusy = true;
        StatusMessage = message;
        try { await action(); }
        finally
        {
            try
            {
                if (IsAdmin)
                {
                    t.State = WireGuardService.GetState(t.Name);
                }
                else
                {
                    try
                    {
                        var states = await ManagerClient.GetStatesAsync();
                        if (states.FirstOrDefault(s => s.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) is { } s)
                            t.State = s.State;
                    }
                    catch { /* nächster Timer-Tick aktualisiert den Status */ }
                }
                t.Stats = null;
                StatesChanged?.Invoke();
            }
            finally { t.IsBusy = false; }
        }
    }

    void EnsureAdmin()
    {
        if (!IsAdmin)
            throw new UnauthorizedAccessException("Diese Aktion erfordert Administratorrechte.");
    }

    void EnsureIdle(TunnelVm t)
    {
        if (t.IsBusy || !Tunnels.Contains(t))
            throw new InvalidOperationException($"Der Tunnel \"{t.Name}\" ist derzeit nicht verfügbar.");
    }
}
