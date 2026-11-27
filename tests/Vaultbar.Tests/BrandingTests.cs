using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>#58 and the 2026-11 polish: the theme tokens carry the mark's colours and keep WCAG AA in both themes.</summary>
public partial class BrandingTests
{
    [GeneratedRegex("<ResourceDictionary x:Key=\"(Light|Dark)\">(.*?)</ResourceDictionary>", RegexOptions.Singleline)]
    private static partial Regex ThemeDictionary();

    [GeneratedRegex("<SolidColorBrush x:Key=\"(Bq\\w+)\" Color=\"(#[0-9A-Fa-f]{6})\" />")]
    private static partial Regex BrushToken();

    [GeneratedRegex("#[0-9A-Fa-f]{6,8}\\b")]
    private static partial Regex ColourLiteral();

    private static readonly string Root = FindRoot();

    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    [Fact]
    public void Accent_IsTheMarksBlue_AndBrand_IsTheMarksYellow()
    {
        var icon = File.ReadAllText(Path.Combine(Root, "branding", "app-icon", "icon.svg"));
        Assert.Contains($"stop-color=\"{Tokens("Light")["BqAccent"]}\"", icon, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"fill=\"{Tokens("Dark")["BqBrand"]}\"", icon, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_MeetsAA_OnEverySurfaceItSitsOn(string theme)
    {
        var t = Tokens(theme);
        foreach (var fg in new[] { "BqText", "BqMuted" })
            foreach (var bg in new[] { "BqSurface", "BqFooter", "BqSelected", "BqHover" })
                AssertContrast(t, fg, bg, 4.5);
        foreach (var bg in new[] { "BqSurface", "BqFooter" }) AssertContrast(t, "BqAccent", bg, 4.5);
        AssertContrast(t, "BqError", "BqSurface", 4.5);
        AssertContrast(t, "BqText", "BqKbd", 4.5);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void BrandIcons_MeetTheNonTextMinimum(string theme)
    {
        var t = Tokens(theme);
        foreach (var bg in new[] { "BqSurface", "BqSelected", "BqHover" })
        {
            AssertContrast(t, "BqBrand", bg, 3);
            AssertContrast(t, "BqAccent", bg, 3);
        }
    }

    [Fact]
    public void Window_UsesTokens_NotColourLiterals()
    {
        var window = File.ReadAllText(Path.Combine(Root, "src", "Vaultbar", "UI", "QuickWindow.axaml"));
        Assert.Empty(ColourLiteral().Matches(window));
    }

    private static Dictionary<string, string> Tokens(string theme)
    {
        var app = File.ReadAllText(Path.Combine(Root, "src", "Vaultbar", "App.axaml"));
        var body = ThemeDictionary().Matches(app).Single(m => m.Groups[1].Value == theme).Groups[2].Value;
        return BrushToken().Matches(body).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static void AssertContrast(Dictionary<string, string> tokens, string fg, string bg, double minimum)
    {
        var ratio = Contrast(tokens[fg], tokens[bg]);
        Assert.True(ratio >= minimum, $"{fg} {tokens[fg]} on {bg} {tokens[bg]} is {ratio:F2}:1, needs {minimum}:1");
    }

    private static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var c = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static string FindRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "vaultbar.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("vaultbar.slnx not found");
    }
}
