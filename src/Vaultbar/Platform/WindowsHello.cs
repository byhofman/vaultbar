using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Vaultbar.Vault;

namespace Vaultbar.Platform;

[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsHello : IQuickUnlockProvider
{
    private const string KeyName = "vaultbar";
    private const string DialogClass = "Credential Dialog Xaml Host";

    private static readonly Guid IidKeyCredentialManagerStatics = new("6aac468b-0ef1-4ce0-8290-4106da6a63b5");
    private static readonly Guid IidCryptographicBufferStatics = new("320b7e22-3cb0-4cdf-8663-1d28910065eb");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    private Task<bool>? _available;

    public string Name => "Windows Hello";

    public Task<bool> IsAvailableAsync() => _available ??= RunAsync(() =>
    {
        try
        {
            using var manager = Factory("Windows.Security.Credentials.KeyCredentialManager", IidKeyCredentialManagerStatics);
            nint op;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(manager, 6))(manager, &op));
            using var operation = new ComPtr(op);
            Wait(operation, foreground: false);
            byte supported;
            Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(operation, 8))(operation, &supported));
            return supported != 0;
        }
        catch (Exception ex)
        {
            Log.Write($"windows hello: {ex.Message}");
            return false;
        }
    });

    public Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(byte[] challenge, bool createKey) => RunAsync<(QuickUnlockStatus, byte[]?)>(() =>
    {
        try
        {
            using var manager = Factory("Windows.Security.Credentials.KeyCredentialManager", IidKeyCredentialManagerStatics);
            using var name = new HString(KeyName);
            nint op;
            Check(createKey
                ? ((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(manager, 8))(manager, name, 0, &op)
                : ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(manager, 9))(manager, name, &op));
            using var retrieval = Result(new ComPtr(op), foreground: createKey);
            var status = Status(retrieval);
            if (status != QuickUnlockStatus.Success) return (status, null);

            nint cred;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(retrieval, 6))(retrieval, &cred));
            using var credential = new ComPtr(cred);
            using var buffers = Factory("Windows.Security.Cryptography.CryptographicBuffer", IidCryptographicBufferStatics);
            nint buf;
            fixed (byte* p = challenge)
                Check(((delegate* unmanaged[Stdcall]<nint, uint, byte*, nint*, int>)Slot(buffers, 9))(buffers, (uint)challenge.Length, p, &buf));
            using var input = new ComPtr(buf);

            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(credential, 9))(credential, input, &op));
            using var signed = Result(new ComPtr(op), foreground: true);
            status = Status(signed);
            if (status != QuickUnlockStatus.Success) return (status, null);

            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(signed, 6))(signed, &buf));
            using var output = new ComPtr(buf);
            uint length;
            byte* data;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, byte**, int>)Slot(buffers, 10))(buffers, output, &length, &data));
            try
            {
                return (QuickUnlockStatus.Success, new ReadOnlySpan<byte>(data, (int)length).ToArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(new Span<byte>(data, (int)length));
                Marshal.FreeCoTaskMem((nint)data);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"windows hello: {ex.Message}");
            return (QuickUnlockStatus.Failed, null);
        }
    });

    public Task DeleteKeyAsync() => RunAsync(() =>
    {
        try
        {
            using var manager = Factory("Windows.Security.Credentials.KeyCredentialManager", IidKeyCredentialManagerStatics);
            using var name = new HString(KeyName);
            nint op;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(manager, 10))(manager, name, &op));
            using var action = new ComPtr(op);
            Wait(action, foreground: false);
        }
        catch (Exception ex)
        {
            Log.Write($"windows hello: {ex.Message}");
        }
        return true;
    });

    private static QuickUnlockStatus Status(ComPtr result)
    {
        int status;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(result, 7))(result, &status));
        return status switch
        {
            0 => QuickUnlockStatus.Success,
            2 => QuickUnlockStatus.Unavailable,
            3 or 4 => QuickUnlockStatus.Canceled,
            _ => QuickUnlockStatus.Failed,
        };
    }

    private static ComPtr Result(ComPtr operation, bool foreground)
    {
        using (operation)
        {
            Wait(operation, foreground);
            nint result;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, 8))(operation, &result));
            return new ComPtr(result);
        }
    }

    private static void Wait(ComPtr operation, bool foreground)
    {
        var iid = IidAsyncInfo;
        Check(Marshal.QueryInterface(operation, in iid, out var raw));
        using var info = new ComPtr(raw);
        var raised = !foreground;
        var deadline = Environment.TickCount64 + 10 * 60 * 1000;
        while (true)
        {
            int status;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));
            if (status == 1) return;
            if (status == 2) throw new OperationCanceledException();
            if (status == 3)
            {
                int error;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 8))(info, &error));
                Marshal.ThrowExceptionForHR(error);
            }
            if (Environment.TickCount64 > deadline) throw new TimeoutException();

            // The Hello dialog cannot be given an owner window from a desktop app and often opens behind ours.
            if (!raised && Native.FindWindowW(DialogClass, null) is var dialog and not 0)
                raised = Native.SetForegroundWindow(dialog) != 0;
            Thread.Sleep(30);
        }
    }

    private static ComPtr Factory(string className, Guid iid)
    {
        using var name = new HString(className);
        nint factory;
        Check(Native.RoGetActivationFactory(name, &iid, &factory));
        return new ComPtr(factory);
    }

    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var hr = Native.RoInitialize(1);
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                if (hr >= 0) Native.RoUninitialize();
            }
        })
        { IsBackground = true, Name = "vaultbar-hello" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }

    private readonly struct ComPtr(nint ptr) : IDisposable
    {
        private readonly nint _ptr = ptr;

        public static implicit operator nint(ComPtr p) => p._ptr;

        public void Dispose()
        {
            if (_ptr != 0) Marshal.Release(_ptr);
        }
    }

    private readonly struct HString : IDisposable
    {
        private readonly nint _handle;

        public HString(string value)
        {
            fixed (char* p = value)
            {
                nint handle;
                Check(Native.WindowsCreateString(p, (uint)value.Length, &handle));
                _handle = handle;
            }
        }

        public static implicit operator nint(HString s) => s._handle;

        public void Dispose() => Native.WindowsDeleteString(_handle);
    }

    private static class Native
    {
        [DllImport("combase.dll")] public static extern int RoInitialize(int type);
        [DllImport("combase.dll")] public static extern void RoUninitialize();
        [DllImport("combase.dll")] public static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
        [DllImport("combase.dll")] public static extern int WindowsCreateString(char* source, uint length, nint* str);
        [DllImport("combase.dll")] public static extern int WindowsDeleteString(nint str);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string className, string? windowName);
        [DllImport("user32.dll")] public static extern int SetForegroundWindow(nint hWnd);
    }
}
