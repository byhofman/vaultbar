using System.Diagnostics;
using Vaultbar.Platform;

namespace Vaultbar;

public static class Setup
{
    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");

    public static int Autostart(string? mode)
    {
        var enable = mode?.ToLowerInvariant() switch
        {
            "on" or "enable" or null => true,
            "off" or "disable" => false,
            _ => throw new ArgumentException("usage: vaultbar autostart [on|off]"),
        };

        if (OperatingSystem.IsWindows())
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enable) run.SetValue("vaultbar", $"\"{Exe}\" background");
            else run.DeleteValue("vaultbar", false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "vaultbar.plist");
            if (enable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
                File.WriteAllText(plist, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                      <key>Label</key><string>vaultbar</string>
                      <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(Exe)}</string><string>background</string></array>
                      <key>RunAtLoad</key><true/>
                      <key>ProcessType</key><string>Interactive</string>
                    </dict>
                    </plist>
                    """);
            }
            else if (File.Exists(plist))
            {
                File.Delete(plist);
            }
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var desktop = Path.Combine(xdg, "autostart", "vaultbar.desktop");
            if (enable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
                File.WriteAllText(desktop, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=Vaultbar
                    Comment=Quick search for Bitwarden
                    Exec="{Exe}" background
                    Terminal=false
                    NoDisplay=true
                    X-GNOME-Autostart-enabled=true
                    """);
            }
            else if (File.Exists(desktop))
            {
                File.Delete(desktop);
            }
        }

        Console.WriteLine(enable ? "Vaultbar will start at login." : "Vaultbar will no longer start at login.");
        return 0;
    }

    /// <summary>Binds the hotkey through GNOME's custom keybindings (needed on Wayland).</summary>
    public static int Shortcut()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("Not needed on this OS: vaultbar registers its hotkey itself.");
            return 0;
        }

        var hk = Hotkey.Parse(Config.Load().EffectiveHotkey);
        var binding = (hk.Ctrl ? "<Control>" : "") + (hk.Shift ? "<Shift>" : "") + (hk.Alt ? "<Alt>" : "") + (hk.Super ? "<Super>" : "")
                      + (hk.Key == "Space" ? "space" : hk.Key.ToLowerInvariant());
        var command = $"\"{Exe}\" toggle";

        if (LinuxPlatform.Which("gsettings") is null)
        {
            Console.WriteLine($"""
                Automatic setup supports GNOME only. Add a custom shortcut in your desktop's keyboard settings:
                  Command:  {command}
                  Shortcut: {hk}
                KDE Plasma: System Settings > Keyboard > Shortcuts > Add New > Command or Script.
                """);
            return 1;
        }

        const string schema = "org.gnome.settings-daemon.plugins.media-keys";
        const string path = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/vaultbar/";
        var list = Run("gsettings", "get", schema, "custom-keybindings").Trim();
        if (!list.Contains(path))
        {
            var updated = list is "@as []" or "[]" or "" ? $"['{path}']" : list.TrimEnd(']') + $", '{path}']";
            Run("gsettings", "set", schema, "custom-keybindings", updated);
        }
        var key = $"{schema}.custom-keybinding:{path}";
        Run("gsettings", "set", key, "name", "Vaultbar");
        Run("gsettings", "set", key, "command", command);
        Run("gsettings", "set", key, "binding", binding);
        Console.WriteLine($"GNOME shortcut {binding} now runs: {command}");
        return 0;
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
