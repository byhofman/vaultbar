using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vaultbar.Vault;

namespace Vaultbar.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (var other in also) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(other));
    }
}

public sealed class HeaderRow(string text)
{
    public string Text { get; } = text;
}

public sealed class ResultRow : Observable
{
    private static readonly string[] TilePalette = ["#3B6FE0", "#24966A", "#C2552E", "#8A4FD3", "#C23B6E", "#23879F", "#A97D10", "#5A6B82"];

    private bool _isSelected;
    private Bitmap? _icon;

    public ResultRow(VaultItem item)
    {
        Item = item;
        var letter = item.Name.FirstOrDefault(char.IsLetterOrDigit);
        Initial = letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
        var hash = (item.PrimaryHost ?? item.Name).Aggregate(17, (h, c) => unchecked(h * 31 + c));
        TileBrush = SolidColorBrush.Parse(TilePalette[(hash & 0x7fffffff) % TilePalette.Length]);
        TypeIcon = item.Type switch
        {
            ItemType.Card => Resource("IconCard"),
            ItemType.SecureNote => Resource("IconNote"),
            ItemType.Identity => Resource("IconPerson"),
            ItemType.SshKey => Resource("IconKey"),
            _ => null,
        };
    }

    public VaultItem Item { get; }
    public string Title => Item.Name;
    public string Subtitle => Item.Subtitle;
    public bool HasSubtitle => Item.Subtitle.Length > 0;
    public string Initial { get; }
    public IBrush TileBrush { get; }
    public Geometry? TypeIcon { get; }
    public bool HasTotp => Item.HasTotp;
    public bool IsFavorite => Item.Favorite;
    public bool ShowTypeIcon => TypeIcon != null && Icon is null;
    public bool ShowTile => TypeIcon is null && Icon is null;
    public bool HasIcon => Icon != null;

    public Bitmap? Icon
    {
        get => _icon;
        set => Set(ref _icon, value, also: [nameof(HasIcon), nameof(ShowTile), nameof(ShowTypeIcon)]);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    internal static Geometry? Resource(string key) =>
        Application.Current!.TryFindResource(key, out var value) ? value as Geometry : null;
}

public sealed class DetailRow : Observable
{
    private const string Mask = "••••••••••••";
    private readonly ItemField _field;
    private string _display = "";
    private string _countdown = "";
    private bool _isSelected;
    private bool _revealed;

    public DetailRow(ItemField field)
    {
        _field = field;
        Refresh();
    }

    public ItemField Field => _field;
    public string Label => _field.Label;
    public bool IsSecret => _field.Kind == FieldKind.Secret;
    public bool IsTotp => _field.Kind == FieldKind.Totp;
    public bool IsMultiline => _field.Kind == FieldKind.Multiline;
    public FontFamily ValueFont => IsSecret && _revealed || IsTotp ? new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace") : FontFamily.Default;

    public string Display
    {
        get => _display;
        private set => Set(ref _display, value);
    }

    public string Countdown
    {
        get => _countdown;
        private set => Set(ref _countdown, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool Revealed
    {
        get => _revealed;
        set
        {
            Set(ref _revealed, value, also: [nameof(ValueFont)]);
            Refresh();
        }
    }

    public void Refresh()
    {
        try
        {
            switch (_field.Kind)
            {
                case FieldKind.Secret:
                    Display = _revealed ? _field.Value() ?? "" : Mask;
                    break;
                case FieldKind.Totp:
                    var code = _field.TotpCode?.Invoke();
                    Display = code is { } c ? Totp.Format(c.Value) : "";
                    Countdown = code is { } r ? $"{r.SecondsRemaining}s" : "";
                    break;
                default:
                    Display = _field.Value() ?? "";
                    break;
            }
        }
        catch (Exception ex)
        {
            Display = "(cannot decrypt)";
            Log.Write($"field {_field.Label}: {ex.Message}");
        }
    }
}

public sealed class FaviconService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();
    private readonly string _dir = Path.Combine(Paths.CacheDir, "icons");

    public FaviconService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("vaultbar/0.1");

    public string BaseUrl { get; set; } = "https://icons.bitwarden.net";

    public Task<Bitmap?> GetAsync(string host) => _cache.GetOrAdd(host, h => Task.Run(() => LoadAsync(h)));

    private async Task<Bitmap?> LoadAsync(string host)
    {
        if (host.Length > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) || !VaultItem.IsPublicHost(host)) return null;
        if (!BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        var file = Path.Combine(_dir, host + ".png");
        var miss = file + ".miss";
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(30))
                return new Bitmap(file);
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromDays(7))
                return null;

            var bytes = await _http.GetByteArrayAsync($"{BaseUrl}/{host}/icon.png");
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            Directory.CreateDirectory(_dir);
            await File.WriteAllBytesAsync(file, bytes);
            return bitmap;
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(_dir);
                await File.WriteAllTextAsync(miss, "");
            }
            catch (IOException)
            {
            }
            return null;
        }
    }
}

public static class AppIcon
{
    public static WindowIcon Create()
    {
        const int size = 64;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
        {
            ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#175DDC")), null, ResultRow.Resource("IconShield")!);
            using (ctx.PushTransform(Matrix.CreateScale(0.56, 0.56) * Matrix.CreateTranslation(5.5, 4.6)))
                ctx.DrawGeometry(Brushes.White, null, ResultRow.Resource("IconSearch")!);
        }
        var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
