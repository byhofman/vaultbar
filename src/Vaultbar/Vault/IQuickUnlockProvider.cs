namespace Vaultbar.Vault;

public interface IQuickUnlockProvider
{
    string Name { get; }

    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Signs the challenge with a device-bound key that asks for the user's face, fingerprint or PIN.
    /// The same key always gives the same signature for the same challenge.
    /// </summary>
    Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(byte[] challenge, bool createKey);

    Task DeleteKeyAsync();
}
