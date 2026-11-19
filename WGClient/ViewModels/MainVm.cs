using System.Collections.ObjectModel;
using WGClient.Core;

namespace WGClient.ViewModels;

public sealed class MainVm : ObservableObject
{
    readonly TunnelStore _store = new();
    bool _refreshing;

    public ObservableCollection<TunnelVm> Tunnels { get; } = [];

    TunnelVm? _selected;
    public TunnelVm? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection), nameof(NoSelection));
            LogText = "";
        }
    }

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
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

    public void Load()
    {
        _store.EnsureCreated();
        Tunnels.Clear();
        foreach (var name in _store.ListNames())
        {
            try
            {
                var cfg = WgConfig.Parse(_store.ReadOriginal(name));
                var opts = _store.ReadOptions(name) ?? TunnelOptions.FromConfig(cfg);
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

    /// <returns>Den importierten Tunnel oder null, wenn der Benutzer abgebrochen hat.</returns>
    public TunnelVm? Import(string path, Func<string, bool> confirmOverwrite)
    {
        var text = File.ReadAllText(path);
        var cfg = WgConfig.Parse(text);
        var errors = cfg.Validate();
        if (errors.Count > 0) throw new FormatException(string.Join(Environment.NewLine, errors));

        var name = TunnelStore.SanitizeName(Path.GetFileNameWithoutExtension(path));
        var existing = Tunnels.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !confirmOverwrite(name)) return null;

        var opts = TunnelOptions.FromConfig(cfg);
        _store.SaveOriginal(name, text);
        _store.SaveOptions(name, opts);

        if (existing is not null)
        {
            existing.ReplaceOriginal(cfg, opts);
            StatusMessage = existing.IsActive
                ? $"\"{name}\" ersetzt – wird beim nächsten Verbinden aktiv."
                : $"\"{name}\" ersetzt.";
            return existing;
        }

        var vm = new TunnelVm(name, cfg, opts) { State = WireGuardService.GetState(name) };
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
        var cfg = ConfigBuilder.Build(t.Original, t.Saved);
        var errors = cfg.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        await RunBusy(t, $"Verbinde \"{t.Name}\" …", async () =>
        {
            var path = _store.WriteActive(t.Name, cfg.Serialize());
            await WireGuardService.ConnectAsync(t.Name, path, t.Saved.Autostart);
        });
        StatusMessage = $"\"{t.Name}\" gestartet.";
    }

    public async Task DisconnectAsync(TunnelVm t)
    {
        await RunBusy(t, $"Trenne \"{t.Name}\" …", () => WireGuardService.DisconnectAsync(t.Name));
        StatusMessage = $"\"{t.Name}\" getrennt.";
    }

    public async Task SaveAsync(TunnelVm t)
    {
        if (!t.TryGetOptions(out var opts, out var error))
            throw new InvalidOperationException(error);

        _store.SaveOptions(t.Name, opts);
        t.ApplySaved(opts);
        StatusMessage = $"Einstellungen für \"{t.Name}\" gespeichert.";

        if (t.IsActive) await ConnectAsync(t); // neu verbinden, damit die Änderungen greifen
    }

    public async Task DeleteAsync(TunnelVm t)
    {
        if (t.IsActive || WireGuardService.GetState(t.Name) != TunnelState.Disconnected)
            await WireGuardService.DisconnectAsync(t.Name);
        _store.Delete(t.Name);
        var idx = Tunnels.IndexOf(t);
        Tunnels.Remove(t);
        Selected = Tunnels.Count == 0 ? null : Tunnels[Math.Min(idx, Tunnels.Count - 1)];
        StatusMessage = $"\"{t.Name}\" entfernt.";
        StatesChanged?.Invoke();
    }

    public void Export(TunnelVm t, string path)
    {
        var opts = t.TryGetOptions(out var o, out _) ? o : t.Saved;
        File.WriteAllText(path, ConfigBuilder.Build(t.Original, opts).Serialize());
        StatusMessage = $"Konfiguration exportiert nach {path}";
    }

    public async Task LoadLogAsync()
    {
        LogText = "Lade Log …";
        var text = await WireGuardService.DumpLogAsync(LogOnlySelected ? Selected?.Name : null);
        LogText = string.IsNullOrWhiteSpace(text) ? "(keine Einträge)" : text;
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var before = AnyActive;
            foreach (var t in Tunnels.ToList())
            {
                if (t.IsBusy) continue;
                t.State = WireGuardService.GetState(t.Name);
                t.Stats = t.State == TunnelState.Running ? await WireGuardService.GetStatsAsync(t.Name) : null;
            }
            if (before != AnyActive) OnPropertyChanged(nameof(AnyActive));
            StatesChanged?.Invoke();
        }
        finally { _refreshing = false; }
    }

    async Task RunBusy(TunnelVm t, string message, Func<Task> action)
    {
        t.IsBusy = true;
        StatusMessage = message;
        try { await action(); }
        finally
        {
            t.IsBusy = false;
            t.State = WireGuardService.GetState(t.Name);
            t.Stats = t.State == TunnelState.Running ? await WireGuardService.GetStatsAsync(t.Name) : null;
            StatesChanged?.Invoke();
        }
    }
}
