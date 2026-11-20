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

    public static ServiceHandle? OpenForQuery(string serviceName, out bool markedForDeletion)
    {
        markedForDeletion = false;
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var service = OpenServiceW(scm, serviceName, SERVICE_QUERY_STATUS);
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
        private ServiceHandle() : base(ownsHandle: true) { }

        public Status QueryStatus()
        {
            if (!QueryServiceStatus(this, out var status)) throw new System.ComponentModel.Win32Exception();
            return (Status)status.dwCurrentState;
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    // ---------- Eigene Tunnel-Dienste (Standalone-Edition) ----------

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
            try
            {
                // Laut WireGuard zwingend: ohne "unrestricted" SID-Typ greifen Firewall-Regeln (Kill-Switch) nicht.
                var sidInfo = SERVICE_SID_TYPE_UNRESTRICTED;
                if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_SERVICE_SID_INFO, ref sidInfo))
                    throw new System.ComponentModel.Win32Exception();
                var desc = new SERVICE_DESCRIPTION { lpDescription = description };
                if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, ref desc))
                    throw new System.ComponentModel.Win32Exception();
                if (!StartServiceW(service, 0, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception();
            }
            catch
            {
                DeleteService(service);
                throw;
            }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Stoppt den Dienst und markiert ihn zum Löschen (Windows entfernt ihn, sobald er beendet ist).</summary>
    /// <returns>false, wenn der Dienst nicht (mehr) existiert.</returns>
    public static bool StopAndDelete(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            using var service = OpenServiceW(scm, name, SERVICE_QUERY_STATUS | SERVICE_STOP | DELETE);
            if (service.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ERROR_SERVICE_DOES_NOT_EXIST or ERROR_SERVICE_MARKED_FOR_DELETE) return false;
                throw new System.ComponentModel.Win32Exception(error);
            }
            if (!DeleteService(service) && Marshal.GetLastWin32Error() is var delError && delError != ERROR_SERVICE_MARKED_FOR_DELETE)
                throw new System.ComponentModel.Win32Exception(delError);
            if (!ControlService(service, SERVICE_CONTROL_STOP, out _) &&
                Marshal.GetLastWin32Error() is var stopError && stopError is not (ERROR_SERVICE_NOT_ACTIVE or ERROR_SERVICE_CANNOT_ACCEPT_CTRL))
                throw new System.ComponentModel.Win32Exception(stopError);
            return true;
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

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ServiceHandle CreateServiceW(IntPtr scm, string name, string displayName, uint access, uint type,
        uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password);

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
