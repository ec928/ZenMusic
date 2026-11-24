using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    internal long CollectionId { get; set; }
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
    private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
    private static DateTime _lastMbRequestUtc = DateTime.MinValue;
    private static readonly ConcurrentDictionary<long, List<AutoTagCandidate>> ItunesAlbumTracksCache = new();
    private static readonly Regex LiveWordRegex = new(@"\b(live|concert|karaoke|tribute|instrumental)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> AlbumStopWords = new(StringComparer.Ordinal)
    {
        "the", "of", "and", "a", "an", "in", "to", "for", "on", "at", "by", "with", "remastered", "remaster", "edition", "deluxe", "version"
    };

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ZenMusic/0.7 (https://github.com/ec928/ZenMusic)");
        return c;
    }

    private static readonly Regex BracketJunkRegex = new(
        @"(\[[^\]]*?(youtube|converter|mp3|flac|official|hd|hq|320|128|audio|lyrics|mv)[^\]]*\]|\([^\)]*?(official\s*(music\s*)?video|official\s*audio|lyric\s*video|audio\s*only|visualizer|hd|hq)[^\)]*\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingTrackNumRegex = new(
        @"^(?<num>\d{1,3})(?:\s*[-._)]\s*|\s+)(?<rest>.+)$",
        RegexOptions.Compiled);

    public static bool HasDirtyFilename(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string rawName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        if (BracketJunkRegex.IsMatch(rawName)) return true;
        return !rawName.Contains(' ') && rawName.Contains('_');
    }

    /// <summary>
    /// Strips leading track numbers (e.g. "01 - Sultans Of Swing") and YouTube/rip tags for clean API queries.
    /// </summary>
    public static string CleanQueryTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return string.Empty;
        string cleaned = BracketJunkRegex.Replace(rawTitle, " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '-', '_');
        var numMatch = LeadingTrackNumRegex.Match(cleaned);
        if (numMatch.Success && int.TryParse(numMatch.Groups["num"].Value, out int parsedNum) && parsedNum is > 0 and <= 999)
            cleaned = numMatch.Groups["rest"].Value.Trim();
        return cleaned;
    }

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

    /// <summary>
    /// Formats a clean candidate dropdown label without duplicating the song title or nesting parentheses.
    /// </summary>
    public static string FormatDisplayLabel(string source, string artist, string title, string album, int year)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            string albHead = string.IsNullOrWhiteSpace(artist)
                ? album
                : (string.IsNullOrWhiteSpace(album) ? artist : $"{artist} — {album}");
            return year > 0 ? $"[{source}] {albHead}, {year}" : $"[{source}] {albHead}";
        }

        string prefix = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} — {title}";
        if (string.IsNullOrWhiteSpace(album))
            return year > 0 ? $"[{source}] {prefix}, {year}" : $"[{source}] {prefix}";

        string yr = year > 0 ? $", {year}" : string.Empty;
        if (!string.IsNullOrWhiteSpace(title) &&
            album.StartsWith(title, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(album, artist, StringComparison.OrdinalIgnoreCase))
        {
            string head = string.IsNullOrWhiteSpace(artist) ? album : $"{artist} — {album}";
            return $"[{source}] {head}{yr}";
        }

        return $"[{source}] {prefix} · {album}{yr}";
    }

    private static string StripArtistPrefix(string album, string artist)
    {
        if (string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(artist)) return album;
        string p1 = artist + " - ";
        if (album.StartsWith(p1, StringComparison.OrdinalIgnoreCase))
            return album[p1.Length..].Trim();
        string p2 = artist + " – ";
        if (album.StartsWith(p2, StringComparison.OrdinalIgnoreCase))
            return album[p2.Length..].Trim();
        return album;
    }

    public static async Task<AutoTagSearchResult> SearchAsync(
        string filePath,
        string? queryTitle,
        string? queryArtist,
        TimeSpan duration,
        CancellationToken ct = default,
        string? albumHint = null,
        int yearHint = 0,
        string? trackMatchHint = null,
        int trackNumberHint = 0)
    {
        var result = new AutoTagSearchResult();
        var fileCandidate = ParseFromFilename(filePath);

        // Respect an intentionally cleared Search Title ("") from the UI instead of overriding it with the filename!
        // Only fall back to filename when queryTitle is null (unspecified).
        string rawTitle = queryTitle != null ? queryTitle.Trim() : fileCandidate.Title;
        string title = CleanQueryTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(rawTitle))
            title = rawTitle;

        string fallbackTrackTitle = CleanQueryTitle(!string.IsNullOrWhiteSpace(trackMatchHint) ? trackMatchHint : fileCandidate.Title);
        string artist = queryArtist != null ? queryArtist.Trim() : fileCandidate.Artist;
        string rawAlbum = albumHint != null ? albumHint.Trim() : fileCandidate.Album;
        string album = StripArtistPrefix(rawAlbum, artist);

        string lyricsLookupTitle = !string.IsNullOrWhiteSpace(title) ? title : fallbackTrackTitle;
        var itunesTask = QueryItunesAsync(title, artist, album, fallbackTrackTitle, trackNumberHint, ct);
        var mbTask = QueryMusicBrainzAsync(title, artist, album, fallbackTrackTitle, trackNumberHint, ct);
        var albumCoversTask = !string.IsNullOrWhiteSpace(album)
            ? SearchAlbumCoversAsync(album, artist, ct)
            : Task.FromResult(new List<AutoTagArtOption>());
        var lyricsTask = !string.IsNullOrWhiteSpace(lyricsLookupTitle)
            ? QueryLrclibAsync(lyricsLookupTitle, artist, album, (int)Math.Round(duration.TotalSeconds), ct)
            : Task.FromResult(string.Empty);

        await Task.WhenAll(itunesTask, mbTask, albumCoversTask, lyricsTask);

        var online = new List<AutoTagCandidate>();
        online.AddRange(await itunesTask);
        online.AddRange(await mbTask);

        var ranked = RankCandidates(online, !string.IsNullOrWhiteSpace(title) ? title : fallbackTrackTitle, artist, album, yearHint);
        result.Candidates.AddRange(ranked);
        result.Candidates.Add(fileCandidate);
        result.Lyrics = await lyricsTask;

        // Prioritize covers that match the requested album first, then top-ranked candidate covers (up to 6 for the 3x2 grid).
        var directAlbumCovers = await albumCoversTask;
        var seenArtUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedArt = new List<AutoTagArtOption>();

        // 1) Top-ranked candidate covers whose album matches albumHint
        var candidateArtToFetch = new List<(string Label, string Url, double AlbumSim)>();
        foreach (var c in ranked)
        {
            if (string.IsNullOrWhiteSpace(c.ArtUrl)) continue;
            double sim = !string.IsNullOrWhiteSpace(album) ? AlbumSimilarity(album, c.Album) : 0.5;
            string label = !string.IsNullOrWhiteSpace(c.Album)
                ? $"{c.Source}: {c.Album}{(c.Year > 0 ? $" ({c.Year})" : "")}"
                : c.Source;
            candidateArtToFetch.Add((label, c.ArtUrl!, sim));
        }

        var uniqueCandidateArt = candidateArtToFetch
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.AlbumSim).First())
            .Take(6)
            .ToList();

        var downloadedCandidates = await Task.WhenAll(uniqueCandidateArt.Select(async x =>
        {
            try
            {
                byte[] bytes = await Http.GetByteArrayAsync(x.Url, ct);
                if (bytes.Length > 0)
                    return (Option: (AutoTagArtOption?)new AutoTagArtOption { Source = x.Label, Url = x.Url, Bytes = bytes }, x.AlbumSim);
            }
            catch { }
            return (Option: null, x.AlbumSim);
        }));

        // Put strong album-matched candidate covers & direct album covers ahead of non-matching single/live covers
        foreach (var (opt, sim) in downloadedCandidates.Where(d => d.Option != null && d.AlbumSim >= 0.45))
        {
            if (seenArtUrls.Add(opt!.Url))
                orderedArt.Add(opt);
        }
        foreach (var opt in directAlbumCovers)
        {
            if (seenArtUrls.Add(opt.Url))
                orderedArt.Add(opt);
        }
        foreach (var (opt, _) in downloadedCandidates.Where(d => d.Option != null))
        {
            if (seenArtUrls.Add(opt!.Url))
                orderedArt.Add(opt);
        }

        result.ArtOptions.AddRange(orderedArt.Take(6));
        return result;
    }

    public static double AlbumSimilarity(string? albumHint, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(albumHint) || string.IsNullOrWhiteSpace(candidateAlbum))
            return 0;

        string cleanA = NormalizeTokens(CleanSearchAlbum(albumHint));
        string cleanB = NormalizeTokens(CleanSearchAlbum(candidateAlbum));
        if (string.IsNullOrEmpty(cleanA) || string.IsNullOrEmpty(cleanB)) return 0;
        if (string.Equals(cleanA, cleanB, StringComparison.Ordinal)) return 1.0;

        var ta = cleanA.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tb = cleanB.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sigA = ta.Where(w => !AlbumStopWords.Contains(w) || w.All(char.IsDigit)).ToArray();
        if (sigA.Length == 0) sigA = ta;

        var setB = new HashSet<string>(tb, StringComparer.Ordinal);
        int sigHits = sigA.Count(setB.Contains);
        double recall = (double)sigHits / sigA.Length;
        double dice = TokenOverlap(cleanA, cleanB);
        return Math.Max(dice, recall * 0.92);
    }

    public static List<AutoTagCandidate> RankCandidates(
        IEnumerable<AutoTagCandidate> candidates,
        string queryTitle,
        string queryArtist,
        string? albumHint = null,
        int yearHint = 0)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scored = new List<(AutoTagCandidate Candidate, double Score, int OrigIndex)>();
        int idx = 0;
        bool hasAlbumHint = !string.IsNullOrWhiteSpace(albumHint);
        bool queryWantsLive = LiveWordRegex.IsMatch(queryTitle ?? string.Empty) || LiveWordRegex.IsMatch(albumHint ?? string.Empty);

        foreach (var c in candidates)
        {
            string key = $"{c.Source}|{c.Artist}|{c.Title}|{c.Album}|{c.Year}";
            if (!seen.Add(key)) continue;

            double score = 0;
            if (hasAlbumHint)
            {
                if (!string.IsNullOrWhiteSpace(c.Album))
                {
                    double albSim = AlbumSimilarity(albumHint, c.Album);
                    if (albSim >= 0.85)
                        score += 140.0 + albSim * 35.0;
                    else if (albSim >= 0.50)
                        score += albSim * 130.0;
                    else if (albSim > 0.0)
                        score += albSim * 45.0 - 20.0;
                    else
                        score -= 65.0; // Unrelated album when user explicitly provided an album!

                    if (string.Equals(NormalizeTokens(CleanSearchAlbum(albumHint)), NormalizeTokens(CleanSearchAlbum(c.Album)), StringComparison.Ordinal))
                        score += 30.0;
                }
                else
                {
                    score -= 40.0;
                }
            }

            // Penalize unrequested Live / Concert / Tribute / Karaoke releases so they don't hijack studio/compilation albums
            if (!queryWantsLive && (LiveWordRegex.IsMatch(c.Album ?? string.Empty) || LiveWordRegex.IsMatch(c.Title ?? string.Empty)))
            {
                score -= 38.0;
            }

            if (!string.IsNullOrWhiteSpace(queryArtist) && !string.IsNullOrWhiteSpace(c.Artist))
                score += TokenOverlap(queryArtist, c.Artist) * 28.0;

            if (!string.IsNullOrWhiteSpace(queryTitle) && !string.IsNullOrWhiteSpace(c.Title))
                score += TokenOverlap(queryTitle, c.Title) * 26.0;

            if (yearHint > 0 && c.Year > 0)
            {
                if (c.Year == yearHint) score += 22.0;
                else if (Math.Abs(c.Year - yearHint) <= 1) score += 10.0;
                else score -= 4.0;
            }

            if (!string.IsNullOrEmpty(c.ArtUrl)) score += 4.0;
            if (c.TrackNumber > 0) score += 2.0;

            scored.Add((c, score, idx++));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.OrigIndex)
            .Select(x => x.Candidate)
            .ToList();
    }

    private static string NormalizeTokens(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static double TokenOverlap(string a, string b)
    {
        var ta = NormalizeTokens(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tb = NormalizeTokens(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ta.Length == 0 || tb.Length == 0) return 0;
        var setB = new HashSet<string>(tb, StringComparer.Ordinal);
        int hits = ta.Count(setB.Contains);
        return (2.0 * hits) / (ta.Length + tb.Length);
    }

    public static Task<string> FetchLyricsAsync(
        string filePath,
        string? queryTitle,
        string? queryArtist,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        var fileCandidate = ParseFromFilename(filePath);
        string rawTitle = !string.IsNullOrWhiteSpace(queryTitle) ? queryTitle.Trim() : fileCandidate.Title;
        string title = CleanQueryTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(title)) title = rawTitle;
        string artist = !string.IsNullOrWhiteSpace(queryArtist) ? queryArtist.Trim() : fileCandidate.Artist;
        return QueryLrclibAsync(title, artist, string.Empty, (int)Math.Round(duration.TotalSeconds), ct);
    }

    public static string CleanSearchAlbum(string? album)
    {
        if (string.IsNullOrWhiteSpace(album)) return string.Empty;
        string cleaned = Regex.Replace(album, @"\s*[\(\[]\s*(?:disc|disk|cd|vol(?:ume)?)\s*\d+[^)\]]*[\)\]]\s*$", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? album.Trim() : cleaned;
    }

    public static string StripTrailingParenthetical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string stripped = Regex.Replace(value, @"\s*[\(\[][^)\]]+[\)\]]\s*$", "").Trim();
        return string.IsNullOrWhiteSpace(stripped) ? value.Trim() : stripped;
    }

    private static async Task<List<AutoTagCandidate>> QueryItunesAsync(
        string title,
        string artist,
        string albumHint,
        string fallbackTrackTitle,
        int trackNumberHint,
        CancellationToken ct)
    {
        var list = new List<AutoTagCandidate>();
        string cleanAlbum = CleanSearchAlbum(albumHint);
        string shortTitle = StripTrailingParenthetical(title);
        string matchTitle = !string.IsNullOrWhiteSpace(shortTitle) ? shortTitle : StripTrailingParenthetical(fallbackTrackTitle);
        string baseTerm = string.IsNullOrWhiteSpace(artist) ? shortTitle : $"{artist} {shortTitle}".Trim();
        if (string.IsNullOrWhiteSpace(baseTerm) && string.IsNullOrWhiteSpace(cleanAlbum)) return list;

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 1) ALBUM-FIRST LOOKUP: Find the matching album collection first, then inspect its official tracklist.
            //    This prevents 10-word song queries from failing or returning random Live albums!
            if (!string.IsNullOrWhiteSpace(cleanAlbum))
            {
                string albQuery = string.IsNullOrWhiteSpace(artist) ? cleanAlbum : $"{artist} {cleanAlbum}".Trim();
                string albUrl = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(albQuery)}&media=music&entity=album&limit=8";
                using var albResp = await Http.GetAsync(albUrl, ct);
                if (albResp.IsSuccessStatusCode)
                {
                    using var albStream = await albResp.Content.ReadAsStreamAsync(ct);
                    using var albDoc = await JsonDocument.ParseAsync(albStream, cancellationToken: ct);
                    if (albDoc.RootElement.TryGetProperty("results", out var albResults))
                    {
                        var matchedCollections = new List<(long CollId, string Album, string Artist, string Genre, int Year, string? ArtUrl, double Sim)>();
                        foreach (var el in albResults.EnumerateArray())
                        {
                            long cid = el.TryGetProperty("collectionId", out var cidEl) && cidEl.TryGetInt64(out long cv) ? cv : 0;
                            string alb = el.TryGetProperty("collectionName", out var cn) ? cn.GetString() ?? "" : "";
                            string aName = el.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
                            string genre = el.TryGetProperty("primaryGenreName", out var gn) ? gn.GetString() ?? "" : "";
                            int year = 0;
                            if (el.TryGetProperty("releaseDate", out var rd))
                            {
                                string? rdStr = rd.GetString();
                                if (!string.IsNullOrEmpty(rdStr) && rdStr.Length >= 4 && int.TryParse(rdStr[..4], out int yv))
                                    year = yv;
                            }
                            string? artUrl = el.TryGetProperty("artworkUrl100", out var au) ? au.GetString() : null;
                            if (!string.IsNullOrEmpty(artUrl))
                                artUrl = artUrl.Replace("100x100bb", "600x600bb");

                            double sim = AlbumSimilarity(cleanAlbum, alb);
                            if (cid > 0 && sim >= 0.42)
                                matchedCollections.Add((cid, alb, aName, genre, year, artUrl, sim));
                        }

                        foreach (var coll in matchedCollections.OrderByDescending(x => x.Sim).Take(2))
                        {
                            var albumTracks = await LookupItunesAlbumTracksAsync(coll.CollId, coll.Album, coll.Artist, coll.Year, coll.ArtUrl, ct);
                            AutoTagCandidate? bestTrack = null;
                            if (!string.IsNullOrWhiteSpace(matchTitle) && albumTracks.Count > 0)
                            {
                                bestTrack = albumTracks
                                    .Select(t => (Track: t, Overlap: TokenOverlap(matchTitle, StripTrailingParenthetical(t.Title))))
                                    .Where(x => x.Overlap >= 0.45)
                                    .OrderByDescending(x => x.Overlap)
                                    .ThenBy(x => trackNumberHint > 0 && x.Track.TrackNumber == trackNumberHint ? 0 : 1)
                                    .Select(x => x.Track)
                                    .FirstOrDefault();
                            }
                            else if (string.IsNullOrWhiteSpace(title) && trackNumberHint > 0 && albumTracks.Count > 0)
                            {
                                bestTrack = albumTracks.FirstOrDefault(t => t.TrackNumber == trackNumberHint);
                            }

                            if (bestTrack != null)
                            {
                                string dedup = $"{bestTrack.CollectionId}|{bestTrack.Title}";
                                if (seenKeys.Add(dedup))
                                    list.Add(bestTrack);
                            }
                            else if (string.IsNullOrWhiteSpace(title))
                            {
                                // Pure Album + Artist search (Search Title intentionally cleared): emit clean Album candidate
                                string dedup = $"{coll.CollId}|__album__";
                                if (seenKeys.Add(dedup))
                                {
                                    string labelHead = string.IsNullOrWhiteSpace(coll.Artist) ? coll.Album : $"{coll.Artist} — {coll.Album}";
                                    list.Add(new AutoTagCandidate
                                    {
                                        Source = "iTunes Album",
                                        DisplayLabel = $"[iTunes Album] {labelHead}{(coll.Year > 0 ? $", {coll.Year}" : "")}",
                                        Title = string.Empty,
                                        Artist = coll.Artist,
                                        Album = coll.Album,
                                        AlbumArtist = coll.Artist,
                                        Genre = coll.Genre,
                                        Year = coll.Year,
                                        ArtUrl = coll.ArtUrl,
                                        CollectionId = coll.CollId
                                    });
                                }
                            }
                        }
                    }
                }
            }

            // 2) SONG SEARCH (skipped if user intentionally cleared Search Title to do an Album-only lookup!)
            if (!string.IsNullOrWhiteSpace(shortTitle))
            {
                var terms = new List<string>();
                void AddTerm(string t)
                {
                    t = t.Trim();
                    if (!string.IsNullOrWhiteSpace(t) && !terms.Contains(t, StringComparer.OrdinalIgnoreCase))
                        terms.Add(t);
                }

                if (!string.IsNullOrWhiteSpace(cleanAlbum))
                {
                    AddTerm(string.IsNullOrWhiteSpace(artist) ? $"{cleanAlbum} {shortTitle}" : $"{artist} {cleanAlbum} {shortTitle}");
                    if (!string.Equals(shortTitle, title, StringComparison.Ordinal))
                        AddTerm(string.IsNullOrWhiteSpace(artist) ? $"{cleanAlbum} {title}" : $"{artist} {cleanAlbum} {title}");
                }
                AddTerm(baseTerm);
                if (!string.Equals(shortTitle, title, StringComparison.Ordinal))
                    AddTerm(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");

                var newSongCandidates = new List<AutoTagCandidate>();
                foreach (string term in terms)
                {
                    string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=song&limit=10";
                    using var resp = await Http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (!doc.RootElement.TryGetProperty("results", out var results)) continue;

                    foreach (var el in results.EnumerateArray())
                    {
                        string tName = el.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
                        string aName = el.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
                        string alb = el.TryGetProperty("collectionName", out var cn) ? cn.GetString() ?? "" : "";
                        string genre = el.TryGetProperty("primaryGenreName", out var gn) ? gn.GetString() ?? "" : "";
                        int trackNum = el.TryGetProperty("trackNumber", out var tno) && tno.TryGetInt32(out int tVal) ? tVal : 0;
                        long collId = el.TryGetProperty("collectionId", out var cid) && cid.TryGetInt64(out long cVal) ? cVal : 0;
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

                        if (string.IsNullOrWhiteSpace(tName)) continue;
                        string dedup = $"{collId}|{tName}";
                        if (!seenKeys.Add(dedup)) continue;

                        var cand = new AutoTagCandidate
                        {
                            Source = "iTunes",
                            DisplayLabel = FormatDisplayLabel("iTunes", aName, tName, alb, year),
                            Title = tName,
                            Artist = aName,
                            Album = alb,
                            AlbumArtist = aName,
                            Genre = genre,
                            Year = year,
                            TrackNumber = trackNum,
                            ArtUrl = artUrl,
                            CollectionId = collId
                        };
                        list.Add(cand);
                        newSongCandidates.Add(cand);
                    }
                }

                var collIds = newSongCandidates.Select(c => c.CollectionId).Where(id => id > 0).Distinct().Take(12).ToList();
                if (collIds.Count > 0)
                {
                    var albumYears = await LookupItunesAlbumYearsAsync(collIds, ct);
                    foreach (var c in newSongCandidates)
                    {
                        if (c.CollectionId > 0 && albumYears.TryGetValue(c.CollectionId, out int albumYear) && albumYear > 0)
                        {
                            c.Year = albumYear;
                            c.DisplayLabel = FormatDisplayLabel("iTunes", c.Artist, c.Title, c.Album, c.Year);
                        }
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private static async Task<List<AutoTagCandidate>> LookupItunesAlbumTracksAsync(
        long collectionId,
        string albumName,
        string albumArtist,
        int albumYear,
        string? albumArtUrl,
        CancellationToken ct)
    {
        if (ItunesAlbumTracksCache.TryGetValue(collectionId, out var cached))
            return cached;

        var tracks = new List<AutoTagCandidate>();
        try
        {
            string url = $"https://itunes.apple.com/lookup?id={collectionId}&entity=song";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return tracks;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return tracks;

            foreach (var el in results.EnumerateArray())
            {
                string wrapper = el.TryGetProperty("wrapperType", out var wt) ? wt.GetString() ?? "" : "";
                if (!string.Equals(wrapper, "track", StringComparison.OrdinalIgnoreCase)) continue;
                string tName = el.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(tName)) continue;

                string aName = el.TryGetProperty("artistName", out var an) ? an.GetString() ?? albumArtist : albumArtist;
                string genre = el.TryGetProperty("primaryGenreName", out var gn) ? gn.GetString() ?? "" : "";
                int trackNum = el.TryGetProperty("trackNumber", out var tno) && tno.TryGetInt32(out int tv) ? tv : 0;

                tracks.Add(new AutoTagCandidate
                {
                    Source = "iTunes",
                    DisplayLabel = FormatDisplayLabel("iTunes", aName, tName, albumName, albumYear),
                    Title = tName,
                    Artist = aName,
                    Album = albumName,
                    AlbumArtist = string.IsNullOrWhiteSpace(albumArtist) ? aName : albumArtist,
                    Genre = genre,
                    Year = albumYear,
                    TrackNumber = trackNum,
                    ArtUrl = albumArtUrl,
                    CollectionId = collectionId
                });
            }
            if (tracks.Count > 0)
                ItunesAlbumTracksCache[collectionId] = tracks;
        }
        catch { }
        return tracks;
    }

    private static async Task<Dictionary<long, int>> LookupItunesAlbumYearsAsync(IReadOnlyList<long> collectionIds, CancellationToken ct)
    {
        var map = new Dictionary<long, int>();
        try
        {
            string url = $"https://itunes.apple.com/lookup?id={string.Join(",", collectionIds)}";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return map;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return map;

            foreach (var el in results.EnumerateArray())
            {
                if (!el.TryGetProperty("collectionId", out var cidEl) || !cidEl.TryGetInt64(out long cid)) continue;
                if (!el.TryGetProperty("releaseDate", out var rdEl)) continue;
                string? rd = rdEl.GetString();
                if (!string.IsNullOrEmpty(rd) && rd.Length >= 4 && int.TryParse(rd[..4], out int yVal) && yVal > 0)
                    map[cid] = yVal;
            }
        }
        catch { }
        return map;
    }

    private static async Task<JsonDocument?> SendMusicBrainzGetAsync(string url, CancellationToken ct)
    {
        await MusicBrainzGate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var elapsed = DateTime.UtcNow - _lastMbRequestUtc;
                if (elapsed.TotalMilliseconds < 1150)
                    await Task.Delay(1150 - (int)elapsed.TotalMilliseconds, ct);

                _lastMbRequestUtc = DateTime.UtcNow;
                using var resp = await Http.GetAsync(url, ct);
                if ((resp.StatusCode == HttpStatusCode.ServiceUnavailable || resp.StatusCode == (HttpStatusCode)429) && attempt < 2)
                {
                    await Task.Delay(1400 + attempt * 800, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return null;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
        }
        catch { }
        finally
        {
            MusicBrainzGate.Release();
        }
        return null;
    }

    private static string SanitizeLucenePhrase(string value) =>
        Regex.Replace(value ?? string.Empty, @"[+\-&|!(){}\[\]^""~*?:\\/]+", " ").Trim();

    public static string BuildLuceneAlbumClause(string safeAlbum)
    {
        // MusicBrainz stores a span such as "1990-2000" as one token. Requiring
        // 1990 AND 2000 matches nothing for that release.
        var words = new List<string>();
        foreach (string raw in NormalizeTokens(safeAlbum).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if ((AlbumStopWords.Contains(raw) || raw.Length < 2) && !raw.All(char.IsDigit))
                continue;
            if (words.Count > 0
                && words[words.Count - 1].Length == 4 && words[words.Count - 1].All(char.IsDigit)
                && raw.Length == 4 && raw.All(char.IsDigit)
                && int.TryParse(words[words.Count - 1], out int y1)
                && int.TryParse(raw, out int y2)
                && y2 > y1 && y2 - y1 <= 50)
            {
                words[words.Count - 1] = $"{y1}-{y2}";
                continue;
            }
            if (!words.Contains(raw, StringComparer.Ordinal))
                words.Add(raw);
        }
        if (words.Count > 6)
            words = words.Take(6).ToList();
        if (words.Count == 0)
            return $"release:\"{safeAlbum}\"";
        if (words.Count == 1)
            return $"release:{words[0]}";
        return $"release:({string.Join(" AND ", words)})";
    }

    private static async Task<List<AutoTagCandidate>> QueryMusicBrainzAsync(
        string title,
        string artist,
        string albumHint,
        string fallbackTrackTitle,
        int trackNumberHint,
        CancellationToken ct)
    {
        var list = new List<AutoTagCandidate>();
        string safeTitle = SanitizeLucenePhrase(title);
        string shortTitle = SanitizeLucenePhrase(StripTrailingParenthetical(title));
        string safeArtist = SanitizeLucenePhrase(artist);
        string safeAlbum = SanitizeLucenePhrase(CleanSearchAlbum(albumHint));
        if (string.IsNullOrWhiteSpace(safeTitle) && string.IsNullOrWhiteSpace(safeAlbum)) return list;

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string albumClause = !string.IsNullOrWhiteSpace(safeAlbum) ? BuildLuceneAlbumClause(safeAlbum) : string.Empty;

            // 1) Pure Album + Artist lookup when Search Title was cleared (or first-class Release lookup!)
            if (string.IsNullOrWhiteSpace(shortTitle) && !string.IsNullOrWhiteSpace(albumClause))
            {
                string relQuery = string.IsNullOrWhiteSpace(safeArtist)
                    ? $"{albumClause} AND status:official"
                    : $"{albumClause} AND artist:\"{safeArtist}\" AND status:official";

                using var relDoc = await SendMusicBrainzGetAsync(
                    $"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(relQuery)}&fmt=json&limit=8",
                    ct);

                if (relDoc != null && relDoc.RootElement.TryGetProperty("releases", out var relArray))
                {
                    foreach (var rel in relArray.EnumerateArray())
                    {
                        string relTitle = rel.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(relTitle)) continue;
                        string releaseId = rel.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
                        string relArtist = "";
                        if (rel.TryGetProperty("artist-credit", out var acEl) && acEl.GetArrayLength() > 0)
                        {
                            relArtist = string.Join(", ", acEl.EnumerateArray()
                                .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null)
                                .Where(s => !string.IsNullOrWhiteSpace(s)));
                        }
                        int year = 0;
                        if (rel.TryGetProperty("date", out var dt))
                        {
                            string? dStr = dt.GetString();
                            if (!string.IsNullOrEmpty(dStr) && dStr.Length >= 4 && int.TryParse(dStr[..4], out int yVal))
                                year = yVal;
                        }

                        string key = $"{relArtist}|__album__|{relTitle}|{year}";
                        if (!seenKeys.Add(key)) continue;

                        string? artUrl = !string.IsNullOrEmpty(releaseId)
                            ? $"https://coverartarchive.org/release/{releaseId}/front-500"
                            : null;
                        string labelHead = string.IsNullOrWhiteSpace(relArtist) ? relTitle : $"{relArtist} — {relTitle}";
                        list.Add(new AutoTagCandidate
                        {
                            Source = "MusicBrainz Album",
                            DisplayLabel = $"[MusicBrainz Album] {labelHead}{(year > 0 ? $", {year}" : "")}",
                            Title = string.Empty,
                            Artist = relArtist,
                            Album = relTitle,
                            AlbumArtist = relArtist,
                            Year = year,
                            ArtUrl = artUrl
                        });
                    }
                }
                return list;
            }

            string MakeRecQuery(string t) =>
                string.IsNullOrWhiteSpace(safeArtist)
                    ? $"recording:\"{t}\" AND status:official"
                    : $"recording:\"{t}\" AND artist:\"{safeArtist}\" AND status:official";

            var albumQueries = new List<string>();
            var fallbackQueries = new List<string>();
            void AddUnique(List<string> target, string q)
            {
                if (!target.Contains(q, StringComparer.Ordinal)) target.Add(q);
            }

            // Use token-AND release matching (e.g. release:(very AND best AND 1990 AND 2000)) so releases like
            // "The Very Best of Sarah Brightman 1990–2000" match cleanly instead of failing exact-phrase quotes!
            if (!string.IsNullOrWhiteSpace(shortTitle) && !string.IsNullOrWhiteSpace(albumClause))
                AddUnique(albumQueries, $"{MakeRecQuery(shortTitle)} AND {albumClause}");
            if (!string.IsNullOrWhiteSpace(safeTitle) && !string.Equals(safeTitle, shortTitle, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(albumClause))
                AddUnique(albumQueries, $"{MakeRecQuery(safeTitle)} AND {albumClause}");

            if (!string.IsNullOrWhiteSpace(shortTitle))
                AddUnique(fallbackQueries, MakeRecQuery(shortTitle));
            if (!string.IsNullOrWhiteSpace(safeTitle) && !string.Equals(safeTitle, shortTitle, StringComparison.Ordinal))
                AddUnique(fallbackQueries, MakeRecQuery(safeTitle));

            async Task RunRecordingQueryBatchAsync(List<string> batch, int stopCount)
            {
                foreach (string q in batch)
                {
                    if (list.Count >= stopCount) break;
                    using var doc = await SendMusicBrainzGetAsync(
                        $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(q)}&fmt=json&limit=12",
                        ct);

                    if (doc == null || !doc.RootElement.TryGetProperty("recordings", out var recordings))
                        continue;

                    foreach (var rec in recordings.EnumerateArray())
                    {
                        string recTitle = rec.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(recTitle)) continue;

                        string recArtist = "";
                        if (rec.TryGetProperty("artist-credit", out var acEl) && acEl.GetArrayLength() > 0)
                        {
                            recArtist = string.Join(", ", acEl.EnumerateArray()
                                .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null)
                                .Where(s => !string.IsNullOrWhiteSpace(s)));
                        }

                        if (!rec.TryGetProperty("releases", out var rels) || rels.GetArrayLength() == 0)
                            continue;

                        var orderedRels = string.IsNullOrWhiteSpace(safeAlbum)
                            ? rels.EnumerateArray().ToList()
                            : rels.EnumerateArray()
                                  .OrderByDescending(r =>
                                  {
                                      string rtStr = r.TryGetProperty("title", out var rtEl) ? rtEl.GetString() ?? "" : "";
                                      return AlbumSimilarity(albumHint, rtStr);
                                  })
                                  .ToList();
                        int addedForRec = 0;
                        foreach (var rel in orderedRels)
                        {
                            if (addedForRec >= 4) break;
                            string album = rel.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
                            string releaseId = rel.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
                            int year = 0;
                            if (rel.TryGetProperty("date", out var dt))
                            {
                                string? dStr = dt.GetString();
                                if (!string.IsNullOrEmpty(dStr) && dStr.Length >= 4 && int.TryParse(dStr[..4], out int yVal))
                                    year = yVal;
                            }

                            string key = $"{recArtist}|{recTitle}|{album}|{year}";
                            if (!seenKeys.Add(key)) continue;

                            int trackNum = 0;
                            if (rel.TryGetProperty("media", out var mediaArr) && mediaArr.GetArrayLength() > 0)
                            {
                                var m0 = mediaArr[0];
                                if (m0.TryGetProperty("track", out var tArr) && tArr.GetArrayLength() > 0 &&
                                    tArr[0].TryGetProperty("number", out var numEl))
                                {
                                    int.TryParse(numEl.GetString(), out trackNum);
                                }
                            }

                            string? artUrl = !string.IsNullOrEmpty(releaseId)
                                ? $"https://coverartarchive.org/release/{releaseId}/front-500"
                                : null;

                            list.Add(new AutoTagCandidate
                            {
                                Source = "MusicBrainz",
                                DisplayLabel = FormatDisplayLabel("MusicBrainz", recArtist, recTitle, album, year),
                                Title = recTitle,
                                Artist = recArtist,
                                Album = album,
                                AlbumArtist = recArtist,
                                Year = year,
                                TrackNumber = trackNum,
                                ArtUrl = artUrl
                            });
                            addedForRec++;
                        }
                    }
                }
            }

            await RunRecordingQueryBatchAsync(albumQueries, stopCount: 6);
            // Only fall back to non-album queries if the album-scoped queries didn't find enough matches
            if (list.Count < 2)
                await RunRecordingQueryBatchAsync(fallbackQueries, stopCount: 6);
        }
        catch { }
        return list;
    }

    private static async Task<string> QueryLrclibAsync(string title, string artist, string albumHint, int durationSec, CancellationToken ct)
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

            JsonElement? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? synced = item.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() : null;
                string? plain = item.TryGetProperty("plainLyrics", out var pl) ? pl.GetString() : null;
                bool hasSynced = !string.IsNullOrWhiteSpace(synced);
                if (!hasSynced && string.IsNullOrWhiteSpace(plain)) continue;
                int itemDur = item.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out double d) ? (int)Math.Round(d) : durationSec;
                int diff = durationSec > 0 ? Math.Abs(itemDur - durationSec) : 0;
                string itemAlbum = item.TryGetProperty("albumName", out var alEl) ? alEl.GetString() ?? "" : "";
                double albumBonus = (!string.IsNullOrWhiteSpace(albumHint) && !string.IsNullOrWhiteSpace(itemAlbum))
                    ? TokenOverlap(albumHint, itemAlbum) * 15.0
                    : 0.0;
                double score = (hasSynced ? 10.0 : 0.0) + albumBonus - diff;
                if (best == null || score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }

            if (best is JsonElement chosen)
            {
                if (chosen.TryGetProperty("syncedLyrics", out var bestSynced))
                {
                    string? s = bestSynced.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (chosen.TryGetProperty("plainLyrics", out var bestLyrics))
                    return bestLyrics.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    /// <summary>
    /// Searches album artwork from iTunes (entity=album) and returns up to 5 downloaded cover options.
    /// </summary>
    public static async Task<List<AutoTagArtOption>> SearchAlbumCoversAsync(
        string? queryText,
        string? artistHint = null,
        CancellationToken ct = default)
    {
        var results = new List<AutoTagArtOption>();
        string cleanQuery = StripArtistPrefix(queryText?.Trim() ?? string.Empty, artistHint ?? string.Empty);
        string term = string.IsNullOrWhiteSpace(artistHint)
            ? cleanQuery
            : (cleanQuery.Contains(artistHint, StringComparison.OrdinalIgnoreCase)
                ? cleanQuery
                : $"{artistHint} {cleanQuery}".Trim());
        if (string.IsNullOrWhiteSpace(term)) return results;

        var urls = new List<(string Label, string Url)>();
        try
        {
            string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=album&limit=8";
            using var resp = await Http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("results", out var items))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var el in items.EnumerateArray())
                    {
                        string? art = el.TryGetProperty("artworkUrl100", out var au) ? au.GetString() : null;
                        if (string.IsNullOrWhiteSpace(art)) continue;
                        string hdArt = art.Replace("100x100bb", "600x600bb");
                        if (!seen.Add(hdArt)) continue;

                        string alb = el.TryGetProperty("collectionName", out var cn) ? cn.GetString() ?? "" : "";
                        string artArtist = el.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
                        string label = string.IsNullOrWhiteSpace(artArtist) ? alb : $"{artArtist} — {alb}";
                        urls.Add((label, hdArt));
                        if (urls.Count >= 5) break;
                    }
                }
            }
        }
        catch { }

        var downloads = urls.Select(async entry =>
        {
            try
            {
                byte[] bytes = await Http.GetByteArrayAsync(entry.Url, ct);
                if (bytes.Length > 0)
                    return new AutoTagArtOption { Source = entry.Label, Url = entry.Url, Bytes = bytes };
            }
            catch { }
            return null;
        });

        foreach (var opt in await Task.WhenAll(downloads))
        {
            if (opt != null) results.Add(opt);
        }
        return results;
    }
}
