using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class MacPlatformTests
{
    [Fact]
    public void EveryDocumentedHotkeyKeyHasAKeyCode()
    {
        var keys = Enumerable.Range('A', 26).Concat(Enumerable.Range('0', 10)).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range(1, 20).Select(f => $"F{f}"))
            .Append("Space");
        Assert.All(keys, k => Assert.True(MacPlatform.KeyCodes.ContainsKey(k), k));
        Assert.Equal(MacPlatform.KeyCodes.Count, MacPlatform.KeyCodes.Values.Distinct().Count());
    }

    [Fact]
    public void DefaultHotkeyParses()
    {
        var hotkey = Hotkey.Parse("Cmd+Shift+Space");
        Assert.True(hotkey is { Super: true, Shift: true, Ctrl: false, Alt: false, Key: "Space" });
    }

    [Fact]
    public void MissingConfiguredVaultNeverFallsBackToTheStandardLocations()
    {
        Assert.Null(DesktopStore.Locate(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "data.json")));
    }

    [Fact]
    public void LooksInBothMacBitwardenLocations()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var paths = DesktopStore.CandidatePaths().ToList();
        Assert.Contains(paths, p => p.EndsWith("Library/Application Support/Bitwarden/data.json") && !p.Contains("Containers"));
        Assert.Contains(paths, p => p.EndsWith("Library/Containers/com.bitwarden.desktop/Data/Library/Application Support/Bitwarden/data.json"));
    }
}
