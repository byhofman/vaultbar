using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;

namespace Vaultbar.Platform;

/// <summary>The window that had focus before the quick search opened.</summary>
public sealed record ForeignWindow(nint Handle, string Title, string App, int Pid = 0);

public interface IPlatform : IDisposable
{
    /// <returns>Null when the hotkey is active, otherwise a human-readable reason.</returns>
    string? StartHotkey(Hotkey hotkey, Action onPressed);

    ForeignWindow? CaptureForeground();

    PixelPoint? AnchorPoint(ForeignWindow? target);

    void AttachWindow(Window window);

    void AfterShow();

    /// <returns>Null when auto-type can run, otherwise why not.</returns>
    string? AutoTypeUnavailableReason(ForeignWindow? target);

    Task<bool> AutoTypeAsync(ForeignWindow? target, IReadOnlyList<TypeStep> steps, Action hideWindow);

    /// <returns>A token identifying this clipboard write, used to clear it later only if unchanged.</returns>
    Task<object?> SetClipboardAsync(string text);

    Task ClearClipboardAsync(object token);

    void OpenUrl(string url);

    void WatchSystemLock(Action onLock);

    void ExcludeFromCapture(Window window)
    {
    }

    Vault.IQuickUnlockProvider? QuickUnlock => null;

    static IPlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        if (OperatingSystem.IsMacOS()) return new MacPlatform();
        if (OperatingSystem.IsLinux()) return new LinuxPlatform();
        throw new PlatformNotSupportedException();
    }
}

public readonly record struct Hotkey(bool Ctrl, bool Shift, bool Alt, bool Super, string Key)
{
    public static Hotkey Parse(string text)
    {
        bool ctrl = false, shift = false, alt = false, super = false;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt" or "option" or "opt": alt = true; break;
                case "win" or "super" or "meta" or "cmd" or "command": super = true; break;
                default:
                    if (key != null) throw new FormatException($"Hotkey '{text}' has more than one key");
                    key = raw.Length == 1 ? raw.ToUpperInvariant()
                        : raw.Equals("space", StringComparison.OrdinalIgnoreCase) ? "Space"
                        : raw.ToUpperInvariant().StartsWith('F') && int.TryParse(raw[1..], out var f) && f is >= 1 and <= 24 ? $"F{f}"
                        : throw new FormatException($"Unsupported hotkey key '{raw}' (use A-Z, 0-9, F1-F24 or Space)");
                    break;
            }
        }
        if (key is null) throw new FormatException($"Hotkey '{text}' has no key");
        if (key.Length == 1 && !char.IsAsciiLetterOrDigit(key[0])) throw new FormatException($"Unsupported hotkey key '{key}'");
        return new Hotkey(ctrl, shift, alt, super, key);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add(OperatingSystem.IsMacOS() ? "Option" : "Alt");
        if (Shift) parts.Add("Shift");
        if (Super) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Super");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}

public abstract record TypeStep;

public sealed record TypeText(string Text) : TypeStep;

public sealed record TypeKey(SpecialKey Key) : TypeStep;

public sealed record TypeDelay(int Milliseconds) : TypeStep;

public enum SpecialKey
{
    Tab,
    Enter,
}

public static partial class AutoTypeSequence
{
    public static List<TypeStep> Build(string template, string? username, string? password, Func<string?> totp)
    {
        var steps = new List<TypeStep>();
        var pos = 0;
        var skipNextTab = false;
        foreach (Match m in Token().Matches(template))
        {
            if (m.Index > pos) steps.Add(new TypeText(template[pos..m.Index]));
            pos = m.Index + m.Length;

            var name = m.Groups[1].Value.ToUpperInvariant();
            if (skipNextTab && name != "TAB") skipNextTab = false;
            switch (name)
            {
                case "USERNAME":
                    if (string.IsNullOrEmpty(username)) skipNextTab = true;
                    else steps.Add(new TypeText(username));
                    break;
                case "PASSWORD":
                    if (!string.IsNullOrEmpty(password)) steps.Add(new TypeText(password));
                    break;
                case "TOTP":
                    if (totp() is { Length: > 0 } code) steps.Add(new TypeText(code));
                    break;
                case "TAB":
                    if (skipNextTab) skipNextTab = false;
                    else steps.Add(new TypeKey(SpecialKey.Tab));
                    break;
                case "ENTER":
                    steps.Add(new TypeKey(SpecialKey.Enter));
                    break;
                case "DELAY":
                    steps.Add(new TypeDelay(int.TryParse(m.Groups[2].Value, out var ms) ? Math.Clamp(ms, 0, 10000) : 250));
                    break;
            }
        }
        if (pos < template.Length) steps.Add(new TypeText(template[pos..]));
        return steps;
    }

    [GeneratedRegex(@"\{(USERNAME|PASSWORD|TOTP|TAB|ENTER|DELAY)(?::(\d+))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex Token();
}
