using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace WGClient.Core;

/// <summary>
/// Standalone-Edition: Tunnel laufen ohne installierte WireGuard-App. WGClient.exe ist selbst der Tunnel-Dienst
/// ("/service &lt;conf&gt;") und lädt tunnel.dll (WireGuard embeddable-dll-service) sowie wireguard.dll (wireguard-nt).
/// </summary>
public static class EmbeddedTunnel
{
    const string TunnelDll = "tunnel.dll";
    const string DriverDll = "wireguard.dll";
    public const string ServiceSwitch = "/service";

    static string AppDir => AppContext.BaseDirectory;
    internal static string DriverPath => Path.Combine(AppDir, DriverDll);

    /// <summary>true, wenn beide Bausteine neben WGClient.exe liegen (Standalone-Installation).</summary>
    public static bool IsAvailable => File.Exists(Path.Combine(AppDir, TunnelDll)) && File.Exists(DriverPath);

    static EmbeddedTunnel()
    {
        // Nur die Dateien aus dem Programmordner laden – nie eine gleichnamige DLL aus dem Suchpfad.
        NativeLibrary.SetDllImportResolver(typeof(EmbeddedTunnel).Assembly, (name, _, _) =>
        {
            if (name is not (TunnelDll or DriverDll)) return IntPtr.Zero;
            VerifyInstallation();
            return NativeLibrary.Load(Path.Combine(AppDir, name));
        });
    }

    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");

    /// <summary>Befehlszeile des Tunnel-Dienstes (immer mit Anführungszeichen, wie im Installer geprüft).</summary>
    public static string ServiceCommandLine(string configPath) => $"\"{ExePath}\" {ServiceSwitch} \"{configPath}\"";

    public static void VerifyInstallation() => ServiceFiles.Verify(ExePath, Path.Combine(AppDir, TunnelDll), DriverPath);

    public static void Install(string tunnel, string configPath, bool autostart)
    {
        VerifyInstallation();
        NativeServices.CreateAndStart(WireGuardService.ServiceName(tunnel), $"WG Client Tunnel: {tunnel}",
            "WireGuard-Tunnel, eingerichtet von WG Client.", ServiceCommandLine(configPath), autostart);
    }

    // ---------- Dienstprozess ----------

    [DllImport(TunnelDll, EntryPoint = "WireGuardTunnelService", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool WireGuardTunnelService([MarshalAs(UnmanagedType.LPWStr)] string configFile);

    /// <summary>Einstieg, wenn Windows WGClient.exe als Tunnel-Dienst startet. Blockiert, bis der Dienst stoppt.</summary>
    public static int RunService(string configPath)
    {
        var full = Path.GetFullPath(configPath);
        // Nur Konfigurationen aus dem geschützten Speicher von WG Client akzeptieren.
        if (!string.Equals(Path.GetDirectoryName(full), TunnelStore.ActiveDirectory, StringComparison.OrdinalIgnoreCase))
            return 2;
        VerifyInstallation();
        TunnelStore.VerifyActiveConfig(full);
        return WireGuardTunnelService(full) ? 0 : 1;
    }

    // ---------- Status über wireguard.dll ----------

    [DllImport(DriverDll, EntryPoint = "WireGuardOpenAdapter", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr WireGuardOpenAdapter(string name);

    [DllImport(DriverDll, EntryPoint = "WireGuardCloseAdapter")]
    static extern void WireGuardCloseAdapter(IntPtr adapter);

    [DllImport(DriverDll, EntryPoint = "WireGuardGetConfiguration", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WireGuardGetConfiguration(IntPtr adapter, byte[] config, ref uint bytes);

    // Layout laut wireguard.h (alle Strukturen ALIGNED(8)).
    const int InterfaceSize = 80, PeerSize = 136, AllowedIpSize = 24;
    const int IfacePeersCount = 72;
    const int PeerFlags = 0, PeerEndpoint = 76, PeerTx = 104, PeerRx = 112, PeerHandshake = 120, PeerAllowedCount = 128;
    const uint PeerHasEndpoint = 1 << 3;
    const ushort AfInet = 2, AfInet6 = 23;
    const int ErrorMoreData = 234;

    public static TunnelStats? GetStats(string tunnel)
    {
        var adapter = WireGuardOpenAdapter(tunnel);
        if (adapter == IntPtr.Zero) return null;
        try
        {
            uint size = 4096;
            byte[] buffer;
            while (true)
            {
                buffer = new byte[size];
                if (WireGuardGetConfiguration(adapter, buffer, ref size)) break;
                if (Marshal.GetLastPInvokeError() != ErrorMoreData) return null;
            }
            try { return ParseStats(buffer.AsSpan(0, (int)Math.Min(size, (uint)buffer.Length))); }
            finally { Array.Clear(buffer); } // enthält den privaten Schlüssel
        }
        finally { WireGuardCloseAdapter(adapter); }
    }

    public static TunnelStats? ParseStats(ReadOnlySpan<byte> data)
    {
        if (data.Length < InterfaceSize) return null;
        var peers = BinaryPrimitives.ReadUInt32LittleEndian(data[IfacePeersCount..]);
        long rx = 0, tx = 0;
        ulong handshake = 0;
        string? endpoint = null;
        var offset = InterfaceSize;
        for (uint i = 0; i < peers; i++)
        {
            if (offset + PeerSize > data.Length) return null;
            var peer = data.Slice(offset, PeerSize);
            if (endpoint is null && (BinaryPrimitives.ReadUInt32LittleEndian(peer[PeerFlags..]) & PeerHasEndpoint) != 0)
                endpoint = ReadEndpoint(peer.Slice(PeerEndpoint, 28));
            tx += (long)BinaryPrimitives.ReadUInt64LittleEndian(peer[PeerTx..]);
            rx += (long)BinaryPrimitives.ReadUInt64LittleEndian(peer[PeerRx..]);
            handshake = Math.Max(handshake, BinaryPrimitives.ReadUInt64LittleEndian(peer[PeerHandshake..]));
            offset += PeerSize + (int)BinaryPrimitives.ReadUInt32LittleEndian(peer[PeerAllowedCount..]) * AllowedIpSize;
        }
        return new TunnelStats(handshake > 0 ? DateTimeOffset.FromFileTime((long)handshake) : null, rx, tx, endpoint);
    }

    // SOCKADDR_INET: Familie (2), Port (2, Netzwerk-Byte-Reihenfolge), IPv4 ab 4 bzw. IPv6 ab 8.
    static string? ReadEndpoint(ReadOnlySpan<byte> sa)
    {
        var family = BinaryPrimitives.ReadUInt16LittleEndian(sa);
        var port = BinaryPrimitives.ReadUInt16BigEndian(sa[2..]);
        return family switch
        {
            AfInet => new IPEndPoint(new IPAddress(sa.Slice(4, 4)), port).ToString(),
            AfInet6 => new IPEndPoint(new IPAddress(sa.Slice(8, 16)), port).ToString(),
            _ => null,
        };
    }

    // ---------- Log (Ringlogger-Format von wireguard-windows) ----------

    const uint LogMagic = 0xbadbabe;
    const int LogMaxLines = 2048, LogLineText = 512, LogLineBytes = LogLineText + 8, LogHeader = 8;

    public static string LogPath => Path.Combine(TunnelStore.ActiveDirectory, "log.bin");

    public static List<string> ReadLog()
    {
        var lines = new List<string>();
        byte[] data;
        try
        {
            using var file = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            data = new byte[Math.Min(file.Length, LogHeader + (long)LogLineBytes * LogMaxLines)];
            file.ReadExactly(data);
        }
        catch (FileNotFoundException) { return lines; }
        catch (DirectoryNotFoundException) { return lines; }
        return ParseLog(data);
    }

    public static List<string> ParseLog(ReadOnlySpan<byte> data)
    {
        var lines = new List<string>();
        if (data.Length < LogHeader || BinaryPrimitives.ReadUInt32LittleEndian(data) != LogMagic) return lines;
        var next = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        for (uint i = 0; i < LogMaxLines; i++)
        {
            var start = LogHeader + (int)((next + i) % LogMaxLines) * LogLineBytes;
            if (start + LogLineBytes > data.Length) continue;
            var ns = BinaryPrimitives.ReadInt64LittleEndian(data[start..]);
            if (ns == 0) continue;
            var text = data.Slice(start + 8, LogLineText);
            var end = text.IndexOf((byte)0);
            if (end <= 0) continue;
            // Gleiches Format wie "wireguard.exe /dumplog", damit sich beide Logs nach Zeit mischen lassen.
            var time = DateTime.UnixEpoch.AddTicks(ns / 100).ToLocalTime();
            lines.Add($"{time:yyyy-MM-dd HH:mm:ss.ffffff}: {Encoding.UTF8.GetString(text[..end])}");
        }
        return lines;
    }
}
