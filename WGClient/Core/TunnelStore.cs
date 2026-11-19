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

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Secure(Root);
        Directory.CreateDirectory(TunnelsDir);
        Directory.CreateDirectory(ActiveDir);
    }

    static void Secure(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(dir).SetAccessControl(sec);
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

    public string ReadOriginal(string name) => File.ReadAllText(Path.Combine(TunnelsDir, name + ".conf"));

    public void SaveOriginal(string name, string text) => File.WriteAllText(Path.Combine(TunnelsDir, name + ".conf"), text);

    public TunnelOptions? ReadOptions(string name)
    {
        var path = Path.Combine(TunnelsDir, name + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TunnelOptions>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public void SaveOptions(string name, TunnelOptions options) =>
        File.WriteAllText(Path.Combine(TunnelsDir, name + ".json"), JsonSerializer.Serialize(options, Json));

    /// <summary>Schreibt die effektive Konfiguration; der Dateiname bestimmt den Tunnelnamen.</summary>
    public string WriteActive(string name, string text)
    {
        var path = Path.Combine(ActiveDir, name + ".conf");
        File.WriteAllText(path, text);
        return path;
    }

    public void Delete(string name)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(TunnelsDir, name + ".conf"),
                     Path.Combine(TunnelsDir, name + ".json"),
                     Path.Combine(ActiveDir, name + ".conf"),
                 })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
