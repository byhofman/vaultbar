using System.Runtime.Versioning;
using Vaultbar.Vault;

namespace Vaultbar.Platform;

/// <summary>
/// The "signature" is a key agreement inside the Secure Enclave. It is deterministic like a Windows Hello signature, and
/// the enclave refuses it without Touch ID. In an unsigned build the file holds a key blob that is useless outside this
/// Mac's enclave, but any process running as the user can load it there (M3). A Developer ID build with a keychain access
/// group keeps one key per account in the data-protection keychain instead, which only Vaultbar's signature can use;
/// the blob of an earlier enrollment is then deleted, and the next unlock sets Touch ID up again.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacTouchId(ITouchIdDevice device, string keyPath) : IQuickUnlockProvider
{
    private const string Reason = "unlock your Bitwarden vault";

    public string Name => "Touch ID";

    public Task<bool> IsAvailableAsync() => Task.FromResult(device.Available);

    public Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(string account, byte[] challenge, bool createKey) =>
        Task.Run<(QuickUnlockStatus, byte[]?)>(() =>
        {
            try
            {
                // With the lid closed Touch ID is off for now, which must not cost the enrollment; Canceled keeps it.
                if (!device.Available) return (createKey ? QuickUnlockStatus.Unavailable : QuickUnlockStatus.Canceled, null);

                if (device.KeychainAvailable) return Keychain(account, challenge, createKey);

                if (createKey)
                {
                    var created = device.Create(out var fresh);
                    if (created != 0)
                    {
                        Log.Write($"touch id: create failed ({created})");
                        return (Classify(created), null);
                    }
                    WritePrivate(fresh);
                }

                if (!File.Exists(keyPath)) return (QuickUnlockStatus.Unavailable, null);
                var result = device.Derive(File.ReadAllBytes(keyPath), challenge, Reason, out var secret);
                if (result == 0) return (QuickUnlockStatus.Success, secret);
                Log.Write($"touch id: derive failed ({result})");
                return (Classify(result), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Write($"touch id: {Log.Shape(ex)}");
                return (QuickUnlockStatus.Unavailable, null);
            }
        });

    internal static string KeychainTag(string account) =>
        "dev.byhofman.vaultbar.quick-unlock." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account)))[..16];

    private (QuickUnlockStatus, byte[]?) Keychain(string account, byte[] challenge, bool createKey)
    {
        if (File.Exists(keyPath)) File.Delete(keyPath);
        var tag = KeychainTag(account);
        if (createKey && device.CreateInKeychain(tag) is var created and not 0)
        {
            Log.Write($"touch id: keychain create failed ({created})");
            return (Classify(created), null);
        }
        var result = device.DeriveFromKeychain(tag, challenge, Reason, out var secret);
        if (result == 0) return (QuickUnlockStatus.Success, secret);
        Log.Write($"touch id: keychain derive failed ({result})");
        return (Classify(result), null);
    }

    public Task DeleteKeyAsync(string account)
    {
        if (device.KeychainAvailable) device.DeleteFromKeychain(KeychainTag(account));
        try
        {
            File.Delete(keyPath);
        }
        catch (IOException ex)
        {
            Log.Write($"touch id: {Log.Shape(ex)}");
        }
        return Task.CompletedTask;
    }

    /// <summary>Unavailable makes the vault forget the enrollment, so only a key that can never work again maps to it.</summary>
    public static QuickUnlockStatus Classify(int code) => code switch
    {
        // LAError userCancel, userFallback, systemCancel, appCancel, notInteractive; errSecUserCanceled
        -2 or -3 or -4 or -9 or -1004 or -128 => QuickUnlockStatus.Canceled,
        // errSecItemNotFound and errSecAuthFailed: the enrolled fingers changed, which voids a biometryCurrentSet key
        -25300 or -25293 => QuickUnlockStatus.Unavailable,
        _ => QuickUnlockStatus.Failed,
    };

    private void WritePrivate(byte[] blob)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        var temp = keyPath + ".tmp";
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            stream.Write(blob);
        }
        File.Move(temp, keyPath, overwrite: true);
    }
}
