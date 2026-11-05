using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace Vaultbar.Platform;

[SupportedOSPlatform("macos")]
internal sealed class MacPlatform(string? pasteboardName = null, string lockNotification = "com.apple.screenIsLocked") : IPlatform
{
    private static Action? s_onHotkey;
    private static Action? s_onLock;
    private nint _hotkeyRef;
    private nint _lastTarget;

    internal static readonly Dictionary<string, uint> KeyCodes = new()
    {
        ["A"] = 0, ["S"] = 1, ["D"] = 2, ["F"] = 3, ["H"] = 4, ["G"] = 5, ["Z"] = 6, ["X"] = 7, ["C"] = 8, ["V"] = 9,
        ["B"] = 11, ["Q"] = 12, ["W"] = 13, ["E"] = 14, ["R"] = 15, ["Y"] = 16, ["T"] = 17, ["1"] = 18, ["2"] = 19,
        ["3"] = 20, ["4"] = 21, ["6"] = 22, ["5"] = 23, ["9"] = 25, ["7"] = 26, ["8"] = 28, ["0"] = 29, ["O"] = 31,
        ["U"] = 32, ["I"] = 34, ["P"] = 35, ["L"] = 37, ["J"] = 38, ["K"] = 40, ["N"] = 45, ["M"] = 46, ["Space"] = 49,
        ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118, ["F5"] = 96, ["F6"] = 97, ["F7"] = 98, ["F8"] = 100,
        ["F9"] = 101, ["F10"] = 109, ["F11"] = 103, ["F12"] = 111, ["F13"] = 105, ["F14"] = 107, ["F15"] = 113, ["F16"] = 106,
        ["F17"] = 64, ["F18"] = 79, ["F19"] = 80, ["F20"] = 90,
    };

    public unsafe string? StartHotkey(Hotkey hk, Action onPressed)
    {
        if (!KeyCodes.TryGetValue(hk.Key, out var code)) return $"Unsupported hotkey key {hk.Key}";
        const uint cmdKey = 0x100, shiftKey = 0x200, optionKey = 0x800, controlKey = 0x1000;
        var mods = (hk.Super ? cmdKey : 0) | (hk.Shift ? shiftKey : 0) | (hk.Alt ? optionKey : 0) | (hk.Ctrl ? controlKey : 0);

        s_onHotkey = onPressed;
        var spec = new Carbon.EventTypeSpec { EventClass = 0x6B657962 /* 'keyb' */, EventKind = 5 /* kEventHotKeyPressed */ };
        var target = Carbon.GetApplicationEventTarget();
        var err = Carbon.InstallEventHandler(target, (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnHotkey, 1, &spec, 0, 0);
        if (err != 0) return $"Could not install hotkey handler ({err})";
        err = Carbon.RegisterEventHotKey(code, mods, new Carbon.EventHotKeyID { Signature = 0x62777174, Id = 1 }, target, 0, out _hotkeyRef);
        return err == 0 ? null : $"{hk} could not be registered ({err}). Change \"hotkey\" in {Config.FilePath}.";
    }

    [UnmanagedCallersOnly]
    private static int OnHotkey(nint nextHandler, nint ev, nint userData)
    {
        try
        {
            s_onHotkey?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"hotkey: {ex}");
        }
        return 0;
    }

    public ForeignWindow? CaptureForeground()
    {
        var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace"));
        var app = ObjC.Send(workspace, ObjC.Sel("frontmostApplication"));
        if (app == 0) return null;
        var pid = ObjC.SendInt(app, ObjC.Sel("processIdentifier"));
        if (pid == Environment.ProcessId) return null;

        ObjC.Send(app, ObjC.Sel("retain"));
        if (_lastTarget != 0) ObjC.Send(_lastTarget, ObjC.Sel("release"));
        _lastTarget = app;

        var name = ObjC.ToManaged(ObjC.Send(app, ObjC.Sel("localizedName"))) ?? "";
        return new ForeignWindow(app, Ax.FocusedWindowTitle(pid) ?? "", name, pid);
    }

    public PixelPoint? AnchorPoint(ForeignWindow? target) => null;

    public void AttachWindow(Window window)
    {
        // Without these the bar drags the user to the Space it last opened on, and cannot appear over a full-screen app.
        const nint moveToActiveSpace = 1 << 1, fullScreenAuxiliary = 1 << 8;
        if (NsWindow(window) is var nsWindow and not 0)
            ObjC.Send(nsWindow, ObjC.Sel("setCollectionBehavior:"), moveToActiveSpace | fullScreenAuxiliary);
    }

    private static nint NsWindow(Window window)
    {
        if (window.TryGetPlatformHandle() is not { Handle: not 0 } handle) return 0;
        return handle.HandleDescriptor == "NSWindow" ? handle.Handle : ObjC.Send(handle.Handle, ObjC.Sel("window"));
    }

    public void ExcludeFromCapture(Window window)
    {
        if (NsWindow(window) is var nsWindow and not 0) ObjC.Send(nsWindow, ObjC.Sel("setSharingType:"), 0);
    }

    public void AfterShow()
    {
        var nsApp = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.Send(nsApp, ObjC.Sel("unhideWithoutActivation"));
        if (ObjC.SendBool(nsApp, ObjC.Sel("respondsToSelector:"), ObjC.Sel("activate")))
            ObjC.Send(nsApp, ObjC.Sel("activate"));
        else
            ObjC.Send(nsApp, ObjC.Sel("activateIgnoringOtherApps:"), 1);
    }

    public void AfterStart(bool shown)
    {
        // Avalonia activates the app when it finishes launching, which would take focus at every login.
        if (!shown) HideApp();
    }

    // An accessory app keeps activation after its last window closes, so without this nothing has focus.
    public void AfterHide() => HideApp();

    private static void HideApp() => ObjC.Send(ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication")), ObjC.Sel("hide:"), 0);

    public string? AutoTypeUnavailableReason(ForeignWindow? target)
    {
        if (target is null) return "no previous app to fill";
        if (Ax.AXIsProcessTrusted() == 0)
        {
            Ax.PromptForAccess();
            return "allow Vaultbar under System Settings › Privacy & Security › Accessibility";
        }
        return null;
    }

    public async Task<bool> AutoTypeAsync(ForeignWindow? target, IReadOnlyList<TypeStep> steps, Action hideWindow)
    {
        if (target is null) return false;
        const ulong modifierMask = 0x20000 | 0x40000 | 0x80000 | 0x100000;
        for (var i = 0; i < 100 && (Cg.CGEventSourceFlagsState(1) & modifierMask) != 0; i++)
            await Task.Delay(20);

        hideWindow();
        ObjC.Send(target.Handle, ObjC.Sel("activateWithOptions:"), 2);
        var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace"));
        var ok = false;
        for (var i = 0; i < 50 && !ok; i++)
        {
            await Task.Delay(20);
            var front = ObjC.Send(workspace, ObjC.Sel("frontmostApplication"));
            ok = front != 0 && ObjC.SendInt(front, ObjC.Sel("processIdentifier")) == target.Pid;
        }
        if (!ok) return false;
        await Task.Delay(80);

        await Task.Run(() =>
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case TypeText t:
                        foreach (var rune in t.Text.EnumerateRunes())
                        {
                            if (rune.Value == '\n') Cg.TapKey(36);
                            else if (rune.Value == '\t') Cg.TapKey(48);
                            else Cg.TypeString(rune.ToString());
                        }
                        break;
                    case TypeKey k:
                        Cg.TapKey(k.Key == SpecialKey.Tab ? (ushort)48 : (ushort)36);
                        break;
                    case TypeDelay delay:
                        Thread.Sleep(delay.Milliseconds);
                        break;
                }
                Thread.Sleep(40);
            }
        });
        return true;
    }

    public Task<object?> SetClipboardAsync(string text)
    {
        var pb = Pasteboard();
        ObjC.SendLong(pb, ObjC.Sel("clearContents"));
        ObjC.Send(pb, ObjC.Sel("setString:forType:"), ObjC.NSString(text), ObjC.NSString("public.utf8-plain-text"));
        ObjC.Send(pb, ObjC.Sel("setString:forType:"), ObjC.NSString(""), ObjC.NSString("org.nspasteboard.ConcealedType"));
        return Task.FromResult<object?>(ObjC.SendLong(pb, ObjC.Sel("changeCount")));
    }

    public Task ClearClipboardAsync(object token)
    {
        var pb = Pasteboard();
        if (token is long count && ObjC.SendLong(pb, ObjC.Sel("changeCount")) == count)
            ObjC.SendLong(pb, ObjC.Sel("clearContents"));
        return Task.CompletedTask;
    }

    private nint Pasteboard() => pasteboardName is null
        ? ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard"))
        : ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("pasteboardWithName:"), ObjC.NSString(pasteboardName));

    public void OpenUrl(string url)
    {
        var psi = new ProcessStartInfo("open") { UseShellExecute = false };
        psi.ArgumentList.Add(url);
        Process.Start(psi)?.Dispose();
    }

    public unsafe void WatchSystemLock(Action onLock)
    {
        s_onLock = onLock;
        var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "VaultbarLockObserver", 0);
        var selector = ObjC.Sel("systemLocked:");
        ObjC.class_addMethod(cls, selector, (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnSystemLock, "v@:@");
        ObjC.objc_registerClassPair(cls);
        var observer = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));

        // NSApplication suspends distributed notifications while inactive, which is nearly always for this app,
        // so a screen lock would otherwise only arrive the next time the bar opens.
        const nint deliverImmediately = 4;
        ObjC.Observe(ObjC.Send(ObjC.Class("NSDistributedNotificationCenter"), ObjC.Sel("defaultCenter")),
            ObjC.Sel("addObserver:selector:name:object:suspensionBehavior:"), observer, selector, ObjC.NSString(lockNotification), 0, deliverImmediately);

        var workspaceCenter = ObjC.Send(ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace")), ObjC.Sel("notificationCenter"));
        foreach (var name in new[] { "NSWorkspaceWillSleepNotification", "NSWorkspaceSessionDidResignActiveNotification" })
            ObjC.Observe(workspaceCenter, ObjC.Sel("addObserver:selector:name:object:"), observer, selector, ObjC.NSString(name), 0);
    }

    [UnmanagedCallersOnly]
    private static void OnSystemLock(nint self, nint selector, nint notification)
    {
        try
        {
            s_onLock?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"system lock: {ex}");
        }
    }

    public void Dispose()
    {
        s_onLock = null;
        if (_hotkeyRef != 0) Carbon.UnregisterEventHotKey(_hotkeyRef);
    }

    private static unsafe class Carbon
    {
        private const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

        [StructLayout(LayoutKind.Sequential)]
        public struct EventTypeSpec
        {
            public uint EventClass;
            public uint EventKind;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventHotKeyID
        {
            public uint Signature;
            public uint Id;
        }

        [DllImport(Lib)] public static extern nint GetApplicationEventTarget();
        [DllImport(Lib)] public static extern int InstallEventHandler(nint target, nint handler, uint numTypes, EventTypeSpec* list, nint userData, nint outRef);
        [DllImport(Lib)] public static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, nint target, uint options, out nint outRef);
        [DllImport(Lib)] public static extern int UnregisterEventHotKey(nint hotKey);
    }

    internal static class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib, EntryPoint = "objc_getClass")] public static extern nint Class([MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport(Lib, EntryPoint = "sel_registerName")] public static extern nint Sel([MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector, nint arg);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector, nint arg1, nint arg2);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern int SendInt(nint receiver, nint selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long SendLong(nint receiver, nint selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern byte SendByte(nint receiver, nint selector, nint arg);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern nint SendUtf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void Observe(nint center, nint selector, nint observer, nint action, nint name, nint obj);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void Observe(nint center, nint selector, nint observer, nint action, nint name, nint obj, nint behavior);
        [DllImport(Lib)] public static extern nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPStr)] string name, nint extraBytes);
        [DllImport(Lib)] public static extern void objc_registerClassPair(nint cls);
        [DllImport(Lib)] public static extern byte class_addMethod(nint cls, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPStr)] string types);

        public static bool SendBool(nint receiver, nint selector, nint arg) => SendByte(receiver, selector, arg) != 0;

        public static nint NSString(string s) => SendUtf8(Class("NSString"), Sel("stringWithUTF8String:"), s);

        public static string? ToManaged(nint nsString) =>
            nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));
    }

    private static class Ax
    {
        private const string Lib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Lib)] public static extern byte AXIsProcessTrusted();
        [DllImport(Lib)] private static extern byte AXIsProcessTrustedWithOptions(nint options);
        [DllImport(Lib)] private static extern nint AXUIElementCreateApplication(int pid);
        [DllImport(Lib)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
        [DllImport(Cf)] public static extern void CFRelease(nint obj);

        public static void PromptForAccess()
        {
            var yes = ObjC.Send(ObjC.Class("NSNumber"), ObjC.Sel("numberWithBool:"), 1);
            var options = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Sel("dictionaryWithObject:forKey:"), yes, ObjC.NSString("AXTrustedCheckOptionPrompt"));
            AXIsProcessTrustedWithOptions(options);
        }

        public static string? FocusedWindowTitle(int pid)
        {
            if (AXIsProcessTrusted() == 0) return null;
            var app = AXUIElementCreateApplication(pid);
            if (app == 0) return null;
            try
            {
                if (AXUIElementCopyAttributeValue(app, ObjC.NSString("AXFocusedWindow"), out var window) != 0 || window == 0) return null;
                try
                {
                    if (AXUIElementCopyAttributeValue(window, ObjC.NSString("AXTitle"), out var title) != 0 || title == 0) return null;
                    try
                    {
                        return ObjC.ToManaged(title);
                    }
                    finally
                    {
                        CFRelease(title);
                    }
                }
                finally
                {
                    CFRelease(window);
                }
            }
            finally
            {
                CFRelease(app);
            }
        }
    }

    private static unsafe class Cg
    {
        private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(Lib)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, byte keyDown);
        [DllImport(Lib)] private static extern void CGEventKeyboardSetUnicodeString(nint ev, nuint length, char* text);
        [DllImport(Lib)] private static extern void CGEventSetFlags(nint ev, ulong flags);
        [DllImport(Lib)] private static extern void CGEventPost(uint tap, nint ev);
        [DllImport(Lib)] public static extern ulong CGEventSourceFlagsState(int stateId);

        public static void TypeString(string s)
        {
            fixed (char* p = s)
            {
                foreach (byte down in new byte[] { 1, 0 })
                {
                    var ev = CGEventCreateKeyboardEvent(0, 0, down);
                    CGEventSetFlags(ev, 0);
                    CGEventKeyboardSetUnicodeString(ev, (nuint)s.Length, p);
                    CGEventPost(0, ev);
                    Ax.CFRelease(ev);
                }
            }
            Thread.Sleep(4);
        }

        public static void TapKey(ushort virtualKey)
        {
            foreach (byte down in new byte[] { 1, 0 })
            {
                var ev = CGEventCreateKeyboardEvent(0, virtualKey, down);
                CGEventSetFlags(ev, 0);
                CGEventPost(0, ev);
                Ax.CFRelease(ev);
            }
            Thread.Sleep(4);
        }
    }
}
