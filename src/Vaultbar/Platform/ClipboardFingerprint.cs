using System.Security.Cryptography;
using System.Text;

namespace Vaultbar.Platform;

/// <summary>
/// Recognises what Vaultbar put on the clipboard without keeping it: X11 and Wayland have no change counter, and holding
/// the copied secret itself would keep it in memory for as long as the app runs.
/// </summary>
public sealed class ClipboardFingerprint
{
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _hash;

    public ClipboardFingerprint(string text) => _hash = Hash(text);

    public bool Matches(string? text) => text != null && CryptographicOperations.FixedTimeEquals(Hash(text), _hash);

    private byte[] Hash(string text) => HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(text));
}
