using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Vaultbar.Platform;

namespace Vaultbar;

public partial class App : Application
{
    public static bool ShowOnStart { get; set; }

    internal static Func<IPlatform>? PlatformFactory { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            new AppController(desktop, PlatformFactory?.Invoke() ?? IPlatform.Create()).Start(ShowOnStart);
        base.OnFrameworkInitializationCompleted();
    }
}
