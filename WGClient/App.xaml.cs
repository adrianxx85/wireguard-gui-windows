using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace WGClient;

public partial class App : Application
{
    // Single-Instance pro Benutzersitzung: Mutex und Event liegen im sitzungslokalen Namespace, damit
    // mehrere angemeldete Benutzer (Schnelle Benutzerumschaltung, RDP) je eine eigene Instanz nutzen können.
    // Hinweis: Weder Local\ noch Global\ schützt vor Namens-Squatting – Mutexe/Events darf jeder Prozess anlegen.
    const string MutexName = @"Local\WGClientAppMutex";
    const string ShowEventName = @"Local\WGClientShowWindow";
    // Nur für den Installer (AppMutex in installer\WGClient.iss): erkennt laufende Instanzen in allen Sitzungen.
    const string PresenceMutexName = @"Global\WGClientAppRunning";

    public const string CoffeeUrl = "https://paypal.me/AdrianZblewski";
    public static string VersionLabel { get; } = "Version " +
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unbekannt");

    Mutex? _mutex;
    Mutex? _presenceMutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool isFirst;
        try { _mutex = new Mutex(true, MutexName, out isFirst); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Ein fremder Prozess hält den Namen mit restriktiven Rechten.
            MessageBox.Show("WG Client kann nicht gestartet werden, weil ein anderer Prozess die Start-Sperre blockiert. " +
                "Bitte abmelden und erneut anmelden.", "WG Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (!isFirst)
        {
            // Läuft schon: ggf. .conf an die laufende Instanz übergeben, Fenster nach vorne holen, beenden
            try
            {
                var conf = FindConfArg(e.Args);
                if (conf is not null) Core.TunnelStore.QueueImport(conf);
            }
            catch { /* ignorieren – Fenster trotzdem nach vorne holen */ }
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                showEvent.Set();
            }
            catch { /* ignorieren */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        try { new Core.TunnelStore().EnsureCreated(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "WG Client – Konfigurationsspeicher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try { _presenceMutex = new Mutex(false, PresenceMutexName); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { /* nur Installer-Hinweis */ }

        try { _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { _showEvent = null; }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (_showEvent is not { } activationEvent) return; // ohne Event: keine Aktivierung durch eine zweite Instanz
        var listener = new Thread(() =>
        {
            try
            {
                while (activationEvent.WaitOne())
                    Dispatcher.BeginInvoke(window.OnExternalActivation);
            }
            catch (ObjectDisposedException) { /* reguläres Beenden */ }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _showEvent = null;
        _presenceMutex?.Dispose();
        _presenceMutex = null;
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }

    public static string? FindConfArg(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

    /// <summary>Öffnet eine HTTPS-URL über explorer.exe, damit der Browser nicht mit Administratorrechten startet.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Es werden nur HTTPS-Links geöffnet.", nameof(url));
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var psi = new ProcessStartInfo(explorer) { UseShellExecute = false };
        psi.ArgumentList.Add(uri.AbsoluteUri);
        Process.Start(psi);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "WG Client – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        if (MainWindow is not { IsLoaded: true })
            Shutdown(1);
    }
}
