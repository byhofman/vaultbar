using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Win32;

namespace Vaultbar.Platform;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatform : IPlatform
{
    private Thread? _hotkeyThread;
    private uint _hotkeyThreadId;
    private Window? _window;

    public string? StartHotkey(Hotkey hk, Action onPressed)
    {
        var vk = VirtualKey(hk.Key);
        if (vk == 0) return $"Unsupported hotkey key {hk.Key}";
        var mods = Native.MOD_NOREPEAT
                   | (hk.Ctrl ? Native.MOD_CONTROL : 0)
                   | (hk.Shift ? Native.MOD_SHIFT : 0)
                   | (hk.Alt ? Native.MOD_ALT : 0)
                   | (hk.Super ? Native.MOD_WIN : 0);

        string? error = null;
        using var ready = new ManualResetEventSlim();
        _hotkeyThread = new Thread(() =>
        {
            _hotkeyThreadId = Native.GetCurrentThreadId();
            Native.PeekMessageW(out _, 0, 0, 0, 0);
            if (Native.RegisterHotKey(0, 1, mods, vk) == 0)
            {
                error = $"{hk} is already used by another application. Change \"hotkey\" in {Config.FilePath}.";
                ready.Set();
                return;
            }
            ready.Set();
            while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message != Native.WM_HOTKEY) continue;
                try
                {
                    onPressed();
                }
                catch (Exception ex)
                {
                    Log.Write($"hotkey: {ex}");
                }
            }
            Native.UnregisterHotKey(0, 1);
        })
        { IsBackground = true, Name = "vaultbar-hotkey" };
        _hotkeyThread.Start();
        ready.Wait(3000);
        return error;
    }

    private static uint VirtualKey(string key) => key switch
    {
        "Space" => 0x20,
        { Length: 1 } k when char.IsAsciiLetterOrDigit(k[0]) => k[0],
        ['F', ..] when int.TryParse(key[1..], out var f) => (uint)(0x70 + f - 1),
        _ => 0,
    };

    public unsafe ForeignWindow? CaptureForeground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return null;

        var buffer = stackalloc char[512];
        var len = Native.GetWindowTextW(hwnd, buffer, 512);
        var title = new string(buffer, 0, Math.Max(0, len));
        string app;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            app = p.ProcessName;
        }
        catch
        {
            app = "";
        }
        return new ForeignWindow(hwnd, title, app, (int)pid);
    }

    public PixelPoint? AnchorPoint(ForeignWindow? target)
    {
        if (target != null && Native.GetWindowRect(target.Handle, out var r) != 0 && r.Right > r.Left)
            return new PixelPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        return Native.GetCursorPos(out var p) != 0 ? new PixelPoint(p.X, p.Y) : null;
    }

    public void AttachWindow(Window window) => _window = window;

    public void AfterShow()
    {
        var hwnd = _window?.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd != 0) Native.SetForegroundWindow(hwnd);
    }

    public string? AutoTypeUnavailableReason(ForeignWindow? target) =>
        target is null ? "no previous window to fill" : null;

    public async Task<bool> AutoTypeAsync(ForeignWindow? target, IReadOnlyList<TypeStep> steps, Action hideWindow)
    {
        if (target is null) return false;
        await WaitForKeysReleased();
        hideWindow();
        await Task.Delay(30);

        for (var i = 0; i < 50 && Native.GetForegroundWindow() != target.Handle; i++)
        {
            if (i % 10 == 0) Native.SetForegroundWindow(target.Handle);
            await Task.Delay(20);
        }
        if (Native.GetForegroundWindow() != target.Handle) return false;
        await Task.Delay(80);

        await Task.Run(() =>
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case TypeText t:
                        SendText(t.Text);
                        break;
                    case TypeKey k:
                        SendVirtualKey(k.Key == SpecialKey.Tab ? Native.VK_TAB : Native.VK_RETURN);
                        break;
                    case TypeDelay d:
                        Thread.Sleep(d.Milliseconds);
                        break;
                }
                Thread.Sleep(40);
            }
        });
        return true;
    }

    private static async Task WaitForKeysReleased()
    {
        int[] keys = [Native.VK_SHIFT, Native.VK_CONTROL, Native.VK_MENU, Native.VK_LWIN, Native.VK_RWIN, Native.VK_RETURN];
        for (var i = 0; i < 100 && keys.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0); i++)
            await Task.Delay(20);
    }

    private static unsafe void SendText(string text)
    {
        var inputs = stackalloc Native.INPUT[2];
        foreach (var c in text)
        {
            if (c == '\n') SendVirtualKey(Native.VK_RETURN);
            else if (c == '\t') SendVirtualKey(Native.VK_TAB);
            else if (c != '\r')
            {
                inputs[0] = Native.KeyInput(0, c, Native.KEYEVENTF_UNICODE);
                inputs[1] = Native.KeyInput(0, c, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP);
                Native.SendInput(2, inputs, sizeof(Native.INPUT));
            }
            Thread.Sleep(4);
        }
    }

    private static unsafe void SendVirtualKey(ushort vk)
    {
        var inputs = stackalloc Native.INPUT[2];
        inputs[0] = Native.KeyInput(vk, 0, 0);
        inputs[1] = Native.KeyInput(vk, 0, Native.KEYEVENTF_KEYUP);
        Native.SendInput(2, inputs, sizeof(Native.INPUT));
    }

    public unsafe Task<object?> SetClipboardAsync(string text)
    {
        var owner = _window?.TryGetPlatformHandle()?.Handle ?? 0;
        if (!OpenClipboard(owner)) return Task.FromResult<object?>(null);
        try
        {
            Native.EmptyClipboard();
            var chars = (text + "\0").ToCharArray();
            fixed (char* p = chars) SetData(Native.CF_UNICODETEXT, (byte*)p, chars.Length * 2);
            uint zero = 0;
            SetData(Native.RegisterClipboardFormatW("CanIncludeInClipboardHistory"), (byte*)&zero, 4);
            SetData(Native.RegisterClipboardFormatW("CanUploadToCloudClipboard"), (byte*)&zero, 4);
            SetData(Native.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), (byte*)&zero, 1);
            Array.Clear(chars);
        }
        finally
        {
            Native.CloseClipboard();
        }
        return Task.FromResult<object?>(Native.GetClipboardSequenceNumber());
    }

    public Task ClearClipboardAsync(object token)
    {
        if (token is uint seq && Native.GetClipboardSequenceNumber() == seq)
        {
            var owner = _window?.TryGetPlatformHandle()?.Handle ?? 0;
            if (OpenClipboard(owner))
            {
                Native.EmptyClipboard();
                Native.CloseClipboard();
            }
        }
        return Task.CompletedTask;
    }

    private static bool OpenClipboard(nint owner)
    {
        for (var i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(owner) != 0) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static unsafe void SetData(uint format, byte* data, int length)
    {
        var mem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)length);
        if (mem == 0) return;
        var dst = (byte*)Native.GlobalLock(mem);
        Buffer.MemoryCopy(data, dst, length, length);
        Native.GlobalUnlock(mem);
        if (Native.SetClipboardData(format, mem) == 0) Native.GlobalFree(mem);
    }

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    public void WatchSystemLock(Action onLock)
    {
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                onLock();
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend) onLock();
        };
    }

    public static void AllowForegroundForOtherProcess() => Native.AllowSetForegroundWindow(-1);

    public static void AttachParentConsole() => Native.AttachConsole(-1);

    public void Dispose()
    {
        if (_hotkeyThreadId != 0) Native.PostThreadMessageW(_hotkeyThreadId, Native.WM_QUIT, 0, 0);
    }

    private static unsafe class Native
    {
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        public const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        public const ushort VK_TAB = 0x09, VK_RETURN = 0x0D;
        public const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        public const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public nint hwnd;
            public uint message;
            public nint wParam, lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        public static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
        {
            type = 1,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
        };

        [DllImport("user32.dll")] public static extern int RegisterHotKey(nint hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern int UnregisterHotKey(nint hWnd, int id);
        [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hWnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern int PeekMessageW(out MSG msg, nint hWnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern int PostThreadMessageW(uint threadId, uint msg, nint w, nint l);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern int SetForegroundWindow(nint hWnd);
        [DllImport("user32.dll")] public static extern int AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] public static extern int GetWindowTextW(nint hWnd, char* text, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern int GetWindowRect(nint hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT* inputs, int size);
        [DllImport("user32.dll")] public static extern int OpenClipboard(nint owner);
        [DllImport("user32.dll")] public static extern int CloseClipboard();
        [DllImport("user32.dll")] public static extern int EmptyClipboard();
        [DllImport("user32.dll")] public static extern nint SetClipboardData(uint format, nint mem);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormatW(string name);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("kernel32.dll")] public static extern nint GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] public static extern nint GlobalLock(nint mem);
        [DllImport("kernel32.dll")] public static extern int GlobalUnlock(nint mem);
        [DllImport("kernel32.dll")] public static extern nint GlobalFree(nint mem);
        [DllImport("kernel32.dll")] public static extern int AttachConsole(int pid);
    }
}
