using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Vaultbar.Platform;

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
