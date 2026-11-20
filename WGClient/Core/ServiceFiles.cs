using System.Security.AccessControl;
using System.Security.Principal;

namespace WGClient.Core;

internal static class ServiceFiles
{
    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    const FileSystemRights ReplaceRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | (FileSystemRights)0x50000000; // GENERIC_WRITE | GENERIC_ALL

    public static void Verify(string executable, string tunnelDll, string driverDll)
    {
        var directory = new FileInfo(executable).Directory!;
        if (new DriveInfo(directory.Root.FullName).DriveType != DriveType.Fixed) throw UnsafePath(directory.FullName);

        for (DirectoryInfo? ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
        {
            // Anlegen anderer Einträge in einem Vorfahren (z. B. C:\\) erlaubt keinen Austausch unserer Dateien.
            var rights = ancestor == directory ? ReplaceRights | FileSystemRights.WriteData | FileSystemRights.AppendData : ReplaceRights;
            VerifyEntry(ancestor, rights);
        }
        foreach (var path in new[] { executable, tunnelDll, driverDll })
            VerifyEntry(new FileInfo(path), ReplaceRights | FileSystemRights.WriteData | FileSystemRights.AppendData);
    }

    static void VerifyEntry(FileSystemInfo entry, FileSystemRights rights)
    {
        if ((File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0) throw UnsafePath(entry.FullName);
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
        FileSystemSecurity security = entry is DirectoryInfo directory ? directory.GetAccessControl(sections) : ((FileInfo)entry).GetAccessControl(sections);
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (!IsTrusted(descriptor.Owner) || descriptor.DiscretionaryAcl is not { } acl ||
            acl.Cast<GenericAce>().Any(ace => ace is QualifiedAce { AceQualifier: AceQualifier.AccessAllowed } allow &&
                (allow.AceFlags & AceFlags.InheritOnly) == 0 && (allow.AccessMask & (int)rights) != 0 &&
                !IsTrusted(allow.SecurityIdentifier)))
            throw UnsafePath(entry.FullName);
    }

    static bool IsTrusted(SecurityIdentifier? sid) => sid is not null &&
        (SystemSid.Equals(sid) || AdministratorsSid.Equals(sid) || TrustedInstallerSid.Equals(sid));

    static IOException UnsafePath(string path) => new($"Der Standalone-Dienst darf diesen Pfad nicht verwenden: \"{path}\". Bitte WG Client unter Programme installieren.");
}
