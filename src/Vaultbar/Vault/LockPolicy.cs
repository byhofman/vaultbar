namespace Vaultbar.Vault;

/// <summary>
/// When the vault locks by itself, from Vaultbar's <c>lockAfterMinutes</c> or else the Bitwarden desktop app's vault
/// timeout (<c>vault-timeout.type.ts</c> in bitwarden/clients): a number of idle minutes, 0 for "Immediately", the words
/// "onIdle", "onSleep", "onLocked", "onRestart" and "never", and the negative numbers older versions stored for them.
/// </summary>
public sealed record LockPolicy(TimeSpan? IdleAfter, bool OnBarHidden = false, bool OnSystemIdle = false, bool OnSystemLock = false)
{
    /// <summary>
    /// Used where Bitwarden would keep the vault open until a restart or forever: Vaultbar runs for weeks, so either
    /// would leave the vault open as long. Bitwarden's own default timeout is the same 15 minutes.
    /// </summary>
    public static readonly TimeSpan Fallback = TimeSpan.FromMinutes(15);

    /// <summary>How long the system must go without input for "On system idle", as Bitwarden's desktop app measures it.</summary>
    public static readonly TimeSpan SystemIdleThreshold = TimeSpan.FromMinutes(5);

    public static LockPolicy From(int? lockAfterMinutes, string? bitwardenTimeout)
    {
        if (lockAfterMinutes is { } minutes) return new LockPolicy(minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);

        return bitwardenTimeout?.Trim() switch
        {
            // The idle fallback stays as a backstop: the vault can be unlocked while the bar is hidden, and on Linux the
            // session-lock signals are missing where no logind or screensaver is on D-Bus.
            "0" => new LockPolicy(Fallback, OnBarHidden: true),
            "onIdle" or "-4" => new LockPolicy(null, OnSystemIdle: true),
            "onSleep" or "-3" or "onLocked" or "-2" => new LockPolicy(Fallback, OnSystemLock: true),
            { } n when int.TryParse(n, out var m) && m > 0 => new LockPolicy(TimeSpan.FromMinutes(m)),
            _ => new LockPolicy(Fallback),
        };
    }

    /// <param name="barIdle">Time since the bar was last used.</param>
    /// <param name="systemIdle">Time since the last keyboard or mouse input anywhere, or null where the system does not say;
    /// the bar's own idle time is then used, which is never shorter.</param>
    public bool IdleExpired(TimeSpan barIdle, TimeSpan? systemIdle) =>
        IdleAfter is { } after && barIdle >= after || OnSystemIdle && (systemIdle ?? barIdle) >= SystemIdleThreshold;
}
