using System.Runtime.InteropServices;
using Vaultbar.Vault;

namespace Vaultbar;

/// <summary>
/// A new issue on GitHub with the version and system filled in (#65). It only opens a page in the browser: Vaultbar
/// itself sends nothing, and nothing from the vault goes into the address.
/// </summary>
public static class IssueReport
{
    private const string NewIssue = "https://github.com/byhofman/vaultbar/issues/new";

    public static WebAddress Address(string version, string system) =>
        WebAddress.From($"{NewIssue}?template=report.yml&version={Uri.EscapeDataString(version)}&os={Uri.EscapeDataString(system)}")!;

    public static WebAddress ForThisMachine() =>
        Address(typeof(IssueReport).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}");
}
