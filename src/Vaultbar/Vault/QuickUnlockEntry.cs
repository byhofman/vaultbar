namespace Vaultbar.Vault;

/// <summary>
/// An enrollment: the challenge the device key signs and a verifier of that signature and the user key. Only with
/// <c>quickUnlockAcrossRestarts</c> does it also carry the wrapped user key, and the master-password unlock it dates from.
/// </summary>
public sealed record QuickUnlockEntry(string UserId, byte[] Challenge, byte[] Verifier, byte[]? WrappedKey = null, DateTimeOffset? PasswordAt = null);
