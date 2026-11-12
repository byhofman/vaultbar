using Vaultbar.Platform;

namespace Vaultbar;

/// <summary>
/// Copies to the clipboard and takes a copied secret off it again: after the configured time, on lock and on quit, and
/// only while the clipboard still holds what was put there.
/// </summary>
public sealed class SecretClipboard(IClipboardAccess clipboard, int clearAfterSeconds)
{
    private readonly object _gate = new();
    private object? _secretToken;
    private CancellationTokenSource? _timer;

    public bool HoldsSecret
    {
        get
        {
            lock (_gate) return _secretToken != null;
        }
    }

    /// <returns>Completes once a sensitive copy has been cleared, or right away for anything else.</returns>
    public async Task CopyAsync(string text, bool sensitive)
    {
        var token = await clipboard.SetClipboardAsync(text);
        CancellationTokenSource? timer = null;
        lock (_gate)
        {
            _timer?.Cancel();
            _timer = null;
            _secretToken = null;
            if (!sensitive || token is null) return;
            _secretToken = token;
            if (clearAfterSeconds <= 0) return;
            timer = _timer = new CancellationTokenSource();
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(clearAfterSeconds), timer.Token);
            await ClearNowAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task ClearNowAsync()
    {
        object token;
        lock (_gate)
        {
            if (_secretToken is null) return;
            token = _secretToken;
            _secretToken = null;
            _timer?.Cancel();
            _timer = null;
        }
        await clipboard.ClearClipboardAsync(token);
    }
}
