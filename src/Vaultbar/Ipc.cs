using System.IO.Pipes;

namespace Vaultbar;

/// <summary>
/// Single-instance channel: a second launch forwards its command to the running instance. Both ends refuse a peer
/// running as another user. On macOS and Linux the socket sits in a folder only the user can open, so another user
/// can neither reach it nor take its name first. Windows pipe names are machine-wide, so there the name carries a random
/// part kept in the user's own folder: another user cannot guess it to take it first (L5).
/// </summary>
public static class Ipc
{
    internal static string Endpoint { get; } = OperatingSystem.IsWindows()
        ? "vaultbar-" + PipeSecret(Path.Combine(Paths.EnsurePrivate(Paths.CacheDir), "pipe-name"))
        : Path.Combine(Paths.RuntimeDir, "vaultbar.sock");

    /// <summary>The random part of the pipe name: made once, then read by every launch, whichever starts first.</summary>
    internal static string PipeSecret(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(file))
                {
                    if (File.ReadAllText(file).Trim() is { Length: 32 } existing && existing.All(char.IsAsciiHexDigitLower)) return existing;
                    File.Delete(file);
                }
                var fresh = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                var temp = file + "." + fresh;
                File.WriteAllText(temp, fresh);
                try
                {
                    // Never overwrites: of two launches making a name at once, the first move wins and the loop reads it.
                    File.Move(temp, file, overwrite: false);
                }
                catch (IOException)
                {
                    File.Delete(temp);
                }
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static bool TrySend(string command, int timeoutMs = 500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Endpoint, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            writer.WriteLine(command);
            reader.ReadLine();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Call only while holding <see cref="SingleInstance"/>: on macOS and Linux it replaces a socket left by a crash.</summary>
    public static void StartServer(Action<string> handler, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            Paths.EnsurePrivate(Paths.RuntimeDir);
            File.Delete(Endpoint);
        }

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Endpoint, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, leaveOpen: true);
                    await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(ct);
                    if (line != null) handler(line.Trim().ToLowerInvariant());
                    await writer.WriteLineAsync("ok");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Write($"ipc: {Log.Shape(ex)}");
                    await Task.Delay(250, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }
}
