using System.Text;

namespace Vaultbar.Platform;

/// <summary>Sends single keystrokes to whatever has keyboard focus.</summary>
public interface IKeyboard
{
    void Type(Rune character);

    void Press(SpecialKey key);
}
