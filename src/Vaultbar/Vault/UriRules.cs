using System.Net;
using System.Text.RegularExpressions;

namespace Vaultbar.Vault;

/// <summary>Bitwarden's URI match detection options, with the numbers its vault data uses.</summary>
public enum UriMatch
{
    Domain = 0,
    Host = 1,
    StartsWith = 2,
    Exact = 3,
    RegularExpression = 4,
    Never = 5,
}

/// <summary>One of a login's URIs with its own match detection, or null for the account's default.</summary>
public sealed record LoginUri(string Uri, UriMatch? Match);

/// <summary>The account's default match detection and its equivalent domains, as the desktop app stores them.</summary>
public sealed record UriMatchSettings(UriMatch DefaultMatch, IReadOnlyList<IReadOnlyList<string>> EquivalentDomains)
{
    public static readonly UriMatchSettings Default = new(UriMatch.Domain, []);
}

/// <summary>
/// Whether a page address matches a login URI, by the rules Bitwarden's clients use for autofill
/// (<c>LoginUriView.matchesUri</c> in bitwarden/clients): base domain by the Public Suffix List plus equivalent domains,
/// host with port, starts with, exact, a case-insensitive regular expression, or never.
/// </summary>
public static class UriRules
{
    // Bitwarden's DomainMatchBlacklist: a google.com login is never offered to Apps Script pages, which anyone can publish.
    private static readonly Dictionary<string, string[]> DomainMatchExclusions = new(StringComparer.Ordinal)
    {
        ["google.com"] = ["script.google.com"],
    };

    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(200);

    public static bool Matches(LoginUri login, string pageUrl, UriMatchSettings settings)
    {
        if (string.IsNullOrWhiteSpace(login.Uri) || string.IsNullOrWhiteSpace(pageUrl)) return false;
        switch (login.Match ?? settings.DefaultMatch)
        {
            case UriMatch.Domain:
                return MatchesDomain(login.Uri, pageUrl, settings);
            case UriMatch.Host:
                return HostOf(pageUrl) is { } pageHost && pageHost == HostOf(login.Uri);
            case UriMatch.Exact:
                return pageUrl == login.Uri;
            case UriMatch.StartsWith:
                return pageUrl.StartsWith(login.Uri, StringComparison.Ordinal);
            case UriMatch.RegularExpression:
                try
                {
                    return Regex.IsMatch(pageUrl, login.Uri, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    private static bool MatchesDomain(string loginUri, string pageUrl, UriMatchSettings settings)
    {
        if (DomainOf(loginUri) is not { } loginDomain || DomainOf(pageUrl) is not { } pageDomain) return false;
        var accepted = settings.EquivalentDomains
            .Where(group => group.Any(d => PublicSuffix.Ascii(d) == pageDomain))
            .SelectMany(group => group)
            .Select(PublicSuffix.Ascii)
            .Append(pageDomain);
        if (!accepted.Contains(loginDomain)) return false;
        return !DomainMatchExclusions.TryGetValue(loginDomain, out var excluded) || !excluded.Contains(HostOf(pageUrl));
    }

    /// <summary>The base domain of a URI or bare host (Bitwarden's <c>Utils.getDomain</c>): IP addresses and localhost as they are.</summary>
    public static string? DomainOf(string uri)
    {
        if (Parse(uri) is not { } u) return null;
        var host = u.IdnHost;
        if (host == "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        return PublicSuffix.RegistrableDomain(host);
    }

    /// <summary>Host and port as a browser's <c>URL.host</c> gives them, the default port left out.</summary>
    public static string? HostOf(string uri) =>
        Parse(uri) is { } u ? u.IsDefaultPort ? u.IdnHost : $"{u.IdnHost}:{u.Port}" : null;

    private static Uri? Parse(string uri)
    {
        uri = uri.Trim();
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;
        // Bitwarden reads an address without a scheme as http.
        if (!uri.Contains("://")) uri = "http://" + uri;
        return Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IdnHost.Length > 0 ? u : null;
    }
}
