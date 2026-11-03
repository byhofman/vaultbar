using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Vaultbar.Platform;
using Vaultbar.UI;
using Vaultbar.Vault;

namespace Vaultbar;

public sealed class AppController
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly CancellationTokenSource _shutdown = new();
    private QuickWindow? _window;
    private object? _clipboardToken;
    private CancellationTokenSource? _clipboardClear;

    public AppController(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        Config = Config.Load();
        Vault = new VaultService(Config);
        Platform = IPlatform.Create();
        Usage = new UsageStore();
        Icons = new FaviconService();
    }

    public Config Config { get; }
    public VaultService Vault { get; }
    public IPlatform Platform { get; }
    public UsageStore Usage { get; }
    public FaviconService Icons { get; }
    public string HotkeyText { get; private set; } = "";
    public string? HotkeyError { get; private set; }

    public void Start(bool show)
    {
        Vault.Probe();
        _window = new QuickWindow(this);
        Platform.AttachWindow(_window);

        try
        {
            var hotkey = Hotkey.Parse(Config.EffectiveHotkey);
            HotkeyText = hotkey.ToString();
            HotkeyError = Platform.StartHotkey(hotkey, OnHotkey);
        }
        catch (FormatException ex)
        {
            HotkeyError = ex.Message;
        }
        if (HotkeyError != null) Log.Write($"hotkey: {HotkeyError}");

        Ipc.StartServer(OnCommand, _shutdown.Token);
        Platform.WatchSystemLock(() => Dispatcher.UIThread.Post(() =>
        {
            if (Config.LockOnSystemLock) Lock();
        }));
        Vault.Changed += () => Dispatcher.UIThread.Post(() => _window?.OnVaultChanged());

        var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        idle.Tick += (_, _) =>
        {
            if (Vault.IdleExpired && _window?.IsVisible != true) Lock();
        };
        idle.Start();

        CreateTrayIcon();
        if (show) Show(Platform.CaptureForeground());
    }

    private void OnHotkey()
    {
        var target = Platform.CaptureForeground();
        Dispatcher.UIThread.Post(() => Toggle(target));
    }

    private void OnCommand(string command)
    {
        switch (command)
        {
            case "show":
                Dispatcher.UIThread.Post(() => Show(Platform.CaptureForeground()));
                break;
            case "toggle":
                Dispatcher.UIThread.Post(() => Toggle(Platform.CaptureForeground()));
                break;
            case "lock":
                Dispatcher.UIThread.Post(Lock);
                break;
            case "quit":
                Dispatcher.UIThread.Post(Quit);
                break;
        }
    }

    public void Toggle(ForeignWindow? target)
    {
        if (_window?.IsVisible == true) _window.Dismiss();
        else Show(target);
    }

    public void Show(ForeignWindow? target)
    {
        if (Vault.State != VaultState.Unlocked) Vault.Probe();
        Icons.BaseUrl = Vault.IconsBaseUrl;
        _window?.Present(target);
    }

    public void Lock()
    {
        Vault.Lock();
        _window?.OnVaultChanged();
    }

    public async Task CopyAsync(string text, bool sensitive)
    {
        _clipboardClear?.Cancel();
        var token = await Platform.SetClipboardAsync(text);
        _clipboardToken = token;
        if (!sensitive || token is null || Config.ClipboardClearSeconds <= 0) return;

        var cts = _clipboardClear = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Config.ClipboardClearSeconds), cts.Token);
            await Platform.ClearClipboardAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async void Quit()
    {
        try
        {
            if (_clipboardToken != null && _clipboardClear is { IsCancellationRequested: false })
                await Platform.ClearClipboardAsync(_clipboardToken).WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Log.Write($"quit: {ex.Message}");
        }
        Usage.Flush();
        Vault.Dispose();
        Platform.Dispose();
        _shutdown.Cancel();
        _lifetime.Shutdown();
    }

    private void CreateTrayIcon()
    {
        var open = new NativeMenuItem($"Search vault  ({HotkeyText})");
        open.Click += (_, _) => Show(null);
        var lockItem = new NativeMenuItem("Lock vault");
        lockItem.Click += (_, _) => Lock();
        var quit = new NativeMenuItem("Quit Vaultbar");
        quit.Click += (_, _) => Quit();

        var tray = new TrayIcon
        {
            Icon = AppIcon.Create(),
            ToolTipText = $"Vaultbar ({HotkeyText})",
            Menu = new NativeMenu { open, lockItem, new NativeMenuItemSeparator(), quit },
        };
        tray.Clicked += (_, _) => Show(null);
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
    }
}
