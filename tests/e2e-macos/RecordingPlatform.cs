using Avalonia;
using Avalonia.Controls;
using Vaultbar.Platform;
using Vaultbar.Vault;

namespace Vaultbar.MacE2E;

internal sealed class RecordingPlatform(MacPlatform mac) : IPlatform
{
    public readonly List<string> Opened = [];
    public readonly FakeTouchIdDevice TouchIdDevice = new();
    public IQuickUnlockProvider? QuickUnlock { get; private set; }

    public void UseFakeTouchId(string keyPath) => QuickUnlock = new MacTouchId(TouchIdDevice, keyPath);

    public string? StartHotkey(Hotkey hotkey, Action onPressed) => mac.StartHotkey(hotkey, onPressed);
    public ForeignWindow? CaptureForeground() => mac.CaptureForeground();
    public PixelPoint? AnchorPoint(ForeignWindow? target) => mac.AnchorPoint(target);
    public void AttachWindow(Window window) => mac.AttachWindow(window);
    public void AfterStart(bool shown) => mac.AfterStart(shown);
    public void AfterShow() => mac.AfterShow();
    public void AfterHide() => mac.AfterHide();
    public void ExcludeFromCapture(Window window) => mac.ExcludeFromCapture(window);

    // MacPlatform would open the system Accessibility dialog here, which is the owner's to answer.
    public string? AutoTypeUnavailableReason(ForeignWindow? target) => "test run: Accessibility not requested";

    public Task<AutoTypeResult> AutoTypeAsync(ForeignWindow? target, IReadOnlyList<TypeStep> steps, Action hideWindow) => mac.AutoTypeAsync(target, steps, hideWindow);
    public Task<object?> SetClipboardAsync(string text) => mac.SetClipboardAsync(text);
    public Task ClearClipboardAsync(object token) => mac.ClearClipboardAsync(token);
    public void OpenUrl(WebAddress address) => Opened.Add(address.Value);
    public void WatchSystemLock(Action onLock) => mac.WatchSystemLock(onLock);
    public void Dispose() => mac.Dispose();
}
