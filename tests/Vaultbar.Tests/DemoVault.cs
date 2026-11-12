using Xunit;

namespace Vaultbar.Tests;

public class DemoVault
{
    [Fact]
    public void WriteDemoVaultWhenRequested()
    {
        var target = Environment.GetEnvironmentVariable("VAULTBAR_DEMO_OUT");
        if (string.IsNullOrEmpty(target)) return;
        VaultFixture.Demo().Write(target, "demo-password");
    }
}
