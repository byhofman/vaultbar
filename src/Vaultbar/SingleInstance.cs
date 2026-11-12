namespace Vaultbar;

/// <summary>
/// Held by the running instance for its lifetime. Windows uses a mutex in the session's own namespace; macOS and Linux
/// lock a file in the user's private runtime folder, since .NET keeps named mutexes there in a folder every user shares.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly IDisposable _hold;

    private SingleInstance(IDisposable hold) => _hold = hold;

    public static SingleInstance? TryAcquire()
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(true, @"Local\" + Ipc.Endpoint, out var createdNew);
            if (createdNew) return new SingleInstance(mutex);
            mutex.Dispose();
            return null;
        }

        try
        {
            var path = Path.Combine(Paths.EnsurePrivate(Paths.RuntimeDir), "vaultbar.lock");
            return new SingleInstance(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _hold.Dispose();
}
