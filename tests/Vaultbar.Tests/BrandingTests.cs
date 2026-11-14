using System.Text.RegularExpressions;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>VBR-18: the interface's brand hint is the yellow of the Vaultbar mark, in both themes.</summary>
public partial class BrandingTests
{
    [GeneratedRegex("<SolidColorBrush x:Key=\"BqBrand\" Color=\"(#[0-9A-Fa-f]{6})\" />")]
    private static partial Regex BrandToken();

    [Fact]
    public void BrandToken_IsTheLogoYellow_InLightAndDark()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "vaultbar.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        var icon = File.ReadAllText(Path.Combine(root.FullName, "branding", "app-icon", "icon.svg"));
        var tokens = BrandToken().Matches(File.ReadAllText(Path.Combine(root.FullName, "src", "Vaultbar", "App.axaml")));

        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.Contains($"fill=\"{t.Groups[1].Value}\"", icon, StringComparison.OrdinalIgnoreCase));
    }
}
