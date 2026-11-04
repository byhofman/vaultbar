using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vaultbar;

public sealed class Config
{
    public string? Hotkey { get; set; }

    /// <summary>Idle minutes before locking. Null follows the Bitwarden desktop vault timeout.</summary>
    public int? LockAfterMinutes { get; set; }

    public bool LockOnSystemLock { get; set; } = true;
    public int ClipboardClearSeconds { get; set; } = 30;
    public string AutotypeSequence { get; set; } = "{USERNAME}{TAB}{PASSWORD}";
    public bool CopyTotpAfterFill { get; set; } = true;
    public bool HideAfterCopy { get; set; } = true;
    public bool ShowFavicons { get; set; } = true;
    public bool HideFromScreenCapture { get; set; } = true;
    public bool WindowsHello { get; set; } = true;
    public string? DataPath { get; set; }

    [JsonIgnore]
    public string EffectiveHotkey => string.IsNullOrWhiteSpace(Hotkey) ? DefaultHotkey : Hotkey;

    public static string DefaultHotkey => OperatingSystem.IsMacOS() ? "Cmd+Shift+Space" : "Ctrl+Shift+Space";

    public static string FilePath => Path.Combine(Paths.ConfigDir, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), JsonCtx.Default.Config) ?? new Config();

            var cfg = new Config { Hotkey = DefaultHotkey };
            Directory.CreateDirectory(Paths.ConfigDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonCtx.Default.Config));
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Write($"config: {ex.Message}; using defaults");
            return new Config();
        }
    }
}

public sealed class UsageEntry
{
    public int C { get; set; }
    public long T { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(Dictionary<string, UsageEntry>))]
internal partial class JsonCtx : JsonSerializerContext;

public static class Paths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigDir { get; } = Environment.GetEnvironmentVariable("VAULTBAR_CONFIG_DIR") is { Length: > 0 } d ? d
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vaultbar")
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support", "vaultbar")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"), "vaultbar");

    public static string CacheDir { get; } = Environment.GetEnvironmentVariable("VAULTBAR_CONFIG_DIR") is { Length: > 0 } d ? Path.Combine(d, "cache")
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vaultbar")
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Caches", "vaultbar")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".cache"), "vaultbar");
}

public static class Log
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Paths.CacheDir, "vaultbar.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.CacheDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
