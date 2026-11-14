using System.Globalization;
using System.Reflection;

namespace Vaultbar.Vault;

/// <summary>
/// The registrable domain of a host by the Public Suffix List, private section included, which is how Bitwarden's
/// clients decide that two addresses share a "base domain" (tldts with allowPrivateDomains). Without the private
/// section, <c>contoso.github.io</c> and <c>fabrikam.github.io</c> would count as the same site.
/// </summary>
internal static class PublicSuffix
{
    private static readonly Lazy<Rules> List = new(Load);

    /// <returns>The registrable domain in ASCII (punycode) form, or null for a public suffix itself or an invalid host.</returns>
    public static string? RegistrableDomain(string host)
    {
        if (Ascii(host) is not { } ascii) return null;
        var labels = ascii.Split('.');
        if (labels.Any(l => l.Length == 0)) return null;

        var rules = List.Value;
        var suffixLength = 1;
        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = string.Join('.', labels[i..]);
            if (rules.Exceptions.Contains(candidate))
            {
                suffixLength = labels.Length - i - 1;
                break;
            }
            var wildcard = i + 1 < labels.Length && rules.Wildcards.Contains(string.Join('.', labels[(i + 1)..]));
            if (rules.Exact.Contains(candidate) || wildcard)
            {
                suffixLength = labels.Length - i;
                break;
            }
        }

        return labels.Length > suffixLength ? string.Join('.', labels[^(suffixLength + 1)..]) : null;
    }

    internal static string? Ascii(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (host.Length == 0 || host.StartsWith('.') || host.EndsWith('.')) return null;
        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record Rules(HashSet<string> Exact, HashSet<string> Wildcards, HashSet<string> Exceptions);

    private static Rules Load()
    {
        var rules = new Rules(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vaultbar.public_suffix_list.dat")
                           ?? throw new InvalidOperationException("The public suffix list is missing from the build.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var rule = line.Trim();
            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal)) continue;
            if (rule.StartsWith('!')) rules.Exceptions.Add(Ascii(rule[1..])!);
            else if (rule.StartsWith("*.", StringComparison.Ordinal)) rules.Wildcards.Add(Ascii(rule[2..])!);
            else rules.Exact.Add(Ascii(rule)!);
        }
        return rules;
    }
}
