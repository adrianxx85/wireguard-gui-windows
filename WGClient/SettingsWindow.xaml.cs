using System.Windows;
using WGClient.Core;

namespace WGClient;

public partial class SettingsWindow : Window
{
    readonly bool _standalone = WireGuardService.Embedded;
    readonly Func<string[]> _activeTunnels;
    readonly CancellationTokenSource _closing = new();
    Uri? _download;
    Version? _officialUpdate;
    bool _closed;

    public SettingsWindow(Func<string[]> activeTunnels)
    {
        _activeTunnels = activeTunnels;
        InitializeComponent();
        EditionLabel.Text = _standalone ? "Standalone" : "Standard";
        ComponentLabel.Text = _standalone ? "Mitgeliefertes WireGuardNT" : "WireGuard für Windows";
        RefreshInstalledVersion();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _closing.Cancel();
        _closing.Dispose();
        base.OnClosed(e);
    }

    Version? RefreshInstalledVersion()
    {
        var path = _standalone ? EmbeddedTunnel.DriverPath : WireGuardService.WireGuardExe;
        var version = WireGuardUpdates.ReadVersion(path);
        InstalledVersionLabel.Text = version?.ToString() ?? (File.Exists(path) ? "Version unbekannt" : "Fehlt");
        return version;
    }

    void ShowAction(string? text)
    {
        UpdateButton.Content = text;
        UpdateButton.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        _download = null;
        _officialUpdate = null;
        ShowAction(null);
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "Suche nach Updates …";
        try
        {
            var installed = RefreshInstalledVersion();
            if (_standalone)
            {
                var release = await WireGuardUpdates.CheckStandaloneAsync(_closing.Token);
                if (_closed) return;
                var appVersion = typeof(App).Assembly.GetName().Version!;
                if (WireGuardUpdates.IsNewer(release.Version, appVersion))
                {
                    _download = release.Installer;
                    ShowAction("Standalone-Installer herunterladen");
                    UpdateStatus.Text = $"WG Client Standalone {release.Version} ist verfügbar.";
                }
                else UpdateStatus.Text = $"WG Client Standalone ist aktuell. Letztes Release: {release.Version}.";
            }
            else
            {
                var latest = await WireGuardUpdates.CheckOfficialAsync(_closing.Token);
                if (_closed) return;
                if (!File.Exists(WireGuardService.WireGuardExe))
                {
                    _download = new Uri(WireGuardUpdates.OfficialDownloads);
                    ShowAction("WireGuard herunterladen");
                    UpdateStatus.Text = $"WireGuard für Windows {latest} steht zur Installation bereit.";
                }
                else if (installed is null || WireGuardUpdates.IsNewer(latest, installed))
                {
                    if (!App.IsAdmin)
                    {
                        UpdateStatus.Text = $"WireGuard für Windows {latest} ist verfügbar. Zum Aktualisieren bitte WG Client als Administrator öffnen.";
                    }
                    else
                    {
                        _officialUpdate = latest;
                        ShowAction("WireGuard aktualisieren");
                        UpdateStatus.Text = installed is null
                            ? $"Der offizielle Updater kann die Installation mit Version {latest} abgleichen."
                            : $"WireGuard für Windows {latest} ist verfügbar.";
                    }
                }
                else UpdateStatus.Text = $"WireGuard für Windows ist aktuell. Letzte Version: {latest}.";
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) UpdateStatus.Text = "Updateprüfung fehlgeschlagen. " + ex.Message;
        }
        finally
        {
            if (!_closed) CheckButton.IsEnabled = true;
        }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        var download = _download;
        var officialUpdate = _officialUpdate;
        if ((download is null && officialUpdate is null) || !ConfirmUpdate()) return;

        CheckButton.IsEnabled = UpdateButton.IsEnabled = false;
        try
        {
            if (download is not null)
            {
                App.OpenUrl(download.AbsoluteUri);
                UpdateStatus.Text = _standalone
                    ? "Der Installer wird im Browser heruntergeladen. Beende WG Client über das Tray-Symbol, bevor du ihn ausführst."
                    : "Die offizielle WireGuard-Downloadseite wurde geöffnet.";
                return;
            }

            UpdateStatus.Text = "Der offizielle WireGuard-Updater läuft …";
            var killedByInstaller = await WireGuardUpdates.RunOfficialUpdaterAsync();
            if (_closed) return;
            var installed = RefreshInstalledVersion();
            if (killedByInstaller && (installed is null || WireGuardUpdates.IsNewer(officialUpdate!, installed)))
            {
                UpdateStatus.Text = "WireGuard-Installation wird abgeschlossen …";
                installed = await WireGuardUpdates.WaitForOfficialVersionAsync(officialUpdate!, TimeSpan.FromSeconds(60), _closing.Token);
                if (_closed) return;
                installed = RefreshInstalledVersion() ?? installed;
            }
            if (installed is not null && !WireGuardUpdates.IsNewer(officialUpdate!, installed))
            {
                _officialUpdate = null;
                ShowAction(null);
                UpdateStatus.Text = $"WireGuard für Windows {installed} ist installiert.";
            }
            else UpdateStatus.Text = "Die erwartete Version wurde noch nicht erkannt. Bitte prüfe das WireGuard-Protokoll und suche erneut nach Updates.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) UpdateStatus.Text = "Update konnte nicht gestartet oder abgeschlossen werden. " + ex.Message;
        }
        finally
        {
            if (!_closed) CheckButton.IsEnabled = UpdateButton.IsEnabled = true;
        }
    }

    bool ConfirmUpdate()
    {
        var tunnels = _activeTunnels();
        var message = "Bei der Installation können laufende WireGuard-Tunnel vorübergehend getrennt werden.";
        if (tunnels.Length > 0)
            message += Environment.NewLine + Environment.NewLine + "Aktive WG-Client-Tunnel: " + string.Join(", ", tunnels);
        return MessageBox.Show(this, message, _download is null ? "WireGuard aktualisieren" : "Download öffnen",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
