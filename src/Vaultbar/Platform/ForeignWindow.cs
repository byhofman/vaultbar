namespace Vaultbar.Platform;

/// <summary>The window that had focus before the quick search opened.</summary>
public sealed record ForeignWindow(nint Handle, string Title, string App, int Pid = 0);
