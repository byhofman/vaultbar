using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vaultbar.Vault;

/// <summary>
/// The quick-unlock enrollment on disk: the challenge, a verifier, and only when the user opted in to quick unlock
/// across restarts, the user key encrypted with a key derived from the device-bound signature. On Windows the file is
/// wrapped once more with DPAPI.
/// </summary>
public sealed class QuickUnlockStore(string path)
{
    private static readonly byte[] Info = "vaultbar quick unlock v1"u8.ToArray();
    private static readonly byte[] VerifierInfo = "vaultbar quick unlock verifier v2"u8.ToArray();

    public QuickUnlockSetting SettingFor(string userId)
    {
        var root = Read();
        if (root is null || DesktopStore.Str(root["userId"]) != userId) return QuickUnlockSetting.NotSetUp;
        if (root["declined"] is JsonValue v && v.TryGetValue<bool>(out var declined) && declined) return QuickUnlockSetting.Off;
        // An enrollment from before #62 has no verifier: it counts as not set up, and Tidy removes it.
        return DesktopStore.Str(root["verifier"]) is not null ? QuickUnlockSetting.On : QuickUnlockSetting.NotSetUp;
    }

    /// <summary>
    /// The only place reading leads to writing: drops an enrollment from before #62 (a key with no age), and a stored
    /// key that persistence no longer allows, is too old, or is dated in the future. Called by explicit actions only (a
    /// probe of data.json, an unlock), so the getters the UI reads stay free of side effects.
    /// </summary>
    public void Tidy(string userId, DateTimeOffset? keepKeyUntil, DateTimeOffset latest)
    {
        var root = Read();
        if (root is null || DesktopStore.Str(root["userId"]) != userId || root["key"] is null) return;
        if (DesktopStore.Str(root["verifier"]) is null || DesktopStore.Str(root["challenge"]) is null)
        {
            Delete();
            return;
        }
        if (Load(userId, keepKeyUntil, latest) is { WrappedKey: null } entry) Save(entry);
    }

    /// <summary>The enrollment for this account, with its stored key only while that key may be used. Reads, never writes.</summary>
    /// <param name="keepKeyUntil">The oldest master-password unlock a stored key may date from, or null when persistence is off.</param>
    /// <param name="latest">The latest one it may date from: a time in the future means a turned-back clock or an edited file.</param>
    public QuickUnlockEntry? Load(string userId, DateTimeOffset? keepKeyUntil, DateTimeOffset latest)
    {
        var root = Read();
        if (root is null || DesktopStore.Str(root["userId"]) != userId) return null;
        if (DesktopStore.Str(root["challenge"]) is not { } challenge) return null;
        if (DesktopStore.Str(root["verifier"]) is not { } verifier) return null;

        var entry = new QuickUnlockEntry(userId, Convert.FromBase64String(challenge), Convert.FromBase64String(verifier));
        if (DesktopStore.Str(root["key"]) is { } key && DateTimeOffset.TryParse(DesktopStore.Str(root["passwordAt"]), out var at))
        {
            if (keepKeyUntil is { } oldest && at >= oldest && at <= latest) return entry with { WrappedKey = Convert.FromBase64String(key), PasswordAt = at };
        }
        return entry;
    }

    public void Save(QuickUnlockEntry entry)
    {
        var root = new JsonObject
        {
            ["userId"] = entry.UserId,
            ["challenge"] = Convert.ToBase64String(entry.Challenge),
            ["verifier"] = Convert.ToBase64String(entry.Verifier),
        };
        if (entry is { WrappedKey: { } key, PasswordAt: { } at })
        {
            root["key"] = Convert.ToBase64String(key);
            root["passwordAt"] = at.ToString("O");
        }
        Write(root);
    }

    public void SaveDeclined(string userId) => Write(new JsonObject { ["userId"] = userId, ["declined"] = true });

    /// <summary>Removes an enrollment whose account is no longer in the desktop app.</summary>
    public void ForgetUnlessIn(IReadOnlyCollection<string> accountIds)
    {
        if (Read() is { } root && DesktopStore.Str(root["userId"]) is { } userId && !accountIds.Contains(userId)) Delete();
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.Write($"quick unlock: {Log.Shape(ex)}");
        }
    }

    /// <summary>
    /// Binds the device signature to this account and user key: a quick unlock is accepted only when the device signs
    /// the challenge to the same value and the key it opens is the one enrolled.
    /// </summary>
    public static byte[] Verifier(byte[] signature, byte[] challenge, string userId, ReadOnlySpan<byte> userKey)
    {
        var info = new byte[VerifierInfo.Length + Encoding.UTF8.GetByteCount(userId) + 32];
        VerifierInfo.CopyTo(info, 0);
        var at = VerifierInfo.Length + Encoding.UTF8.GetBytes(userId, info.AsSpan(VerifierInfo.Length));
        SHA256.HashData(userKey, info.AsSpan(at));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, challenge, info);
    }

    public static bool Verifies(QuickUnlockEntry entry, byte[] signature, ReadOnlySpan<byte> userKey) =>
        CryptographicOperations.FixedTimeEquals(Verifier(signature, entry.Challenge, entry.UserId, userKey), entry.Verifier);

    /// <summary>Encrypts the user key for the disk, with the master-password time it dates from bound in as associated data.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> userKey, byte[] signature, byte[] challenge, string userId, DateTimeOffset passwordAt)
    {
        var key = DeriveKey(signature, challenge);
        try
        {
            return Seal(userKey, key, StoredKeyContext(userId, passwordAt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <exception cref="CryptographicException">The signature does not belong to this entry.</exception>
    public static byte[] Unwrap(QuickUnlockEntry entry, byte[] signature)
    {
        if (entry is not { WrappedKey: { } data, PasswordAt: { } at }) throw new CryptographicException("No stored key.");
        var key = DeriveKey(signature, entry.Challenge);
        try
        {
            return Open(data, key, StoredKeyContext(entry.UserId, at));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string StoredKeyContext(string userId, DateTimeOffset passwordAt) => $"{userId}|{passwordAt.UtcTicks}";

    internal static byte[] Seal(ReadOnlySpan<byte> plain, byte[] key, string context)
    {
        var output = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length), Encoding.UTF8.GetBytes(context));
        return output;
    }

    /// <exception cref="CryptographicException">Wrong key or damaged data.</exception>
    internal static byte[] Open(byte[] data, byte[] key, string context)
    {
        if (data.Length < 12 + 16 + 32) throw new CryptographicException("Stored key is too short.");
        var plain = new byte[data.Length - 12 - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(12 + plain.Length), plain, Encoding.UTF8.GetBytes(context));
        return plain;
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
            Log.Write($"quick unlock: {Log.Shape(ex)}");
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
