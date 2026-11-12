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
    public bool TouchId { get; set; } = true;
    public string? DataPath { get; set; }

    [JsonIgnore]
    public string EffectiveHotkey => string.IsNullOrWhiteSpace(Hotkey) ? DefaultHotkey : Hotkey;

    public static string DefaultHotkey => OperatingSystem.IsMacOS() ? "Cmd+Shift+Space" : "Ctrl+Shift+Space";

    public static string FilePath => Path.Combine(Paths.ConfigDir, "config.json");

    public static Config Load()
    {
        try
        {
            Paths.EnsurePrivate(Paths.ConfigDir);
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), JsonCtx.Default.Config) ?? new Config();

            var cfg = new Config { Hotkey = DefaultHotkey };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonCtx.Default.Config));
            return cfg;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The settings file is the user's own, so its parse error (line and position) is safe and useful to log.
            Log.Write($"config: {ex.Message}; using defaults");
            return new Config();
        }
    }
}
