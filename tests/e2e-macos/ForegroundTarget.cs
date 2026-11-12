using System.Runtime.InteropServices;
using ObjC = Vaultbar.Platform.MacPlatform.ObjC;

namespace Vaultbar.MacE2E;

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
