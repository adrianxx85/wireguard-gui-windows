using System.Runtime.InteropServices;

namespace WGClient.Core;

/// <summary>Minimale Win32-Abfrage des Dienststatus (ohne NuGet-Abhängigkeit).</summary>
internal static class NativeServices
{
    public enum Status : uint
    {
        Stopped = 1, StartPending = 2, StopPending = 3, Running = 4,
        ContinuePending = 5, PausePending = 6, Paused = 7,
    }

    /// <returns>Status des Dienstes oder null, wenn er nicht existiert.</returns>
    public static Status? Query(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var svc = OpenServiceW(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err is ERROR_SERVICE_DOES_NOT_EXIST or ERROR_SERVICE_MARKED_FOR_DELETE) return null;
                throw new System.ComponentModel.Win32Exception(err);
            }
            try
            {
                if (!QueryServiceStatus(svc, out var st)) throw new System.ComponentModel.Win32Exception();
                return (Status)st.dwCurrentState;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
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
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
