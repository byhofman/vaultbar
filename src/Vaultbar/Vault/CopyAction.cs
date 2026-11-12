namespace Vaultbar.Vault;

public sealed record CopyAction(string Label, Func<string?> Value, bool Sensitive);
