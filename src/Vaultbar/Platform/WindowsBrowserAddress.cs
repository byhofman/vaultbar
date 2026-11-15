using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vaultbar.Platform;

/// <summary>
/// Reads the address of the page a browser window shows, through UI Automation. It asks for the first on-screen
/// document in the window and reads its value, which Chromium and Firefox set to the document's URL. That is the address
/// that was loaded, not the text in the address bar, and a page cannot set it: a page can only choose its title.
/// Hidden tabs are off screen, so another tab's address is never read; frames inside the page are never read either, and a
/// window showing two pages (a side panel next to the tab) has no single address, so it counts as unknown.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WindowsBrowserAddress
{
    private const int UIA_ControlTypePropertyId = 30003, UIA_IsOffscreenPropertyId = 30022, UIA_ValueValuePropertyId = 30045;
    private const int UIA_LegacyIAccessibleValuePropertyId = 30093, UIA_FrameworkIdPropertyId = 30024, UIA_DocumentControlTypeId = 50030, TreeScope_Descendants = 4;
    private const ushort VT_I4 = 3, VT_BSTR = 8, VT_BOOL = 11;

    // IUIAutomation and IUIAutomationElement vtable slots, IUnknown's three first (UIAutomationClient.h).
    private const int ElementFromHandle = 6, CreatePropertyCondition = 23, CreateAndCondition = 25;
    private const int FindFirst = 5, FindAll = 6, GetCurrentPropertyValue = 10, ArrayLength = 3;

    private static readonly Guid ClsidCuiAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IidIuiAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    /// <summary>How long a hung browser may hold up the bar before the address counts as unknown.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long an app that is not a known browser may take to show whether it has a web page at all.</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMilliseconds(300);

    /// <param name="knownBrowser">False for any other app: one quick look, to catch browsers and web views not on the list.</param>
    public static string? Read(nint hwnd, bool knownBrowser = true)
    {
        // UI Automation clients belong on a multithreaded apartment, never on the UI thread that hosts the bar.
        var read = Task.Run(() => ReadWithRetries(hwnd, knownBrowser));
        return read.Wait(knownBrowser ? Budget : ProbeBudget) ? read.Result : null;
    }

    private static string? ReadWithRetries(nint hwnd, bool knownBrowser)
    {
        CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        // Chromium builds its accessibility tree only once a client asks, so the first look can find an empty document.
        for (var attempt = 0; attempt < (knownBrowser ? 6 : 1); attempt++)
        {
            try
            {
                if (ReadOnce(hwnd, knownBrowser) is { Length: > 0 } address) return address;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                Log.Write($"browser address: {Log.Shape(ex)}");
                return null;
            }
            Thread.Sleep(80);
        }
        return null;
    }

    // Web content: Chromium (WebView2 and Electron too) and Firefox. In any other app a Document holds its text, which
    // a file could fill with a web address.
    private static readonly string[] WebFrameworks = ["Chrome", "Gecko"];

    private static string? ReadOnce(nint hwnd, bool knownBrowser)
    {
        var clsid = ClsidCuiAutomation;
        var iid = IidIuiAutomation;
        Check(CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, out var automation));
        nint window = 0, isDocument = 0, onScreen = 0, both = 0, document = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, ElementFromHandle))(automation, hwnd, &window));
            if (window == 0) return null;
            Check(Condition(automation, UIA_ControlTypePropertyId, new Variant { Type = VT_I4, Int = UIA_DocumentControlTypeId }, out isDocument));
            Check(Condition(automation, UIA_IsOffscreenPropertyId, new Variant { Type = VT_BOOL, Int = 0 }, out onScreen));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(automation, CreateAndCondition))(automation, isDocument, onScreen, &both));
            Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Slot(window, FindFirst))(window, TreeScope_Descendants, both, &document));
            if (document == 0) return null;
            if (!knownBrowser && !WebFrameworks.Contains(StringProperty(document, UIA_FrameworkIdPropertyId))) return null;
            if (Count(window, both) != Count(document, both) + 1) return null;
            return StringProperty(document, UIA_ValueValuePropertyId) is { Length: > 0 } value
                ? value
                : StringProperty(document, UIA_LegacyIAccessibleValuePropertyId);
        }
        finally
        {
            foreach (var p in new[] { document, both, onScreen, isDocument, window, automation })
                if (p != 0) Marshal.Release(p);
        }
    }

    /// <summary>How many elements under this one meet the condition.</summary>
    private static int Count(nint element, nint condition)
    {
        nint array = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Slot(element, FindAll))(element, TreeScope_Descendants, condition, &array));
        if (array == 0) return 0;
        try
        {
            int length;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(array, ArrayLength))(array, &length));
            return length;
        }
        finally
        {
            Marshal.Release(array);
        }
    }

    private static int Condition(nint automation, int property, Variant value, out nint condition)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, Variant, nint*, int>)Slot(automation, CreatePropertyCondition))(automation, property, value, &result);
        condition = result;
        return hr;
    }

    private static string? StringProperty(nint element, int property)
    {
        Variant value = default;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)Slot(element, GetCurrentPropertyValue))(element, property, &value);
        try
        {
            return hr >= 0 && value.Type == VT_BSTR && value.Pointer != 0 ? Marshal.PtrToStringBSTR(value.Pointer) : null;
        }
        finally
        {
            VariantClear(&value);
        }
    }

    private static nint Slot(nint comObject, int index) => (*(nint**)comObject)[index];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>A 64-bit VARIANT: Vaultbar ships x64 and arm64 builds only.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public int Int;
        [FieldOffset(8)] public nint Pointer;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);
    [DllImport("oleaut32.dll")] private static extern int VariantClear(Variant* value);
}
