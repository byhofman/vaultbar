using System.Runtime.InteropServices;

namespace Vaultbar;

/// <summary>
/// Keeps the running bar out of crash dumps, which would hold the vault key while it is unlocked (L1): Windows Error
/// Reporting excludes it, and on macOS and Linux the core-dump size limit is zero.
/// </summary>
internal static class CrashDumps
{
    private const int RLIMIT_CORE = 4;

    public static void Exclude()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var hr = WerAddExcludedApplication(Environment.ProcessPath ?? "vaultbar.exe", 0);
                if (hr < 0) Log.Write($"crash dumps: WER 0x{hr:x8}");
            }
            else
            {
                var none = new RLimit();
                if (setrlimit(RLIMIT_CORE, ref none) != 0) Log.Write($"crash dumps: setrlimit error {Marshal.GetLastPInvokeError()}");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Write($"crash dumps: {Log.Shape(ex)}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current, Maximum;
    }

    [DllImport("wer.dll", CharSet = CharSet.Unicode)] private static extern int WerAddExcludedApplication(string exeName, int allUsers);
    [DllImport("libc", SetLastError = true)] private static extern int setrlimit(int resource, ref RLimit limit);
}
