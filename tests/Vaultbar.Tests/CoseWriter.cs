using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Vaultbar.Vault;

namespace Vaultbar.Tests;

/// <summary>Writes what the Bitwarden SDK writes, because Vaultbar itself only ever reads this format.</summary>
public static class CoseWriter
{
    public const string ContentPaddedUtf8 = "application/x.bitwarden.utf8-padded";
    public const string ContentLegacyKey = "application/x.bitwarden.legacy-key";
    public const int ContentPkcs8 = 8;

    public static SymmetricKey MakeKey(KeyAlgorithm algorithm)
    {
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(4);
        writer.WriteInt32(2);
        writer.WriteByteString(RandomNumberGenerator.GetBytes(16));
        writer.WriteInt32(3);
        writer.WriteInt32(algorithm == KeyAlgorithm.XAes256Gcm ? -70010 : -70000);
        writer.WriteInt32(4);
        writer.WriteStartArray(4);
        foreach (var op in new[] { 3, 4, 5, 6 }) writer.WriteInt32(op);
        writer.WriteEndArray();
        writer.WriteInt32(-1);
        writer.WriteByteString(RandomNumberGenerator.GetBytes(32));
        writer.WriteEndMap();
        return SymmetricKey.FromBytes(Pad(writer.Encode(), 65));
    }

    public static byte[] PadUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Pad(bytes, 32 * (1 + bytes.Length / 32));
    }

    public static string Encrypt(byte[] plain, SymmetricKey key, object contentType, byte[]? keyIdOverride = null, int? algOverride = null)
    {
        var header = new CborWriter(CborConformanceMode.Lax);
        header.WriteStartMap(3);
        header.WriteInt32(1);
        header.WriteInt32(algOverride ?? (key.Algorithm == KeyAlgorithm.XAes256Gcm ? -70010 : -70000));
        header.WriteInt32(3);
        if (contentType is string text) header.WriteTextString(text);
        else header.WriteInt32((int)contentType);
        header.WriteInt32(4);
        header.WriteByteString(keyIdOverride ?? key.KeyId!);
        header.WriteEndMap();
        var protectedBytes = header.Encode();
        var aad = Cose.EncStructure(protectedBytes);

        var nonce = RandomNumberGenerator.GetBytes(24);
        var output = new byte[plain.Length + 16];
        if (key.Algorithm == KeyAlgorithm.XAes256Gcm)
        {
            using var aead = new AesGcm(Cose.XAesDeriveKey(key.Enc, nonce), 16);
            aead.Encrypt(nonce.AsSpan(12, 12), plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), aad);
        }
        else
        {
            var shortNonce = new byte[12];
            nonce.AsSpan(16, 8).CopyTo(shortNonce.AsSpan(4));
            using var aead = new ChaCha20Poly1305(Cose.HChaCha20(key.Enc, nonce.AsSpan(0, 16)));
            aead.Encrypt(shortNonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), aad);
        }

        var message = new CborWriter(CborConformanceMode.Lax);
        message.WriteStartArray(3);
        message.WriteByteString(protectedBytes);
        message.WriteStartMap(1);
        message.WriteInt32(5);
        message.WriteByteString(nonce);
        message.WriteEndMap();
        message.WriteByteString(output);
        message.WriteEndArray();
        return $"7.{Convert.ToBase64String(message.Encode())}";
    }

    private static byte[] Pad(byte[] bytes, int minLength)
    {
        var padding = Math.Max(minLength - bytes.Length, 1);
        return [.. bytes, .. Enumerable.Repeat((byte)padding, padding)];
    }
}
