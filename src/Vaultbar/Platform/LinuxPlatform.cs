using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

#pragma warning disable CS0618

namespace Vaultbar.Platform;

[SupportedOSPlatform("linux")]
internal sealed class LinuxPlatform : IPlatform
{
    private static volatile bool s_grabFailed;
    private static nint s_grabDisplay;

    private readonly bool _wayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
                                     || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    private readonly bool _hasX = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
    private readonly object _xGate = new();
    private nint _queryDisplay;
    private IClipboard? _clipboard;
    private Window? _window;
    private volatile bool _stop;

    public unsafe string? StartHotkey(Hotkey hk, Action onPressed)
    {
        if (_wayland)
            return "Wayland does not let apps grab global shortcuts. Run `vaultbar shortcut` (GNOME) or bind `vaultbar toggle` in your desktop's keyboard settings.";
        if (!_hasX) return "No X11 display available for a global hotkey.";

        var dpy = X11.XOpenDisplay(0);
        if (dpy == 0) return "Cannot open the X display.";
        var keysym = X11.XStringToKeysym(hk.Key == "Space" ? "space" : hk.Key.Length == 1 ? hk.Key.ToLowerInvariant() : hk.Key);
        var keycode = keysym == 0 ? (byte)0 : X11.XKeysymToKeycode(dpy, keysym);
        if (keycode == 0)
        {
            X11.XCloseDisplay(dpy);
            return $"Unsupported hotkey key {hk.Key}";
        }

        uint mods = (hk.Shift ? 1u : 0) | (hk.Ctrl ? 4u : 0) | (hk.Alt ? 8u : 0) | (hk.Super ? 64u : 0);
        var root = X11.XDefaultRootWindow(dpy);
        s_grabFailed = false;
        s_grabDisplay = dpy;
        var previous = X11.XSetErrorHandler(&OnXError);
        foreach (var lockMods in new uint[] { 0, 2, 16, 18 })
            X11.XGrabKey(dpy, keycode, mods | lockMods, root, 1, 1, 1);
        X11.XSync(dpy, 0);
        X11.XSetErrorHandler((delegate* unmanaged<nint, nint, int>)previous);
        if (s_grabFailed)
        {
            X11.XCloseDisplay(dpy);
            return $"{hk} is already used by another application. Change \"hotkey\" in {Config.FilePath}.";
        }

        new Thread(() => HotkeyLoop(dpy, onPressed)) { IsBackground = true, Name = "vaultbar-hotkey" }.Start();
        return null;
    }

    [UnmanagedCallersOnly]
    private static int OnXError(nint display, nint errorEvent)
    {
        const int badAccess = 10;
        if (display == s_grabDisplay && Marshal.ReadByte(errorEvent, 32) == badAccess) s_grabFailed = true;
        return 0;
    }

    private unsafe void HotkeyLoop(nint dpy, Action onPressed)
    {
        var fd = X11.XConnectionNumber(dpy);
        var ev = stackalloc byte[192];
        long last = 0;
        while (!_stop)
        {
            while (X11.XPending(dpy) > 0)
            {
                X11.XNextEvent(dpy, ev);
                const int keyPress = 2;
                if (*(int*)ev != keyPress) continue;
                var now = Environment.TickCount64;
                if (now - last < 300) continue;
                last = now;
                try
                {
                    onPressed();
                }
                catch (Exception ex)
                {
                    Log.Write($"hotkey: {Log.Shape(ex)}");
                }
            }
            var pfd = new Libc.PollFd { Fd = fd, Events = 1 };
            Libc.poll(&pfd, 1, 500);
        }
        X11.XCloseDisplay(dpy);
    }

    private nint QueryDisplay()
    {
        if (_queryDisplay == 0 && _hasX) _queryDisplay = X11.XOpenDisplay(0);
        return _queryDisplay;
    }

    private nuint OurWindow => (nuint)(_window?.TryGetPlatformHandle()?.Handle ?? 0);

    public ForeignWindow? CaptureForeground()
    {
        if (_wayland || !_hasX) return null;
        lock (_xGate)
        {
            var d = QueryDisplay();
            if (d == 0) return null;
            var active = X11.ActiveWindow(d);
            if (active == 0 || active == OurWindow) return null;
            var title = X11.StringProperty(d, active, "_NET_WM_NAME", "UTF8_STRING") ?? X11.StringProperty(d, active, "WM_NAME", null) ?? "";
            var wmClass = X11.StringProperty(d, active, "WM_CLASS", null)?.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var app = wmClass is { Length: > 1 } ? wmClass[1] : wmClass?.FirstOrDefault() ?? "";
            return new ForeignWindow((nint)active, title, app);
        }
    }

    public PixelPoint? AnchorPoint(ForeignWindow? target) => null;

    public void AttachWindow(Window window)
    {
        _window = window;
        _clipboard = window.Clipboard;
    }

    public void AfterShow()
    {
        if (_wayland || !_hasX) return;
        lock (_xGate)
        {
            var d = QueryDisplay();
            var ours = OurWindow;
            if (d != 0 && ours != 0) X11.Activate(d, ours);
        }
    }

    public string? AutoTypeUnavailableReason(ForeignWindow? target)
    {
        if (_wayland)
            return (Which("wtype") ?? Which("ydotool")) is null ? "auto-type on Wayland needs wtype or ydotool installed" : null;
        return target is null ? "no previous window to fill" : null;
    }

    public async Task<AutoTypeResult> AutoTypeAsync(ForeignWindow? target, IReadOnlyList<TypeStep> steps, Action hideWindow)
    {
        if (_wayland) return await WaylandTypeAsync(steps, hideWindow);
        if (target is null) return AutoTypeResult.TargetNotFocused;

        var d = X11.XOpenDisplay(0);
        if (d == 0) return AutoTypeResult.TargetNotFocused;
        try
        {
            await Task.Run(() => X11.WaitForKeysReleased(d, 2000));
            hideWindow();
            await Task.Delay(40);
            X11.Activate(d, (nuint)target.Handle);
            var active = await Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    if (X11.ActiveWindow(d) == (nuint)target.Handle) return true;
                    Thread.Sleep(20);
                }
                return false;
            });
            if (!active)
            {
                Log.Write("autotype: the previous window did not become active");
                return AutoTypeResult.TargetNotFocused;
            }
            await Task.Delay(80);
            return await Task.Run(() =>
            {
                using var typer = new X11.Typer(d);
                var result = AutoTyper.Run(steps, typer, () => X11.ActiveWindow(d) == (nuint)target.Handle);
                if (typer.Skipped > 0) Log.Write($"autotype: {typer.Skipped} characters could not be typed on this keyboard layout");
                return result;
            });
        }
        finally
        {
            X11.XCloseDisplay(d);
        }
    }

    // Wayland lets no app see which window has focus, so unlike X11 nothing can stop typing when focus moves.
    private static async Task<AutoTypeResult> WaylandTypeAsync(IReadOnlyList<TypeStep> steps, Action hideWindow)
    {
        var wtype = Which("wtype");
        var ydotool = wtype is null ? Which("ydotool") : null;
        if (wtype is null && ydotool is null) return AutoTypeResult.TargetNotFocused;

        hideWindow();
        await Task.Delay(300);
        foreach (var step in steps)
        {
            switch (step)
            {
                case TypeText t when wtype != null:
                    await RunAsync(wtype, ["-"], t.Text);
                    break;
                case TypeText t:
                    await RunAsync(ydotool!, ["type", "--file", "-"], t.Text);
                    break;
                case TypeKey k when wtype != null:
                    await RunAsync(wtype, ["-k", k.Key == SpecialKey.Tab ? "Tab" : "Return"], null);
                    break;
                case TypeKey k:
                    var code = k.Key == SpecialKey.Tab ? "15" : "28";
                    await RunAsync(ydotool!, ["key", $"{code}:1", $"{code}:0"], null);
                    break;
                case TypeDelay delay:
                    await Task.Delay(delay.Milliseconds);
                    break;
            }
            await Task.Delay(40);
        }
        return AutoTypeResult.Typed;
    }

    private static async Task RunAsync(string exe, string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardInput = stdin != null, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin != null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        await p.WaitForExitAsync();
    }

    internal static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir, name))
        .FirstOrDefault(File.Exists);

    public async Task<object?> SetClipboardAsync(string text)
    {
        if (_clipboard is null) return null;
        try
        {
            var data = new DataObject();
            data.Set(DataFormats.Text, text);
            data.Set("x-kde-passwordManagerHint", Encoding.UTF8.GetBytes("secret"));
            await _clipboard.SetDataObjectAsync(data);
        }
        catch (Exception ex)
        {
            Log.Write($"clipboard: {Log.Shape(ex)}");
            await _clipboard.SetTextAsync(text);
        }
        return new ClipboardFingerprint(text);
    }

    public async Task ClearClipboardAsync(object token)
    {
        if (_clipboard is null || token is not ClipboardFingerprint fingerprint) return;
        if (fingerprint.Matches(await _clipboard.GetTextAsync())) await _clipboard.ClearAsync();
    }

    public void OpenUrl(Vault.WebAddress address)
    {
        var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(address.Value);
        Process.Start(psi)?.Dispose();
    }

    public void WatchSystemLock(Action onLock) => _ = LinuxSessionLock.WatchAsync(onLock);

    public void Dispose()
    {
        _stop = true;
        lock (_xGate)
        {
            if (_queryDisplay != 0) X11.XCloseDisplay(_queryDisplay);
            _queryDisplay = 0;
        }
    }

    private static unsafe class Libc
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true)]
        public static extern int poll(PollFd* fds, nuint count, int timeout);
    }

    internal static unsafe class X11
    {
        private const string Lib = "libX11.so.6";
        private const string Tst = "libXtst.so.6";
        public const nuint XK_Tab = 0xff09, XK_Return = 0xff0d, XK_Shift_L = 0xffe1;

        [DllImport(Lib)] public static extern nint XOpenDisplay(nint name);
        [DllImport(Lib)] public static extern int XCloseDisplay(nint d);
        [DllImport(Lib)] public static extern nuint XDefaultRootWindow(nint d);
        [DllImport(Lib)] public static extern nuint XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string s);
        [DllImport(Lib)] public static extern byte XKeysymToKeycode(nint d, nuint keysym);
        [DllImport(Lib)] public static extern int XGrabKey(nint d, int keycode, uint mods, nuint window, int ownerEvents, int pointerMode, int keyboardMode);
        [DllImport(Lib)] public static extern int XPending(nint d);
        [DllImport(Lib)] public static extern int XNextEvent(nint d, byte* ev);
        [DllImport(Lib)] public static extern int XConnectionNumber(nint d);
        [DllImport(Lib)] public static extern int XSync(nint d, int discard);
        [DllImport(Lib)] public static extern int XFlush(nint d);
        [DllImport(Lib)] public static extern nint XSetErrorHandler(delegate* unmanaged<nint, nint, int> handler);
        [DllImport(Lib)] public static extern nuint XInternAtom(nint d, [MarshalAs(UnmanagedType.LPStr)] string name, int onlyIfExists);
        [DllImport(Lib)] public static extern int XGetWindowProperty(nint d, nuint w, nuint property, nint offset, nint length, int delete, nuint reqType, out nuint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out nint data);
        [DllImport(Lib)] public static extern int XFree(nint data);
        [DllImport(Lib)] public static extern int XSendEvent(nint d, nuint w, int propagate, nint mask, byte* ev);
        [DllImport(Lib)] public static extern int XQueryKeymap(nint d, byte* keys);
        [DllImport(Lib)] public static extern int XDisplayKeycodes(nint d, out int min, out int max);
        [DllImport(Lib)] public static extern nuint* XGetKeyboardMapping(nint d, uint first, int count, out int perKeycode);
        [DllImport(Lib)] public static extern int XChangeKeyboardMapping(nint d, int first, int perKeycode, nuint* keysyms, int count);
        [DllImport(Tst)] public static extern int XTestFakeKeyEvent(nint d, uint keycode, int isPress, nuint delay);

        public static nuint ActiveWindow(nint d)
        {
            const nuint xaWindow = 33;
            var atom = XInternAtom(d, "_NET_ACTIVE_WINDOW", 0);
            if (XGetWindowProperty(d, XDefaultRootWindow(d), atom, 0, 1, 0, xaWindow, out _, out var format, out var n, out _, out var data) != 0 || data == 0)
                return 0;
            try
            {
                return n > 0 && format == 32 ? *(nuint*)data : 0;
            }
            finally
            {
                XFree(data);
            }
        }

        public static string? StringProperty(nint d, nuint w, string property, string? type)
        {
            var atom = XInternAtom(d, property, 1);
            if (atom == 0) return null;
            var reqType = type is null ? 0 : XInternAtom(d, type, 0);
            if (XGetWindowProperty(d, w, atom, 0, 1024, 0, reqType, out _, out var format, out var n, out _, out var data) != 0 || data == 0)
                return null;
            try
            {
                return n > 0 && format == 8 ? Marshal.PtrToStringUTF8(data, (int)n) : null;
            }
            finally
            {
                XFree(data);
            }
        }

        public static void Activate(nint d, nuint window)
        {
            var ev = stackalloc byte[192];
            new Span<byte>(ev, 192).Clear();
            *(int*)ev = 33;
            *(nint*)(ev + 24) = d;
            *(nuint*)(ev + 32) = window;
            *(nuint*)(ev + 40) = XInternAtom(d, "_NET_ACTIVE_WINDOW", 0);
            *(int*)(ev + 48) = 32;
            *(nint*)(ev + 56) = 2;
            const nint substructureRedirect = 1 << 20, substructureNotify = 1 << 19;
            XSendEvent(d, XDefaultRootWindow(d), 0, substructureRedirect | substructureNotify, ev);
            XFlush(d);
        }

        public static void WaitForKeysReleased(nint d, int timeoutMs)
        {
            var keys = stackalloc byte[32];
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                XQueryKeymap(d, keys);
                var anyDown = false;
                for (var i = 0; i < 32; i++) anyDown |= keys[i] != 0;
                if (!anyDown) return;
                Thread.Sleep(20);
            }
        }

        /// <summary>Types arbitrary Unicode via XTest, borrowing an unused keycode for symbols not on the layout.</summary>
        public sealed class Typer : IKeyboard, IDisposable
        {
            private readonly nint d;
            private readonly byte _shift;
            private readonly Dictionary<nuint, (byte Keycode, bool Shift)> _keys = new();
            private readonly int _scratch = -1;
            private nuint _scratchKeysym;

            public int Skipped { get; private set; }

            public Typer(nint display)
            {
                d = display;
                XDisplayKeycodes(d, out var min, out var max);
                var map = XGetKeyboardMapping(d, (uint)min, max - min + 1, out var per);
                if (map == null) return;
                try
                {
                    for (var kc = max; kc >= min; kc--)
                    {
                        var row = map + (kc - min) * per;
                        var empty = true;
                        for (var j = 0; j < per; j++) empty &= row[j] == 0;
                        if (empty && _scratch < 0) _scratch = kc;
                    }
                    for (var level = 0; level < Math.Min(per, 2); level++)
                    for (var kc = min; kc <= max; kc++)
                    {
                        var sym = map[(kc - min) * per + level];
                        if (sym != 0) _keys.TryAdd(sym, ((byte)kc, level == 1));
                    }
                }
                finally
                {
                    XFree((nint)map);
                }
                _shift = _keys.TryGetValue(XK_Shift_L, out var s) ? s.Keycode : XKeysymToKeycode(d, XK_Shift_L);
            }

            public void Press(SpecialKey key) => Tap(key == SpecialKey.Tab ? XK_Tab : XK_Return);

            public void Type(Rune rune)
            {
                var v = rune.Value;
                nuint keysym = v switch
                {
                    (>= 0x20 and <= 0x7e) or (>= 0xa0 and <= 0xff) => (nuint)v,
                    _ => (nuint)(0x01000000 | v),
                };
                Tap(keysym);
            }

            public void Tap(nuint keysym)
            {
                byte keycode;
                var shift = false;
                if (_keys.TryGetValue(keysym, out var key)) (keycode, shift) = key;
                else keycode = Remap(keysym);
                if (keycode == 0) {
                    Skipped++;
                    return;
                }

                if (shift) XTestFakeKeyEvent(d, _shift, 1, 0);
                XTestFakeKeyEvent(d, keycode, 1, 0);
                XTestFakeKeyEvent(d, keycode, 0, 0);
                if (shift) XTestFakeKeyEvent(d, _shift, 0, 0);
                XSync(d, 0);
                Thread.Sleep(4);
            }

            private byte Remap(nuint keysym)
            {
                if (_scratch < 0) return 0;
                if (_scratchKeysym != keysym)
                {
                    var syms = stackalloc nuint[2] { keysym, keysym };
                    XChangeKeyboardMapping(d, _scratch, 2, syms, 1);
                    XSync(d, 0);
                    Thread.Sleep(30);
                    _scratchKeysym = keysym;
                }
                return (byte)_scratch;
            }

            public void Dispose()
            {
                if (_scratchKeysym == 0) return;
                nuint none = 0;
                XChangeKeyboardMapping(d, _scratch, 1, &none, 1);
                XSync(d, 0);
            }
        }
    }
}
