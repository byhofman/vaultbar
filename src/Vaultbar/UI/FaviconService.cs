using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Vaultbar.Vault;

namespace Vaultbar.UI;

public sealed class FaviconService
{
    private const int MaxIconBytes = 256 * 1024;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = MaxIconBytes };

    // Only in memory: a folder of icons named after each website would tell anyone reading the disk where you have accounts.
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();

    public FaviconService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("vaultbar");
        RemoveDiskCacheOfOlderVersions();
    }

    public string BaseUrl { get; set; } = "https://icons.bitwarden.net";

    public Task<Bitmap?> GetAsync(string host) => _cache.GetOrAdd(host, h => Task.Run(() => LoadAsync(h)));

    private async Task<Bitmap?> LoadAsync(string host)
    {
        if (host.Length > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) || !VaultItem.IsPublicHost(host)) return null;
        if (!BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var bytes = await _http.GetByteArrayAsync($"{BaseUrl}/{host}/icon.png");
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch (Exception)
        {
            // Any failure, from the network or the image decoder, leaves the row with its letter tile.
            return null;
        }
    }

    private static void RemoveDiskCacheOfOlderVersions()
    {
        try
        {
            var dir = Path.Combine(Paths.CacheDir, "icons");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"icon cache: {Log.Shape(ex)}");
        }
    }
}
