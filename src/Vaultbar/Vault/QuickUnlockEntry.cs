namespace Vaultbar.Vault;

public sealed record QuickUnlockEntry(string UserId, byte[] Challenge, byte[] WrappedKey);
