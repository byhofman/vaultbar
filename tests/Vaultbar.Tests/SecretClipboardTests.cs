using Vaultbar.Platform;
using Xunit;

namespace Vaultbar.Tests;

public class SecretClipboardTests
{
    private sealed class FakeClipboard : IClipboardAccess
    {
        private int _sequence;
        public string? Text;
        public int Clears;

        public Task<object?> SetClipboardAsync(string text)
        {
            Text = text;
            return Task.FromResult<object?>(++_sequence);
        }

        public Task ClearClipboardAsync(object token)
        {
            if ((int)token != _sequence) return Task.CompletedTask;
            Clears++;
            Text = null;
            return Task.CompletedTask;
        }

        public void UserCopies(string text)
        {
            _sequence++;
            Text = text;
        }
    }

    [Fact]
    public async Task ASecretIsClearedAfterTheConfiguredTime()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 1);
        var copy = clipboard.CopyAsync("s3cret", sensitive: true);
        Assert.Equal("s3cret", board.Text);
        await copy.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(board.Text);
        Assert.False(clipboard.HoldsSecret);
    }

    [Fact]
    public async Task SomethingTheUserCopiedSinceIsLeftAlone()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 30);
        _ = clipboard.CopyAsync("s3cret", sensitive: true);
        board.UserCopies("their own text");
        await clipboard.ClearNowAsync();
        Assert.Equal("their own text", board.Text);
    }

    [Fact]
    public async Task LockingClearsASecretEvenWithTheTimerOff()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 0);
        await clipboard.CopyAsync("s3cret", sensitive: true);
        Assert.True(clipboard.HoldsSecret);
        await clipboard.ClearNowAsync();
        Assert.Null(board.Text);
    }

    [Fact]
    public async Task AUsernameIsNotCleared()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 30);
        await clipboard.CopyAsync("alex", sensitive: false);
        await clipboard.ClearNowAsync();
        Assert.Equal("alex", board.Text);
        Assert.Equal(0, board.Clears);
    }

    [Fact]
    public void AFingerprintRecognisesTheTextWithoutKeepingIt()
    {
        var fingerprint = new ClipboardFingerprint("s3cret");
        Assert.True(fingerprint.Matches("s3cret"));
        Assert.False(fingerprint.Matches("s3cret "));
        Assert.False(fingerprint.Matches(null));
    }
}
