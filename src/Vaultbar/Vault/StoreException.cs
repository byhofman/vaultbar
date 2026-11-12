namespace Vaultbar.Vault;

public sealed class StoreException(VaultError error) : Exception(error.ToString())
{
    public VaultError Error { get; } = error;
}
