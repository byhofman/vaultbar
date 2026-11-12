using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vaultbar.Vault;

namespace Vaultbar.UI;

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
