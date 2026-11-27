using System.Diagnostics;
using Vaultbar.Platform;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>
/// #60 against real browsers: a local look-alike page titled as Contoso's sign-in, opened in a throwaway profile, and
/// the address Vaultbar reads must be the page's own. Runs where CI sets VAULTBAR_BROWSER_TESTS=1 (the Windows and macOS
/// jobs, whose images have the browsers and are thrown away); never against the user's own browser profile.
/// </summary>
public sealed class BrowserAddressTests : IDisposable
{
    private const string Marker = "Contoso: Sign in to your account";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-browser-" + Guid.NewGuid().ToString("N"));

    public BrowserAddressTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        for (var i = 0; i < 10 && Directory.Exists(_dir); i++)
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    public static TheoryData<string> Windows() => ["msedge", "chrome", "firefox"];

    [BrowserTheory(Os = "windows")]
    [MemberData(nameof(Windows))]
    public async Task Windows_ReadsTheLoadedAddress_NotTheTitle(string browser)
    {
        var exe = WindowsBrowserExe(browser);
        Assert.True(exe is not null, $"{browser} is not installed on this runner");

        var page = Path.Combine(_dir, "lookalike.html");
        File.WriteAllText(page, $"<!doctype html><title>{Marker}</title><h1>Contoso</h1><input value=\"https://login.contoso.com/\">");
        var expected = new Uri(page).AbsoluteUri;
        var profile = Path.Combine(_dir, "profile-" + browser);
        Directory.CreateDirectory(profile);
        string[] args = browser == "firefox"
            ? ["-no-remote", "-new-instance", "-profile", profile, expected]
            : [$"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--disable-sync", "--new-window", expected];
        if (browser == "firefox")
            File.WriteAllText(Path.Combine(profile, "user.js"), """
                user_pref("browser.aboutwelcome.enabled", false);
                user_pref("browser.startup.homepage_override.mstone", "ignore");
                user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification", true);
                user_pref("browser.shell.checkDefaultBrowser", false);
                """);

        var started = DateTime.Now.AddSeconds(-1);
        using var launched = Process.Start(exe!, args);
        try
        {
            var window = await WindowWithTitleAsync(Marker);
            Assert.True(window != 0, $"no {browser} window titled \"{Marker}\"");

            string? address = null;
            for (var i = 0; i < 20 && address != expected; i++)
            {
                address = WindowsBrowserAddress.Read(window);
                if (address != expected) await Task.Delay(500);
            }
            Assert.Equal(expected, address);
            // An unlisted Chromium browser or web view (WebView2, Electron) is recognised the same way. Firefox reaches UI
            // Automation through its MSAA bridge, which does not name the framework, so it is known by name only.
            if (browser != "firefox") Assert.Equal(expected, WindowsBrowserAddress.Read(window, knownBrowser: false));
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName(browser))
            {
                try
                {
                    if (p.StartTime >= started) p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            if (!launched.HasExited) launched.Kill(entireProcessTree: true);
        }
    }

    [BrowserTheory(Os = "windows")]
    [InlineData("notepad")]
    public async Task Windows_ADocumentThatOnlyContainsAWebAddress_IsNotAPage(string editor)
    {
        var file = Path.Combine(_dir, "contoso-notes.txt");
        File.WriteAllText(file, "https://login.contoso.com/");
        var started = DateTime.Now.AddSeconds(-1);
        using var launched = Process.Start(new ProcessStartInfo(editor, $"\"{file}\"") { UseShellExecute = true });
        try
        {
            var window = await WindowWithTitleAsync("contoso-notes");
            Assert.True(window != 0, $"no {editor} window");
            Assert.Null(WindowsBrowserAddress.Read(window, knownBrowser: false));
        }
        finally
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.StartTime >= started && p.MainWindowTitle.Contains("contoso-notes", StringComparison.Ordinal)) p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    public static TheoryData<string, string> Mac() => new()
    {
        // Not Firefox: its web area reports no size, so the read cannot tell it from a hidden tab and the address stays
        // unknown, which makes every fill into Firefox ask for a second Enter (README, Limitations).
        { "Safari", "Safari" }, { "Google Chrome", "Google Chrome" }, { "Microsoft Edge", "Microsoft Edge" },
    };

    [BrowserTheory(Os = "macos")]
    [MemberData(nameof(Mac))]
    public async Task Mac_ReadsTheLoadedAddress_NotTheTitle(string app, string process)
    {
        Assert.True(Directory.Exists($"/Applications/{app}.app"), $"{app} is not installed on this runner");
        Assert.True(MacAccessibilityTrusted(), "this runner has not granted Accessibility, which reading the address needs");

        // /var is a link to /private/var, and the browser reports the resolved path.
        var page = Path.Combine(_dir.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + _dir : _dir, "lookalike.html");
        File.WriteAllText(page, $"<!doctype html><title>{Marker}</title><h1>Contoso</h1>");
        var expected = new Uri(page).AbsoluteUri;
        var started = DateTime.Now.AddSeconds(-1);
        var profile = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(profile);
        // Safari through Launch Services; the others straight from their binary, since on the runner a second instance
        // started by open -n sits stopped (state T) and never answers.
        var (file, args) = app switch
        {
            "Safari" => ("open", new[] { "-a", app, expected }),
            "Firefox" => ($"/Applications/{app}.app/Contents/MacOS/firefox", new[] { "-no-remote", "-new-instance", "-profile", profile, expected }),
            _ => ($"/Applications/{app}.app/Contents/MacOS/{app}", new[] { $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--use-mock-keychain", expected }),
        };
        if (app == "Firefox")
            File.WriteAllText(Path.Combine(profile, "user.js"), """
                user_pref("browser.aboutwelcome.enabled", false);
                user_pref("browser.startup.homepage_override.mstone", "ignore");
                user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification", true);
                user_pref("browser.shell.checkDefaultBrowser", false);
                """);
        using (Process.Start(new ProcessStartInfo(file, args) { RedirectStandardInput = true })) { }
        try
        {
            string? address = null;
            var pid = 0;
            for (var i = 0; i < 8 && address != expected; i++)
            {
                await Task.Delay(500);
                pid = Process.GetProcessesByName(process).Select(p => (p.Id, Start: StartOf(p))).Where(p => p.Start >= started).OrderBy(p => p.Start).Select(p => p.Id).FirstOrDefault();
                // The hosted runner answers each accessibility call slowly, so this gives the read more time than the
                // bar would; what is tested is which address is read, not how fast.
                if (pid != 0) address = MacBrowserAddress.Read(pid, knownBrowser: true, TimeSpan.FromSeconds(15));
            }
            var described = pid == 0 ? "no process" : MacBrowserAddress.Describe(pid);
            var afterDescribe = pid == 0 ? null : MacBrowserAddress.Read(pid, knownBrowser: true, TimeSpan.FromSeconds(15));
            Assert.True(expected == address, $"read {address ?? "nothing"} ({MacBrowserAddress.LastAttempt}); read after describing: {afterDescribe ?? "nothing"}; {described}; processes: {string.Join(", ", Process.GetProcesses().Where(p => p.ProcessName.Contains(process, StringComparison.OrdinalIgnoreCase)).Select(p => $"{p.ProcessName} {p.Id}"))}; windows: {Run("/bin/sh", "-c", $"osascript -e 'tell application \"System Events\" to get name of every window of (every process whose unix id is {pid})' 2>&1; ps -o stat=,etime= -p {pid}")}");
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName(process))
            {
                try
                {
                    if (p.StartTime >= started) p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    /// <summary>A helper process can exit between the listing and the question, which throws.</summary>
    private static DateTime StartOf(Process p)
    {
        try
        {
            return p.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTime.MinValue;
        }
    }

    private static string Run(string file, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        // A diagnostic must never hang the run: osascript can wait on a permission prompt nobody answers.
        if (!p.WaitForExit(15000))
        {
            p.Kill(entireProcessTree: true);
            return "(timed out)";
        }
        return (output.Result + errors.Result).Trim();
    }

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", EntryPoint = "AXIsProcessTrusted")]
    private static extern byte AxTrusted();

    private static bool MacAccessibilityTrusted() => AxTrusted() != 0;

    private static string? WindowsBrowserExe(string browser)
    {
        var programs = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        var relative = browser switch
        {
            "msedge" => @"Microsoft\Edge\Application\msedge.exe",
            "chrome" => @"Google\Chrome\Application\chrome.exe",
            _ => @"Mozilla Firefox\firefox.exe",
        };
        return programs.Select(p => Path.Combine(p, relative)).FirstOrDefault(File.Exists);
    }

    private static async Task<nint> WindowWithTitleAsync(string marker)
    {
        for (var i = 0; i < 60; i++)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle != 0 && p.MainWindowTitle.Contains(marker, StringComparison.Ordinal)) return p.MainWindowHandle;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            await Task.Delay(500);
        }
        return 0;
    }
}

/// <summary>A test that opens real browsers, run only where VAULTBAR_BROWSER_TESTS=1 says the machine is a throwaway one.</summary>
public sealed class BrowserTheoryAttribute : TheoryAttribute
{
    public required string Os
    {
        get;
        init
        {
            field = value;
            if (Environment.GetEnvironmentVariable("VAULTBAR_BROWSER_TESTS") != "1") Skip = "set VAULTBAR_BROWSER_TESTS=1 on a throwaway machine";
            else if (!OperatingSystem.IsOSPlatform(value)) Skip = $"{value} only";
        }
    }
}
