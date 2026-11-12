using Avalonia.Controls;
using Avalonia.Platform;

namespace Vaultbar.UI;

public static class AppIcon
{
    public static WindowIcon Create() =>
        new(AssetLoader.Open(new Uri(OperatingSystem.IsWindows() ? "avares://vaultbar/Assets/app.ico" : "avares://vaultbar/Assets/icon-256.png")));
}
