using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vaultbar.Vault;

/// <summary>
/// Buffers for key material that the garbage collector never moves, so zeroing reaches the only copy, and whose pages
/// are locked in RAM so they are not written to the page file or swap (L1). Pages stay locked for the life of the process:
/// unlocking one would also unlock any other key sharing its page.
/// </summary>
internal static unsafe class LockedMemory
{
    private static int s_warned;

    /// <summary>Moves the bytes into a pinned, locked buffer and zeroes the array they came in.</summary>
    public static byte[] Adopt(byte[] source)
    {
        var buffer = Copy(source);
        CryptographicOperations.ZeroMemory(source);
        return buffer;
    }

    public static byte[] Copy(ReadOnlySpan<byte> source)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(source.Length, pinned: true);
        source.CopyTo(buffer);
        if (buffer.Length == 0) return buffer;
        bool locked;
        fixed (byte* p = buffer)
            locked = OperatingSystem.IsWindows() ? VirtualLock(p, (nuint)buffer.Length) != 0 : mlock(p, (nuint)buffer.Length) == 0;
        // The working-set or RLIMIT_MEMLOCK limit was reached: the key still works, it may just be paged out.
        if (!locked && Interlocked.Exchange(ref s_warned, 1) == 0) Log.Write($"locked memory: error {Marshal.GetLastPInvokeError()}");
        return buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern int VirtualLock(void* address, nuint size);
    [DllImport("libc", SetLastError = true)] private static extern int mlock(void* address, nuint size);
}
