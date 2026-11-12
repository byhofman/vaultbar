using Xunit;

namespace Vaultbar.Tests;

/// <summary>A theory over macOS-only code, skipped elsewhere.</summary>
public sealed class MacTheoryAttribute : TheoryAttribute
{
    public MacTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS only";
    }
}
