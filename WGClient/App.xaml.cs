using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace WGClient;

public partial class App : Application
{
    // Name muss mit AppMutex im Installer (installer\WGClient.iss) übereinstimmen
    const string MutexName = "WGClientAppMutex";
    const string ShowEventName = "WGClientShowWindow";

    public const string CoffeeUrl = "https://paypal.me/AdrianZblewski";

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
                if (conf is not null) File.WriteAllText(PendingImportFile, Path.GetFullPath(conf));
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch { /* ignorieren */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(window.OnExternalActivation);
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Übergabedatei für "Öffnen mit", wenn bereits eine Instanz läuft (Ordner nur für Admins).</summary>
    public static string PendingImportFile => Path.Combine(Core.TunnelStore.Root, "pending-import.txt");

    public static string? FindConfArg(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "WG Client – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
