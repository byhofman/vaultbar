using Avalonia;
using Vaultbar.Platform;

namespace Vaultbar;

internal static class Program
{
    private const string Usage = """
        vaultbar - quick search for your Bitwarden vault

        Usage: vaultbar [command]

          (none)           start in the background (if needed) and open the search bar
          show | toggle    open, or toggle, the search bar of the running instance
          background       start without opening the search bar (used for autostart)
          lock             lock the vault
          quit             stop the running instance
          autostart on|off start vaultbar when you log in
          shortcut         Linux/Wayland: bind the hotkey via GNOME settings
          version          print the version
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : "";
        try
        {
            switch (command)
            {
                case "help" or "h" or "?":
                    AttachConsole();
                    Console.WriteLine(Usage);
                    return 0;
                case "version" or "v":
                    AttachConsole();
                    Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
                    return 0;
                case "autostart":
                    AttachConsole();
                    return Setup.Autostart(args.Length > 1 ? args[1] : null);
                case "shortcut":
                    AttachConsole();
                    return Setup.Shortcut();
                case "lock" or "quit":
                    if (Ipc.TrySend(command)) return 0;
                    AttachConsole();
                    Console.Error.WriteLine("Vaultbar is not running.");
                    return 1;
                case "" or "show" or "toggle":
                    if (OperatingSystem.IsWindows()) WindowsPlatform.AllowForegroundForOtherProcess();
                    return Ipc.TrySend(command == "" ? "show" : command) ? 0 : Run(show: true);
                case "background":
                    return Ipc.TrySend("ping") ? 0 : Run(show: false);
                default:
                    AttachConsole();
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{Usage}");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"fatal: {Log.Shape(ex)}\n{ex.StackTrace}");
            AppController.Current?.ClearClipboardOnCrash();
            AttachConsole();
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Run(bool show)
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Thread.Sleep(750);
            return Ipc.TrySend(show ? "show" : "ping") ? 0 : 1;
        }

        App.ShowOnStart = show;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([], Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();

    private static void AttachConsole()
    {
        if (OperatingSystem.IsWindows()) WindowsPlatform.AttachParentConsole();
    }
}
