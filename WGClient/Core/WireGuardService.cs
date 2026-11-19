using System.Diagnostics;
using System.Text;

namespace WGClient.Core;

public enum TunnelState { Disconnected, Starting, Running, Stopping }

public sealed record TunnelStats(DateTimeOffset? LastHandshake, long RxBytes, long TxBytes, string? Endpoint);

/// <summary>Steuert Tunnel über die offizielle WireGuard-Installation (wireguard.exe / wg.exe).</summary>
public static class WireGuardService
{
    static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard");

    public static string WireGuardExe => Path.Combine(InstallDir, "wireguard.exe");
    public static string WgExe => Path.Combine(InstallDir, "wg.exe");
    public static bool IsInstalled => File.Exists(WireGuardExe) && File.Exists(WgExe);

    public static string ServiceName(string tunnel) => "WireGuardTunnel$" + tunnel;

    public static TunnelState GetState(string tunnel) => NativeServices.Query(ServiceName(tunnel)) switch
    {
        NativeServices.Status.Running => TunnelState.Running,
        NativeServices.Status.StartPending or NativeServices.Status.ContinuePending => TunnelState.Starting,
        NativeServices.Status.StopPending => TunnelState.Stopping,
        _ => TunnelState.Disconnected, // gestoppt oder Dienst existiert nicht
    };

    static bool IsServiceInstalled(string tunnel) => NativeServices.Query(ServiceName(tunnel)) is not null;

    public static async Task ConnectAsync(string tunnel, string confPath, bool autostart)
    {
        EnsureInstalled();
        if (IsServiceInstalled(tunnel))
        {
            await RunAsync(WireGuardExe, "/uninstalltunnelservice", tunnel);
            await WaitUntilAsync(() => !IsServiceInstalled(tunnel), TimeSpan.FromSeconds(15));
        }

        var (code, output) = await RunAsync(WireGuardExe, "/installtunnelservice", confPath);
        if (code != 0)
            throw new InvalidOperationException($"wireguard.exe hat den Tunnel nicht gestartet (Code {code}). {output}".Trim());

        // WireGuard legt den Dienst mit Starttyp "Automatisch" an
        if (!autostart)
            await RunAsync("sc.exe", "config", ServiceName(tunnel), "start=", "demand");
    }

    public static async Task DisconnectAsync(string tunnel)
    {
        EnsureInstalled();
        if (!IsServiceInstalled(tunnel)) return;
        var (code, output) = await RunAsync(WireGuardExe, "/uninstalltunnelservice", tunnel);
        if (code != 0)
            throw new InvalidOperationException($"Tunnel konnte nicht getrennt werden (Code {code}). {output}".Trim());
        await WaitUntilAsync(() => !IsServiceInstalled(tunnel), TimeSpan.FromSeconds(15));
    }

    public static async Task<TunnelStats?> GetStatsAsync(string tunnel)
    {
        if (!IsInstalled) return null;
        var (code, output) = await RunAsync(WgExe, "show", tunnel, "dump");
        if (code != 0) return null;

        long rx = 0, tx = 0, handshake = 0;
        string? endpoint = null;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Skip(1)) // Zeile 1 = Interface (enthält den privaten Schlüssel, wird ignoriert)
        {
            var f = line.Split('\t');
            if (f.Length < 8) continue;
            if (endpoint is null && f[2] != "(none)") endpoint = f[2];
            if (long.TryParse(f[4], out var h)) handshake = Math.Max(handshake, h);
            if (long.TryParse(f[5], out var r)) rx += r;
            if (long.TryParse(f[6], out var t)) tx += t;
        }
        return new TunnelStats(handshake > 0 ? DateTimeOffset.FromUnixTimeSeconds(handshake) : null, rx, tx, endpoint);
    }

    public static async Task<string> DumpLogAsync(string? tunnelFilter, int maxLines = 400)
    {
        EnsureInstalled();
        var (_, output) = await RunAsync(WireGuardExe, "/dumplog");
        var lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> filtered = lines;
        if (!string.IsNullOrEmpty(tunnelFilter))
            filtered = lines.Where(l => l.Contains($"[{tunnelFilter}]", StringComparison.OrdinalIgnoreCase));
        var list = filtered.ToList();
        return string.Join(Environment.NewLine, list.Skip(Math.Max(0, list.Count - maxLines)));
    }

    static void EnsureInstalled()
    {
        if (!IsInstalled)
            throw new InvalidOperationException("WireGuard für Windows ist nicht installiert (https://www.wireguard.com/install/).");
    }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout) await Task.Delay(300);
    }

    static async Task<(int ExitCode, string Output)> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} konnte nicht gestartet werden.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* ignorieren */ }
            throw new TimeoutException($"{Path.GetFileName(exe)} reagiert nicht.");
        }
        return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
    }
}
