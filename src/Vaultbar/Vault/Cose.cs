using System.Formats.Cbor;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Vaultbar.Vault;

public enum KeyAlgorithm
{
    AesCbc,
    AesCbcHmac,
    XChaCha20Poly1305,
    XAes256Gcm,
}

/// <summary>
/// Bitwarden's "v2" keys and type 7 EncStrings: COSE_Key and COSE_Encrypt0, as written by bitwarden/sdk-internal
/// (bitwarden-crypto, cose/). Reading only, since Vaultbar never writes to the vault.
/// </summary>
internal static class Cose
{
    private const int LabelKty = 1;
    private const int LabelKeyId = 2;
    private const int LabelKeyAlg = 3;
    private const int LabelKeyMaterial = -1;
    private const int HeaderAlg = 1;
    private const int HeaderContentType = 3;
    private const int HeaderKeyId = 4;
    private const int HeaderIv = 5;
    private const int KtySymmetric = 4;
    private const int AlgXChaCha20Poly1305 = -70000;
    private const int AlgXAes256Gcm = -70010;
    private const int KeyIdSize = 16;
    private const int KeySize = 32;
    private const int NonceSize = 24;

    private const string ContentPaddedUtf8 = "application/x.bitwarden.utf8-padded";
    private static readonly string[] ContentTextKinds = ["application/x.bitwarden.legacy-key", "application/x.bitwarden.spki-public-key"];

    // CoAP content formats: octet-stream, cbor, pkcs8, cose-key.
    private static readonly int[] ContentCoapKinds = [42, 60, 8, 101];

    /// <summary>Keys longer than the legacy 64 bytes are padded so their size alone tells them apart.</summary>
    public static SymmetricKey ParseKey(ReadOnlySpan<byte> padded)
    {
        var encoded = padded.ToArray();
        var cose = Unpad(encoded);
        int? kty = null, alg = null;
        byte[]? keyId = null, material = null;
        try
        {
            var reader = new CborReader(cose, CborConformanceMode.Lax);
            var count = reader.ReadStartMap();
            for (var i = 0; count is null ? reader.PeekState() != CborReaderState.EndMap : i < count; i++)
            {
                if (reader.PeekState() is not (CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger))
                {
                    reader.SkipValue();
                    reader.SkipValue();
                    continue;
                }
                switch (reader.ReadInt32())
                {
                    case LabelKty when reader.PeekState() == CborReaderState.UnsignedInteger: kty = reader.ReadInt32(); break;
                    case LabelKeyId when reader.PeekState() == CborReaderState.ByteString: keyId = reader.ReadByteString(); break;
                    case LabelKeyAlg when reader.PeekState() is CborReaderState.NegativeInteger or CborReaderState.UnsignedInteger: alg = reader.ReadInt32(); break;
                    case LabelKeyMaterial when reader.PeekState() == CborReaderState.ByteString: material = reader.ReadByteString(); break;
                    default: reader.SkipValue(); break;
                }
            }
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new CryptographicException("Malformed COSE key", ex);
        }

        var algorithm = alg switch
        {
            AlgXChaCha20Poly1305 => KeyAlgorithm.XChaCha20Poly1305,
            AlgXAes256Gcm => KeyAlgorithm.XAes256Gcm,
            _ => throw new NotSupportedException($"This account uses a key algorithm ({alg?.ToString() ?? "none"}) Vaultbar does not support yet."),
        };
        if (kty != KtySymmetric || keyId is not { Length: KeyIdSize } || material is not { Length: KeySize })
            throw new CryptographicException("Malformed COSE key");
        return new SymmetricKey(material, algorithm, keyId, encoded);
    }

    public static byte[] DecryptEncrypt0(ReadOnlySpan<byte> message, SymmetricKey key)
    {
        byte[] protectedBytes, iv, ciphertext;
        int? alg;
        byte[]? keyId;
        object? contentType;
        try
        {
            var reader = new CborReader(message.ToArray(), CborConformanceMode.Lax);
            if (reader.PeekState() == CborReaderState.Tag) reader.ReadTag();
            if (reader.ReadStartArray() != 3) throw new CryptographicException("Malformed COSE message");
            protectedBytes = reader.ReadByteString();
            iv = ReadIv(reader) ?? throw new CryptographicException("COSE message has no nonce");
            ciphertext = reader.ReadByteString();
            (alg, keyId, contentType) = ReadProtected(protectedBytes);
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new CryptographicException("Malformed COSE message", ex);
        }

        var expected = key.Algorithm switch
        {
            KeyAlgorithm.XChaCha20Poly1305 => AlgXChaCha20Poly1305,
            KeyAlgorithm.XAes256Gcm => AlgXAes256Gcm,
            _ => throw new CryptographicException("This key cannot open a COSE message"),
        };
        if (alg != expected) throw new CryptographicException("COSE message uses another algorithm than its key");
        if (keyId is null || key.KeyId is null || !keyId.AsSpan().SequenceEqual(key.KeyId)) throw new CryptographicException("COSE message is for another key");
        var padded = contentType is string text && text == ContentPaddedUtf8;
        if (!padded && !(contentType is string other && ContentTextKinds.Contains(other)) && !(contentType is int coap && ContentCoapKinds.Contains(coap)))
            throw new CryptographicException("COSE message has an unknown content type");
        if (iv.Length != NonceSize || ciphertext.Length < 16) throw new CryptographicException("Malformed COSE message");

        var aad = EncStructure(protectedBytes);
        var plain = key.Algorithm == KeyAlgorithm.XChaCha20Poly1305
            ? XChaCha20Poly1305Decrypt(key.Enc, iv, ciphertext, aad)
            : XAes256GcmDecrypt(key.Enc, iv, ciphertext, aad);
        if (!padded) return plain;
        var unpadded = Unpad(plain);
        CryptographicOperations.ZeroMemory(plain);
        return unpadded;
    }

    /// <summary>The associated data COSE defines: ["Encrypt0", protected header, empty external data].</summary>
    internal static byte[] EncStructure(byte[] protectedBytes)
    {
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartArray(3);
        writer.WriteTextString("Encrypt0");
        writer.WriteByteString(protectedBytes);
        writer.WriteByteString([]);
        writer.WriteEndArray();
        return writer.Encode();
    }

    private static byte[]? ReadIv(CborReader reader)
    {
        byte[]? iv = null;
        var count = reader.ReadStartMap();
        for (var i = 0; count is null ? reader.PeekState() != CborReaderState.EndMap : i < count; i++)
        {
            if (reader.PeekState() is CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger && reader.ReadInt32() == HeaderIv && reader.PeekState() == CborReaderState.ByteString)
                iv = reader.ReadByteString();
            else
                reader.SkipValue();
        }
        reader.ReadEndMap();
        return iv;
    }

    private static (int? Alg, byte[]? KeyId, object? ContentType) ReadProtected(byte[] bytes)
    {
        int? alg = null;
        byte[]? keyId = null;
        object? contentType = null;
        var reader = new CborReader(bytes, CborConformanceMode.Lax);
        var count = reader.ReadStartMap();
        for (var i = 0; count is null ? reader.PeekState() != CborReaderState.EndMap : i < count; i++)
        {
            if (reader.PeekState() is not (CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger))
            {
                reader.SkipValue();
                reader.SkipValue();
                continue;
            }
            switch (reader.ReadInt32())
            {
                case HeaderAlg when reader.PeekState() is CborReaderState.NegativeInteger or CborReaderState.UnsignedInteger: alg = reader.ReadInt32(); break;
                case HeaderKeyId when reader.PeekState() == CborReaderState.ByteString: keyId = reader.ReadByteString(); break;
                case HeaderContentType when reader.PeekState() == CborReaderState.TextString: contentType = reader.ReadTextString(); break;
                case HeaderContentType when reader.PeekState() == CborReaderState.UnsignedInteger: contentType = reader.ReadInt32(); break;
                default: reader.SkipValue(); break;
            }
        }
        return (alg, keyId, contentType);
    }

    private static byte[] Unpad(byte[] padded)
    {
        var length = padded.Length == 0 ? 0 : padded[^1];
        if (length == 0 || length > padded.Length) throw new CryptographicException("Invalid padding");
        return padded[..^length];
    }

    private static byte[] XChaCha20Poly1305Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] aad)
    {
        var subkey = HChaCha20(key, nonce.AsSpan(0, 16));
        try
        {
            Span<byte> shortNonce = stackalloc byte[12];
            nonce.AsSpan(16, 8).CopyTo(shortNonce[4..]);
            var plain = new byte[ciphertext.Length - 16];
            using var aead = new ChaCha20Poly1305(subkey);
            aead.Decrypt(shortNonce, ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length), plain, aad);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }
    }

    /// <summary>draft-irtf-cfrg-xchacha section 2.2. .NET has ChaCha20-Poly1305 but not the extended-nonce form.</summary>
    internal static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865;
        s[1] = 0x3320646e;
        s[2] = 0x79622d32;
        s[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) s[4 + i] = BitConverter.ToUInt32(key[(i * 4)..]);
        for (var i = 0; i < 4; i++) s[12 + i] = BitConverter.ToUInt32(nonce[(i * 4)..]);

        for (var round = 0; round < 10; round++)
        {
            Quarter(s, 0, 4, 8, 12);
            Quarter(s, 1, 5, 9, 13);
            Quarter(s, 2, 6, 10, 14);
            Quarter(s, 3, 7, 11, 15);
            Quarter(s, 0, 5, 10, 15);
            Quarter(s, 1, 6, 11, 12);
            Quarter(s, 2, 7, 8, 13);
            Quarter(s, 3, 4, 9, 14);
        }

        var output = new byte[32];
        for (var i = 0; i < 4; i++)
        {
            BitConverter.TryWriteBytes(output.AsSpan(i * 4), s[i]);
            BitConverter.TryWriteBytes(output.AsSpan(16 + i * 4), s[12 + i]);
        }
        s.Clear();
        return output;
    }

    private static void Quarter(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b];
        s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d];
        s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b];
        s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d];
        s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 7);
    }

    /// <summary>https://c2sp.org/XAES-256-GCM: a per-nonce key from CMAC in counter mode, then ordinary AES-256-GCM.</summary>
    internal static byte[] XAes256GcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] aad)
    {
        var derived = XAesDeriveKey(key, nonce);
        try
        {
            var plain = new byte[ciphertext.Length - 16];
            using var aead = new AesGcm(derived, 16);
            aead.Decrypt(nonce.AsSpan(12, 12), ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length), plain, aad);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    internal static byte[] XAesDeriveKey(byte[] key, ReadOnlySpan<byte> nonce)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var k1 = aes.EncryptEcb(new byte[16], PaddingMode.None);
        var msb = k1[0] >> 7;
        for (var i = 0; i < 15; i++) k1[i] = (byte)((k1[i] << 1) | (k1[i + 1] >> 7));
        k1[15] = (byte)((k1[15] << 1) ^ (msb * 0x87));

        var derived = new byte[32];
        var block = new byte[16];
        for (byte counter = 1; counter <= 2; counter++)
        {
            block[0] = 0;
            block[1] = counter;
            block[2] = (byte)'X';
            block[3] = 0;
            nonce[..12].CopyTo(block.AsSpan(4));
            for (var i = 0; i < 16; i++) block[i] ^= k1[i];
            aes.EncryptEcb(block, derived.AsSpan((counter - 1) * 16, 16), PaddingMode.None);
        }
        CryptographicOperations.ZeroMemory(k1);
        return derived;
    }
}
