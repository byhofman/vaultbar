namespace Vaultbar.Platform;

/// <summary>
/// Known web browsers, by Windows process name, Linux WM_CLASS or macOS bundle identifier. A browser's window title is
/// written by the page, so a browser never gets suggestions or a silent fill from its title (VBR-22).
/// </summary>
public static class Browsers
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "chromium", "arc", "zen", "librewolf", "waterfox",
        "floorp", "thorium", "whale", "browser", "iexplore", "palemoon", "seamonkey", "duckduckgo", "mullvadbrowser",
        "google-chrome", "google-chrome-stable", "chromium-browser", "microsoft-edge", "microsoft-edge-stable",
        "brave-browser", "vivaldi-stable", "navigator", "firefox-esr", "tor browser", "safari", "orion",
    };

    private static readonly string[] BundlePrefixes =
    [
        "com.apple.Safari", "com.google.Chrome", "com.microsoft.edgemac", "org.mozilla.firefox", "org.mozilla.nightly",
        "com.brave.Browser", "company.thebrowser.", "com.vivaldi.Vivaldi", "com.operasoftware.Opera", "org.chromium.Chromium",
        "app.zen-browser.", "io.gitlab.librewolf", "net.waterfox.", "com.kagi.kagimacOS", "com.duckduckgo.macos.browser",
        "org.torproject.torbrowser", "net.mullvad.mullvadbrowser", "one.ablaze.floorp", "com.naver.Whale",
    ];

    public static bool IsWebAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public static bool IsBrowserApp(string processOrClass) => Names.Contains(processOrClass.Trim());

    public static bool IsBrowserBundle(string? bundleId) =>
        bundleId is { Length: > 0 } && BundlePrefixes.Any(p => bundleId.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
