namespace Vaultbar.Vault;

/// <summary>
/// An http or https address: the only kind Vaultbar hands to the system to open, so a stored URI or a web vault setting
/// can never start a program, open a file or follow an app link.
/// </summary>
public sealed class WebAddress
{
    private WebAddress(Uri uri) => Value = uri.AbsoluteUri;

    public string Value { get; }

    /// <returns>The address, or null for anything that is not a web address (file paths, app links, programs).</returns>
    public static WebAddress? From(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        uri = uri.Trim();
        var candidate = uri.Contains("://") ? uri : "https://" + uri;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host))
            return null;
        return new WebAddress(u);
    }

    /// <summary>The item's page in the Bitwarden web vault.</summary>
    public static WebAddress? ForWebVaultItem(string webVault, string itemId) =>
        From(webVault) is { } vault ? From($"{vault.Value.TrimEnd('/')}/#/vault?itemId={Uri.EscapeDataString(itemId)}&action=view") : null;

    public override string ToString() => Value;
}
