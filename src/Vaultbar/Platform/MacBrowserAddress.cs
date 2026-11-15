using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ObjC = Vaultbar.Platform.MacPlatform.ObjC;

namespace Vaultbar.Platform;

/// <summary>
/// Reads the address of the page a browser shows, through the Accessibility API: the URL of the first visible web area
/// in the focused window. That is the document that was loaded, which a page cannot fake; it can only choose its title.
/// Frames inside the page are never read, and a window showing two pages (a side panel next to the tab) has no single
/// address, so it counts as unknown.
/// Needs the Accessibility permission auto-type already asks for; without it the address is unknown.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacBrowserAddress
{
    private const string Ax = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int MaxElements = 1500, KAxValueCgSizeType = 2;

    private static readonly nint Children = Retained("AXChildren"), Role = Retained("AXRole"), Url = Retained("AXURL");
    private static readonly nint FocusedElement = Retained("AXFocusedUIElement"), Parent = Retained("AXParent");
    private static readonly nint Size = Retained("AXSize"), FocusedWindow = Retained("AXFocusedWindow"), MainWindow = Retained("AXMainWindow");
    private static readonly nint WebArea = Retained("AXWebArea"), ManualAccessibility = Retained("AXManualAccessibility");

    /// <summary>How long a slow or hung browser may hold up the bar before the address counts as unknown.</summary>
    /// <remarks>A walk of a browser window takes a hundred or so calls into the browser; on a slow machine they need it.</remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMilliseconds(300);

    /// <param name="knownBrowser">False for any other app: one quick look, to catch browsers and web views not on the list.</param>
    public static string? Read(int pid, bool knownBrowser = true) => Read(pid, knownBrowser, knownBrowser ? Budget : ProbeBudget);

    /// <param name="budget">Only a test passes its own, on a runner where each call into a browser is slow.</param>
    internal static string? Read(int pid, bool knownBrowser, TimeSpan budget) => WithApp(pid, knownBrowser, budget, app =>
    {
        var started = DateTime.UtcNow;
        var deadline = started + budget;
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempts++;
            if (FocusedPage(app) is { Length: > 0 } focused) return focused;
            var scan = Scan(app, deadline);
            LastAttempt = scan is null ? "no window" : $"{scan.Visited} elements, complete={scan.Complete}, visible: {scan.Visible.Count}";
            if (scan is { Complete: true } && scan.Visible is [{ Length: > 0 } address]) return address;
            Thread.Sleep(80);
        }
        LastAttempt += $"; {attempts} attempts in {(DateTime.UtcNow - started).TotalSeconds:0.0} s";
        return null;
    });

    /// <summary>What the last read that found nothing saw, for a test to report.</summary>
    internal static string? LastAttempt { get; private set; }

    /// <summary>What a read finds, for a test to say why it found no address.</summary>
    internal static string Describe(int pid)
    {
        if (AXIsProcessTrusted() == 0) return "not trusted";
        var app = AXUIElementCreateApplication(pid);
        if (app == 0) return "no such app";
        try
        {
            AXUIElementSetMessagingTimeout(app, 5f);
            var before = $"before: role {AXUIElementCopyAttributeValue(app, Role, out _)}, windows {AXUIElementCopyAttributeValue(app, Retained("AXWindows"), out _)}, focused {AXUIElementCopyAttributeValue(app, FocusedWindow, out _)}";
            var set = AXUIElementSetAttributeValue(app, ManualAccessibility, ObjC.Send(ObjC.Class("NSNumber"), ObjC.Sel("numberWithBool:"), 1));
            Thread.Sleep(2000);
            var scan = Scan(app, DateTime.UtcNow + TimeSpan.FromSeconds(10));
            var after = scan is null
                ? $"no window: focused {AXUIElementCopyAttributeValue(app, FocusedWindow, out _)}, main {AXUIElementCopyAttributeValue(app, MainWindow, out _)}"
                : $"{scan.Visited} elements, complete={scan.Complete}, visible web areas: [{string.Join(", ", scan.Visible)}], hidden: {scan.Hidden}";
            return $"{before}; set manual accessibility: {set}; after: {after}";
        }
        finally
        {
            CFRelease(app);
        }
    }

    private static T? WithApp<T>(int pid, bool askForWebTree, TimeSpan budget, Func<nint, T?> read)
    {
        if (AXIsProcessTrusted() == 0) return default;
        var app = AXUIElementCreateApplication(pid);
        if (app == 0) return default;
        try
        {
            // A hung browser must not hold up the bar for the system's default of six seconds.
            // Each call gets the whole budget: Chromium answers for its page from the renderer process, which can take
            // more than a second, and a shorter limit cuts the page out of the tree. The read still ends at the budget.
            AXUIElementSetMessagingTimeout(app, (float)budget.TotalSeconds);
            // Chromium browsers build their web accessibility tree only for assistive apps that ask for it this way. Asked
            // on every read: a browser that was asked before its window existed never builds it (seen on Chrome and Edge).
            if (askForWebTree) AXUIElementSetAttributeValue(app, ManualAccessibility, ObjC.Send(ObjC.Class("NSNumber"), ObjC.Sel("numberWithBool:"), 1));
            return read(app);
        }
        finally
        {
            CFRelease(app);
        }
    }

    /// <summary>
    /// The page that holds the keyboard focus, found by walking up from the focused element: a few calls instead of a walk
    /// of the whole window, and exactly the page the keystrokes will go to. The outermost web area counts, so a frame
    /// inside the page never does. Null when the focus is outside any page, such as in the address bar.
    /// </summary>
    private static string? FocusedPage(nint app)
    {
        if (AXUIElementCopyAttributeValue(app, FocusedElement, out var element) != 0 || element == 0) return null;
        nint outermost = 0;
        try
        {
            for (var depth = 0; depth < 100 && element != 0; depth++)
            {
                if (IsWebArea(element))
                {
                    if (outermost != 0) CFRelease(outermost);
                    outermost = CFRetain(element);
                }
                var next = AXUIElementCopyAttributeValue(element, Parent, out var parent) == 0 ? parent : 0;
                CFRelease(element);
                element = next;
            }
            return outermost != 0 && IsVisible(outermost) ? UrlOf(outermost) : null;
        }
        finally
        {
            if (element != 0) CFRelease(element);
            if (outermost != 0) CFRelease(outermost);
        }
    }

    private sealed record ScanResult(List<string> Visible, int Hidden, int Visited, bool Complete);

    /// <summary>The web areas of the focused window, without looking inside them, so frames are never among them.</summary>
    private static ScanResult? Scan(nint app, DateTime deadline)
    {
        // An app that is not frontmost, as the browser is once the bar has opened, may report only its main window.
        if ((AXUIElementCopyAttributeValue(app, FocusedWindow, out var window) != 0 || window == 0)
            && (AXUIElementCopyAttributeValue(app, MainWindow, out window) != 0 || window == 0)) return null;
        var queue = new Queue<nint>();
        queue.Enqueue(window);
        var result = new ScanResult([], 0, 0, true);
        var hidden = 0;
        var visited = 0;
        try
        {
            while (queue.TryDequeue(out var element))
            {
                try
                {
                    if (++visited > MaxElements || DateTime.UtcNow > deadline) return result with { Hidden = hidden, Visited = visited, Complete = false };
                    if (IsWebArea(element))
                    {
                        if (IsVisible(element)) result.Visible.Add(UrlOf(element) ?? "");
                        else hidden++;
                        continue;
                    }
                    if (AXUIElementCopyAttributeValue(element, Children, out var children) != 0 || children == 0) continue;
                    try
                    {
                        var count = CFArrayGetCount(children);
                        for (nint i = 0; i < count; i++) queue.Enqueue(CFRetain(CFArrayGetValueAtIndex(children, i)));
                    }
                    finally
                    {
                        CFRelease(children);
                    }
                }
                finally
                {
                    CFRelease(element);
                }
            }
            return result with { Hidden = hidden, Visited = visited };
        }
        finally
        {
            while (queue.TryDequeue(out var left)) CFRelease(left);
        }
    }

    private static bool IsWebArea(nint element)
    {
        if (AXUIElementCopyAttributeValue(element, Role, out var role) != 0 || role == 0) return false;
        try
        {
            return CFEqual(role, WebArea) != 0;
        }
        finally
        {
            CFRelease(role);
        }
    }

    /// <summary>A background tab's web area, where a browser keeps one in the tree, has no size on screen.</summary>
    private static bool IsVisible(nint element)
    {
        if (AXUIElementCopyAttributeValue(element, Size, out var value) != 0 || value == 0) return false;
        try
        {
            CGSize size;
            return AXValueGetValue(value, KAxValueCgSizeType, &size) != 0 && size.Width > 0 && size.Height > 0;
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static string? UrlOf(nint element)
    {
        if (AXUIElementCopyAttributeValue(element, Url, out var url) != 0 || url == 0) return null;
        try
        {
            return CFGetTypeID(url) == CFURLGetTypeID() ? ObjC.ToManaged(CFURLGetString(url)) : null;
        }
        finally
        {
            CFRelease(url);
        }
    }

    private static nint Retained(string name) => ObjC.Send(ObjC.NSString(name), ObjC.Sel("retain"));

    private struct CGSize
    {
        public double Width, Height;
    }

    [DllImport(Ax)] private static extern byte AXIsProcessTrusted();
    [DllImport(Ax)] private static extern nint AXUIElementCreateApplication(int pid);
    [DllImport(Ax)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport(Ax)] private static extern int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);
    [DllImport(Ax)] private static extern int AXUIElementSetMessagingTimeout(nint element, float seconds);
    [DllImport(Ax)] private static extern byte AXValueGetValue(nint value, int type, void* result);
    [DllImport(Cf)] private static extern void CFRelease(nint obj);
    [DllImport(Cf)] private static extern nint CFRetain(nint obj);
    [DllImport(Cf)] private static extern byte CFEqual(nint a, nint b);
    [DllImport(Cf)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(Cf)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(Cf)] private static extern nuint CFGetTypeID(nint obj);
    [DllImport(Cf)] private static extern nuint CFURLGetTypeID();
    [DllImport(Cf)] private static extern nint CFURLGetString(nint url);
}
