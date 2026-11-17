using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ZenMusic.Library;

public sealed class CachedTrackEntry
{
    public string Path { get; set; } = string.Empty;
    public long LastWriteUtcTicks { get; set; }
    public long FileSize { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public long DurationTicks { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; } = 2;
    public string Codec { get; set; } = string.Empty;
    public DateTime DateAddedUtc { get; set; }
    public double? Rating { get; set; }
    public bool HasEmbeddedArt { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public double? ReplayGainTrackDb { get; set; }
    public double? ReplayGainAlbumDb { get; set; }
    public float? ReplayGainTrackPeak { get; set; }
    public float? ReplayGainAlbumPeak { get; set; }
    public bool ReplayGainFromTag { get; set; }
}

public static class LibraryCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static Dictionary<string, CachedTrackEntry> Load(string cachePath)
    {
        var map = new Dictionary<string, CachedTrackEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(cachePath)) return map;
        try
        {
            string json = File.ReadAllText(cachePath);
            var list = JsonSerializer.Deserialize<List<CachedTrackEntry>>(json, JsonOptions);
            if (list != null)
            {
                foreach (var entry in list)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Path))
                        map[entry.Path] = entry;
                }
            }
        }
        catch { }
        return map;
    }

    public static IReadOnlyList<TrackItem> LoadInstantTracks(string cachePath, string folder)
        => TracksInFolder(Load(cachePath), folder);

    public static IReadOnlyList<TrackItem> TracksInFolder(IReadOnlyDictionary<string, CachedTrackEntry> cache, string folder)
    {
        var list = new List<TrackItem>();
        if (string.IsNullOrWhiteSpace(folder) || cache.Count == 0) return list;
        string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedFolder = trimmed + Path.DirectorySeparatorChar;
        int index = 1;
        foreach (var entry in cache.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;
            if (!entry.Path.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetDirectoryName(entry.Path), trimmed, StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(ToTrackItem(entry, index++, isCloudOnly: false));
        }
        return list;
    }

    public static void Save(string cachePath, IEnumerable<TrackItem> tracks)
    {
        try
        {
            var entries = new List<CachedTrackEntry>();
            foreach (var t in tracks)
            {
                entries.Add(FromTrackItem(t));
            }
            string json = JsonSerializer.Serialize(entries, JsonOptions);
            AtomicFile.WriteAllText(cachePath, json);
        }
        catch { }
    }

    public static TrackItem ToTrackItem(CachedTrackEntry e, int index, bool isCloudOnly) => new()
    {
        Index = index,
        Path = e.Path,
        Title = e.Title,
        Artist = e.Artist,
        Album = e.Album,
        Genre = e.Genre,
        Year = e.Year,
        TrackNumber = e.TrackNumber,
        DiscNumber = e.DiscNumber,
        Duration = TimeSpan.FromTicks(e.DurationTicks),
        Bitrate = e.Bitrate,
        SampleRate = e.SampleRate,
        Channels = e.Channels,
        Codec = e.Codec,
        DateAddedUtc = e.DateAddedUtc == default ? new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc) : e.DateAddedUtc,
        Rating = e.Rating,
        HasEmbeddedArt = e.HasEmbeddedArt,
        PlayCount = e.PlayCount,
        LastPlayedUtc = e.LastPlayedUtc,
        ReplayGainTrackDb = e.ReplayGainTrackDb,
        ReplayGainAlbumDb = e.ReplayGainAlbumDb,
        ReplayGainTrackPeak = e.ReplayGainTrackPeak,
        ReplayGainAlbumPeak = e.ReplayGainAlbumPeak,
        ReplayGainFromTag = e.ReplayGainFromTag,
        IsCloudOnly = isCloudOnly,
        FileSize = e.FileSize,
        FileWriteUtcTicks = e.LastWriteUtcTicks
    };

    public static CachedTrackEntry FromTrackItem(TrackItem t) => new()
    {
        Path = t.Path,
        LastWriteUtcTicks = t.FileWriteUtcTicks > 0 ? t.FileWriteUtcTicks : t.DateAddedUtc.Ticks,
        FileSize = t.FileSize,
        Title = t.Title,
        Artist = t.Artist,
        Album = t.Album,
        Genre = t.Genre,
        Year = t.Year,
        TrackNumber = t.TrackNumber,
        DiscNumber = t.DiscNumber,
        DurationTicks = t.Duration.Ticks,
        Bitrate = t.Bitrate,
        SampleRate = t.SampleRate,
        Channels = t.Channels,
        Codec = t.Codec,
        DateAddedUtc = t.DateAddedUtc,
        Rating = t.Rating,
        HasEmbeddedArt = t.HasEmbeddedArt,
        PlayCount = t.PlayCount,
        LastPlayedUtc = t.LastPlayedUtc,
        ReplayGainTrackDb = t.ReplayGainTrackDb,
        ReplayGainAlbumDb = t.ReplayGainAlbumDb,
        ReplayGainTrackPeak = t.ReplayGainTrackPeak,
        ReplayGainAlbumPeak = t.ReplayGainAlbumPeak,
        ReplayGainFromTag = t.ReplayGainFromTag
    };
}
