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
    private static readonly nint Size = Retained("AXSize"), FocusedWindow = Retained("AXFocusedWindow"), MainWindow = Retained("AXMainWindow");
    private static readonly nint WebArea = Retained("AXWebArea"), ManualAccessibility = Retained("AXManualAccessibility");

    public static string? Read(int pid) => WithApp(pid, app =>
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (Scan(app) is { Complete: true } scan && scan.Visible is [{ Length: > 0 } address]) return address;
            Thread.Sleep(80);
        }
        return null;
    });

    /// <summary>What a read finds, for a test to say why it found no address.</summary>
    internal static string Describe(int pid) =>
        WithApp(pid, app => Scan(app) is { } scan
            ? $"{scan.Visited} elements, complete={scan.Complete}, visible web areas: [{string.Join(", ", scan.Visible)}], hidden: {scan.Hidden}"
            : $"no window: focused error {AXUIElementCopyAttributeValue(app, FocusedWindow, out _)}, main error {AXUIElementCopyAttributeValue(app, MainWindow, out _)}")
        ?? "not trusted or no such app";

    private static T? WithApp<T>(int pid, Func<nint, T?> read)
    {
        if (AXIsProcessTrusted() == 0) return default;
        var app = AXUIElementCreateApplication(pid);
        if (app == 0) return default;
        try
        {
            // A hung browser must not hold up the bar for the system's default of six seconds.
            AXUIElementSetMessagingTimeout(app, 0.5f);
            // Chromium browsers build their web accessibility tree only for assistive apps that ask for it this way.
            AXUIElementSetAttributeValue(app, ManualAccessibility, ObjC.Send(ObjC.Class("NSNumber"), ObjC.Sel("numberWithBool:"), 1));
            return read(app);
        }
        finally
        {
            CFRelease(app);
        }
    }

    private sealed record ScanResult(List<string> Visible, int Hidden, int Visited, bool Complete);

    /// <summary>The web areas of the focused window, without looking inside them, so frames are never among them.</summary>
    private static ScanResult? Scan(nint app)
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
                    if (++visited > MaxElements) return result with { Hidden = hidden, Visited = visited, Complete = false };
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
