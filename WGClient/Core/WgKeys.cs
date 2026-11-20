using System.Security.Cryptography;

namespace WGClient.Core;

/// <summary>WireGuard-Schlüssel (X25519) über die Windows-Kryptographie (CNG) – ohne wg.exe.</summary>
public static class WgKeys
{
    static readonly ECCurve Curve25519 = ECCurve.CreateFromFriendlyName("curve25519");

    public static string GeneratePrivateKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Clamp(key);
            return Convert.ToBase64String(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Pre-shared Key: 32 zufällige Bytes (entspricht "wg genpsk").</summary>
    public static string GeneratePresharedKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try { return Convert.ToBase64String(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Öffentlicher Schlüssel zum privaten (entspricht "wg pubkey").</summary>
    public static string PublicKey(string privateKey)
    {
        if (!WgValidation.IsKey(privateKey))
            throw new ArgumentException("Der private Schlüssel ist ungültig (44 Zeichen Base64).", nameof(privateKey));
        var d = Convert.FromBase64String(privateKey.Trim());
        try
        {
            Clamp(d); // wie X25519 selbst – CNG erwartet einen bereits geklemmten Skalar
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportParameters(new ECParameters { Curve = Curve25519, D = d });
            var x = ecdh.ExportParameters(false).Q.X;
            if (x is not { Length: 32 }) throw new CryptographicException("Unerwartete Länge des öffentlichen Schlüssels.");
            return Convert.ToBase64String(x);
        }
        finally { CryptographicOperations.ZeroMemory(d); }
    }

    static void Clamp(byte[] key)
    {
        key[0] &= 248;
        key[31] = (byte)((key[31] & 127) | 64);
    }
}
