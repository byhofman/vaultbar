namespace Vaultbar;

public static class Paths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? Override => Environment.GetEnvironmentVariable("VAULTBAR_CONFIG_DIR") is { Length: > 0 } d ? d : null;

    public static string ConfigDir { get; } = Override
        ?? (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vaultbar")
            : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support", "vaultbar")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"), "vaultbar"));

    public static string CacheDir { get; } = Override is { } o ? Path.Combine(o, "cache")
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vaultbar")
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Caches", "vaultbar")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".cache"), "vaultbar");

    /// <summary>Where the single-instance lock and socket live on macOS and Linux: never a shared folder like /tmp.</summary>
    public static string RuntimeDir { get; } = Override is null && !OperatingSystem.IsMacOS()
                                               && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && Path.IsPathRooted(run)
        ? Path.Combine(run, "vaultbar")
        : CacheDir;

    /// <summary>Creates the folder readable by the current user only; on Windows the profile folders already are.</summary>
    public static string EnsurePrivate(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return dir;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, ownerOnly);
        if (File.GetUnixFileMode(dir) != ownerOnly) File.SetUnixFileMode(dir, ownerOnly);
        return dir;
    }
}
