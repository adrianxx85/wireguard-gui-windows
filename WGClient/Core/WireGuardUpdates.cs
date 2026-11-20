using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WGClient.Core;

internal static class WireGuardUpdates
{
    public const string OfficialDownloads = "https://download.wireguard.com/windows-client/";
    const string LatestOfficial = OfficialDownloads + "latest.sig";
    const string Repository = "adrianxx85/wireguard-gui-windows";
    const string LatestRelease = "https://api.github.com/repos/" + Repository + "/releases/latest";
    const string InstallerDownloads = "https://github.com/" + Repository + "/releases/download/";

    static readonly HttpClient Http = CreateHttpClient();
    static readonly SemaphoreSlim OfficialUpdater = new(1, 1);

    public sealed record StandaloneRelease(Version Version, Uri Installer);

    static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 512 * 1024,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WGClient");
        return client;
    }

    public static Version? ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return Version.TryParse(info.ProductVersion, out var version) || Version.TryParse(info.FileVersion, out version) ? version : null;
        }
        catch (FileNotFoundException) { return null; }
    }

    public static bool IsNewer(Version candidate, Version installed) => Normalize(candidate) > Normalize(installed);

    static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public static async Task<Version> CheckOfficialAsync(CancellationToken token)
    {
        var listing = await Http.GetStringAsync(LatestOfficial, token).ConfigureAwait(false);
        return ParseOfficialListing(listing, RuntimeInformation.OSArchitecture);
    }

    // Die HTTPS-Liste liefert hier nur Versionsnummern. Der offizielle Updater prüft die Signatur und das MSI selbst.
    internal static Version ParseOfficialListing(string listing, Architecture architecture)
    {
        var prefix = architecture switch
        {
            Architecture.X64 => "wireguard-amd64-",
            Architecture.Arm64 => "wireguard-arm64-",
            Architecture.X86 => "wireguard-x86-",
            _ => throw new PlatformNotSupportedException("Für diese Windows-Architektur wurde kein Paket gefunden."),
        };
        var lines = listing.Split('\n');
        if (lines.Length < 3 || !lines[0].StartsWith("untrusted comment: ", StringComparison.Ordinal))
            throw new InvalidDataException("Die WireGuard-Versionsliste hat ein unerwartetes Format.");

        Version? latest = null;
        foreach (var raw in lines.Skip(2))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length <= 66 || line[64..66] != "  " || line.AsSpan(0, 64).ContainsAnyExcept("0123456789abcdefABCDEF"))
                continue;
            var file = line[66..];
            if (!file.StartsWith(prefix, StringComparison.Ordinal) || !file.EndsWith(".msi", StringComparison.Ordinal)) continue;
            var number = file[prefix.Length..^4];
            if (Version.TryParse(number, out var version) && version.ToString() == number && (latest is null || IsNewer(version, latest)))
                latest = version;
        }
        return latest ?? throw new InvalidDataException("Die WireGuard-Versionsliste enthält kein passendes Windows-Paket.");
    }

    public static async Task<StandaloneRelease> CheckStandaloneAsync(CancellationToken token)
    {
        var json = await Http.GetStringAsync(LatestRelease, token).ConfigureAwait(false);
        return ParseStandaloneRelease(json);
    }

    internal static StandaloneRelease ParseStandaloneRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        var tag = release.GetProperty("tag_name").GetString();
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            tag is not { Length: > 1 } || !tag.StartsWith('v') ||
            !Version.TryParse(tag[1..], out var version) || tag != $"v{version}")
            throw new InvalidDataException("GitHub meldet keine reguläre WG-Client-Version.");

        var file = $"WGClient-Standalone-Setup-{version}.exe";
        var installer = new Uri($"{InstallerDownloads}{tag}/{file}");
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == file && asset.GetProperty("state").GetString() == "uploaded" &&
                asset.GetProperty("browser_download_url").GetString() == installer.AbsoluteUri)
                return new StandaloneRelease(version, installer);
        }
        throw new InvalidDataException("Für die neueste Version gibt es keinen passenden Standalone-Installer.");
    }

    const int KilledByInstaller = unchecked((int)0xC000026B);

    public static async Task RunOfficialUpdaterAsync()
    {
        if (!await OfficialUpdater.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("Der WireGuard-Updater läuft bereits.");
        try
        {
            var exe = WireGuardService.WireGuardExe;
            ServiceFiles.Verify(exe);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add("/update");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("WireGuard-Updater konnte nicht gestartet werden.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            // Das WireGuard-MSI beendet beim Installieren alle wireguard.exe aus dem Programmordner – auch diesen
            // Updater (KillWireGuardProcesses, Code STATUS_DLL_INIT_FAILED_LOGOFF). Ob das Update gelang, zeigt die Version.
            if (process.ExitCode is not (0 or KilledByInstaller))
                throw new InvalidOperationException($"Der WireGuard-Updater endete mit Code {process.ExitCode}.");
        }
        finally { OfficialUpdater.Release(); }
    }
}
