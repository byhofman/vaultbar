using Vaultbar.Platform;

namespace Vaultbar.MacE2E;

/// <summary>Stands in for the Secure Enclave and the finger, so the full unlock flow runs with no system dialog.</summary>
internal sealed class FakeTouchIdDevice : ITouchIdDevice
{
    public int NextResult;
    public int Prompts;

    public bool Available => true;

    public int Create(out byte[] blob)
    {
        blob = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return 0;
    }

    public int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret)
    {
        Prompts++;
        secret = NextResult == 0 ? System.Security.Cryptography.HMACSHA256.HashData(blob, challenge) : [];
        return NextResult;
    }
}
