using System.Collections.Concurrent;

namespace Meridian.Web.Services;

/// <summary>
/// Live notification delivery: every signed-in browser tab watches its user's inbox, whatever project it has open.
/// Callbacks only queue work (e.g. Blazor <c>InvokeAsync</c>); they never block the caller.
/// </summary>
public sealed partial class CollaborationHub
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Action>> _inboxes = new();

    /// <summary>Calls <paramref name="onNotify"/> whenever a notification is created for the user. Dispose to stop.</summary>
    public IDisposable WatchInbox(Guid userId, Action onNotify)
    {
        var token = Guid.NewGuid();
        _inboxes.GetOrAdd(userId, _ => new())[token] = onNotify;
        return new InboxWatch(this, userId, token);
    }

    /// <summary>True while the user has at least one tab open.</summary>
    public bool IsOnline(Guid userId) => _inboxes.TryGetValue(userId, out var w) && !w.IsEmpty;

    /// <summary>Tells every open tab of the user that their inbox changed.</summary>
    public void NotifyUser(Guid userId)
    {
        if (!_inboxes.TryGetValue(userId, out var watchers)) return;
        foreach (var callback in watchers.Values)
        {
            try { callback(); }
            catch (Exception ex) { log.LogWarning(ex, "Notification delivery to {User} failed", userId); }
        }
    }

    private sealed class InboxWatch(CollaborationHub hub, Guid userId, Guid token) : IDisposable
    {
        public void Dispose()
        {
            if (hub._inboxes.TryGetValue(userId, out var w)) w.TryRemove(token, out _);
        }
    }
}
