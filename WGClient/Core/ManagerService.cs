using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WGClient.Core;

/// <summary>
/// Privilegierter Hintergrunddienst (LocalSystem), der Standardbenutzern über eine lokale Named Pipe
/// ausschließlich das Anzeigen (ohne Schlüssel), Verbinden und Trennen von bereits durch Administratoren
/// eingerichteten Tunneln erlaubt.
/// </summary>
internal static class ManagerService
{
    public const string ManagerSwitch = "/manager";
    public const string InstallSwitch = "/install-manager";
    public const string UninstallSwitch = "/uninstall-manager";

    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier InteractiveSid = new(WellKnownSidType.InteractiveSid, null);
    static readonly SecurityIdentifier NetworkSid = new(WellKnownSidType.NetworkSid, null);

    static readonly ConcurrentDictionary<string, SemaphoreSlim> TunnelLocks = new(StringComparer.OrdinalIgnoreCase);

    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");
    public static string ServiceCommandLine => $"\"{ExePath}\" {ManagerSwitch}";

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void VerifyInstallation()
    {
        if (EmbeddedTunnel.IsAvailable) EmbeddedTunnel.VerifyInstallation();
        else ServiceFiles.Verify(ExePath);
    }

    public static int InstallAndStart()
    {
        VerifyInstallation();
        new TunnelStore().EnsureCreated();
        NativeServices.CreateOrUpdateAndStart(
            ManagerProtocol.ServiceName,
            "WG Client Manager",
            "Ermöglicht Standardbenutzern das Verbinden und Trennen eingerichteter WireGuard-Tunnel über WG Client.",
            ServiceCommandLine,
            autostart: true,
            unrestrictedSid: false);
        return 0;
    }

    public static int Uninstall()
    {
        NativeServices.StopAndDelete(ManagerProtocol.ServiceName, TimeSpan.FromSeconds(15));
        return 0;
    }

    // ---------- Windows Service Control Manager Host ----------

    static CancellationTokenSource? _serviceCts;
    static IntPtr _statusHandle;
    static ServiceMainDelegate? _serviceMainKeepAlive;
    static HandlerExDelegate? _handlerKeepAlive;

    public static int RunAsService()
    {
        VerifyInstallation();
        new TunnelStore().EnsureCreated();

        _serviceMainKeepAlive = ServiceMain;
        var table = new SERVICE_TABLE_ENTRY[]
        {
            new() { lpServiceName = ManagerProtocol.ServiceName, lpServiceProc = _serviceMainKeepAlive },
            new() { lpServiceName = null, lpServiceProc = null },
        };
        return StartServiceCtrlDispatcherW(table) ? 0 : 1;
    }

    static void ServiceMain(uint argc, IntPtr argv)
    {
        _handlerKeepAlive = ControlHandler;
        _statusHandle = RegisterServiceCtrlHandlerExW(ManagerProtocol.ServiceName, _handlerKeepAlive, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;

        ReportStatus(SERVICE_START_PENDING, 0, 5000);
        using var cts = new CancellationTokenSource();
        _serviceCts = cts;

        try
        {
            VerifyInstallation();
            new TunnelStore().EnsureCreated();
            var serverTask = RunPipeServerAsync(ManagerProtocol.PipeName, CreatePipeSecurity(), HandleRequestAsync, cts.Token);
            ReportStatus(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN, 0);
            serverTask.GetAwaiter().GetResult();
            ReportStatus(SERVICE_STOPPED, 0, 0);
        }
        catch
        {
            ReportStatus(SERVICE_STOPPED, 0, 0, exitCode: 1);
        }
        finally
        {
            _serviceCts = null;
        }
    }

    static uint ControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
        {
            ReportStatus(SERVICE_STOP_PENDING, 0, 5000);
            try { _serviceCts?.Cancel(); } catch (ObjectDisposedException) { }
            return 0;
        }
        if (control == SERVICE_CONTROL_INTERROGATE) return 0;
        return ERROR_CALL_NOT_IMPLEMENTED;
    }

    static void ReportStatus(uint state, uint controlsAccepted, uint waitHint, uint exitCode = 0)
    {
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = controlsAccepted,
            dwWin32ExitCode = exitCode,
            dwWaitHint = waitHint,
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    // ---------- Named Pipe Server ----------

    static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        // Remote-SMB-Zugriffe explizit blockieren; nur lokal/RDP angemeldete Benutzer zulassen.
        security.AddAccessRule(new PipeAccessRule(NetworkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(SystemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(AdministratorsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(InteractiveSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    internal static async Task RunPipeServerAsync(
        string pipeName,
        PipeSecurity? security,
        Func<ManagerRequest, Task<ManagerResponse>> handler,
        CancellationToken token)
    {
        var activeClients = new List<Task>();
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = security is not null
                    ? NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security)
                    : new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch when (token.IsCancellationRequested) { break; }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var clientTask = HandleClientAsync(pipe, handler, token);
            activeClients.RemoveAll(t => t.IsCompleted);
            activeClients.Add(clientTask);
        }

        if (activeClients.Count > 0)
            await Task.WhenAll(activeClients).ConfigureAwait(false);
    }

    static async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        Func<ManagerRequest, Task<ManagerResponse>> handler,
        CancellationToken token)
    {
        await using (pipe)
        {
            try
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readCts.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ManagerProtocol.ReadMessageAsync<ManagerRequest>(pipe, ManagerProtocol.MaxRequestBytes, readCts.Token)
                    .ConfigureAwait(false);
                if (request is null) return;

                ManagerResponse response;
                try { response = await handler(request).ConfigureAwait(false); }
                catch (Exception ex) { response = new ManagerResponse(false, ex.Message); }

                await ManagerProtocol.WriteMessageAsync(pipe, response, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                try { await ManagerProtocol.WriteMessageAsync(pipe, new ManagerResponse(false, ex.Message), token).ConfigureAwait(false); }
                catch { /* Client bereits getrennt */ }
            }
        }
    }

    // ---------- Strikte Befehlsausführung (Allowlist) ----------

    static async Task<ManagerResponse> HandleRequestAsync(ManagerRequest request) =>
        request.Command switch
        {
            "ping" => new ManagerResponse(true),
            "list" => new ManagerResponse(true, Tunnels: BuildSnapshots()),
            "states" => new ManagerResponse(true, States: await QueryStatesAsync().ConfigureAwait(false)),
            "connect" => await ConnectTunnelAsync(request.Name).ConfigureAwait(false),
            "disconnect" => await DisconnectTunnelAsync(request.Name).ConfigureAwait(false),
            "log" => await ReadLogAsync(request.Name).ConfigureAwait(false),
            _ => new ManagerResponse(false, "Unbekannter Befehl."),
        };

    static TunnelStore CreateStore()
    {
        var store = new TunnelStore();
        store.EnsureCreated();
        return store;
    }

    static string? ResolveExistingTunnel(TunnelStore store, string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName)) return null;
        return store.ListNames().FirstOrDefault(n => n.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
    }

    internal static TunnelSnapshot CreateSnapshot(string name, WgConfig original, TunnelOptions options, TunnelState state)
    {
        var effective = ConfigBuilder.Build(original, options);
        var usesManualRoutes = ConfigBuilder.UsesManualRoutes(original);
        string routingSummary;
        if (usesManualRoutes)
        {
            routingSummary = "Manuelle Routen (Table = off)";
        }
        else
        {
            var summary = options.Mode == RouteMode.Split
                ? "Split-Tunnel: " + (string.IsNullOrWhiteSpace(options.SplitAllowedIps) ? "—" : options.SplitAllowedIps)
                : "Gesamter Traffic" + (options.AllowLan ? " · LAN erreichbar" : "") + (options.IncludeIPv6 ? " · inkl. IPv6" : "");
            routingSummary = ConfigBuilder.HasKillSwitch(effective) ? summary + " · Kill-Switch" : summary;
        }

        // Sicherheit: PrivateKey und PresharedKey werden NIEMALS über die Pipe übertragen.
        return new TunnelSnapshot(
            Name: name,
            Options: options,
            AddressText: original.Interface?.Get("Address") ?? "—",
            PeerCount: original.Peers.Count(),
            UsesManualRoutes: usesManualRoutes,
            SupportsKillSwitch: ConfigBuilder.SupportsKillSwitch(original),
            RoutingSummary: routingSummary,
            AllowedIpsPreview: effective.Peers.FirstOrDefault()?.Get("AllowedIPs") ?? "",
            EffectivePreview: effective.Serialize(maskSecrets: true),
            State: state);
    }

    static List<TunnelSnapshot> BuildSnapshots()
    {
        var store = CreateStore();
        var result = new List<TunnelSnapshot>();
        foreach (var name in store.ListNames())
        {
            try
            {
                var profile = store.ReadProfile(name);
                var cfg = WgConfig.Parse(profile.Config);
                var opts = profile.Options ?? TunnelOptions.FromConfig(cfg);
                opts.UpdateImportedDefaults(cfg);
                result.Add(CreateSnapshot(name, cfg, opts, WireGuardService.GetState(name)));
            }
            catch { /* defekte Einzelprofile überspringen */ }
        }
        return result;
    }

    static async Task<List<TunnelStateSnapshot>> QueryStatesAsync()
    {
        var store = CreateStore();
        var result = new List<TunnelStateSnapshot>();
        foreach (var name in store.ListNames())
        {
            var state = WireGuardService.GetState(name);
            var stats = state == TunnelState.Running ? await WireGuardService.GetStatsAsync(name).ConfigureAwait(false) : null;
            result.Add(new TunnelStateSnapshot(name, state, stats));
        }
        return result;
    }

    static async Task<ManagerResponse> ConnectTunnelAsync(string? requestedName)
    {
        var store = CreateStore();
        var name = ResolveExistingTunnel(store, requestedName)
            ?? throw new InvalidOperationException("Der angeforderte Tunnel existiert nicht.");
        var gate = TunnelLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var profile = store.ReadProfile(name);
            var original = WgConfig.Parse(profile.Config);
            var opts = profile.Options ?? TunnelOptions.FromConfig(original);
            opts.UpdateImportedDefaults(original);
            var effective = ConfigBuilder.Build(original, opts);
            var errors = effective.Validate();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

            var running = await WireGuardService.ConnectAsync(name, opts.Autostart, () => store.WriteActive(name, effective.Serialize()))
                .ConfigureAwait(false);
            return new ManagerResponse(true, Running: running, State: WireGuardService.GetState(name));
        }
        finally { gate.Release(); }
    }

    static async Task<ManagerResponse> DisconnectTunnelAsync(string? requestedName)
    {
        var store = CreateStore();
        var name = ResolveExistingTunnel(store, requestedName)
            ?? throw new InvalidOperationException("Der angeforderte Tunnel existiert nicht.");
        var gate = TunnelLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WireGuardService.DisconnectAsync(name).ConfigureAwait(false);
            return new ManagerResponse(true, State: WireGuardService.GetState(name));
        }
        finally { gate.Release(); }
    }

    static async Task<ManagerResponse> ReadLogAsync(string? requestedFilter)
    {
        var store = CreateStore();
        string? filter = null;
        if (!string.IsNullOrWhiteSpace(requestedFilter))
            filter = ResolveExistingTunnel(store, requestedFilter)
                ?? throw new InvalidOperationException("Der angeforderte Tunnel existiert nicht.");
        var log = await WireGuardService.DumpLogAsync(filter).ConfigureAwait(false);
        return new ManagerResponse(true, Log: log);
    }

    // ---------- Win32 SCM P/Invoke ----------

    delegate void ServiceMainDelegate(uint argc, IntPtr argv);
    delegate uint HandlerExDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_TABLE_ENTRY
    {
        public string? lpServiceName;
        public ServiceMainDelegate? lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                    dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    const uint SERVICE_STOPPED = 1;
    const uint SERVICE_START_PENDING = 2;
    const uint SERVICE_STOP_PENDING = 3;
    const uint SERVICE_RUNNING = 4;
    const uint SERVICE_ACCEPT_STOP = 0x1;
    const uint SERVICE_ACCEPT_SHUTDOWN = 0x4;
    const uint SERVICE_CONTROL_STOP = 1;
    const uint SERVICE_CONTROL_INTERROGATE = 4;
    const uint SERVICE_CONTROL_SHUTDOWN = 5;
    const uint ERROR_CALL_NOT_IMPLEMENTED = 120;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRY[] serviceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerExDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceStatus(IntPtr statusHandle, ref SERVICE_STATUS status);
}
