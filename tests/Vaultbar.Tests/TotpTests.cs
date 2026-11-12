using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class TotpTests
{
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(2000000000, "69279037")]
    public void Rfc6238_Sha1(long time, string expected)
    {
        var code = Totp.Generate("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&digits=8", DateTimeOffset.FromUnixTimeSeconds(time));
        Assert.Equal(expected, code.Value);
    }

    [Fact]
    public void Rfc6238_Sha256()
    {
        var code = Totp.Generate("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA&digits=8&algorithm=SHA256",
            DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal("46119246", code.Value);
    }

    [Fact]
    public void PlainSecret_DefaultsToSixDigitsAnd30s()
    {
        var code = Totp.Generate("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal("287082", code.Value);
        Assert.Equal(1, code.SecondsRemaining);
    }

    [Fact]
    public void Steam_UsesFiveCharAlphabet()
    {
        var code = Totp.Generate("steam://GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal(5, code.Value.Length);
        Assert.All(code.Value, c => Assert.Contains(c, "23456789BCDFGHJKMNPQRTVWXY"));
    }
}
