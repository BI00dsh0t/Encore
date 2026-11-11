using System.Security.Cryptography;
using System.Text;

namespace Encore.App.Updates;

/// <summary>Checks that a release feed was signed by the release workflow's key, before any checksum in it is trusted.</summary>
internal static class FeedSignature
{
    /// <summary>The public half of the key the release workflow signs feeds with (ECDSA P-256, SHA-256).</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjW3ks2WlhwU5PkjeEyiQHG/Wq7Ly
        lH1XSRkH47eY18Xmf8+gf8KNtxPcYjqi8WUKdiTBJSt1GZNdHo/tb/BfEw==
        -----END PUBLIC KEY-----
        """;

    /// <summary>The signature's file name beside a feed.</summary>
    public static string SignatureName(string feedName) => feedName + ".sig";

    /// <summary>Whether <paramref name="signatureText"/>, base64 DER, is a valid signature of the feed's exact bytes.</summary>
    public static bool IsValid(byte[] feed, string signatureText, string publicKeyPem = PublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(feed);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.VerifyData(feed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>Signs a feed; the release workflow's script does the same.</summary>
    public static string Sign(byte[] feed, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.SignData(feed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    /// <summary>The feed's text, without a byte order mark.</summary>
    public static string Text(byte[] feed)
    {
        var text = Encoding.UTF8.GetString(feed);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}
