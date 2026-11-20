using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace WGClient.Core;

public enum TunnelState { Disconnected, Starting, Running, Stopping }

public sealed record TunnelStats(DateTimeOffset? LastHandshake, long RxBytes, long TxBytes, string? Endpoint);

/// <summary>
/// Steuert Tunnel über die offizielle WireGuard-Installation (wireguard.exe / wg.exe) oder – in der
/// Standalone-Edition – über eigene Tunnel-Dienste mit tunnel.dll/wireguard.dll (<see cref="EmbeddedTunnel"/>).
/// </summary>
public static class WireGuardService
{
    static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard");

    public static string WireGuardExe => Path.Combine(InstallDir, "wireguard.exe");
    public static string WgExe => Path.Combine(InstallDir, "wg.exe");
    static bool OfficialInstalled => File.Exists(WireGuardExe) && File.Exists(WgExe);

    /// <summary>Standalone-Edition: Tunnel-Bausteine liegen neben WGClient.exe.</summary>
    public static bool Embedded { get; } = EmbeddedTunnel.IsAvailable;

    public static bool IsInstalled => Embedded || OfficialInstalled;

    public static string ServiceName(string tunnel) => "WireGuardTunnel$" + tunnel;

    public static TunnelState GetState(string tunnel) => (IsOwned(tunnel) ? NativeServices.Query(ServiceName(tunnel)) : null) switch
    {
        NativeServices.Status.Running => TunnelState.Running,
        NativeServices.Status.StartPending or NativeServices.Status.ContinuePending => TunnelState.Starting,
        NativeServices.Status.StopPending or NativeServices.Status.MarkedForDeletion => TunnelState.Stopping,
        _ => TunnelState.Disconnected, // gestoppt oder Dienst existiert nicht
    };

    static string ServiceRegistryPath(string tunnel) => @"SYSTEM\CurrentControlSet\Services\" + ServiceName(tunnel);

    static bool IsOwned(string tunnel)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceRegistryPath(tunnel));
        if (key?.GetValue("ImagePath") is not string imagePath) return false;
        // Beide Editionen erkennen die Dienste der jeweils anderen, damit ein Wechsel keine Tunnel verwaist.
        var config = TunnelStore.ActivePath(tunnel);
        return string.Equals(imagePath, $"{QuotePath(WireGuardExe)} /tunnelservice {QuotePath(config)}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(imagePath, EmbeddedTunnel.ServiceCommandLine(config), StringComparison.OrdinalIgnoreCase);
    }

    // WireGuard's service installer quotes executable and configuration paths when they contain whitespace.
    static string QuotePath(string path) => path.Contains(' ') || path.Contains('\t') ? $"\"{path}\"" : path;

    static bool IsServiceInstalled(string tunnel)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceRegistryPath(tunnel));
        return key is not null || NativeServices.Query(ServiceName(tunnel)) is not null;
    }

    // WireGuard löst Endpoint-Hostnamen beim Start mit Wiederholungen auf (bis ca. 40 s ohne Netz).
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(15);

    /// <returns>true, wenn der Tunnel läuft; false, wenn der Dienst nach dem Zeitlimit noch startet.</returns>
    public static async Task<bool> ConnectAsync(string tunnel, bool autostart, Action writeConfig)
    {
        EnsureInstalled();
        if (IsServiceInstalled(tunnel))
        {
            if (!IsOwned(tunnel))
                throw new InvalidOperationException($"Ein anderer WireGuard-Dienst verwendet bereits den Namen \"{tunnel}\".");
            await DisconnectAsync(tunnel);
            // Vor dem Neuanlegen muss der alte Dienst wirklich entfernt sein, sonst schlägt die Installation fehl.
            if (!await WaitUntilAsync(() => !IsServiceInstalled(tunnel), RemoveTimeout))
                throw new InvalidOperationException($"Der Dienstname \"{tunnel}\" ist weiterhin belegt. " +
                    "Ist die Windows-Diensteverwaltung geöffnet? Bitte schließen und erneut versuchen.");
        }

        writeConfig();
        try
        {
            if (Embedded)
                await Task.Run(() => EmbeddedTunnel.Install(tunnel, TunnelStore.ActivePath(tunnel), autostart));
            else
            {
                var (code, output) = await RunAsync(WireGuardExe, "/installtunnelservice", TunnelStore.ActivePath(tunnel));
                if (code != 0)
                    throw new InvalidOperationException($"wireguard.exe hat den Tunnel nicht gestartet (Code {code}). {output}".Trim());
            }

            // WireGuard legt den Dienst mit Starttyp "Automatisch" an.
            if (!Embedded && !autostart)
            {
                if (!IsOwned(tunnel)) throw new InvalidOperationException($"Der Dienst für \"{tunnel}\" wurde nicht gefunden.");
                var (scCode, scOutput) = await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"),
                    "config", ServiceName(tunnel), "start=", "demand");
                if (scCode != 0)
                    throw new InvalidOperationException($"Autostart konnte nicht deaktiviert werden (Code {scCode}). {scOutput}".Trim());
            }

            // Läuft der Start länger, bleibt der Dienst bestehen – die Statusaktualisierung zeigt den weiteren Verlauf.
            return await WaitUntilAsync(() => GetState(tunnel) switch
            {
                TunnelState.Running => true,
                TunnelState.Starting => false,
                _ => throw new InvalidOperationException($"Der Dienst für \"{tunnel}\" wurde vorzeitig beendet. Details stehen im WireGuard-Log.")
            }, StartTimeout);
        }
        catch (Exception startError)
        {
            try { await DisconnectAsync(tunnel); }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException($"{startError.Message} Dienst konnte nicht entfernt werden: {cleanupError.Message}", startError);
            }
            throw;
        }
    }

    public static async Task DisconnectAsync(string tunnel)
    {
        if (!IsOwned(tunnel)) return;
        // Ein vorhandenes Handle bleibt zur Statusabfrage gültig, wenn WireGuard den Dienst zum Löschen markiert.
        using var service = NativeServices.OpenForQuery(ServiceName(tunnel), out var markedForDeletion);
        if (service is not null)
        {
            if (Embedded || !OfficialInstalled)
                await Task.Run(() => NativeServices.StopAndDelete(ServiceName(tunnel)));
            else
            {
                var (code, output) = await RunAsync(WireGuardExe, "/uninstalltunnelservice", tunnel);
                if (code != 0)
                    throw new InvalidOperationException($"Tunnel konnte nicht getrennt werden (Code {code}). {output}".Trim());
            }
        }
        else if (!markedForDeletion) return;

        if (!await WaitUntilAsync(() => service is not null
                ? service.QueryStatus() == NativeServices.Status.Stopped
                : !IsServiceInstalled(tunnel), RemoveTimeout))
            throw new TimeoutException($"Der Dienst für \"{tunnel}\" wurde nicht rechtzeitig gestoppt.");
    }

    public static async Task<TunnelStats?> GetStatsAsync(string tunnel)
    {
        if (Embedded) return await Task.Run(() => EmbeddedTunnel.GetStats(tunnel));
        if (!OfficialInstalled) return null;
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
        string[] lines;
        if (Embedded)
            lines = [.. await Task.Run(EmbeddedTunnel.ReadLog)];
        else
        {
            EnsureInstalled();
            var (_, output) = await RunAsync(WireGuardExe, "/dumplog");
            lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
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

    /// <returns>true, sobald die Bedingung erfüllt ist; false nach Ablauf des Zeitlimits.</returns>
    static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(300);
        }
        return true;
    }

    // ---------- Schlüssel (X25519 über Windows-CNG, siehe WgKeys) ----------

    /// <summary>Erzeugt einen privaten Schlüssel bzw. mit <paramref name="preshared"/> einen Pre-shared Key.</summary>
    public static Task<string> GenerateKeyAsync(bool preshared = false) =>
        Task.FromResult(preshared ? WgKeys.GeneratePresharedKey() : WgKeys.GeneratePrivateKey());

    public static Task<string> PublicKeyAsync(string privateKey)
    {
        try { return Task.FromResult(WgKeys.PublicKey(privateKey)); }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("Öffentlicher Schlüssel konnte nicht berechnet werden. Ist der private Schlüssel gültig?", ex);
        }
    }

    static Task<(int ExitCode, string Output)> RunAsync(string exe, params string[] args) => RunCoreAsync(exe, args, null);

    static async Task<(int ExitCode, string Output)> RunCoreAsync(string exe, string[] args, string? input)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} konnte nicht gestartet werden.");
        if (input is not null)
        {
            await p.StandardInput.WriteAsync(input);
            p.StandardInput.Close();
        }
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
