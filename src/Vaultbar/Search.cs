using System.Text.Json;
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

    public static List<VaultItem> Suggest(IReadOnlyList<VaultItem> items, ForeignWindow? context, UsageStore usage, int limit = 4)
    {
        if (context is null) return [];
        var title = BrowserSuffix().Replace(context.Title, "");
        var app = Browsers.Contains(context.App.ToLowerInvariant()) ? "" : context.App;
        var text = $"{title} {app}".ToLowerInvariant();
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
            if (score > 0) results.Add((item, score + usage.Boost(item.Id) + (item.Favorite ? 1 : 0)));
        }

        return results.OrderByDescending(r => r.Score).Take(limit).Select(r => r.Item).ToList();
    }

    internal static string MainLabel(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 2) return host;
        var i = labels.Length - 2;
        if (labels.Length >= 3 && labels[^1].Length <= 3 && labels[^2].Length <= 3 && labels[^2] is "co" or "com" or "org" or "net" or "gov" or "ac" or "edu")
            i = labels.Length - 3;
        return labels[i];
    }

    private static readonly HashSet<string> Browsers = new(StringComparer.Ordinal)
    {
        "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "chromium", "safari", "arc", "zen", "librewolf",
        "google-chrome", "microsoft-edge", "navigator", "firefox-esr", "google chrome", "microsoft edge",
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"\s[-—–]\s(?:Google Chrome|Mozilla Firefox|Firefox|Brave|Vivaldi|Opera|Chromium|Safari|Arc|Zen Browser|LibreWolf|(?:[^-—–]{1,40}\s[-—–]\s)?Microsoft\W{0,2}Edge)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserSuffix();
}

public sealed class UsageStore
{
    private readonly string _path = Path.Combine(Paths.ConfigDir, "usage.json");
    private readonly Dictionary<string, UsageEntry> _entries;
    private Timer? _saveTimer;

    public UsageStore()
    {
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), JsonCtx.Default.DictionaryStringUsageEntry) ?? new()
                : new();
        }
        catch
        {
            _entries = new();
        }
    }

    public void Record(string id)
    {
        lock (_entries)
        {
            var e = _entries.TryGetValue(id, out var existing) ? existing : _entries[id] = new UsageEntry();
            e.C++;
            e.T = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(2000, Timeout.Infinite);
    }

    public double Boost(string id)
    {
        UsageEntry? e;
        lock (_entries) _entries.TryGetValue(id, out e);
        if (e is null) return 0;
        var ageDays = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - e.T) / 86400.0;
        var recency = ageDays < 1 ? 6 : ageDays < 7 ? 3 : ageDays < 30 ? 1 : 0;
        return Math.Log(1 + e.C) * 3 + recency;
    }

    public IEnumerable<VaultItem> Recent(IReadOnlyList<VaultItem> items)
    {
        Dictionary<string, long> times;
        lock (_entries) times = _entries.ToDictionary(kv => kv.Key, kv => kv.Value.T);
        return items.Where(i => times.ContainsKey(i.Id)).OrderByDescending(i => times[i.Id]);
    }

    public void Flush() => Save();

    private void Save()
    {
        try
        {
            string json;
            lock (_entries) json = JsonSerializer.Serialize(_entries, JsonCtx.Default.DictionaryStringUsageEntry);
            Directory.CreateDirectory(Paths.ConfigDir);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Write($"usage save: {ex.Message}");
        }
    }
}
