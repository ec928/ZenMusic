using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ZenMusic.Tagging;

public sealed class AutoTagCandidate
{
    public string Source { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public string? ArtUrl { get; set; }
}

public sealed class AutoTagArtOption
{
    public string Source { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
}

public sealed class AutoTagSearchResult
{
    public List<AutoTagCandidate> Candidates { get; } = new();
    public List<AutoTagArtOption> ArtOptions { get; } = new();
    public string Lyrics { get; set; } = string.Empty;
}

public static class AutoTagger
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ZenMusic/1.0 (https://github.com/zenmusic)");
        return c;
    }

    private static readonly Regex BracketJunkRegex = new(
        @"(\[[^\]]*?(youtube|converter|mp3|flac|official|hd|hq|320|128|audio|lyrics|mv)[^\]]*\]|\([^\)]*?(official\s*(music\s*)?video|official\s*audio|lyric\s*video|audio\s*only|visualizer|hd|hq)[^\)]*\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingTrackNumRegex = new(
        @"^(?<num>\d{1,3})(?:\s*[-._)]\s*|\s+)(?<rest>.+)$",
        RegexOptions.Compiled);

    public static AutoTagCandidate ParseFromFilename(string path)
    {
        string rawName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        string cleaned = BracketJunkRegex.Replace(rawName, " ").Trim();
        if (!cleaned.Contains(' ') && cleaned.Contains('_'))
            cleaned = cleaned.Replace('_', ' ');
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '-', '_');

        int trackNum = 0;
        var numMatch = LeadingTrackNumRegex.Match(cleaned);
        if (numMatch.Success && int.TryParse(numMatch.Groups["num"].Value, out int parsedNum) && parsedNum is > 0 and <= 999)
        {
            trackNum = parsedNum;
            cleaned = numMatch.Groups["rest"].Value.Trim();
        }

        string artist = string.Empty;
        string title = cleaned;

        int dashIdx = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0)
        {
            artist = cleaned[..dashIdx].Trim();
            title = cleaned[(dashIdx + 3)..].Trim();
        }
        else
        {
            // Also handle "Artist_-_Title" after underscore replacement if single dash surrounded by spaces failed
            int altDash = cleaned.IndexOf(" – ", StringComparison.Ordinal);
            if (altDash > 0)
            {
                artist = cleaned[..altDash].Trim();
                title = cleaned[(altDash + 3)..].Trim();
            }
        }

        string album = string.Empty;
        try
        {
            string? parentDir = Path.GetDirectoryName(path);
            string? folderName = string.IsNullOrEmpty(parentDir) ? null : Path.GetFileName(parentDir);
            if (!string.IsNullOrWhiteSpace(folderName) &&
                !folderName.Equals("Music", StringComparison.OrdinalIgnoreCase) &&
                !folderName.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
            {
                album = folderName.Trim();
            }
        }
        catch { }

        return new AutoTagCandidate
        {
            Source = "Filename",
            DisplayLabel = $"[Filename] {(string.IsNullOrEmpty(artist) ? title : $"{artist} — {title}")}",
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = artist,
            TrackNumber = trackNum
        };
    }

    public static async Task<AutoTagSearchResult> SearchAsync(
        string filePath,
        string? queryTitle,
        string? queryArtist,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        var result = new AutoTagSearchResult();
        var fileCandidate = ParseFromFilename(filePath);

        string title = !string.IsNullOrWhiteSpace(queryTitle) ? queryTitle.Trim() : fileCandidate.Title;
        string artist = !string.IsNullOrWhiteSpace(queryArtist) ? queryArtist.Trim() : fileCandidate.Artist;
        string searchTerm = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";

        var itunesTask = QueryItunesAsync(searchTerm, ct);
        var mbTask = QueryMusicBrainzAsync(title, artist, ct);
        var lyricsTask = QueryLrclibAsync(title, artist, (int)Math.Round(duration.TotalSeconds), ct);

        await Task.WhenAll(itunesTask, mbTask, lyricsTask);

        result.Candidates.AddRange(await itunesTask);
        result.Candidates.AddRange(await mbTask);
        result.Candidates.Add(fileCandidate);
        result.Lyrics = await lyricsTask;

        // Collect up to 4 distinct cover art options
        var artUrls = result.Candidates
            .Select(c => (c.Source, c.ArtUrl))
            .Where(x => !string.IsNullOrWhiteSpace(x.ArtUrl))
            .GroupBy(x => x.ArtUrl!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(4)
            .ToList();

        var artDownloads = artUrls.Select(async x =>
        {
            try
            {
                byte[] bytes = await Http.GetByteArrayAsync(x.ArtUrl!, ct);
                if (bytes.Length > 0)
                    return new AutoTagArtOption { Source = x.Source, Url = x.ArtUrl!, Bytes = bytes };
            }
            catch { }
            return null;
        });

        var downloaded = await Task.WhenAll(artDownloads);
        foreach (var opt in downloaded)
        {
            if (opt != null) result.ArtOptions.Add(opt);
        }

        return result;
    }

    private static async Task<List<AutoTagCandidate>> QueryItunesAsync(string term, CancellationToken ct)
    {
        var list = new List<AutoTagCandidate>();
        if (string.IsNullOrWhiteSpace(term)) return list;

        try
        {
            string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=song&limit=5";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return list;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return list;

            foreach (var el in results.EnumerateArray())
            {
                string title = el.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
                string artist = el.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
                string album = el.TryGetProperty("collectionName", out var cn) ? cn.GetString() ?? "" : "";
                string genre = el.TryGetProperty("primaryGenreName", out var gn) ? gn.GetString() ?? "" : "";
                int trackNum = el.TryGetProperty("trackNumber", out var tno) && tno.TryGetInt32(out int tVal) ? tVal : 0;
                int year = 0;
                if (el.TryGetProperty("releaseDate", out var rd))
                {
                    string? rdStr = rd.GetString();
                    if (!string.IsNullOrEmpty(rdStr) && rdStr.Length >= 4 && int.TryParse(rdStr[..4], out int yVal))
                        year = yVal;
                }

                string? artUrl = el.TryGetProperty("artworkUrl100", out var au) ? au.GetString() : null;
                if (!string.IsNullOrEmpty(artUrl))
                    artUrl = artUrl.Replace("100x100bb", "600x600bb");

                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new AutoTagCandidate
                {
                    Source = "iTunes",
                    DisplayLabel = $"[iTunes] {artist} — {title} ({album}{(year > 0 ? $", {year}" : "")})",
                    Title = title,
                    Artist = artist,
                    Album = album,
                    AlbumArtist = artist,
                    Genre = genre,
                    Year = year,
                    TrackNumber = trackNum,
                    ArtUrl = artUrl
                });
            }
        }
        catch { }
        return list;
    }

    private static async Task<List<AutoTagCandidate>> QueryMusicBrainzAsync(string title, string artist, CancellationToken ct)
    {
        var list = new List<AutoTagCandidate>();
        if (string.IsNullOrWhiteSpace(title)) return list;

        try
        {
            string q = string.IsNullOrWhiteSpace(artist)
                ? $"recording:\"{title}\""
                : $"recording:\"{title}\" AND artist:\"{artist}\"";
            string url = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(q)}&fmt=json&limit=4";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return list;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("recordings", out var recs)) return list;

            foreach (var rec in recs.EnumerateArray())
            {
                string recTitle = rec.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                string recArtist = "";
                if (rec.TryGetProperty("artist-credit", out var acEl) && acEl.GetArrayLength() > 0)
                {
                    recArtist = string.Join(", ", acEl.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                }

                string album = "";
                string releaseId = "";
                int year = 0;
                if (rec.TryGetProperty("releases", out var rels) && rels.GetArrayLength() > 0)
                {
                    var rel = rels[0];
                    album = rel.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
                    releaseId = rel.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
                    if (rel.TryGetProperty("date", out var dt))
                    {
                        string? dStr = dt.GetString();
                        if (!string.IsNullOrEmpty(dStr) && dStr.Length >= 4 && int.TryParse(dStr[..4], out int yVal))
                            year = yVal;
                    }
                }

                string? artUrl = !string.IsNullOrEmpty(releaseId)
                    ? $"https://coverartarchive.org/release/{releaseId}/front-500"
                    : null;

                if (string.IsNullOrWhiteSpace(recTitle)) continue;
                list.Add(new AutoTagCandidate
                {
                    Source = "MusicBrainz",
                    DisplayLabel = $"[MusicBrainz] {recArtist} — {recTitle} ({album}{(year > 0 ? $", {year}" : "")})",
                    Title = recTitle,
                    Artist = recArtist,
                    Album = album,
                    AlbumArtist = recArtist,
                    Year = year,
                    ArtUrl = artUrl
                });
            }
        }
        catch { }
        return list;
    }

    private static async Task<string> QueryLrclibAsync(string title, string artist, int durationSec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        try
        {
            string url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}" +
                         (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return string.Empty;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return string.Empty;

            // Prefer closest duration match with plainLyrics
            JsonElement? best = null;
            int bestDiff = int.MaxValue;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? plain = item.TryGetProperty("plainLyrics", out var pl) ? pl.GetString() : null;
                if (string.IsNullOrWhiteSpace(plain)) continue;
                int itemDur = item.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out double d) ? (int)Math.Round(d) : durationSec;
                int diff = durationSec > 0 ? Math.Abs(itemDur - durationSec) : 0;
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = item;
                }
            }

            if (best is JsonElement chosen && chosen.TryGetProperty("plainLyrics", out var bestLyrics))
                return bestLyrics.GetString()?.Trim() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }
}
