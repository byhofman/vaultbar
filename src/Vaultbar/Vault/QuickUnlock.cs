using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vaultbar.Vault;

public enum QuickUnlockStatus
{
    Success,
    Canceled,
    Unavailable,
    Failed,
}

public enum QuickUnlockSetting
{
    NotSetUp,
    On,
    Off,
}

public interface IQuickUnlockProvider
{
    string Name { get; }

    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Signs the challenge with a device-bound key that asks for the user's face, fingerprint or PIN.
    /// The same key always gives the same signature for the same challenge.
    /// </summary>
    Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(byte[] challenge, bool createKey);

    Task DeleteKeyAsync();
}

public sealed record QuickUnlockEntry(string UserId, byte[] Challenge, byte[] WrappedKey);

/// <summary>
/// Keeps the vault's user key on this device, encrypted with a key derived from a device-bound signature,
/// and on Windows wrapped once more with DPAPI.
/// </summary>
public sealed class QuickUnlockStore(string path)
{
    private static readonly byte[] Info = "vaultbar quick unlock v1"u8.ToArray();

    public QuickUnlockSetting SettingFor(string userId)
    {
        var root = Read();
        if (root is null || DesktopStore.Str(root["userId"]) != userId) return QuickUnlockSetting.NotSetUp;
        return root["declined"] is JsonValue v && v.TryGetValue<bool>(out var declined) && declined
            ? QuickUnlockSetting.Off
            : QuickUnlockSetting.On;
    }

    public QuickUnlockEntry? Load(string userId)
    {
        var root = Read();
        if (root is null || DesktopStore.Str(root["userId"]) != userId) return null;
        if (DesktopStore.Str(root["challenge"]) is not { } challenge || DesktopStore.Str(root["key"]) is not { } key) return null;
        return new QuickUnlockEntry(userId, Convert.FromBase64String(challenge), Convert.FromBase64String(key));
    }

    public void Save(QuickUnlockEntry entry) => Write(new JsonObject
    {
        ["userId"] = entry.UserId,
        ["challenge"] = Convert.ToBase64String(entry.Challenge),
        ["key"] = Convert.ToBase64String(entry.WrappedKey),
    });

    public void SaveDeclined(string userId) => Write(new JsonObject { ["userId"] = userId, ["declined"] = true });

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.Write($"quick unlock: {ex.Message}");
        }
    }

    public static byte[] Wrap(ReadOnlySpan<byte> userKey, byte[] signature, byte[] challenge, string userId)
    {
        var key = DeriveKey(signature, challenge);
        try
        {
            var output = new byte[12 + userKey.Length + 16];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(0, 12), userKey, output.AsSpan(12, userKey.Length), output.AsSpan(12 + userKey.Length), Encoding.UTF8.GetBytes(userId));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <exception cref="CryptographicException">The signature does not belong to this entry.</exception>
    public static byte[] Unwrap(QuickUnlockEntry entry, byte[] signature)
    {
        var data = entry.WrappedKey;
        if (data.Length < 12 + 16 + 32) throw new CryptographicException("Stored key is too short.");
        var key = DeriveKey(signature, entry.Challenge);
        try
        {
            var plain = new byte[data.Length - 12 - 16];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(12 + plain.Length), plain, Encoding.UTF8.GetBytes(entry.UserId));
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(byte[] signature, byte[] challenge) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, challenge, Info);

    private JsonObject? Read()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, Info, DataProtectionScope.CurrentUser);
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (Exception ex)
        {
            Log.Write($"quick unlock: {ex.Message}");
            return null;
        }
    }

    private void Write(JsonObject root)
    {
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, Info, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(temp, bytes);
        }
        else
        {
            using var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(bytes);
        }
        File.Move(temp, path, overwrite: true);
    }
}
