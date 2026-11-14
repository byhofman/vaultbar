namespace Vaultbar.Platform;

/// <summary>Native side of Touch ID. The test double replaces this, since real use needs a finger on the sensor.</summary>
public interface ITouchIdDevice
{
    bool Available { get; }

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int Create(out byte[] blob);

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret);

    /// <summary>
    /// True when this build holds a keychain access group (Developer ID with its provisioning profile): the key then
    /// lives in the data-protection keychain, where only apps signed into that group can use it (M3).
    /// </summary>
    bool KeychainAvailable => false;

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int CreateInKeychain(string tag) => -4;

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int DeriveFromKeychain(string tag, byte[] challenge, string reason, out byte[] secret)
    {
        secret = [];
        return -4;
    }

    void DeleteFromKeychain(string tag)
    {
    }
}
