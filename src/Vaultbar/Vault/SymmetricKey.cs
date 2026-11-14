using System.Security.Cryptography;

namespace Vaultbar.Vault;

public sealed class SymmetricKey : IDisposable
{
    private readonly byte[]? _encoded;

    /// <summary>Takes the arrays over: their bytes move to locked memory and the arrays passed in are zeroed.</summary>
    public SymmetricKey(byte[] enc, byte[]? mac)
    {
        Enc = LockedMemory.Adopt(enc);
        Mac = mac is null ? null : LockedMemory.Adopt(mac);
        Algorithm = mac is null ? KeyAlgorithm.AesCbc : KeyAlgorithm.AesCbcHmac;
    }

    internal SymmetricKey(byte[] enc, KeyAlgorithm algorithm, byte[] keyId, byte[] encoded)
    {
        Enc = LockedMemory.Adopt(enc);
        Algorithm = algorithm;
        KeyId = keyId;
        _encoded = LockedMemory.Adopt(encoded);
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
    public byte[] ToBytes()
    {
        if (_encoded is { } encoded) return LockedMemory.Copy(encoded);
        if (Mac is null) return LockedMemory.Copy(Enc);
        var both = LockedMemory.Copy(new byte[Enc.Length + Mac.Length]);
        Enc.CopyTo(both, 0);
        Mac.CopyTo(both, Enc.Length);
        return both;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Enc);
        if (Mac != null) CryptographicOperations.ZeroMemory(Mac);
        if (_encoded != null) CryptographicOperations.ZeroMemory(_encoded);
    }
}
