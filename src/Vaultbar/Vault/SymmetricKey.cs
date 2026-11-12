using System.Security.Cryptography;

namespace Vaultbar.Vault;

public sealed class SymmetricKey : IDisposable
{
    private readonly byte[]? _encoded;

    public SymmetricKey(byte[] enc, byte[]? mac)
    {
        Enc = enc;
        Mac = mac;
        Algorithm = mac is null ? KeyAlgorithm.AesCbc : KeyAlgorithm.AesCbcHmac;
    }

    internal SymmetricKey(byte[] enc, KeyAlgorithm algorithm, byte[] keyId, byte[] encoded)
    {
        Enc = enc;
        Algorithm = algorithm;
        KeyId = keyId;
        _encoded = encoded;
    }

    public byte[] Enc { get; }
    public byte[]? Mac { get; }
    public KeyAlgorithm Algorithm { get; }
    public byte[]? KeyId { get; }

    public static SymmetricKey FromBytes(ReadOnlySpan<byte> key) => key.Length switch
    {
        64 => new SymmetricKey(key[..32].ToArray(), key[32..].ToArray()),
        32 => new SymmetricKey(key.ToArray(), null),
        > 64 => Cose.ParseKey(key),
        _ => throw new CryptographicException($"Unexpected key length {key.Length}"),
    };

    /// <summary>The same bytes <see cref="FromBytes"/> takes.</summary>
    public byte[] ToBytes() => _encoded is { } encoded ? encoded.ToArray() : Mac is null ? Enc.ToArray() : [.. Enc, .. Mac];

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Enc);
        if (Mac != null) CryptographicOperations.ZeroMemory(Mac);
        if (_encoded != null) CryptographicOperations.ZeroMemory(_encoded);
    }
}
