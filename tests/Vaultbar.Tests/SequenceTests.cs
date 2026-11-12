using Vaultbar.Platform;
using Xunit;

namespace Vaultbar.Tests;

public class SequenceTests
{
    [Fact]
    public void DefaultSequence_TypesUserTabPassword()
    {
        var steps = AutoTypeSequence.Build("{USERNAME}{TAB}{PASSWORD}", "alex", "pw", () => null);
        Assert.Equal<TypeStep>([new TypeText("alex"), new TypeKey(SpecialKey.Tab), new TypeText("pw")], steps);
    }

    [Fact]
    public void MissingUsername_DropsFollowingTab()
    {
        var steps = AutoTypeSequence.Build("{USERNAME}{TAB}{PASSWORD}{ENTER}", "", "pw", () => null);
        Assert.Equal<TypeStep>([new TypeText("pw"), new TypeKey(SpecialKey.Enter)], steps);
    }

    [Fact]
    public void Tokens_AreCaseInsensitiveAndSupportDelayAndTotp()
    {
        var steps = AutoTypeSequence.Build("{password}{delay:500}{TOTP}", "u", "pw", () => "123456");
        Assert.Equal<TypeStep>([new TypeText("pw"), new TypeDelay(500), new TypeText("123456")], steps);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Space", true, true, false, false, "Space")]
    [InlineData("cmd+shift+space", false, true, false, true, "Space")]
    [InlineData("Ctrl+Alt+k", true, false, true, false, "K")]
    [InlineData("Super+F12", false, false, false, true, "F12")]
    public void Hotkey_Parses(string text, bool ctrl, bool shift, bool alt, bool super, string key)
    {
        Assert.Equal(new Hotkey(ctrl, shift, alt, super, key), Hotkey.Parse(text));
    }

    [Fact]
    public void Hotkey_RejectsGarbage()
    {
        Assert.Throws<FormatException>(() => Hotkey.Parse("Ctrl+Shift"));
        Assert.Throws<FormatException>(() => Hotkey.Parse("Ctrl+PrintScreenPlease"));
    }

    [Fact]
    public void MainLabel_HandlesSecondLevelSuffixes()
    {
        Assert.Equal("bankofexample", Matcher.MainLabel("bankofexample.co.uk"));
        Assert.Equal("azure", Matcher.MainLabel("portal.azure.com"));
        Assert.Equal("github", Matcher.MainLabel("github.com"));
    }
}
