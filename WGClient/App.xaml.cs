using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace WGClient;

public partial class App : Application
{
    // Name muss mit AppMutex im Installer (installer\WGClient.iss) übereinstimmen.
    // Global\ verhindert Mutex-/Event-Squatting durch unprivilegierte Prozesse in derselben Session.
    const string MutexName = @"Global\WGClientAppMutex";
    const string ShowEventName = @"Global\WGClientShowWindow";

    public const string CoffeeUrl = "https://paypal.me/AdrianZblewski";
    public static string VersionLabel { get; } = "Version " +
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unbekannt");

    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out var isFirst);
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

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        var activationEvent = _showEvent;
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
        if (MainWindow is { IsLoaded: true })
            e.Handled = true;
        else
            Shutdown(1);
    }
}
