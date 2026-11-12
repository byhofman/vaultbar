namespace Vaultbar;

/// <summary>An error log without secrets or vault content: callers pass identifiers and exception shapes, never values.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Paths.CacheDir, "vaultbar.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Paths.EnsurePrivate(Paths.CacheDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk or a read-only folder must not take the app down over a log line.
        }
    }

    /// <summary>The type and HRESULT of an exception: its message can carry paths, user names or vault content.</summary>
    public static string Shape(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:x8}{(ex.InnerException is { } inner ? " <- " + Shape(inner) : "")}";
}
