using Avalonia;
using Avalonia.Controls;

namespace Vaultbar.Platform;

public interface IPlatform : IClipboardAccess, IDisposable
{
    /// <returns>Null when the hotkey is active, otherwise a human-readable reason.</returns>
    string? StartHotkey(Hotkey hotkey, Action onPressed);

    ForeignWindow? CaptureForeground();

    PixelPoint? AnchorPoint(ForeignWindow? target);

    void AttachWindow(Window window);

    void AfterShow();

    void AfterStart(bool shown)
    {
    }

    void AfterHide()
    {
    }

    /// <returns>Null when auto-type can run, otherwise why not.</returns>
    string? AutoTypeUnavailableReason(ForeignWindow? target);

    Task<AutoTypeResult> AutoTypeAsync(ForeignWindow? target, IReadOnlyList<TypeStep> steps, Action hideWindow);

    void OpenUrl(Vault.WebAddress address);

    void WatchSystemLock(Action onLock);

    void ExcludeFromCapture(Window window)
    {
    }

    Vault.IQuickUnlockProvider? QuickUnlock => null;

    static IPlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        if (OperatingSystem.IsMacOS()) return new MacPlatform();
        if (OperatingSystem.IsLinux()) return new LinuxPlatform();
        throw new PlatformNotSupportedException();
    }
}
