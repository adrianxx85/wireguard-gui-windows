using System.Net;
using System.Net.Sockets;

namespace WGClient.Core;

/// <summary>Erzeugt den [Peer]-Block, den der WireGuard-Server für dieses Gerät braucht.</summary>
public static class ServerPeer
{
    public static string Snippet(string name, string publicKey, string? presharedKey, string? addresses, bool maskPresharedKey = false)
    {
        // Der Server routet nur die Tunnel-Adressen dieses Geräts hierher (Host-Routen).
        var hostRoutes = ConfigBuilder.SplitList(addresses)
            .Select(a => IPAddress.TryParse(a.Split('/')[0], out var ip) ? ip : null)
            .OfType<IPAddress>()
            .Select(ip => $"{ip}/{(ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}")
            .ToList();

        var lines = new List<string> { "[Peer]", $"# {name}", $"PublicKey = {publicKey}" };
        if (!string.IsNullOrWhiteSpace(presharedKey))
            lines.Add($"PresharedKey = {(maskPresharedKey ? "(verborgen)" : presharedKey.Trim())}");
        lines.Add($"AllowedIPs = {string.Join(", ", hostRoutes)}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>Block für einen gespeicherten Tunnel (öffentlicher Schlüssel wird aus dem privaten berechnet).</summary>
    public static async Task<string> SnippetAsync(string name, WgConfig cfg, WgSection peer)
    {
        if (!cfg.Peers.Contains(peer)) throw new ArgumentException("Die Gegenstelle gehört nicht zu diesem Tunnel.", nameof(peer));
        var privateKey = cfg.Interface?.Get("PrivateKey") ?? throw new InvalidOperationException("PrivateKey fehlt in [Interface].");
        var publicKey = await WireGuardService.PublicKeyAsync(privateKey);
        return Snippet(name, publicKey, peer.Get("PresharedKey"), cfg.Interface.Get("Address"));
    }
}
