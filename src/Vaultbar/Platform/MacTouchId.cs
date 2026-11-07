using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Vaultbar.Vault;

namespace Vaultbar.Platform;

/// <summary>Native side of Touch ID. The test double replaces this, since real use needs a finger on the sensor.</summary>
public interface ITouchIdDevice
{
    bool Available { get; }

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int Create(out byte[] blob);

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret);
}

/// <summary>
/// The "signature" is a key agreement inside the Secure Enclave. It is deterministic like a Windows Hello signature, and
/// the enclave refuses it without Touch ID. The file holds only a key blob that is useless outside this Mac's enclave.
/// </summary>
public sealed class MacTouchId(ITouchIdDevice device, string keyPath) : IQuickUnlockProvider
{
    private const string Reason = "unlock your Bitwarden vault";

    public string Name => "Touch ID";

    public Task<bool> IsAvailableAsync() => Task.FromResult(device.Available);

    public Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(byte[] challenge, bool createKey) =>
        Task.Run<(QuickUnlockStatus, byte[]?)>(() =>
        {
            try
            {
                // With the lid closed Touch ID is off for now, which must not cost the enrollment; Canceled keeps it.
                if (!device.Available) return (createKey ? QuickUnlockStatus.Unavailable : QuickUnlockStatus.Canceled, null);

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
                Log.Write($"touch id: {ex.Message}");
                return (QuickUnlockStatus.Unavailable, null);
            }
        });

    public Task DeleteKeyAsync()
    {
        try
        {
            File.Delete(keyPath);
        }
        catch (IOException ex)
        {
            Log.Write($"touch id: {ex.Message}");
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

[SupportedOSPlatform("macos")]
internal sealed partial class NativeTouchId : ITouchIdDevice
{
    private const string Lib = "libvaultbar-touchid";
    private const int BlobCapacity = 1024;

    [LibraryImport(Lib)] private static partial int vaultbar_touchid_available();
    [LibraryImport(Lib)] private static partial int vaultbar_touchid_create([Out] byte[] blob, int capacity, out int written);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int vaultbar_touchid_derive(byte[] blob, int blobLength, byte[] challenge, int challengeLength, string reason, int noPrompt, [Out] byte[] secret);

    public bool Available
    {
        get
        {
            try
            {
                return vaultbar_touchid_available() != 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public int Create(out byte[] blob)
    {
        var buffer = new byte[BlobCapacity];
        var status = vaultbar_touchid_create(buffer, buffer.Length, out var written);
        blob = status == 0 ? buffer[..written] : [];
        return status;
    }

    public int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret) =>
        Derive(blob, challenge, reason, prompt: true, out secret);

    /// <summary>With <paramref name="prompt"/> off the enclave refuses instead of asking, which lets a test prove the gate holds.</summary>
    internal static int Derive(byte[] blob, byte[] challenge, string reason, bool prompt, out byte[] secret)
    {
        var buffer = new byte[32];
        var status = vaultbar_touchid_derive(blob, blob.Length, challenge, challenge.Length, reason, prompt ? 0 : 1, buffer);
        secret = status == 0 ? buffer : [];
        if (status != 0) CryptographicOperations.ZeroMemory(buffer);
        return status;
    }
}
