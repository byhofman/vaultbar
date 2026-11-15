using Xunit;

namespace Vaultbar.Tests;

/// <summary>VBR-29: "Report an issue" opens GitHub's form for this repository with version and system filled in.</summary>
public class IssueReportTests
{
    [Fact]
    public void TheAddress_PrefillsTheTemplateFields()
    {
        var url = IssueReport.Address("0.4.0", "macOS 26.1 & more arm64").Value;

        Assert.StartsWith("https://github.com/byhofman/vaultbar/issues/new?template=report.yml&", url);
        Assert.Contains("version=0.4.0", url);
        Assert.Contains("os=macOS%2026.1%20%26%20more%20arm64", url);
    }

    [Fact]
    public void TheTemplate_HasTheFieldsTheAddressFills()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "vaultbar.slnx"))) root = root.Parent;
        var template = File.ReadAllText(Path.Combine(root!.FullName, ".github", "ISSUE_TEMPLATE", "report.yml"));

        Assert.Contains("id: version", template);
        Assert.Contains("id: os", template);
        Assert.Contains("id: steps", template);
        Assert.Contains("Never paste anything from your vault", template);
    }
}
