using System.Runtime.InteropServices;
using Vaultbar.Platform;
using ObjC = Vaultbar.Platform.MacPlatform.ObjC;

namespace Vaultbar.MacE2E;

/// <summary>Native probes and in-process input. Events go only to this process's own window.</summary>
internal static class Native
{
    public const ulong Shift = 1 << 17, Control = 1 << 18, Option = 1 << 19, Command = 1 << 20;
    public const string FakeLockNotification = "vaultbar-e2e-system-lock";

    public const ushort Tab = 48, Space = 49, Delete = 51, Escape = 53, Enter = 36, Down = 125, Up = 126, F1 = 122;

    private static readonly Dictionary<char, ushort> Codes = new()
    {
        ['a'] = 0, ['s'] = 1, ['d'] = 2, ['f'] = 3, ['h'] = 4, ['g'] = 5, ['z'] = 6, ['x'] = 7, ['c'] = 8, ['v'] = 9,
        ['b'] = 11, ['q'] = 12, ['w'] = 13, ['e'] = 14, ['r'] = 15, ['y'] = 16, ['t'] = 17, ['1'] = 18, ['2'] = 19,
        ['3'] = 20, ['4'] = 21, ['6'] = 22, ['5'] = 23, ['-'] = 27, ['9'] = 25, ['7'] = 26, ['8'] = 28, ['0'] = 29,
        ['o'] = 31, ['u'] = 32, ['i'] = 34, ['p'] = 35, ['l'] = 37, ['j'] = 38, ['k'] = 40, ['n'] = 45, ['m'] = 46,
        ['/'] = 44, [' '] = 49,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X, Y;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint KeyEvent(nint cls, nint sel, ulong type, NSPoint location, ulong flags, double time, long window,
        nint context, nint chars, nint charsIgnoringModifiers, byte isRepeat, ushort keyCode);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSPoint SendSize(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void Post(nint receiver, nint selector, nint name, nint obj, nint info, byte immediately);

    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    [DllImport(Carbon)] private static extern int CreateEvent(nint allocator, uint cls, uint kind, double when, uint attrs, out nint ev);
    [DllImport(Carbon)] private static extern int SendEventToEventTarget(nint ev, nint target);
    [DllImport(Carbon)] private static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern void ReleaseEvent(nint ev);

    private static nint App => ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));

    public static bool AppIsActive => (ObjC.SendInt(App, ObjC.Sel("isActive")) & 0xff) != 0;

    public static nint KeyWindow => ObjC.Send(App, ObjC.Sel("keyWindow"));

    public static nint FrontApp =>
        ObjC.Send(ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace")), ObjC.Sel("frontmostApplication"));

    public static int FrontPid => FrontApp == 0 ? 0 : ObjC.SendInt(FrontApp, ObjC.Sel("processIdentifier"));

    public static string FrontName => FrontApp == 0 ? "none" : ObjC.ToManaged(ObjC.Send(FrontApp, ObjC.Sel("localizedName"))) ?? "?";

    public static long SharingType(nint nsWindow) => ObjC.SendLong(nsWindow, ObjC.Sel("sharingType"));

    public static long CollectionBehavior(nint nsWindow) => ObjC.SendLong(nsWindow, ObjC.Sel("collectionBehavior"));

    public static string? PasteboardText(string name) =>
        ObjC.ToManaged(ObjC.Send(Board(name), ObjC.Sel("stringForType:"), ObjC.NSString("public.utf8-plain-text")));

    public static bool PasteboardIsConcealed(string name) =>
        ObjC.Send(Board(name), ObjC.Sel("stringForType:"), ObjC.NSString("org.nspasteboard.ConcealedType")) != 0;

    private static nint Board(string name) => ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("pasteboardWithName:"), ObjC.NSString(name));

    /// <summary>AppKit class of each of this app's windows, which shows whether a status item exists.</summary>
    public static List<string> WindowClasses()
    {
        var windows = ObjC.Send(App, ObjC.Sel("windows"));
        var names = new List<string>();
        for (var i = 0L; i < ObjC.SendLong(windows, ObjC.Sel("count")); i++)
        {
            var window = ObjC.Send(windows, ObjC.Sel("objectAtIndex:"), (nint)i);
            names.Add(ObjC.ToManaged(ObjC.Send(ObjC.Send(window, ObjC.Sel("className")), ObjC.Sel("description"))) ?? "?");
        }
        return names;
    }

    public static nint StatusButton()
    {
        var windows = ObjC.Send(App, ObjC.Sel("windows"));
        for (var i = 0L; i < ObjC.SendLong(windows, ObjC.Sel("count")); i++)
        {
            var window = ObjC.Send(windows, ObjC.Sel("objectAtIndex:"), (nint)i);
            if (ClassName(window) == "NSStatusBarWindow" && FindView(ObjC.Send(window, ObjC.Sel("contentView"))) is var button and not 0) return button;
        }
        return 0;
    }

    public static double StatusImageHeight(nint button)
    {
        var image = ObjC.Send(button, ObjC.Sel("image"));
        return image == 0 ? 0 : SendSize(image, ObjC.Sel("size")).Y;
    }

    private static string ClassName(nint obj) => ObjC.ToManaged(ObjC.Send(ObjC.Send(obj, ObjC.Sel("className")), ObjC.Sel("description"))) ?? "";

    private static nint FindView(nint view)
    {
        if (view == 0) return 0;
        if (ClassName(view).Contains("StatusBarButton")) return view;
        var subviews = ObjC.Send(view, ObjC.Sel("subviews"));
        for (var i = 0L; i < ObjC.SendLong(subviews, ObjC.Sel("count")); i++)
            if (FindView(ObjC.Send(subviews, ObjC.Sel("objectAtIndex:"), (nint)i)) is var found and not 0) return found;
        return 0;
    }

    public static void Key(ushort code, string chars, ulong mods = 0, string? ignoring = null)
    {
        foreach (var type in new ulong[] { 10, 11 })
        {
            var window = KeyWindow == 0 ? 0 : ObjC.SendLong(KeyWindow, ObjC.Sel("windowNumber"));
            var ev = KeyEvent(ObjC.Class("NSEvent"),
                ObjC.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
                type, default, mods, Environment.TickCount64 / 1000.0, window, 0, ObjC.NSString(chars), ObjC.NSString(ignoring ?? chars), 0, code);
            ObjC.Send(App, ObjC.Sel("sendEvent:"), ev);
        }
    }

    public static void Chord(char c, ulong mods)
    {
        var chars = (mods & Option) != 0 && c == 'c' ? "ç" : c.ToString();
        Key(Codes[c], chars, mods, c.ToString());
    }

    public static void Type(string text)
    {
        foreach (var c in text) Key(Codes[char.ToLowerInvariant(c)], c.ToString(), char.IsUpper(c) ? Shift : 0, c.ToString());
    }

    public static bool PressHotkeyThroughHandler()
    {
        if (CreateEvent(0, 0x6B657962, 5, 0, 0, out var ev) != 0) return false;
        var err = SendEventToEventTarget(ev, GetApplicationEventTarget());
        ReleaseEvent(ev);
        return err == 0;
    }

    /// <summary>A private name: posting the real screen-lock notification would lock other apps, Bitwarden included.</summary>
    public static void PostFakeSystemLock() => Post(ObjC.Send(ObjC.Class("NSDistributedNotificationCenter"), ObjC.Sel("defaultCenter")),
        ObjC.Sel("postNotificationName:object:userInfo:deliverImmediately:"), ObjC.NSString(FakeLockNotification), 0, 0, 0);
}

/// <summary>A plain foreground app in its own process: the "app you came from" that focus must return to.</summary>
internal static class ForegroundTarget
{
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    [DllImport(AppKit)] private static extern void NSApplicationLoad();

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint InitWindow(nint window, nint sel, NSRect frame, ulong style, ulong backing, byte defer);

    public static void Run(int parentPid)
    {
        // The parent can die without cleaning up; a test window must not outlive the run.
        new Thread(() =>
        {
            using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
            parent.WaitForExit();
            Environment.Exit(0);
        }) { IsBackground = true }.Start();

        NSApplicationLoad();
        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.Send(app, ObjC.Sel("setActivationPolicy:"), 0);
        var window = InitWindow(ObjC.Send(ObjC.Class("NSWindow"), ObjC.Sel("alloc")), ObjC.Sel("initWithContentRect:styleMask:backing:defer:"),
            new NSRect { X = 40, Y = 40, Width = 320, Height = 120 }, 1, 2, 0);
        ObjC.Send(window, ObjC.Sel("setTitle:"), ObjC.NSString("Vaultbar test target"));
        ObjC.Send(window, ObjC.Sel("makeKeyAndOrderFront:"), 0);
        ObjC.Send(app, ObjC.Sel("activateIgnoringOtherApps:"), 1);
        ObjC.Send(app, ObjC.Sel("run"));
    }
}
