using System.Globalization;
using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>#60: which logins a browser page gets, by its address and Bitwarden's URI rules, never by its title.</summary>
public class AddressMatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    public static TheoryData<string, string?> SuffixVectors()
    {
        var data = new TheoryData<string, string?>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "public-suffix-tests.txt")))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || line.StartsWith("//", StringComparison.Ordinal) || parts[0] == "null") continue;
            data.Add(parts[0], parts[1] == "null" ? null : parts[1]);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SuffixVectors))]
    public void RegistrableDomain_FollowsThePublicSuffixListTests(string host, string? expected)
    {
        var ascii = expected is null ? null : new IdnMapping().GetAscii(expected.ToLowerInvariant());
        Assert.Equal(ascii, PublicSuffix.RegistrableDomain(host));
    }

    [Theory]
    [InlineData("contoso.github.io", "contoso.github.io")]
    [InlineData("www.contoso.co.uk", "contoso.co.uk")]
    [InlineData("login.contoso.com", "contoso.com")]
    public void RegistrableDomain_KeepsPrivateSuffixesApart(string host, string expected)
    {
        Assert.Equal(expected, PublicSuffix.RegistrableDomain(host));
    }

    [Theory]
    // Base domain, Bitwarden's default.
    [InlineData("https://contoso.com", null, "https://login.contoso.com/sign-in", true)]
    [InlineData("contoso.com", null, "https://www.contoso.com/", true)]
    [InlineData("https://contoso.com", null, "https://contoso-login.example/contoso.com", false)]
    [InlineData("https://contoso.com", null, "https://contoso.com.fabrikam.example/", false)]
    [InlineData("https://contoso.github.io", null, "https://fabrikam.github.io/", false)]
    [InlineData("https://xn--bcher-kva.example", null, "https://shop.bücher.example/", true)]
    [InlineData("https://192.168.1.20:8443", null, "https://192.168.1.20/admin", true)]
    [InlineData("https://accounts.google.com", null, "https://script.google.com/macros/s/x/exec", false)]
    [InlineData("https://accounts.google.com", null, "https://mail.google.com/", true)]
    // Host: hostname and port.
    [InlineData("https://login.contoso.com", 1, "https://login.contoso.com/x", true)]
    [InlineData("https://login.contoso.com", 1, "https://www.contoso.com/", false)]
    [InlineData("https://contoso.com:8443", 1, "https://contoso.com/", false)]
    [InlineData("https://contoso.com:8443", 1, "https://contoso.com:8443/a", true)]
    // Starts with, exact, regular expression, never.
    [InlineData("https://contoso.com/app", 2, "https://contoso.com/app/login", true)]
    [InlineData("https://contoso.com/app", 2, "https://contoso.com/other", false)]
    [InlineData("https://contoso.com/login", 3, "https://contoso.com/login", true)]
    [InlineData("https://contoso.com/login", 3, "https://contoso.com/login?next=1", false)]
    [InlineData(@"^https://(www\.)?contoso\.com/", 4, "https://WWW.contoso.com/a", true)]
    [InlineData(@"^https://contoso\.com/", 4, "https://contoso.com.fabrikam.example/", false)]
    [InlineData("(unclosed", 4, "https://contoso.com/", false)]
    [InlineData("https://contoso.com", 5, "https://contoso.com/", false)]
    public void Matches_FollowsBitwardensRules(string uri, int? match, string page, bool expected)
    {
        Assert.Equal(expected, UriRules.Matches(new LoginUri(uri, (UriMatch?)match), page, UriMatchSettings.Default));
    }

    [Fact]
    public void Matches_UsesTheAccountsDefaultAndEquivalentDomains()
    {
        var hostDefault = new UriMatchSettings(UriMatch.Host, []);
        Assert.False(UriRules.Matches(new LoginUri("https://login.contoso.com", null), "https://www.contoso.com/", hostDefault));
        Assert.True(UriRules.Matches(new LoginUri("https://login.contoso.com", UriMatch.Domain), "https://www.contoso.com/", hostDefault));

        var equivalent = new UriMatchSettings(UriMatch.Domain, [["contoso.com", "contoso-cdn.net"]]);
        Assert.True(UriRules.Matches(new LoginUri("https://contoso.com", null), "https://id.contoso-cdn.net/", equivalent));
        Assert.False(UriRules.Matches(new LoginUri("https://contoso.com", null), "https://fabrikam.com/", equivalent));
    }

    private async Task<VaultService> UnlockedAsync(Action<VaultFixture>? setup = null)
    {
        var fixture = new VaultFixture();
        fixture.AddLogin("Contoso", "alex@contoso.com", "contoso-pass", ["https://login.contoso.com"]);
        fixture.AddLogin("Fabrikam", "alex@fabrikam.com", "fabrikam-pass", ["https://fabrikam.com"]);
        fixture.AddLogin("Northwind admin", "admin", "nw-pass", ["https://admin.northwind.example:8443"], matches: [1]);
        setup?.Invoke(fixture);
        fixture.Write(DataPath, "correct horse");
        var vault = new VaultService(new Config { DataPath = DataPath });
        vault.Probe();
        Assert.Null(await vault.UnlockAsync("correct horse"));
        return vault;
    }

    private static ForeignWindow Browser(string title, string? address) => new(0, title, "msedge") { IsBrowser = true, Address = address };

    [Fact]
    public async Task LookAlikePage_TitledAsTheRealSite_GetsNoSuggestionAndNeedsConfirmation()
    {
        using var vault = await UnlockedAsync();
        var phishing = Browser("Contoso: Sign in to your account - Microsoft Edge", "https://contoso-signin.example/login");

        Assert.Empty(Matcher.Suggest(vault.Items, phishing, vault.UriMatch, new UsageStore()));
        var contoso = vault.Items.Single(i => i.Name == "Contoso");
        Assert.True(Matcher.FillNeedsConfirmation(contoso, phishing, vault.UriMatch));
    }

    [Fact]
    public async Task RealSite_IsSuggestedByItsAddress_AndFillsWithoutConfirmation()
    {
        using var vault = await UnlockedAsync();
        var page = Browser("Sign in - Microsoft Edge", "https://www.contoso.com/account");

        var suggested = Matcher.Suggest(vault.Items, page, vault.UriMatch, new UsageStore());
        Assert.Equal(["Contoso"], suggested.Select(i => i.Name));
        Assert.False(Matcher.FillNeedsConfirmation(suggested[0], page, vault.UriMatch));
    }

    [Fact]
    public async Task UnreadableAddress_GivesNoSuggestionsFromTheTitle_AndNeedsConfirmation()
    {
        using var vault = await UnlockedAsync();
        var page = Browser("Contoso - Microsoft Edge", null);

        Assert.Empty(Matcher.Suggest(vault.Items, page, vault.UriMatch, new UsageStore()));
        Assert.True(Matcher.FillNeedsConfirmation(vault.Items.Single(i => i.Name == "Contoso"), page, vault.UriMatch));
    }

    [Fact]
    public async Task PerUriMatchDetection_ComesFromTheVault()
    {
        using var vault = await UnlockedAsync();
        var admin = vault.Items.Single(i => i.Name == "Northwind admin");

        Assert.True(Matcher.FillNeedsConfirmation(admin, Browser("Admin", "https://admin.northwind.example/"), vault.UriMatch));
        Assert.False(Matcher.FillNeedsConfirmation(admin, Browser("Admin", "https://admin.northwind.example:8443/"), vault.UriMatch));
    }

    [Fact]
    public async Task AccountSettings_ComeFromDataJson()
    {
        using var vault = await UnlockedAsync(f =>
        {
            f.DefaultUriMatch = 1;
            f.EquivalentDomains = [["fabrikam.com", "fabrikam-id.example"]];
        });

        Assert.Equal(UriMatch.Host, vault.UriMatch.DefaultMatch);
        var contoso = vault.Items.Single(i => i.Name == "Contoso");
        Assert.True(Matcher.FillNeedsConfirmation(contoso, Browser("Contoso", "https://www.contoso.com/"), vault.UriMatch));
        var fabrikam = vault.Items.Single(i => i.Name == "Fabrikam");
        Assert.Equal(["fabrikam.com", "fabrikam-id.example"], vault.UriMatch.EquivalentDomains.Single());
        Assert.True(UriRules.Matches(new LoginUri("https://fabrikam.com", UriMatch.Domain), "https://fabrikam-id.example/", vault.UriMatch));
        Assert.True(Matcher.FillNeedsConfirmation(fabrikam, Browser("Fabrikam", "https://fabrikam-id.example/"), vault.UriMatch));
    }

    [Fact]
    public async Task OtherApps_AreStillMatchedOnTheirTitle_AndFillWithoutConfirmation()
    {
        using var vault = await UnlockedAsync();
        var app = new ForeignWindow(0, "Fabrikam Desktop - Sign in", "FabrikamClient");

        var suggested = Matcher.Suggest(vault.Items, app, vault.UriMatch, new UsageStore());
        Assert.Equal("Fabrikam", suggested.First().Name);
        Assert.False(Matcher.FillNeedsConfirmation(suggested[0], app, vault.UriMatch));
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("msedge", true)]
    [InlineData("firefox", true)]
    [InlineData("google-chrome", true)]
    [InlineData("notepad", false)]
    [InlineData("FabrikamClient", false)]
    public void Browsers_AreKnownByProcessName(string process, bool expected)
    {
        Assert.Equal(expected, Browsers.IsBrowserApp(process));
    }

    [Theory]
    [InlineData("com.apple.Safari", true)]
    [InlineData("com.google.Chrome.canary", true)]
    [InlineData("org.mozilla.firefox", true)]
    [InlineData("com.microsoft.edgemac", true)]
    [InlineData("com.apple.TextEdit", false)]
    [InlineData(null, false)]
    public void Browsers_AreKnownByBundleIdentifier(string? bundle, bool expected)
    {
        Assert.Equal(expected, Browsers.IsBrowserBundle(bundle));
    }

    [Theory]
    [InlineData("https://app.contoso.com/", true)]
    [InlineData("http://intranet.example.org/", true)]
    [InlineData("vscode-file://vscode-app/index.html", false)]
    [InlineData("file:///C:/Users/alex/page.html", false)]
    [InlineData(null, false)]
    public void AnUnlistedAppShowingAWebPage_CountsAsABrowser(string? address, bool expected)
    {
        Assert.Equal(expected, Browsers.IsWebAddress(address));
    }
}
