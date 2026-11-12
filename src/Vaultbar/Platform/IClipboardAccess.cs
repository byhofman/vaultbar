namespace Vaultbar.Platform;

public interface IClipboardAccess
{
    /// <returns>A token identifying this clipboard write, used to clear it later only if unchanged; null if nothing was written.</returns>
    Task<object?> SetClipboardAsync(string text);

    Task ClearClipboardAsync(object token);
}
