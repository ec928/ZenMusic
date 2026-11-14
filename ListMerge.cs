using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ZenMusic;

public static class ListMerge
{
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
