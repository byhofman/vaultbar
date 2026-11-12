using System.Text;
using Vaultbar.Platform;
using Xunit;

namespace Vaultbar.Tests;

public class AutoTyperTests
{
    private sealed class RecordingKeyboard : IKeyboard
    {
        public readonly StringBuilder Typed = new();
        public Action? AfterKey;

        public void Type(Rune character)
        {
            Typed.Append(character.ToString());
            AfterKey?.Invoke();
        }

        public void Press(SpecialKey key)
        {
            Typed.Append(key == SpecialKey.Tab ? "<TAB>" : "<ENTER>");
            AfterKey?.Invoke();
        }
    }

    private static List<TypeStep> Login() => AutoTypeSequence.Build("{USERNAME}{TAB}{PASSWORD}{ENTER}", "alex", "s3cret", () => null);

    [Fact]
    public void TypesEverythingWhileTheTargetKeepsFocus()
    {
        var keyboard = new RecordingKeyboard();
        Assert.Equal(AutoTypeResult.Typed, AutoTyper.Run(Login(), keyboard, () => true, _ => { }));
        Assert.Equal("alex<TAB>s3cret<ENTER>", keyboard.Typed.ToString());
    }

    [Fact]
    public void StopsAtTheFirstKeystrokeAfterFocusMoves()
    {
        var keyboard = new RecordingKeyboard();
        var focused = true;
        keyboard.AfterKey = () => focused = keyboard.Typed.ToString() != "alex<TAB>";

        Assert.Equal(AutoTypeResult.Interrupted, AutoTyper.Run(Login(), keyboard, () => focused, _ => { }));
        Assert.Equal("alex<TAB>", keyboard.Typed.ToString());
    }

    [Fact]
    public void TypesNothingWithoutFocus()
    {
        var keyboard = new RecordingKeyboard();
        Assert.Equal(AutoTypeResult.Interrupted, AutoTyper.Run(Login(), keyboard, () => false, _ => { }));
        Assert.Equal("", keyboard.Typed.ToString());
    }

    [Fact]
    public void LineBreaksAndTabsInsideTextBecomeKeys()
    {
        var keyboard = new RecordingKeyboard();
        AutoTyper.Run([new TypeText("a\r\nb\tc€😀")], keyboard, () => true, _ => { });
        Assert.Equal("a<ENTER>b<TAB>c€😀", keyboard.Typed.ToString());
    }

    [Fact]
    public void DelaysAreWaitedOut()
    {
        var waits = new List<int>();
        AutoTyper.Run([new TypeDelay(300)], new RecordingKeyboard(), () => true, waits.Add);
        Assert.Contains(300, waits);
    }
}
