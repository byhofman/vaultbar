namespace Vaultbar.Platform;

public static class AutoTyper
{
    private const int PauseBetweenSteps = 40;

    /// <summary>
    /// Types the steps, checking before every keystroke that the target still has focus: a password must never land in
    /// a chat window or notification that popped up halfway.
    /// </summary>
    public static AutoTypeResult Run(IReadOnlyList<TypeStep> steps, IKeyboard keyboard, Func<bool> targetHasFocus, Action<int>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        foreach (var step in steps)
        {
            switch (step)
            {
                case TypeText text:
                    foreach (var character in text.Text.EnumerateRunes())
                    {
                        if (character.Value == '\r') continue;
                        if (!targetHasFocus()) return AutoTypeResult.Interrupted;
                        if (character.Value == '\n') keyboard.Press(SpecialKey.Enter);
                        else if (character.Value == '\t') keyboard.Press(SpecialKey.Tab);
                        else keyboard.Type(character);
                    }
                    break;
                case TypeKey key:
                    if (!targetHasFocus()) return AutoTypeResult.Interrupted;
                    keyboard.Press(key.Key);
                    break;
                case TypeDelay delay:
                    sleep(delay.Milliseconds);
                    break;
            }
            sleep(PauseBetweenSteps);
        }
        return AutoTypeResult.Typed;
    }
}
