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

    internal AppController(IClassicDesktopStyleApplicationLifetime lifetime, IPlatform platform)
    {
        _lifetime = lifetime;
        Config = Config.Load();
        Vault = new VaultService(Config);
        Platform = platform;
        if (OperatingSystem.IsMacOS() ? Config.TouchId : Config.WindowsHello) Vault.QuickUnlock = Platform.QuickUnlock;
        Usage = new UsageStore();
        Icons = new FaviconService();
        Clipboard = new SecretClipboard(platform, Config.ClipboardClearSeconds);
    }

    public Config Config { get; }
    public VaultService Vault { get; }
    public static AppController? Current { get; private set; }
    public IPlatform Platform { get; }
    public UsageStore Usage { get; }
    public FaviconService Icons { get; }
    public SecretClipboard Clipboard { get; }
    internal QuickWindow? Window => _window;
    public string HotkeyText { get; private set; } = "";
    public string? HotkeyError { get; private set; }

    public void Start(bool show)
    {
        Current = this;
        _lifetime.ShutdownRequested += (_, _) => OnSystemQuit();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ClearClipboardOnCrash();
        Vault.Probe();
        _window = new QuickWindow(this) { Icon = AppIcon.Create() };
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
        Vault.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            // The vault also locks itself, when the desktop app switches accounts.
            if (Vault.State != VaultState.Unlocked) _ = ClearCopiedSecretAsync();
            _window?.OnVaultChanged();
        });

        var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        idle.Tick += (_, _) =>
        {
            if (Vault.IdleExpired && _window?.IsVisible != true) Lock();
        };
        idle.Start();

        CreateTrayIcon();
        if (Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) Show(Platform.CaptureForeground());
            };
        }
        if (show) Show(Platform.CaptureForeground());
        Dispatcher.UIThread.Post(() => Platform.AfterStart(show), DispatcherPriority.Background);
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
        _ = ClearCopiedSecretAsync();
        _window?.OnVaultChanged();
    }

    public Task CopyAsync(string text, bool sensitive) => Clipboard.CopyAsync(text, sensitive);

    private async Task ClearCopiedSecretAsync()
    {
        try
        {
            await Clipboard.ClearNowAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        // Runs on lock, quit and crash: nothing may escape from here.
        catch (Exception ex)
        {
            Log.Write($"clipboard clear: {Log.Shape(ex)}");
        }
    }

    // Cmd+Q and logging out arrive here, not through Quit. Cancelling to run Quit instead would abort a logout.
    private void OnSystemQuit()
    {
        ClearClipboardOnCrash();
        Usage.Flush();
    }

    public void ClearClipboardOnCrash()
    {
        try
        {
            ClearCopiedSecretAsync().Wait(700);
        }
        catch
        {
        }
    }

    public async void Quit()
    {
        await ClearCopiedSecretAsync();
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

        var menu = new NativeMenu { open, lockItem };
        if (Vault.QuickUnlock is { } quick)
        {
            var hello = new NativeMenuItem($"Unlock with {quick.Name}") { ToggleType = NativeMenuItemToggleType.CheckBox };
            void Sync() => hello.IsChecked = Vault.QuickUnlockSetting != QuickUnlockSetting.Off;
            Sync();
            menu.NeedsUpdate += (_, _) => Sync();
            Vault.Changed += () => Dispatcher.UIThread.Post(Sync);
            hello.Click += async (_, _) =>
            {
                await Vault.SetQuickUnlockEnabledAsync(Vault.QuickUnlockSetting == QuickUnlockSetting.Off);
                Sync();
            };
            menu.Add(hello);
            _ = RemoveUnlessAvailableAsync();

            async Task RemoveUnlessAvailableAsync()
            {
                if (!await quick.IsAvailableAsync()) menu.Items.Remove(hello);
            }
        }
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        var tray = new TrayIcon
        {
            Icon = AppIcon.Create(),
            ToolTipText = $"Vaultbar ({HotkeyText})",
            Menu = menu,
        };
        tray.Clicked += (_, _) => Show(null);
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
    }
}
