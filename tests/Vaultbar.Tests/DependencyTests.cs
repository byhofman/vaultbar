using System.Reflection;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>
/// Avalonia's Linux backend is compiled against one Tmds.DBus.Protocol and its API changes between minor versions
/// (0.93 removed <c>Connection</c>). A newer copy compiles here but breaks Avalonia on Linux at run time, so the app
/// takes the version Avalonia brings and never its own (D-13).
/// </summary>
public class DependencyTests
{
    [Fact]
    public void TheDBusLibraryIsTheOneAvaloniaWasBuiltAgainst()
    {
        var expected = Assembly.Load("Avalonia.FreeDesktop").GetReferencedAssemblies().Single(a => a.Name == "Tmds.DBus.Protocol");
        var shipped = Assembly.Load("Tmds.DBus.Protocol").GetName();

        Assert.Equal(expected.Version, shipped.Version);
    }
}
