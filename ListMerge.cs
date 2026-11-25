using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ZenMusic;

public static class ListMerge
{
    /// <summary>
    /// Puts <paramref name="items"/> right after the playing entry (playNext) or at the end of
    /// <paramref name="queue"/>. An item already in the queue is moved, never added a second time:
    /// a WinUI ListView hangs its UI thread when one object appears in its items twice. The playing
    /// entry itself is never moved. Returns the playing entry's new index (-1 when nothing plays).
    /// </summary>
    public static int Enqueue<T>(IList<T> queue, int playingIndex, IEnumerable<T> items, bool playNext) where T : class
    {
        T? playing = playingIndex >= 0 && playingIndex < queue.Count ? queue[playingIndex] : null;
        var moving = new List<T>();
        foreach (var t in items)
            if (!ReferenceEquals(t, playing) && !moving.Any(m => ReferenceEquals(m, t))) moving.Add(t);
        if (moving.Count == 0) return playing == null ? playingIndex : IndexOfRef(queue, playing);

        foreach (var t in moving)
        {
            int at = IndexOfRef(queue, t);
            if (at >= 0) queue.RemoveAt(at);
        }
        int now = playing == null ? -1 : IndexOfRef(queue, playing);
        int insertAt = playNext ? now + 1 : queue.Count;
        for (int i = 0; i < moving.Count; i++)
            queue.Insert(insertAt + i, moving[i]);
        return playing == null ? -1 : IndexOfRef(queue, playing);
    }

    private static int IndexOfRef<T>(IList<T> list, T item) where T : class
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    /// <summary>
    /// Updates <paramref name="current"/> to match <paramref name="next"/> with minimal changes,
    /// preserving selection, scroll position, and live row containers.
    /// </summary>
    public static void Apply<T>(ObservableCollection<T> current, IReadOnlyList<T> next, Func<T, string> key, Action<T, T> keep)
    {
        var wanted = new HashSet<string>(next.Select(key), StringComparer.OrdinalIgnoreCase);
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(key(current[i]))) current.RemoveAt(i);
        }

        var existing = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in current) existing[key(item)] = item;

        for (int i = 0; i < next.Count; i++)
        {
            var fresh = next[i];
            string k = key(fresh);
            if (!existing.TryGetValue(k, out var old))
            {
                current.Insert(i, fresh);
                continue;
            }
            if (i >= current.Count || !string.Equals(key(current[i]), k, StringComparison.OrdinalIgnoreCase))
                current.Move(current.IndexOf(old), i);
            keep(old, fresh);
        }
    }
}
