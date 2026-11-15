using System.Text.RegularExpressions;
using Vaultbar.Platform;
using Vaultbar.Vault;

namespace Vaultbar;

public static partial class Matcher
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "and", "the", "for", "com", "net", "org", "www", "http", "https", "new", "tab", "page", "pages", "more",
        "window", "private", "inprivate", "browsing", "incognito", "home", "welcome", "login", "log", "sign",
        "personal", "work", "profile", "default", "untitled",
    };

    public static List<VaultItem> Search(IReadOnlyList<VaultItem> items, string query, UsageStore usage, int limit = 60)
    {
        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return [];

        var scored = new List<(VaultItem Item, double Score)>();
        foreach (var item in items)
        {
            double total = 0;
            var matchedAll = true;
            foreach (var t in tokens)
            {
                var s = Math.Max(Field(item.NameLower, t), Fuzzy(item.NameLower, t));
                s = Math.Max(s, Field(item.UserLower, t) * 0.7);
                foreach (var host in item.Hosts) s = Math.Max(s, Field(host, t) * 0.8);
                s = Math.Max(s, Field(item.FolderLower, t) * 0.4);
                if (s <= 0)
                {
                    matchedAll = false;
                    break;
                }
                total += s;
            }
            if (!matchedAll) continue;
            if (item.Favorite) total += 4;
            total += usage.Boost(item.Id);
            scored.Add((item, total));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();
    }

    internal static double Field(string field, string token)
    {
        if (field.Length == 0) return 0;
        if (field == token) return 100;
        if (field.StartsWith(token, StringComparison.Ordinal)) return 80;
        var idx = field.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return 0;
        while (idx > 0)
        {
            if (!char.IsLetterOrDigit(field[idx - 1])) return 65;
            idx = field.IndexOf(token, idx + 1, StringComparison.Ordinal);
        }
        return 45;
    }

    internal static double Fuzzy(string field, string token)
    {
        if (token.Length < 2 || field.Length == 0) return 0;
        int fi = 0, first = -1, gaps = 0, last = -1;
        foreach (var c in token)
        {
            while (fi < field.Length && field[fi] != c) fi++;
            if (fi == field.Length) return 0;
            if (first < 0) first = fi;
            if (last >= 0 && fi != last + 1) gaps++;
            last = fi++;
        }
        return Math.Max(5, 30 - gaps * 4 - first * 0.5);
    }

    /// <summary>
    /// Items for the window the bar opened over. In a browser only the page address counts, matched with Bitwarden's URI
    /// rules: the title is the page's own text, so a look-alike page titled "Contoso: sign in" would otherwise get the real
    /// Contoso login offered first. Other apps are matched on their title and name.
    /// </summary>
    public static List<VaultItem> Suggest(IReadOnlyList<VaultItem> items, ForeignWindow? context, UriMatchSettings settings, UsageStore usage, int limit = 4)
    {
        if (context is null) return [];
        if (context.IsBrowser)
        {
            if (context.Address is not { } address) return [];
            return Rank(items.Where(i => i.MatchesPage(address, settings)).Select(i => (i, 10.0)), usage, limit);
        }

        var text = $"{context.Title} {context.App}".ToLowerInvariant();
        var tokens = WordSplit().Split(text).Where(t => t.Length >= 3 && !StopWords.Contains(t)).ToHashSet();
        if (tokens.Count == 0) return [];

        var results = new List<(VaultItem Item, double Score)>();
        foreach (var item in items)
        {
            double score = 0;
            foreach (var host in item.Hosts)
            {
                var label = MainLabel(host);
                if (label.Length >= 3 && tokens.Contains(label)) score = Math.Max(score, 10);
            }
            if (score == 0 && item.NameLower.Length >= 3)
            {
                var nameTokens = WordSplit().Split(item.NameLower).Where(t => t.Length >= 3 && !StopWords.Contains(t)).ToList();
                if (nameTokens.Count > 0 && nameTokens.All(tokens.Contains)) score = 6;
            }
            if (score > 0) results.Add((item, score));
        }

        return Rank(results, usage, limit);
    }

    /// <summary>
    /// Whether filling this item needs a second Enter: the window is a browser and its address is unknown or is not one of
    /// the item's websites by Bitwarden's rules.
    /// </summary>
    public static bool FillNeedsConfirmation(VaultItem item, ForeignWindow? target, UriMatchSettings settings) =>
        target is { IsBrowser: true } && (target.Address is not { } address || !item.MatchesPage(address, settings));

    private static List<VaultItem> Rank(IEnumerable<(VaultItem Item, double Score)> scored, UsageStore usage, int limit) =>
        scored.Select(r => (r.Item, Score: r.Score + usage.Boost(r.Item.Id) + (r.Item.Favorite ? 1 : 0)))
            .OrderByDescending(r => r.Score).Take(limit).Select(r => r.Item).ToList();

    internal static string MainLabel(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 2) return host;
        var i = labels.Length - 2;
        if (labels.Length >= 3 && labels[^1].Length <= 3 && labels[^2].Length <= 3 && labels[^2] is "co" or "com" or "org" or "net" or "gov" or "ac" or "edu")
            i = labels.Length - 3;
        return labels[i];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSplit();
}
