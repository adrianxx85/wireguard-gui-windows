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

    public static void QueueImport(string path)
    {
        new TunnelStore().EnsureCreated();
        WriteProtectedFile(PendingImportPath, Path.GetFullPath(path));
    }

    public static string? TakePendingImport()
    {
        var path = CheckedFile(PendingImportPath);
        if (!File.Exists(path)) return null;
        var pending = File.ReadAllText(path).Trim();
        File.Delete(path);
        return pending;
    }

    [GeneratedRegex("[^a-zA-Z0-9_=+.-]")]
    private static partial Regex InvalidNameChars();

    /// <summary>WireGuard erlaubt als Tunnelname nur [a-zA-Z0-9_=+.-], max. 32 Zeichen.</summary>
    public static string SanitizeName(string name)
    {
        var n = InvalidNameChars().Replace(name.Trim(), "_");
        if (n.Length > 32) n = n[..32];
        return n.Length == 0 ? "tunnel" : n;
    }

    public IEnumerable<string> ListNames() =>
        Directory.GetFiles(TunnelsDir, "*.conf")
                 .Select(f => Path.GetFileNameWithoutExtension(f))
                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    public string ReadOriginal(string name) => File.ReadAllText(CheckedFile(Path.Combine(TunnelsDir, name + ".conf")));

    public void SaveOriginal(string name, string text) => WriteProtectedFile(Path.Combine(TunnelsDir, name + ".conf"), text);

    public TunnelOptions? ReadOptions(string name)
    {
        var path = CheckedFile(Path.Combine(TunnelsDir, name + ".json"));
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TunnelOptions>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public void SaveOptions(string name, TunnelOptions options) =>
        WriteProtectedFile(Path.Combine(TunnelsDir, name + ".json"), JsonSerializer.Serialize(options, Json));

    public static string ActivePath(string name) => Path.Combine(ActiveDir, name + ".conf");

    /// <summary>Schreibt die effektive Konfiguration; der Dateiname bestimmt den Tunnelnamen.</summary>
    public void WriteActive(string name, string text) => WriteProtectedFile(ActivePath(name), text);

    public void Delete(string name)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(TunnelsDir, name + ".conf"),
                     Path.Combine(TunnelsDir, name + ".json"),
                     ActivePath(name),
                 })
        {
            File.Delete(CheckedFile(path));
        }
    }
}
