namespace Vaultbar.Platform;

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
