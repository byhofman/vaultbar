using System.Runtime.Versioning;
using System.Text;
using Tmds.DBus.Protocol;

namespace Vaultbar.Platform;

/// <summary>
/// Locks the vault when the Linux session locks or the machine goes to sleep. The screensaver signals cover the desktops
/// (KDE, GNOME and others), logind covers `loginctl lock-session` and suspend. Tmds.DBus.Protocol already ships with
/// Avalonia for the same bus.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxSessionLock
{
    private const string Login1 = "org.freedesktop.login1";

    public static async Task WatchAsync(Action onLock)
    {
        foreach (var screensaver in new[] { "org.freedesktop.ScreenSaver", "org.gnome.ScreenSaver" })
            await SubscribeAsync(() => Connection.Session, new MatchRule { Type = MessageType.Signal, Interface = screensaver, Member = "ActiveChanged" }, ReadBool, onLock);

        await SubscribeAsync(() => Connection.System,
            new MatchRule { Type = MessageType.Signal, Sender = Login1, Interface = Login1 + ".Manager", Member = "PrepareForSleep" }, ReadBool, onLock);

        if (Environment.GetEnvironmentVariable("XDG_SESSION_ID") is { Length: > 0 } session)
        {
            await SubscribeAsync(() => Connection.System,
                new MatchRule { Type = MessageType.Signal, Sender = Login1, Interface = Login1 + ".Session", Member = "Lock", Path = SessionPath(session) },
                (_, _) => true, onLock);
        }
    }

    /// <summary>The object path logind gives a session, escaped the way sd_bus_path_encode does it.</summary>
    internal static string SessionPath(string sessionId)
    {
        var path = new StringBuilder("/org/freedesktop/login1/session/");
        if (sessionId.Length == 0) return path.Append('_').ToString();
        for (var i = 0; i < sessionId.Length; i++)
        {
            var c = sessionId[i];
            if (char.IsAsciiLetter(c) || i > 0 && char.IsAsciiDigit(c)) path.Append(c);
            else foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) path.Append('_').Append(b.ToString("x2"));
        }
        return path.ToString();
    }

    private static bool ReadBool(Message message, object? state) => message.GetBodyReader().ReadBool();

    private static async Task SubscribeAsync(Func<Connection> bus, MatchRule rule, MessageValueReader<bool> reader, Action onLock)
    {
        try
        {
            await bus().AddMatchAsync(rule, reader, (error, locking, _, _) =>
            {
                if (error is null && locking) onLock();
            }, ObserverFlags.None, emitOnCapturedContext: false);
        }
        catch (Exception ex) when (ex is DBusException or ConnectException or DisconnectedException or IOException or InvalidOperationException)
        {
            // No session or system bus (a container, a minimal window manager): there is nothing to watch.
            Log.Write($"session lock: {rule.Interface} {Log.Shape(ex)}");
        }
    }
}
