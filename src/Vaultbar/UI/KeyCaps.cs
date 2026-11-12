using Avalonia.Controls;
using Avalonia.Layout;

namespace Vaultbar.UI;

/// <summary>The modifier names of this platform, and the key caps the footer hints and the shortcut list draw.</summary>
internal static class KeyCaps
{
    public static string Primary { get; } = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
    public static string Option { get; } = OperatingSystem.IsMacOS() ? "⌥" : "Alt";
    public static string Shift { get; } = OperatingSystem.IsMacOS() ? "⇧" : "Shift";

    public static StackPanel Draw(IEnumerable<string> keys)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in keys)
            group.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = key } });
        return group;
    }
}
