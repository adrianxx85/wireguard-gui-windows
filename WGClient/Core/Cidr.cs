using System.Net;
using System.Net.Sockets;

namespace WGClient.Core;

public static class Cidr
{
    static readonly string[] PrivateV4Ranges = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    /// <summary>Private/lokale IPv4-Bereiche, die bei "LAN erreichbar" am Tunnel vorbei geroutet werden.</summary>
    public static readonly string[] LocalV4Ranges =
    [
        ..PrivateV4Ranges, "169.254.0.0/16",                                 // privat + link-local
        "224.0.0.0/4", "255.255.255.255/32"                                 // Multicast/Broadcast (Discovery, mDNS …)
    ];

    public static bool IsValid(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return false;
        if (parts.Length == 1) return true;
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(parts[1], out var p) && p >= 0 && p <= max;
    }

    public static bool TryParseV4(string cidr, out uint start, out uint end)
    {
        start = end = 0;
        var parts = cidr.Trim().Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var prefix = 32;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        start = ToUInt(ip) & mask;
        end = start | ~mask;
        return true;
    }

    /// <summary>Netzadresse, z. B. (192.168.1.37, 24) → "192.168.1.0/24".</summary>
    public static string NetworkV4(IPAddress ip, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return $"{FromUInt(ToUInt(ip) & mask)}/{prefix}";
    }

    /// <summary>Adresse im Netz mit Abstand <paramref name="offset"/> zur Netzadresse, z. B. ("10.8.0.0/24", 1) → "10.8.0.1".</summary>
    public static string? HostV4(string subnet, uint offset) =>
        TryParseV4(subnet, out var s, out var e) && offset <= e - s ? FromUInt(s + offset) : null;

    public static bool OverlapsV4(string a, string b) =>
        TryParseV4(a, out var s1, out var e1) && TryParseV4(b, out var s2, out var e2) && s1 <= e2 && s2 <= e1;

    /// <summary>
    /// Schlägt ein freies privates /24 vor, bevorzugt unter 10.0.0.0/8.
    /// </summary>
    public static string? SuggestSubnetV4(IEnumerable<string> used, Random? random = null)
    {
        random ??= Random.Shared;
        var blocked = new List<(uint First, uint Last)>();
        foreach (var address in used)
            if (TryParseV4(address, out var start, out var end)) blocked.Add((start >> 8, end >> 8));

        foreach (var pool in PrivateV4Ranges)
        {
            if (!TryParseV4(pool, out var start, out var end)) continue;
            var first = start >> 8;
            var count = (int)((end >> 8) - first + 1);
            var offset = random.Next(count);
            for (var i = 0; i < count; i++)
            {
                var candidate = first + (uint)((offset + i) % count);
                if (!blocked.Any(range => candidate >= range.First && candidate <= range.Last))
                    return $"{FromUInt(candidate << 8)}/24";
            }
        }
        return null;
    }

    public static bool ContainsV4(IEnumerable<string> cidrs, IPAddress ip)
    {
        var a = ToUInt(ip);
        return cidrs.Any(c => TryParseV4(c, out var s, out var e) && a >= s && a <= e);
    }

    /// <summary>Zieht Netze von einem Netz ab und liefert die minimale CIDR-Liste des Rests.</summary>
    public static List<string> SubtractV4(string from, IEnumerable<string> exclude)
    {
        if (!TryParseV4(from, out var fs, out var fe)) throw new ArgumentException(from);
        var ranges = new List<(uint S, uint E)> { (fs, fe) };
        foreach (var ex in exclude)
        {
            if (!TryParseV4(ex, out var xs, out var xe)) continue;
            var next = new List<(uint, uint)>();
            foreach (var (rs, re) in ranges)
            {
                if (xe < rs || xs > re) { next.Add((rs, re)); continue; }
                if (xs > rs) next.Add((rs, xs - 1));
                if (xe < re) next.Add((xe + 1, re));
            }
            ranges = next;
        }
        return ranges.OrderBy(r => r.S).SelectMany(r => RangeToCidrs(r.S, r.E)).ToList();
    }

    static IEnumerable<string> RangeToCidrs(uint start, uint end)
    {
        ulong s = start, e = end;
        while (s <= e)
        {
            var prefix = 32;
            while (prefix > 0)
            {
                var size = 1UL << (33 - prefix); // Blockgröße für prefix-1
                if ((s & (size - 1)) != 0 || s + size - 1 > e) break;
                prefix--;
            }
            yield return $"{FromUInt((uint)s)}/{prefix}";
            s += 1UL << (32 - prefix);
        }
    }

    static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    static string FromUInt(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
}
