using Vaultbar.Vault;

namespace Vaultbar.UI;

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
    /// <summary>Revealed secrets and codes read character by character, so they get a monospaced font.</summary>
    public bool IsMonospaced => IsSecret && _revealed || IsTotp;

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
            Set(ref _revealed, value, also: [nameof(IsMonospaced)]);
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
            Log.Write($"field: {Log.Shape(ex)}");
        }
    }
}
