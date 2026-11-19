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
