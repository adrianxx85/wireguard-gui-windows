using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WGClient.Core;

/// <summary>Minimale Win32-Abfrage des Dienststatus (ohne NuGet-Abhängigkeit).</summary>
internal static class NativeServices
{
    public enum Status : uint
    {
        Stopped = 1, StartPending = 2, StopPending = 3, Running = 4,
        ContinuePending = 5, PausePending = 6, Paused = 7,
        MarkedForDeletion = uint.MaxValue,
    }

    /// <returns>Status des Dienstes oder null, wenn er nicht existiert.</returns>
    public static Status? Query(string serviceName)
    {
        using var service = OpenForQuery(serviceName, out var markedForDeletion);
        return service?.QueryStatus() ?? (markedForDeletion ? Status.MarkedForDeletion : null);
    }

    static ServiceHandle? OpenForQuery(string serviceName, out bool markedForDeletion) =>
        Open(serviceName, SERVICE_QUERY_STATUS, out markedForDeletion);

    public static ServiceHandle? OpenForRemoval(string serviceName, out bool markedForDeletion) =>
        Open(serviceName, SERVICE_QUERY_STATUS | SERVICE_STOP | DELETE, out markedForDeletion);

    static ServiceHandle? Open(string serviceName, uint access, out bool markedForDeletion)
    {
        markedForDeletion = false;
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var service = OpenServiceW(scm, serviceName, access);
            if (!service.IsInvalid) return service;
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            markedForDeletion = error == ERROR_SERVICE_MARKED_FOR_DELETE;
            if (markedForDeletion || error == ERROR_SERVICE_DOES_NOT_EXIST) return null;
            throw new System.ComponentModel.Win32Exception(error);
        }
        finally { CloseServiceHandle(scm); }
    }

    internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ServiceHandle() : base(ownsHandle: true) { }

        public Status QueryStatus()
        {
            if (!QueryServiceStatus(this, out var status)) throw new System.ComponentModel.Win32Exception();
            return (Status)status.dwCurrentState;
        }

        public bool RequestStop()
        {
            if (ControlService(this, SERVICE_CONTROL_STOP, out _)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error is ERROR_SERVICE_NOT_ACTIVE or ERROR_SERVICE_CANNOT_ACCEPT_CTRL or ERROR_INVALID_SERVICE_CONTROL) return false;
            throw new System.ComponentModel.Win32Exception(error);
        }

        public void DeleteStopped()
        {
            if (QueryStatus() != Status.Stopped) throw new InvalidOperationException("Der Tunnel-Dienst läuft noch.");
            MarkForDeletion();
        }

        /// <summary>Löscht den Dienst; läuft er noch, entfernt Windows ihn, sobald er stoppt.</summary>
        public void MarkForDeletion()
        {
            if (DeleteService(this)) return;
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_SERVICE_MARKED_FOR_DELETE) throw new System.ComponentModel.Win32Exception(error);
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    // ---------- Eigene Tunnel-Dienste (Standalone-Edition) ----------

    /// <summary>Legt einen Dienst an (oder aktualisiert ihn) und startet ihn.</summary>
    public static void CreateOrUpdateAndStart(string name, string displayName, string description, string binaryPathName, bool autostart, bool unrestrictedSid)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            const uint access = SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP | SERVICE_CHANGE_CONFIG | DELETE;
            var startType = autostart ? SERVICE_AUTO_START : SERVICE_DEMAND_START;
            var service = CreateServiceW(scm, name, displayName, access,
                SERVICE_WIN32_OWN_PROCESS, startType, SERVICE_ERROR_NORMAL,
                binaryPathName, null, IntPtr.Zero, unrestrictedSid ? "Nsi\0TcpIp\0\0" : null, null, null);
            if (service.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                service.Dispose();
                if (error != ERROR_SERVICE_EXISTS) throw new System.ComponentModel.Win32Exception(error);
                service = OpenServiceW(scm, name, access);
                if (service.IsInvalid)
                {
                    var openErr = Marshal.GetLastWin32Error();
                    service.Dispose();
                    throw new System.ComponentModel.Win32Exception(openErr);
                }
                using (service)
                {
                    if (!ChangeServiceConfigW(service, SERVICE_WIN32_OWN_PROCESS, startType, SERVICE_ERROR_NORMAL,
                            binaryPathName, null, IntPtr.Zero, unrestrictedSid ? "Nsi\0TcpIp\0\0" : null, null, null, displayName))
                        throw new System.ComponentModel.Win32Exception();
                    ConfigureAndStart(service, description, unrestrictedSid);
                    return;
                }
            }
            using (service) ConfigureAndStart(service, description, unrestrictedSid);
        }
        finally { CloseServiceHandle(scm); }
    }

    static void ConfigureAndStart(ServiceHandle service, string description, bool unrestrictedSid)
    {
        if (unrestrictedSid)
        {
            var sidInfo = SERVICE_SID_TYPE_UNRESTRICTED;
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_SERVICE_SID_INFO, ref sidInfo))
                throw new System.ComponentModel.Win32Exception();
        }
        var desc = new SERVICE_DESCRIPTION { lpDescription = description };
        if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, ref desc))
            throw new System.ComponentModel.Win32Exception();
        if (service.QueryStatus() != Status.Running && !StartServiceW(service, 0, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_SERVICE_ALREADY_RUNNING) throw new System.ComponentModel.Win32Exception(err);
        }
    }

    public static void StopAndDelete(string serviceName, TimeSpan timeout)
    {
        using var service = OpenForRemoval(serviceName, out var markedForDeletion);
        if (service is null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stopRequested = false;
        while (service.QueryStatus() != Status.Stopped)
        {
            if (sw.Elapsed >= timeout) break;
            if (!stopRequested && service.QueryStatus() != Status.StopPending)
                stopRequested = service.RequestStop();
            Thread.Sleep(250);
        }
        service.MarkForDeletion();
    }

    /// <summary>Legt einen Dienst nach den Vorgaben des WireGuard embeddable-dll-service an und startet ihn.</summary>
    public static void CreateAndStart(string name, string displayName, string description, string binaryPathName, bool autostart)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            using var service = CreateServiceW(scm, name, displayName,
                SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP | SERVICE_CHANGE_CONFIG | DELETE,
                SERVICE_WIN32_OWN_PROCESS, autostart ? SERVICE_AUTO_START : SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL,
                binaryPathName, null, IntPtr.Zero, "Nsi\0TcpIp\0\0", null, null);
            if (service.IsInvalid) throw new System.ComponentModel.Win32Exception();
            // Laut WireGuard zwingend: ohne "unrestricted" SID-Typ greifen Firewall-Regeln (Kill-Switch) nicht.
            var sidInfo = SERVICE_SID_TYPE_UNRESTRICTED;
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_SERVICE_SID_INFO, ref sidInfo))
                throw new System.ComponentModel.Win32Exception();
            var desc = new SERVICE_DESCRIPTION { lpDescription = description };
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, ref desc))
                throw new System.ComponentModel.Win32Exception();
            if (!StartServiceW(service, 0, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            // Bei einem Fehler räumt ConnectAsync den Dienst über den regulären Stopp-Pfad auf.
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_DESCRIPTION { public string? lpDescription; }

    const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    const uint SERVICE_CHANGE_CONFIG = 0x0002;
    const uint SERVICE_START = 0x0010;
    const uint SERVICE_STOP = 0x0020;
    const uint DELETE = 0x00010000;
    const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    const uint SERVICE_AUTO_START = 2;
    const uint SERVICE_DEMAND_START = 3;
    const uint SERVICE_ERROR_NORMAL = 1;
    const uint SERVICE_CONTROL_STOP = 1;
    const uint SERVICE_CONFIG_DESCRIPTION = 1;
    const uint SERVICE_CONFIG_SERVICE_SID_INFO = 5;
    const uint SERVICE_SID_TYPE_UNRESTRICTED = 1;
    const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;
    const int ERROR_INVALID_SERVICE_CONTROL = 1052;
    const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    const int ERROR_SERVICE_EXISTS = 1073;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ServiceHandle CreateServiceW(IntPtr scm, string name, string displayName, uint access, uint type,
        uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ChangeServiceConfigW(ServiceHandle svc, uint type, uint startType, uint errorControl,
        string binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ChangeServiceConfig2W(ServiceHandle svc, uint infoLevel, ref uint info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ChangeServiceConfig2W(ServiceHandle svc, uint infoLevel, ref SERVICE_DESCRIPTION info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool StartServiceW(ServiceHandle svc, uint argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ControlService(ServiceHandle svc, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DeleteService(ServiceHandle svc);

    const uint SC_MANAGER_CONNECT = 0x0001;
    const uint SERVICE_QUERY_STATUS = 0x0004;
    const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                    dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ServiceHandle OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(ServiceHandle svc, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
