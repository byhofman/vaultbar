using System.IO.Pipes;

namespace Vaultbar;

/// <summary>Single-instance channel: a second launch forwards its command to the running instance.</summary>
public static class Ipc
{
    private static string PipeName { get; } = "vaultbar-" + new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray())
        + (Environment.GetEnvironmentVariable("VAULTBAR_CONFIG_DIR") is { Length: > 0 } d
            ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d)))[..8]
            : "");

    public static string MutexName => (OperatingSystem.IsWindows() ? @"Local\" : @"Global\") + PipeName;

    public static bool TrySend(string command, int timeoutMs = 500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
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

    public static void StartServer(Action<string> handler, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            var stale = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName);
            try
            {
                if (File.Exists(stale)) File.Delete(stale);
            }
            catch (IOException)
            {
            }
        }

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
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
                    Log.Write($"ipc: {ex.Message}");
                    await Task.Delay(250, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }
}
