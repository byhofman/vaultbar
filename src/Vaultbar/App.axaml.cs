using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Vaultbar;

public partial class App : Application
{
    public static bool ShowOnStart { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            new AppController(desktop).Start(ShowOnStart);
        base.OnFrameworkInitializationCompleted();
    }
}
