using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using WGClient.ViewModels;

namespace WGClient;

public partial class MainWindow : Window
{
    readonly MainVm _vm = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    TrayIcon? _tray;
    bool _exiting;
    bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
        _timer.Tick += async (_, _) =>
        {
            try { await _vm.RefreshAsync(); }
            catch (Exception ex) { _vm.StatusMessage = "Status konnte nicht gelesen werden: " + ex.Message; }
        };
        _vm.StatesChanged += UpdateTray;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { _vm.Load(); }
        catch (Exception ex) { ShowError("Tunnel konnten nicht geladen werden", ex); }

        _tray = new TrayIcon(ShowFromTray, () => App.OpenUrl(App.CoffeeUrl), ExitApp);
        UpdateTray();
        _timer.Start();

        // .conf als Startargument (z. B. "Öffnen mit") sowie ggf. während des Starts eingereihte Imports
        var arg = App.FindConfArg(Environment.GetCommandLineArgs().Skip(1));
        if (arg is not null) ImportFile(arg);
        ImportPendingFiles();
    }

    /// <summary>Eine zweite Instanz wurde gestartet: Fenster zeigen und ggf. übergebene .conf importieren.</summary>
    public void OnExternalActivation()
    {
        ShowFromTray();
        ImportPendingFiles();
    }

    void ImportPendingFiles()
    {
        try
        {
            var paths = Core.TunnelStore.TakePendingImports(out var error);
            foreach (var path in paths)
                if (File.Exists(path)) ImportFile(path);
            if (error is not null)
                _vm.StatusMessage = "Eine übergebene Datei konnte noch nicht gelesen werden: " + error;
        }
        catch (Exception ex) { _vm.StatusMessage = "Übergebene Datei konnte nicht importiert werden: " + ex.Message; }
    }

    // ---------- Fenster / Tray ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloon("WG Client läuft weiter", "Aktive Tunnel bleiben verbunden. Beenden über das Tray-Symbol.");
            }
            return;
        }
        _timer.Stop();
        _tray?.Dispose();
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void UpdateTray()
    {
        var active = _vm.Tunnels.Where(t => t.IsActive).Select(t => t.Name).ToList();
        var stopping = _vm.Tunnels.Any(t => t.State == Core.TunnelState.Stopping);
        var status = active.Count > 0 ? string.Join(", ", active) : stopping ? "Trenne …" : "getrennt";
        _tray?.SetStatus(active.Count > 0 || stopping, "WG Client – " + status);
    }

    void Coffee_Click(object sender, RoutedEventArgs e) => App.OpenUrl(App.CoffeeUrl);

    void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(() => _vm.Tunnels
        .Where(t => t.IsBusy || t.State != Core.TunnelState.Disconnected).Select(t => t.Name).ToArray()) { Owner = this }.ShowDialog();

    // ---------- Aktionen ----------

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "WireGuard-Konfiguration importieren",
            Filter = "WireGuard-Konfiguration (*.conf)|*.conf|Alle Dateien (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var file in dlg.FileNames) ImportFile(file);
    }

    void NewTunnel_Click(object sender, RoutedEventArgs e)
    {
        TunnelVm? created = null;
        // Gespeichert wird noch im Assistenten: Schlägt es fehl, bleibt er mit dem Schlüsselpaar offen.
        var wizard = new NewTunnelWindow(_vm.NameExists, _vm.TunnelAddresses(), (name, config) =>
            created = _vm.Add(name, config, _ => false)
                ?? throw new InvalidOperationException($"Ein Tunnel \"{name}\" existiert bereits. Bitte unter „Zurück“ einen anderen Namen wählen."))
        { Owner = this };
        if (wizard.ShowDialog() != true || created is null) return;
        _vm.Selected = created;
        _vm.StatusMessage = $"\"{created.Name}\" erstellt. Sobald der Peer auf dem Server eingetragen ist, kannst du verbinden.";
    }

    void ImportFile(string path)
    {
        try
        {
            var t = _vm.Import(path, name => MessageBox.Show(this,
                $"Ein Tunnel \"{name}\" existiert bereits. Konfiguration ersetzen?\n\nEigene Optionen werden dabei zurückgesetzt.",
                "Importieren", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            if (t is not null) _vm.Selected = t;
        }
        catch (Exception ex)
        {
            ShowError($"Import von \"{Path.GetFileName(path)}\" fehlgeschlagen", ex);
        }
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } t) return;
        var msg = $"Tunnel \"{t.Name}\" entfernen?" + (t.IsActive ? "\n\nDer Tunnel wird dabei getrennt." : "");
        if (MessageBox.Show(this, msg, "Tunnel entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _vm.DeleteAsync(t); }
        catch (Exception ex) { ShowError("Entfernen fehlgeschlagen", ex); }
    }

    async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } t) return;
        var disconnect = t.IsActive;
        if (!disconnect && t.IsDirty)
        {
            var r = MessageBox.Show(this, "Es gibt ungespeicherte Optionen. Vor dem Verbinden speichern?",
                "Verbinden", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes)
            {
                try { await _vm.SaveAsync(t, reconnectIfActive: false); }
                catch (Exception ex) { ShowError("Speichern fehlgeschlagen", ex); return; }
            }
        }
        try
        {
            if (disconnect) await _vm.DisconnectAsync(t);
            else await _vm.ConnectAsync(t);
        }
        catch (Exception ex) { ShowError(disconnect ? "Trennen fehlgeschlagen" : "Verbinden fehlgeschlagen", ex); }
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } t) return;
        try { await _vm.SaveAsync(t); }
        catch (Exception ex) { ShowError("Speichern fehlgeschlagen", ex); }
    }

    void Revert_Click(object sender, RoutedEventArgs e) => _vm.Selected?.LoadEdit(_vm.Selected.Saved);

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } t) return;
        var dlg = new SaveFileDialog
        {
            Title = "Konfiguration exportieren",
            FileName = t.Name + ".conf",
            Filter = "WireGuard-Konfiguration (*.conf)|*.conf",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.Export(t, dlg.FileName); }
        catch (Exception ex) { ShowError("Export fehlgeschlagen", ex); }
    }

    async void CopyServerPeer_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { SelectedServerPeer: { } selectedPeer } t) return;
        var original = t.Original;
        try
        {
            var snippet = await Core.ServerPeer.SnippetAsync(t.Name, original, selectedPeer.Peer);
            if (_vm.Selected != t || t.Original != original || t.SelectedServerPeer != selectedPeer) return;
            PrivateClipboard.SetText(snippet);
            _vm.StatusMessage = $"[Peer]-Block für \"{t.Name}\" in die Zwischenablage kopiert – auf dem Server eintragen.";
        }
        catch (Exception ex) { ShowError("Server-Block konnte nicht erstellt werden", ex); }
    }

    async void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.LoadLogAsync();
            LogBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            _vm.LogText = "";
            ShowError("Log konnte nicht gelesen werden", ex);
        }
    }

    void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        App.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    // ---------- Drag & Drop ----------

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files.Where(f => f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))) ImportFile(f);
    }

    // ---------- Hilfen ----------

    void ShowError(string title, Exception ex)
    {
        _vm.StatusMessage = $"{title}: {ex.Message}";
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
