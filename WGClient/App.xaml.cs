using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace WGClient;

public partial class App : Application
{
    // Single-Instance pro Benutzersitzung (Local\), damit mehrere Sitzungen (RDP, Benutzerwechsel) unabhängig bleiben.
    const string MutexName = @"Local\WGClientAppMutex";
    const string ShowEventName = @"Local\WGClientShowWindow";
    const string TakeoverEventName = @"Local\WGClientTakeover";
    const string AdminMarkerName = @"Local\WGClientAdminActive";
    // Nur für den Installer (AppMutex in installer\WGClient.iss): erkennt laufende Instanzen in allen Sitzungen.
    const string PresenceMutexName = @"Global\WGClientAppRunning";

    public const string CoffeeUrl = "https://paypal.me/AdrianZblewski";
    public static bool IsAdmin { get; } = Core.ManagerService.IsAdministrator();

    public static string VersionLabel { get; } = "Version " +
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unbekannt");

    Mutex? _mutex;
    Mutex? _presenceMutex;
    Mutex? _adminMarker;
    WaitHandle? _showEvent;
    WaitHandle? _takeoverEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        var confArg = FindConfArg(e.Args);

        // .conf-Import („Öffnen mit“) erfordert Administratorrechte → direkt per UAC neu starten.
        if (confArg is not null && !IsAdmin)
        {
            TryLaunchElevated(confArg);
            Shutdown();
            return;
        }

        bool isFirst;
        try { _mutex = new Mutex(true, MutexName, out isFirst); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            MessageBox.Show("WG Client kann nicht gestartet werden, weil ein anderer Prozess die Start-Sperre blockiert. " +
                "Bitte abmelden und erneut anmelden.", "WG Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!isFirst && IsAdmin && !IsAdminInstanceRunning())
        {
            // Eine Standardbenutzer-Instanz läuft bereits → sie zum sauberen Beenden auffordern und übernehmen.
            _mutex.Dispose();
            _mutex = null;
            SignalEvent(TakeoverEventName);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(100);
                try
                {
                    var candidate = new Mutex(true, MutexName, out isFirst);
                    if (isFirst) { _mutex = candidate; break; }
                    candidate.Dispose();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { break; }
            }
        }

        if (!isFirst)
        {
            // Läuft schon: ggf. .conf an die laufende Admin-Instanz übergeben, Fenster nach vorne holen, beenden
            if (confArg is not null && IsAdmin)
            {
                try { Core.TunnelStore.QueueImport(confArg); }
                catch { /* ignorieren – Fenster trotzdem nach vorne holen */ }
            }
            SignalEvent(ShowEventName);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        try { _presenceMutex = new Mutex(false, PresenceMutexName); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { /* nur Installer-Hinweis */ }

        if (IsAdmin)
        {
            try { _adminMarker = new Mutex(true, AdminMarkerName, out _); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
            try { new Core.TunnelStore().EnsureCreated(); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "WG Client – Konfigurationsspeicher", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            // Auch Standardbenutzer in derselben Sitzung dürfen das Admin-Fenster in den Vordergrund holen (EVENT_MODIFY_STATE | SYNCHRONIZE).
            _showEvent = TryCreateEventWithSddl(ShowEventName, "D:(A;;0x001F0003;;;SY)(A;;0x001F0003;;;BA)(A;;0x00100002;;;IU)");
        }
        else
        {
            try { _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { _showEvent = null; }
            // Administratoren dürfen diese Standardbenutzer-Instanz zur Übernahme beenden.
            _takeoverEvent = TryCreateEventWithSddl(TakeoverEventName, "D:(A;;0x001F0003;;;SY)(A;;0x001F0003;;;BA)(A;;0x001F0003;;;OW)");
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        var handles = new List<WaitHandle>();
        if (_showEvent is not null) handles.Add(_showEvent);
        if (_takeoverEvent is not null) handles.Add(_takeoverEvent);
        if (handles.Count == 0) return;

        var showHandle = _showEvent;
        var takeoverHandle = _takeoverEvent;
        var waitArray = handles.ToArray();
        var listener = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var idx = WaitHandle.WaitAny(waitArray);
                    var signaled = waitArray[idx];
                    if (ReferenceEquals(signaled, showHandle)) Dispatcher.BeginInvoke(window.OnExternalActivation);
                    else if (ReferenceEquals(signaled, takeoverHandle)) { Dispatcher.BeginInvoke(window.ExitForElevation); break; }
                }
            }
            catch (ObjectDisposedException) { /* reguläres Beenden */ }
            catch (AbandonedMutexException) { }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _showEvent = null;
        _takeoverEvent?.Dispose();
        _takeoverEvent = null;
        _adminMarker?.Dispose();
        _adminMarker = null;
        _presenceMutex?.Dispose();
        _presenceMutex = null;
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }

    public static bool TryLaunchElevated(string? argument = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = argument is null ? "" : $"\"{argument}\"",
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (UAC abgebrochen)
        {
            return false;
        }
    }

    static bool IsAdminInstanceRunning()
    {
        try
        {
            using var existing = Mutex.OpenExisting(AdminMarkerName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; } // existiert, aber nur für Admins zugreifbar
    }

    static void SignalEvent(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            handle.Set();
        }
        catch { /* ignorieren */ }
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

    // ---------- Win32 Event mit expliziter SDDL ----------

    sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    static WaitHandle? TryCreateEventWithSddl(string name, string sddl)
    {
        try
        {
            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, IntPtr.Zero))
            {
                try
                {
                    var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
                    var handle = CreateEventW(ref sa, false, false, name);
                    if (handle != IntPtr.Zero) return new NativeWaitHandle(new SafeWaitHandle(handle, ownsHandle: true));
                }
                finally { LocalFree(sd); }
            }
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr sdSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LocalFree(IntPtr hMem);
}
