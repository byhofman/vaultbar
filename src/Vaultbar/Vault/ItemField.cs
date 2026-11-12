namespace Vaultbar.Vault;

public sealed record ItemField(string Label, FieldKind Kind, Func<string?> Value, Func<Totp.Code?>? TotpCode = null);
