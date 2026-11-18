using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// This process's <c>once</c> windows (AD-21), keyed by <see cref="GrantKeys.SectionKey"/>, with the lock that guards
/// them. Memory only: never written, never revoked; a window ends with its <see cref="ProtectedSectionGrant.ExpiresAt"/>
/// or the process.
/// </summary>
internal sealed class OnceWindows
{
    private readonly Dictionary<string, ProtectedSectionGrant> windows = new(GrantKeys.Comparer);
    private readonly Lock gate = new();

    /// <summary>True when a window for <paramref name="key"/> is open at <paramref name="now"/>. An empty key never has one.</summary>
    internal bool IsOpen(string key, DateTimeOffset now)
    {
        if (key.Length == 0)
        {
            return false;
        }

        lock (gate)
        {
            return windows.TryGetValue(key, out var window) && !window.IsExpired(now);
        }
    }

    /// <summary>
    /// True when a window for <paramref name="key"/> was opened at or before <paramref name="at"/> and is still open then
    /// (backlog 9): a window opened, or reopened, after <paramref name="at"/> doesn't count. An empty key never has one.
    /// </summary>
    internal bool OpenedBy(string key, DateTimeOffset at)
    {
        if (key.Length == 0)
        {
            return false;
        }

        lock (gate)
        {
            return windows.TryGetValue(key, out var window) && window.GrantedAt <= at && !window.IsExpired(at);
        }
    }

    /// <summary>Opens <paramref name="window"/>, replacing any window for the same key.</summary>
    internal void Open(ProtectedSectionGrant window)
    {
        lock (gate)
        {
            windows[window.Key] = window;
        }
    }

    /// <summary>The windows still open at <paramref name="now"/>. Expired ones are dropped.</summary>
    internal IReadOnlyList<ProtectedSectionGrant> Live(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var key in windows.Where(w => w.Value.IsExpired(now)).Select(w => w.Key).ToList())
            {
                windows.Remove(key);
            }

            return [.. windows.Values];
        }
    }
}
