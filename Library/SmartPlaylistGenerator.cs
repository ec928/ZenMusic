using System;
using System.Collections.Generic;
using System.Linq;

namespace ZenMusic.Library;

public enum SmartPlayHistoryFilter
{
    Any,
    Unplayed,
    LeastPlayed,
    NotPlayedRecently,
    MostPlayed
}

public enum SmartMixOrder
{
    Random,
    BalancedMix,
    MostPlayed,
    NewestAdded,
    HighestRated
}

public sealed class SmartMixRule
{
    public string Name { get; set; } = "Quick Mix";
    public string Subtitle { get; set; } = string.Empty;
    public int MaxTracks { get; set; } = 30;
    public double? MinRating { get; set; }
    public bool UnratedOnly { get; set; }
    public string? GenreContains { get; set; }
    public string? ArtistEquals { get; set; }
    public int? MinYear { get; set; }
    public int? MaxYear { get; set; }
    public SmartPlayHistoryFilter History { get; set; } = SmartPlayHistoryFilter.Any;
    public SmartMixOrder Order { get; set; } = SmartMixOrder.Random;
    public int StaleDays { get; set; } = 21;
}

/// <summary>
/// Builds randomized and rule-based on-the-fly mixes from library metadata and play history.
/// </summary>
public static class SmartPlaylistGenerator
{
    public static IReadOnlyList<SmartMixRule> GetQuickPresets(TrackItem? contextTrack)
    {
        var list = new List<SmartMixRule>
        {
            new()
            {
                Name = "Lucky Shuffle",
                Subtitle = "30 random tracks across your library",
                MaxTracks = 30,
                Order = SmartMixOrder.Random
            },
            new()
            {
                Name = "Top Rated Mix",
                Subtitle = "30 random picks rated 4★ or higher",
                MaxTracks = 30,
                MinRating = 4.0,
                Order = SmartMixOrder.Random
            },
            new()
            {
                Name = "Forgotten Gems",
                Subtitle = "25 well-rated tracks not heard recently",
                MaxTracks = 25,
                MinRating = 3.5,
                History = SmartPlayHistoryFilter.NotPlayedRecently,
                StaleDays = 14,
                Order = SmartMixOrder.BalancedMix
            },
            new()
            {
                Name = "Heavy Rotation",
                Subtitle = "Your 30 most played tracks",
                MaxTracks = 30,
                History = SmartPlayHistoryFilter.MostPlayed,
                Order = SmartMixOrder.MostPlayed
            },
            new()
            {
                Name = "Fresh Arrivals",
                Subtitle = "30 newest tracks added to your folder",
                MaxTracks = 30,
                Order = SmartMixOrder.NewestAdded
            },
            new()
            {
                Name = "Unrated Discovery",
                Subtitle = "25 unrated tracks to explore & rate",
                MaxTracks = 25,
                UnratedOnly = true,
                Order = SmartMixOrder.Random
            }
        };

        if (contextTrack != null)
        {
            if (!string.IsNullOrWhiteSpace(contextTrack.Genre))
            {
                list.Add(new SmartMixRule
                {
                    Name = $"{contextTrack.Genre} Mix",
                    Subtitle = $"25 tracks matching \"{contextTrack.Genre}\"",
                    MaxTracks = 25,
                    GenreContains = contextTrack.Genre,
                    Order = SmartMixOrder.BalancedMix
                });
            }
            else if (!string.IsNullOrWhiteSpace(contextTrack.Artist))
            {
                list.Add(new SmartMixRule
                {
                    Name = $"More by {contextTrack.Artist}",
                    Subtitle = "Random mix from this artist",
                    MaxTracks = 25,
                    ArtistEquals = contextTrack.Artist,
                    Order = SmartMixOrder.Random
                });
            }
        }

        return list;
    }

    public static List<TrackItem> Generate(
        IEnumerable<TrackItem> library,
        SmartMixRule rule,
        DateTime? nowUtc = null,
        Random? rng = null,
        bool excludeCloudOnly = true)
    {
        DateTime utcNow = nowUtc ?? DateTime.UtcNow;
        Random random = rng ?? Random.Shared;
        DateTime staleCutoff = utcNow.AddDays(-Math.Max(1, rule.StaleDays));

        var all = library.ToList();
        IEnumerable<TrackItem> candidates = all;
        if (excludeCloudOnly)
        {
            var localOnly = all.Where(t => !t.IsCloudOnly).ToList();
            if (localOnly.Count > 0) candidates = localOnly;
        }

        if (rule.UnratedOnly)
        {
            candidates = candidates.Where(t => t.Rating is null or <= 0);
        }
        else if (rule.MinRating is double minR && minR > 0)
        {
            candidates = candidates.Where(t => t.Rating is double r && r >= minR);
        }

        if (!string.IsNullOrWhiteSpace(rule.GenreContains))
        {
            string g = rule.GenreContains.Trim();
            candidates = candidates.Where(t => t.Genre.Contains(g, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(rule.ArtistEquals))
        {
            string a = rule.ArtistEquals.Trim();
            candidates = candidates.Where(t => string.Equals(t.Artist.Trim(), a, StringComparison.OrdinalIgnoreCase));
        }

        if (rule.MinYear is int minY && minY > 0)
            candidates = candidates.Where(t => t.Year >= minY);
        if (rule.MaxYear is int maxY && maxY > 0)
            candidates = candidates.Where(t => t.Year > 0 && t.Year <= maxY);

        var filtered = candidates.ToList();

        switch (rule.History)
        {
            case SmartPlayHistoryFilter.Unplayed:
                filtered = filtered.Where(t => t.PlayCount == 0).ToList();
                break;
            case SmartPlayHistoryFilter.NotPlayedRecently:
            {
                var stale = filtered.Where(t => t.LastPlayedUtc == null || t.LastPlayedUtc.Value <= staleCutoff).ToList();
                // If user only recently imported everything, fall back gracefully to least played
                filtered = stale.Count > 0
                    ? stale
                    : filtered.OrderBy(t => t.PlayCount).ThenBy(t => t.LastPlayedUtc ?? DateTime.MinValue).Take(Math.Max(rule.MaxTracks * 2, 40)).ToList();
                break;
            }
            case SmartPlayHistoryFilter.MostPlayed:
            {
                var played = filtered.Where(t => t.PlayCount > 0).ToList();
                if (played.Count > 0) filtered = played;
                break;
            }
            case SmartPlayHistoryFilter.LeastPlayed:
                filtered = filtered.OrderBy(t => t.PlayCount).ThenBy(t => t.LastPlayedUtc ?? DateTime.MinValue).Take(Math.Max(rule.MaxTracks * 2, 40)).ToList();
                break;
        }

        int limit = Math.Clamp(rule.MaxTracks, 1, 500);

        List<TrackItem> ordered = rule.Order switch
        {
            SmartMixOrder.MostPlayed => filtered
                .OrderByDescending(t => t.PlayCount)
                .ThenByDescending(t => t.Rating ?? 0)
                .ThenBy(_ => random.Next())
                .Take(limit)
                .ToList(),

            SmartMixOrder.NewestAdded => filtered
                .OrderByDescending(t => t.DateAddedUtc)
                .ThenByDescending(t => t.Rating ?? 0)
                .Take(limit)
                .ToList(),

            SmartMixOrder.HighestRated => filtered
                .OrderByDescending(t => t.Rating ?? 0)
                .ThenBy(_ => random.Next())
                .Take(limit)
                .ToList(),

            SmartMixOrder.BalancedMix => filtered
                .OrderByDescending(t =>
                {
                    double ratingBoost = (t.Rating ?? 2.5) * 0.6;
                    double freshnessBoost = t.LastPlayedUtc == null
                        ? 3.0
                        : Math.Min(3.0, (utcNow - t.LastPlayedUtc.Value).TotalDays / 10.0);
                    double lowPlayBoost = Math.Max(0.0, 2.0 - t.PlayCount * 0.25);
                    return ratingBoost + freshnessBoost + lowPlayBoost + random.NextDouble() * 3.5;
                })
                .Take(limit)
                .ToList(),

            _ => filtered
                .OrderBy(_ => random.Next())
                .Take(limit)
                .ToList()
        };

        return ordered;
    }
}
