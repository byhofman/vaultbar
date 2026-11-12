using Xunit;

namespace Vaultbar.Tests;

/// <summary>A test of macOS-only code, such as the Secure Enclave key file, skipped elsewhere.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS only";
    }
}
