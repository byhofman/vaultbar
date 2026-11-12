namespace Vaultbar.Platform;

/// <summary>Native side of Touch ID. The test double replaces this, since real use needs a finger on the sensor.</summary>
public interface ITouchIdDevice
{
    bool Available { get; }

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int Create(out byte[] blob);

    /// <returns>Zero on success, otherwise an OSStatus or LAError code.</returns>
    int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret);
}
