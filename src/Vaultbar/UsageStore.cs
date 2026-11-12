using System.Text.Json;
using Vaultbar.Vault;

namespace Vaultbar;

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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Usage only ranks results; a damaged file starts the ranking over.
            Log.Write($"usage load: {Log.Shape(ex)}");
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
            Paths.EnsurePrivate(Paths.ConfigDir);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"usage save: {Log.Shape(ex)}");
        }
    }
}
