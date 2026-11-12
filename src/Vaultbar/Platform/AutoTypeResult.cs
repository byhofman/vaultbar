namespace Vaultbar.Platform;

public enum AutoTypeResult
{
    Typed,

    /// <summary>The previous window could not be brought back; nothing was typed.</summary>
    TargetNotFocused,

    /// <summary>Another window took focus while typing; nothing more was typed after that.</summary>
    Interrupted,
}
