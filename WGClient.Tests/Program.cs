using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using WGClient.Core;
using WGClient.ViewModels;

var passed = 0;
var failed = 0;

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void AssertEqual<T>(T expected, T actual, string? label = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label ?? "Mismatch"}: expected <{expected}>, got <{actual}>");
}

Run("Cidr.IsValid strict IPv4/IPv6", () =>
{
    Assert(Cidr.IsValid("10.8.0.2/32"), "valid v4 /32");
    Assert(Cidr.IsValid("192.168.1.0/24"), "valid v4 /24");
    Assert(Cidr.IsValid("0.0.0.0/0"), "valid v4 /0");
    Assert(Cidr.IsValid("10.8.0.2"), "valid bare v4");
    Assert(Cidr.IsValid("2001:db8::/32"), "valid v6");
    Assert(Cidr.IsValid("::/0"), "valid v6 /0");

    Assert(!Cidr.IsValid("10"), "reject single-int v4");
    Assert(!Cidr.IsValid("192.168.1/24"), "reject 3-octet v4");
    Assert(!Cidr.IsValid("010.0.0.1/32"), "reject octal/leading zero v4");
    Assert(!Cidr.IsValid("fe80::1%eth0/64"), "reject v6 scope id");
    Assert(!Cidr.IsValid("10.0.0.0/+24"), "reject +prefix");
    Assert(!Cidr.IsValid("10.0.0.0/-1"), "reject -prefix");
    Assert(!Cidr.IsValid("10.0.0.0/33"), "reject v4 /33");
    Assert(!Cidr.IsValid("2001:db8::/129"), "reject v6 /129");
    Assert(!Cidr.IsValid("10.0.0.1/24/8"), "reject double slash");
});

Run("Cidr.TryParseV4 rejects double slash and shortened IPv4", () =>
{
    Assert(Cidr.TryParseV4("192.168.1.37/24", out var s, out var e), "valid /24");
    AssertEqual(0xC0A80100u, s, "start");
    AssertEqual(0xC0A801FFu, e, "end");
    Assert(!Cidr.TryParseV4("10.0.0.1/24/foo", out _, out _), "double slash");
    Assert(!Cidr.TryParseV4("192.168.1/24", out _, out _), "shortened v4");
    Assert(!Cidr.TryParseV4("10.0.0.1/+24", out _, out _), "plus prefix");
});

Run("Cidr.IsValidDnsEntry", () =>
{
    Assert(Cidr.IsValidDnsEntry("1.1.1.1"), "v4");
    Assert(Cidr.IsValidDnsEntry("2606:4700:4700::1111"), "v6");
    Assert(Cidr.IsValidDnsEntry("fritz.box"), "search domain");
    Assert(Cidr.IsValidDnsEntry("corp.internal"), "search domain 2");
    Assert(!Cidr.IsValidDnsEntry("192.168.1.1/24"), "reject cidr");
    Assert(!Cidr.IsValidDnsEntry("192.168.1"), "reject shortened v4");
    Assert(!Cidr.IsValidDnsEntry("fe80::1%eth0"), "reject v6 scope");
    Assert(!Cidr.IsValidDnsEntry("1.1.1.1=x"), "reject equals");
    Assert(!Cidr.IsValidDnsEntry("1.1.1.1#c"), "reject hash");
});

Run("Cidr.SubtractV4 excludes local ranges", () =>
{
    var rest = Cidr.SubtractV4("0.0.0.0/0", Cidr.LocalV4Ranges);
    Assert(Cidr.ContainsV4(rest, IPAddress.Parse("1.1.1.1")), "public ip included");
    Assert(!Cidr.ContainsV4(rest, IPAddress.Parse("192.168.178.1")), "lan excluded");
    Assert(!Cidr.ContainsV4(rest, IPAddress.Parse("10.0.0.5")), "10/8 excluded");
    Assert(!Cidr.ContainsV4(rest, IPAddress.Parse("172.16.0.1")), "172.16/12 excluded");
    Assert(!Cidr.ContainsV4(rest, IPAddress.Parse("169.254.1.1")), "link-local excluded");
    Assert(!Cidr.ContainsV4(rest, IPAddress.Parse("224.0.0.251")), "mDNS excluded");
});

Run("TunnelStore.SanitizeName reserved device names and dots", () =>
{
    AssertEqual("_CON", TunnelStore.SanitizeName("CON"));
    AssertEqual("_nul", TunnelStore.SanitizeName("nul"));
    AssertEqual("_COM1", TunnelStore.SanitizeName("COM1"));
    AssertEqual("_LPT9.vpn", TunnelStore.SanitizeName("LPT9.vpn"));
    AssertEqual("tunnel", TunnelStore.SanitizeName("."));
    AssertEqual("tunnel", TunnelStore.SanitizeName(".."));
    AssertEqual("tunnel", TunnelStore.SanitizeName("..."));
    AssertEqual("foo", TunnelStore.SanitizeName("..foo.."));
    AssertEqual("Buero_Sued", TunnelStore.SanitizeName("Büro Süd"));
    AssertEqual(32, TunnelStore.SanitizeName(new string('a', 40)).Length);
    // Idempotenz
    foreach (var sample in new[] { "CON", "nul.conf", "..", "Büro", "home-1" })
        AssertEqual(TunnelStore.SanitizeName(sample), TunnelStore.SanitizeName(TunnelStore.SanitizeName(sample)));
});

const string SampleConfig = """
    [Interface]
    PrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
    Address = 10.8.0.2/32
    DNS = 1.1.1.1

    [Peer]
    PublicKey = BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=
    PresharedKey = CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC=
    AllowedIPs = 0.0.0.0/0, ::/0
    Endpoint = vpn.example.com:51820
    PersistentKeepalive = 25
    """;

Run("ConfigBuilder AllowLan uses 2000::/3 for IPv6 and clears Endpoint", () =>
{
    var original = WgConfig.Parse(SampleConfig);
    var opts = TunnelOptions.FromConfig(original);
    opts.PreserveImportedRoutes = false;
    opts.AllowLan = true;
    opts.IncludeIPv6 = true;
    opts.Endpoint = "";
    opts.PersistentKeepalive = 0;

    var built = ConfigBuilder.Build(original, opts);
    var peer = built.Peers.Single();
    var allowed = ConfigBuilder.SplitList(peer.Get("AllowedIPs"));
    Assert(allowed.Contains("2000::/3"), "global unicast v6 routed");
    Assert(!allowed.Contains("::/1") && !allowed.Contains("8000::/1"), "local v6 not routed");
    Assert(peer.Get("Endpoint") is null, "endpoint cleared");
    Assert(peer.Get("PersistentKeepalive") is null, "keepalive cleared");
    Assert(!ConfigBuilder.HasKillSwitch(built), "no kill switch with allow lan");
});

Run("WgConfig Serialize masks secrets", () =>
{
    var cfg = WgConfig.Parse(SampleConfig);
    var masked = cfg.Serialize(maskSecrets: true);
    Assert(!masked.Contains("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="), "private key masked");
    Assert(!masked.Contains("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC="), "psk masked");
    Assert(masked.Contains("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB="), "public key visible");
});

Run("WgKeys X25519 RFC 7748 test vector", () =>
{
    // RFC 7748 Section 6.1 Alice private key -> public key
    var privBytes = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
    var expectedPub = Convert.ToBase64String(Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"));
    var pub = WgKeys.PublicKey(Convert.ToBase64String(privBytes));
    AssertEqual(expectedPub, pub, "RFC 7748 pubkey");
    Assert(WgValidation.IsKey(WgKeys.GeneratePrivateKey()), "generated private key valid");
    Assert(WgValidation.IsKey(WgKeys.GeneratePresharedKey()), "generated psk valid");
});

Run("WgValidation.IsEndpoint", () =>
{
    Assert(WgValidation.IsEndpoint("vpn.example.com:51820"), "dns:port");
    Assert(WgValidation.IsEndpoint("1.2.3.4:51820"), "v4:port");
    Assert(WgValidation.IsEndpoint("[2001:db8::1]:51820"), "v6:port");
    Assert(WgValidation.IsEndpoint("[fe80::1%eth0]:51820"), "v6 zone:port");
    Assert(!WgValidation.IsEndpoint("vpn.example.com:0"), "port 0");
    Assert(!WgValidation.IsEndpoint("vpn.example.com:70000"), "port > 65535");
    Assert(!WgValidation.IsEndpoint("vpn.example.com"), "missing port");
    Assert(!WgValidation.IsEndpoint("vpn.example.com:51820\nPostUp=x"), "newline rejected");
});

Run("EmbeddedTunnel.ParseStats valid and overflow protection", () =>
{
    var data = new byte[80 + 136];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), 1); // 1 peer
    var peer = data.AsSpan(80, 136);
    BinaryPrimitives.WriteUInt32LittleEndian(peer[0..], 1u << 3); // HAS_ENDPOINT
    BinaryPrimitives.WriteUInt16LittleEndian(peer[76..], 2); // AF_INET
    BinaryPrimitives.WriteUInt16BigEndian(peer[78..], 51820);
    peer[80] = 203; peer[81] = 0; peer[82] = 113; peer[83] = 5;
    BinaryPrimitives.WriteUInt64LittleEndian(peer[104..], 1234); // tx
    BinaryPrimitives.WriteUInt64LittleEndian(peer[112..], 5678); // rx
    var ft = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero).ToFileTime();
    BinaryPrimitives.WriteUInt64LittleEndian(peer[120..], (ulong)ft);

    var stats = EmbeddedTunnel.ParseStats(data);
    Assert(stats is not null, "parsed stats");
    AssertEqual(1234L, stats!.TxBytes, "tx");
    AssertEqual(5678L, stats.RxBytes, "rx");
    AssertEqual("203.0.113.5:51820", stats.Endpoint, "endpoint");
    AssertEqual(ft, stats.LastHandshake!.Value.ToFileTime(), "handshake");

    // Overflow test: 2 peers where first claims huge AllowedIPsCount
    var bad = new byte[80 + 136 * 2];
    BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(72), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(80 + 128), 0x40000000u);
    Assert(EmbeddedTunnel.ParseStats(bad) is null, "overflow returns null");
});

Run("EmbeddedTunnel.ParseLog uses invariant time separator", () =>
{
    var buf = new byte[8 + 520];
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), 0xbadbabe);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 0);
    var ns = 1_700_000_000_123_456_700L;
    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8), ns);
    Encoding.UTF8.GetBytes("[test] Handshake ok").CopyTo(buf, 16);

    var prev = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI"); // uses '.' as time separator
        var lines = EmbeddedTunnel.ParseLog(buf);
        AssertEqual(1, lines.Count, "line count");
        Assert(lines[0].Length > 28 && lines[0][13] == ':' && lines[0][16] == ':', $"expected ':' separator, got {lines[0]}");
    }
    finally { CultureInfo.CurrentCulture = prev; }
});

Run("WireGuardUpdates parsers and version comparison", () =>
{
    const string sig = """
        untrusted comment: signature from wireguard
        RWQf6LRCGA9i53mlYecO4IzT51TGPpvWucNSCh1CBM0QTaLn73Y7GFO3
        0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  wireguard-amd64-0.5.3.msi
        abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  wireguard-arm64-0.5.4.msi
        """;
    AssertEqual(new Version(0, 5, 3), WireGuardUpdates.ParseOfficialListing(sig, Architecture.X64));
    AssertEqual(new Version(0, 5, 4), WireGuardUpdates.ParseOfficialListing(sig, Architecture.Arm64));

    const string releaseJson = """
        {
          "tag_name": "v1.4.2",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "WGClient-Standalone-Setup-1.4.2.exe",
              "state": "uploaded",
              "browser_download_url": "https://github.com/adrianxx85/wireguard-gui-windows/releases/download/v1.4.2/WGClient-Standalone-Setup-1.4.2.exe"
            }
          ]
        }
        """;
    var rel = WireGuardUpdates.ParseStandaloneRelease(releaseJson);
    AssertEqual(new Version(1, 4, 2), rel.Version);
    Assert(WireGuardUpdates.IsNewer(new Version(1, 4, 2), new Version(1, 4, 1, 0)), "1.4.2 > 1.4.1.0");
    Assert(!WireGuardUpdates.IsNewer(new Version(1, 4, 2), new Version(1, 4, 2, 0)), "1.4.2 == 1.4.2.0");
});

Run("TunnelVm validation rejects invalid DNS and signed MTU", () =>
{
    var cfg = WgConfig.Parse(SampleConfig);
    var vm = new TunnelVm("test", cfg, TunnelOptions.FromConfig(cfg));
    vm.Dns = "192.168.1.1/24";
    Assert(!vm.TryGetOptions(out _, out var dnsErr) && dnsErr!.Contains("DNS"), "invalid dns rejected");

    vm.Dns = "1.1.1.1";
    vm.MtuText = "+1420";
    Assert(!vm.TryGetOptions(out _, out var mtuErr) && mtuErr!.Contains("MTU"), "signed mtu rejected");

    vm.MtuText = "1420";
    vm.Endpoint = "";
    Assert(vm.TryGetOptions(out var opts, out _), "valid options");
    Assert(!vm.EffectivePreview.Contains("Endpoint ="), "cleared endpoint removed from preview");
    AssertEqual(1420, opts.Mtu);
});

Console.WriteLine($"Tests: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
