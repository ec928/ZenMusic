using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZenMusic.Tagging;

public sealed record LrcLine(TimeSpan Timestamp, string Text);

/// <summary>
/// Parses time-synced .LRC lyrics ([mm:ss.xx] or [mm:ss.xxx]) and locates the active line for playback position.
/// </summary>
public static class LrcParser
{
    private static readonly Regex TimestampRegex = new(
        @"\[(?<min>\d{1,3}):(?<sec>\d{2})(?:\.(?<frac>\d{1,3}))?\]",
        RegexOptions.Compiled);

    public static bool IsSynced(string? rawLyrics)
    {
        return Parse(rawLyrics).Count > 0;
    }

    public static IReadOnlyList<LrcLine> Parse(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics)) return Array.Empty<LrcLine>();

        var result = new List<LrcLine>();
        string[] lines = rawLyrics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string rawLine in lines)
        {
            var matches = TimestampRegex.Matches(rawLine);
            if (matches.Count == 0) continue;

            string text = TimestampRegex.Replace(rawLine, string.Empty).Trim();
            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups["min"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int min)) continue;
                if (!int.TryParse(m.Groups["sec"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec)) continue;
                int ms = 0;
                string frac = m.Groups["frac"].Value;
                if (!string.IsNullOrEmpty(frac))
                {
                    if (frac.Length == 1) ms = int.Parse(frac, CultureInfo.InvariantCulture) * 100;
                    else if (frac.Length == 2) ms = int.Parse(frac, CultureInfo.InvariantCulture) * 10;
                    else ms = int.Parse(frac[..3], CultureInfo.InvariantCulture);
                }

                var ts = new TimeSpan(0, 0, min, sec, ms);
                result.Add(new LrcLine(ts, text));
            }
        }

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    public static int GetActiveLineIndex(IReadOnlyList<LrcLine> lines, TimeSpan position)
    {
        if (lines.Count == 0) return -1;
        int lo = 0;
        int hi = lines.Count - 1;
        int active = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (lines[mid].Timestamp <= position)
            {
                active = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return active;
    }
}
