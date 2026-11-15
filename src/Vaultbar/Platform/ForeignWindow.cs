namespace Vaultbar.Platform;

/// <summary>The window that had focus before the quick search opened.</summary>
public sealed record ForeignWindow(nint Handle, string Title, string App, int Pid = 0)
{
    /// <summary>A web browser, whose title the page chooses: only its address says which site is open.</summary>
    public bool IsBrowser { get; init; }

    /// <summary>The address of the page a browser shows, from its accessibility tree; null when it could not be read.</summary>
    public string? Address { get; init; }

    /// <summary>On the list of browsers, as opposed to an app found to show a web page.</summary>
    public bool KnownBrowser { get; init; }
}
