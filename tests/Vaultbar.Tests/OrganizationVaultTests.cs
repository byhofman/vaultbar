using System.Security.Cryptography;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public sealed class OrganizationVaultTests : IDisposable
{
    private const string Password = "correct horse";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private async Task<VaultService> Unlock(VaultFixture fixture)
    {
        fixture.Write(DataPath, Password);
        var vault = new VaultService(new Config { DataPath = DataPath });
        vault.Probe();
        Assert.Null(await vault.UnlockAsync(Password));
        return vault;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrgItems_DecryptWithTheOrgKey_WhereverThePrivateKeyIsStored(bool legacySlot)
    {
        var fixture = VaultFixture.Demo();
        fixture.LegacyPrivateKeySlot = legacySlot;
        var org = fixture.AddOrganization();
        fixture.AddOrgLogin(org, "Team wiki", "team@contoso.com", "wiki-pass");
        fixture.AddOrgLogin(org, "Team VPN", "vpn@contoso.com", "vpn-pass", itemKey: true);

        using var vault = await Unlock(fixture);

        Assert.Equal(0, vault.FailedItems);
        Assert.Equal("wiki-pass", vault.Items.Single(i => i.Name == "Team wiki").Password);
        Assert.Equal("vpn-pass", vault.Items.Single(i => i.Name == "Team VPN").Password);
        Assert.Equal("gh-Secr3t!pass", vault.Items.Single(i => i.Name == "GitHub").Password);
    }

    [Fact]
    public async Task SeveralOrganizations_KeepTheirKeysApart()
    {
        var fixture = new VaultFixture();
        fixture.AddOrgLogin(fixture.AddOrganization(), "First", "a", "first-pass");
        fixture.AddOrgLogin(fixture.AddOrganizationBareKey(), "Second", "b", "second-pass");

        using var vault = await Unlock(fixture);

        Assert.Equal("first-pass", vault.Items.Single(i => i.Name == "First").Password);
        Assert.Equal("second-pass", vault.Items.Single(i => i.Name == "Second").Password);
    }

    [Fact]
    public async Task ProviderManagedOrg_IsCountedAsFailed_AndTheRestStillOpens()
    {
        var fixture = VaultFixture.Demo();
        fixture.AddOrgLogin(fixture.AddProviderOrganization(), "Client login", "c", "client-pass");

        using var vault = await Unlock(fixture);

        Assert.Equal(1, vault.FailedItems);
        Assert.DoesNotContain(vault.Items, i => i.Name == "Client login");
        Assert.Contains(vault.Items, i => i.Name == "GitHub");
    }

    [Fact]
    public async Task OrgKeyForSomeoneElse_IsCountedAsFailed_AndOtherOrgsStillOpen()
    {
        var fixture = new VaultFixture();
        fixture.AddOrgLogin(fixture.AddOrganizationWrappedForSomeoneElse(), "Unreachable", "x", "x-pass");
        fixture.AddOrgLogin(fixture.AddOrganization(), "Reachable", "y", "y-pass");

        using var vault = await Unlock(fixture);

        Assert.Equal(1, vault.FailedItems);
        Assert.Equal("y-pass", vault.Items.Single(i => i.Name == "Reachable").Password);
    }

    [Fact]
    public async Task DamagedPrivateKey_CostsOnlyTheOrgItems()
    {
        var fixture = VaultFixture.Demo();
        fixture.AddOrgLogin(fixture.AddOrganization(), "Team wiki", "t", "wiki-pass");
        using var wrongKey = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        fixture.PrivateKeyOverride = EncString.Encrypt(RandomNumberGenerator.GetBytes(32), wrongKey);

        using var vault = await Unlock(fixture);

        Assert.Equal(1, vault.FailedItems);
        Assert.Equal("gh-Secr3t!pass", vault.Items.Single(i => i.Name == "GitHub").Password);
    }

    [Fact]
    public async Task OrgItemAddedBySync_AppearsWhileUnlocked()
    {
        var fixture = new VaultFixture();
        var org = fixture.AddOrganization();
        fixture.AddOrgLogin(org, "Team wiki", "t", "wiki-pass");
        using var vault = await Unlock(fixture);
        Assert.Single(vault.Items);

        var changed = new TaskCompletionSource();
        vault.Changed += () => changed.TrySetResult();
        fixture.AddOrgLogin(org, "Added by sync", "s", "sync-pass");
        fixture.Write(DataPath, Password);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("sync-pass", vault.Items.Single(i => i.Name == "Added by sync").Password);
    }

    // RSA test key and ciphertexts from bitwarden/sdk-internal (bitwarden-crypto, enc_string/asymmetric.rs): output
    // of the real client's code, so these prove the padding choice for each type against something other than ourselves.
    private const string SdkTestPrivateKey = "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCXRVrCX+2hfOQS8HzYUS2oc/jGVTZpv+/Ryuoh9d8ihYX9dd0cYh2tl6KWdFc88lPUH11Oxqy20Rk2e5r/RF6T9yM0Me3NPnaKt+hlhLtfoc0h86LnhD56A9FDUfuI0dVnPcrwNv0YJIo94LwxtbqBULNvXl6wJ7WAbODrCQy5ZgMVg+iH+gGpwiqsZqHt+KuoHWcN53MSPDfaF4/YMB99U3TziJMOOJask1TEEnakMPln11PczNDazT17DXIxYrbPfutPdh6sLs6AQOajdZijfEvepgnOe7cQ7aeatiOJFrjTApKPGxOVRzEMX4XS4xbyhH0QxQeB6l16l8C0uxIBAgMBAAECggEASaWfeVDA3cVzOPFSpvJm20OTE+R6uGOU+7vh36TX/POq92qBuwbd0h0oMD32FxsXywd2IxtBDUSiFM9699qufTVuM0Q3tZw6lHDTOVG08+tPdr8qSbMtw7PGFxN79fHLBxejjO4IrM9lapjWpxEF+11x7r+wM+0xRZQ8sNFYG46aPfIaty4BGbL0I2DQ2y8I57iBCAy69eht59NLMm27fRWGJIWCuBIjlpfzET1j2HLXUIh5bTBNzqaN039WH49HczGE3mQKVEJZc/efk3HaVd0a1Sjzyn0QY+N1jtZN3jTRbuDWA1AknkX1LX/0tUhuS3/7C3ejHxjw4Dk1ZLo5/QKBgQDIWvqFn0+IKRSu6Ua2hDsufIHHUNLelbfLUMmFthxabcUn4zlvIscJO00Tq/ezopSRRvbGiqnxjv/mYxucvOUBeZtlus0Q9RTACBtw9TGoNTmQbEunJ2FOSlqbQxkBBAjgGEppRPt30iGj/VjAhCATq2MYOa/X4dVR51BqQAFIEwKBgQDBSIfTFKC/hDk6FKZlgwvupWYJyU9RkyfstPErZFmzoKhPkQ3YORo2oeAYmVUbS9I2iIYpYpYQJHX8jMuCbCz4ONxTCuSIXYQYUcUq4PglCKp31xBAE6TN8SvhfME9/MvuDssnQinAHuF0GDAhF646T3LLS1not6Vszv7brwSoGwKBgQC88v/8cGfi80ssQZeMnVvq1UTXIeQcQnoY5lGHJl3K8mbS3TnXE6c9j417Fdz+rj8KWzBzwWXQB5pSPflWcdZO886Xu/mVGmy9RWgLuVFhXwCwsVEPjNX5ramRb0/vY0yzenUCninBsIxFSbIfrPtLUYCc4hpxr+sr2Mg/y6jpvQKBgBezMRRs3xkcuXepuI2R+BCXL1/b02IJTUf1F+1eLLGd7YV0H+J3fgNc7gGWK51hOrF9JBZHBGeOUPlaukmPwiPdtQZpu4QNE3l37VlIpKTF30E6mb+BqR+nht3rUjarnMXgAoEZ18y6/KIjpSMpqC92Nnk/EBM9EYe6Cf4eA9ApAoGAeqEUg46UTlJySkBKURGpIs3v1kkf5I0X8DnOhwb+HPxNaiEdmO7ckm8+tPVgppLcG0+tMdLjigFQiDUQk2y3WjyxP5ZvXu7U96jaJRI8PFMoE06WeVYcdIzrID2HvqH+w0UQJFrLJ/0Mn4stFAEzXKZBokBGnjFnTnKcs7nv/O8=";
    private const string SdkOaepSha256 = "3.SUx5gWrgmAKs/S1BoQrqOmx2Hl5fPVBVHokW17Flvm4TpBnJJRkfoitp7Jc4dfazPYjWGlckJz6X+qe+/AWilS1mxtzS0PmDy7tS5xP0GRlB39dstCd5jDw1wPmTbXiLcQ5VTvzpRAfRMEYVveTsEvVTByvEYAGSn4TnCsUDykyhRbD0YcJ4r1KHLs1b3BCBy2M1Gl5nmwckH08CAXaf8VfuBFStAGRKueovqp4euneQla+4G4fXdVvb8qKPnu0iVuALIE6nUNmeOiA3xN3d+akMxbbGxrQ1Ca4TYWjHVdj9C6abngQHkjKNYQwGUXrYo160hP4LIHn/huK6bZe5dQ==";
    private const string SdkOaepSha1 = "4.DMD1D5r6BsDDd7C/FE1eZbMCKrmryvAsCKj6+bO54gJNUxisOI7SDcpPLRXf+JdhqY15pT+wimQ5cD9C+6OQ6s71LFQHewXPU29l9Pa1JxGeiKqp37KLYf+1IS6UB2K3ANN35C52ZUHh2TlzIS5RuntxnpCw7APbcfpcnmIdLPJBtuj/xbFd6eBwnI3GSe5qdS6/Ixdd0dgsZcpz3gHJBKmIlSo0YN60SweDq3kTJwox9xSqdCueIDg5U4khc7RhjYx8b33HXaNJj3DwgIH8iLj+lqpDekogr630OhHG3XRpvl4QzYO45bmHb8wAh67Dj70nsZcVg6bAEFHdSFohww==";

    [Fact]
    public void OrgKeyWrapping_MatchesTheBitwardenSdk()
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(SdkTestPrivateKey), out _);

        var sha1 = EncString.DecryptRsa(SdkOaepSha1, rsa);
        var sha256 = EncString.DecryptRsa(SdkOaepSha256, rsa);

        Assert.Equal(64, sha1.Length);
        Assert.Equal(sha1, sha256);
        Assert.Equal(sha1, EncString.DecryptRsa("6." + SdkOaepSha1[2..] + "|AA==", rsa));
    }
}
