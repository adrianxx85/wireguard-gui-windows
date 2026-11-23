using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WGClient.Core;

/// <summary>
/// Speichert Tunnel unter %ProgramData%\WGClient. Der Ordner ist nur für SYSTEM und Administratoren lesbar,
/// da die Konfigurationen private Schlüssel enthalten und der WireGuard-Dienst (SYSTEM) sie lesen muss.
/// </summary>
public sealed partial class TunnelStore
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WGClient");

    static string TunnelsDir => Path.Combine(Root, "tunnels");
    static string ActiveDir => Path.Combine(Root, "active");
    static string PendingImportPath => Path.Combine(Root, "pending-import.txt");

    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void EnsureCreated()
    {
        EnsureDirectory(Root);
        EnsureDirectory(TunnelsDir);
        EnsureDirectory(ActiveDir);
    }

    static void EnsureDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        var sec = new DirectorySecurity();
        sec.SetOwner(AdministratorsSid);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { SystemSid, AdministratorsSid })
            sec.AddAccessRule(new FileSystemAccessRule(sid,
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(sec);
        RejectReparsePoint(directory);
        VerifySecurity(directory, directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    }

    static bool IsTrusted(IdentityReference? sid) => SystemSid.Equals(sid) || AdministratorsSid.Equals(sid);

    static void VerifySecurity(FileSystemInfo entry, FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (!IsTrusted(descriptor.Owner) || descriptor.DiscretionaryAcl is not { } acl ||
            acl.Cast<GenericAce>().Any(ace => ace switch
            {
                CommonAce { AceType: AceType.AccessDenied, IsCallback: false } => false,
                CommonAce { AceType: AceType.AccessAllowed, IsCallback: false } allowed => !IsTrusted(allowed.SecurityIdentifier),
                _ => true
            }))
            throw UnsafeEntry(entry);
    }

    static void RejectReparsePoint(FileSystemInfo entry)
    {
        if ((File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0) throw UnsafeEntry(entry);
    }

    static IOException UnsafeEntry(FileSystemInfo entry) =>
        new($"Unsicherer Konfigurationspfad: \"{entry.FullName}\". Nur Administratoren und SYSTEM dürfen Besitzer sein und Zugriff haben." +
            Environment.NewLine + Environment.NewLine +
            $"Abhilfe: WG Client beenden, den Ordner \"{Root}\" löschen und die .conf-Dateien danach neu importieren. " +
            "Wurde der Ordner nicht von dir angelegt oder verändert, prüfe den PC bitte auf Fremdzugriffe.");

    static string CheckedFile(string path) => CheckedFile(path, out _);

    static string CheckedFile(string path, out bool exists)
    {
        var file = new FileInfo(path);
        exists = false;
        try
        {
            RejectReparsePoint(file);
            exists = true;
            VerifySecurity(file, file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        }
        catch (FileNotFoundException) { }
        return path;
    }

    static void WriteProtectedFile(string path, string text)
    {
        CheckedFile(path, out var exists);
        if (exists)
        {
            File.WriteAllText(path, text);
            return;
        }

        var security = new FileSecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, AdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.Read,
            4096, FileOptions.None, security);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Pro Benutzersitzung eine eigene Warteschlange – jede Sitzung hat ihre eigene App-Instanz.
    static readonly string PendingPrefix =
        $"pending-import-{System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}-";

    [GeneratedRegex("^pending-import-[0-9a-f]{32}\\.txt$")]
    private static partial Regex LegacyPendingName(); // 1.4.2: ohne Sitzungs-ID

    public static void QueueImport(string path)
    {
        new TunnelStore().EnsureCreated();
        var id = $"{PendingPrefix}{Guid.NewGuid():N}";
        var staged = Path.Combine(Root, id + ".tmp");
        try
        {
            // Erst vollständig schreiben, dann umbenennen: die laufende Instanz sieht nie eine halb geschriebene Datei.
            WriteProtectedFile(staged, Path.GetFullPath(path));
            File.Move(staged, Path.Combine(Root, id + ".txt"));
        }
        finally { File.Delete(staged); }
    }

    /// <summary>
    /// Liefert die eingereihten Importe dieser Sitzung. Jede Datei wird einzeln verarbeitet: Schlägt Lesen oder
    /// Löschen fehl, bleibt sie für einen späteren Versuch liegen und die übrigen werden trotzdem geliefert.
    /// </summary>
    public static List<string> TakePendingImports(out string? error)
    {
        var result = new List<string>();
        error = null;
        if (!Directory.Exists(Root)) return result;

        var files = new List<string>();
        if (File.Exists(PendingImportPath)) files.Add(PendingImportPath);
        files.AddRange(Directory.EnumerateFiles(Root, "pending-import-*.txt")
            .Where(f => Path.GetFileName(f) is var n &&
                (n.StartsWith(PendingPrefix, StringComparison.OrdinalIgnoreCase) || LegacyPendingName().IsMatch(n)))
            .OrderBy(File.GetCreationTimeUtc));

        foreach (var file in files)
        {
            try
            {
                var checkedPath = CheckedFile(file, out var exists);
                if (!exists) continue;
                var pending = File.ReadAllText(checkedPath).Trim();
                File.Delete(checkedPath);
                if (pending.Length > 0) result.Add(pending);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
        }
        return result;
    }

    [GeneratedRegex("[^a-zA-Z0-9_=+.-]")]
    private static partial Regex InvalidNameChars();

    /// <summary>WireGuard erlaubt als Tunnelname nur [a-zA-Z0-9_=+.-], max. 32 Zeichen, keine Windows-Gerätenamen.</summary>
    public static string SanitizeName(string name)
    {
        var n = name.Trim()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
            .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue").Replace("ß", "ss");
        n = InvalidNameChars().Replace(n, "_").Trim('.');
        if (n.Length == 0) return "tunnel";
        var dot = n.IndexOf('.');
        var stem = dot >= 0 ? n[..dot] : n;
        if (ReservedDeviceNames.Contains(stem)) n = "_" + n;
        if (n.Length > 32) n = n[..32].TrimEnd('.');
        return n.Length == 0 ? "tunnel" : n;
    }

    public IEnumerable<string> ListNames() =>
        Directory.EnumerateFiles(TunnelsDir, "*" + ProfileExtension)
            .Select(file => Path.GetFileName(file)[..^ProfileExtension.Length])
            .Concat(Directory.EnumerateFiles(TunnelsDir, "*.conf").Select(file => Path.GetFileNameWithoutExtension(file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    const string ProfileExtension = ".wgprofile";
    static string ProfilePath(string name) => Path.Combine(TunnelsDir, name + ProfileExtension);

    public sealed record Profile(string Config, TunnelOptions? Options);

    public Profile ReadProfile(string name)
    {
        var profile = CheckedFile(ProfilePath(name), out var hasProfile);
        if (hasProfile)
        {
            var saved = JsonSerializer.Deserialize<Profile>(File.ReadAllText(profile), Json);
            if (saved?.Config is null || saved.Options is null) throw new IOException("Das Tunnelprofil ist unvollständig.");
            return saved;
        }

        var config = File.ReadAllText(CheckedFile(Path.Combine(TunnelsDir, name + ".conf")));
        var path = CheckedFile(Path.Combine(TunnelsDir, name + ".json"));
        if (!File.Exists(path)) return new Profile(config, null);
        try { return new Profile(config, JsonSerializer.Deserialize<TunnelOptions>(File.ReadAllText(path), Json)); }
        catch (JsonException) { return new Profile(config, null); }
    }

    public void SaveProfile(string name, string config, TunnelOptions options)
    {
        var path = CheckedFile(ProfilePath(name));
        var staged = Path.Combine(TunnelsDir, $"{Guid.NewGuid():N}.tmp");
        try
        {
            WriteProtectedFile(staged, JsonSerializer.Serialize(new Profile(config, options), Json));
            File.Move(staged, path, overwrite: true);
        }
        finally { File.Delete(staged); }

        // Veraltete Klartext-Dateien (vor 1.3.1) nach erfolgreicher Migration/Aktualisierung entfernen.
        foreach (var legacy in new[] { Path.Combine(TunnelsDir, name + ".conf"), Path.Combine(TunnelsDir, name + ".json") })
            File.Delete(CheckedFile(legacy));
    }

    public static string ActivePath(string name) => Path.Combine(ActiveDir, name + ".conf");

    /// <summary>Ordner der effektiven Konfigurationen (Standalone: dort liegt auch das Tunnel-Log log.bin).</summary>
    public static string ActiveDirectory => ActiveDir;

    internal static void VerifyActiveConfig(string path)
    {
        foreach (var directory in new[] { new DirectoryInfo(Root), new DirectoryInfo(ActiveDir) })
        {
            RejectReparsePoint(directory);
            VerifySecurity(directory, directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        }
        CheckedFile(path, out var exists);
        if (!exists || !string.Equals(Path.GetExtension(path), ".conf", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Die aktive Tunnel-Konfiguration fehlt oder ist ungültig.");
        CheckedFile(Path.Combine(ActiveDir, "log.bin"));
    }

    /// <summary>Schreibt die effektive Konfiguration; der Dateiname bestimmt den Tunnelnamen.</summary>
    public void WriteActive(string name, string text) => WriteProtectedFile(ActivePath(name), text);

    public void Delete(string name)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(TunnelsDir, name + ".conf"),
                     Path.Combine(TunnelsDir, name + ".json"),
                     ActivePath(name),
                     ProfilePath(name),
                 })
        {
            File.Delete(CheckedFile(path));
        }
    }
}
