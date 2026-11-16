using System;
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
        int yearHint = 0)
    {
        var result = new AutoTagSearchResult();
        var fileCandidate = ParseFromFilename(filePath);

        string rawTitle = !string.IsNullOrWhiteSpace(queryTitle) ? queryTitle.Trim() : fileCandidate.Title;
        string title = CleanQueryTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

        string artist = !string.IsNullOrWhiteSpace(queryArtist) ? queryArtist.Trim() : fileCandidate.Artist;
        string rawAlbum = albumHint != null ? albumHint.Trim() : fileCandidate.Album;
        string album = StripArtistPrefix(rawAlbum, artist);

        var itunesTask = QueryItunesAsync(title, artist, album, ct);
        var mbTask = QueryMusicBrainzAsync(title, artist, album, ct);
        var lyricsTask = QueryLrclibAsync(title, artist, album, (int)Math.Round(duration.TotalSeconds), ct);

        await Task.WhenAll(itunesTask, mbTask, lyricsTask);

        var online = new List<AutoTagCandidate>();
        online.AddRange(await itunesTask);
        online.AddRange(await mbTask);

        var ranked = RankCandidates(online, title, artist, album, yearHint);
        result.Candidates.AddRange(ranked);
        result.Candidates.Add(fileCandidate);
        result.Lyrics = await lyricsTask;

        // Collect up to 4 distinct cover art options matching top-ranked order
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

        foreach (var c in candidates)
        {
            string key = $"{c.Source}|{c.Artist}|{c.Title}|{c.Album}|{c.Year}";
            if (!seen.Add(key)) continue;

            double score = 0;
            if (!string.IsNullOrWhiteSpace(albumHint) && !string.IsNullOrWhiteSpace(c.Album))
            {
                score += TokenOverlap(albumHint, c.Album) * 50.0;
                if (string.Equals(NormalizeTokens(albumHint), NormalizeTokens(c.Album), StringComparison.Ordinal))
                    score += 35.0;
            }

            if (yearHint > 0 && c.Year > 0)
            {
                if (c.Year == yearHint) score += 40.0;
                else if (Math.Abs(c.Year - yearHint) <= 1) score += 12.0;
                else score -= 8.0;
            }

            if (!string.IsNullOrWhiteSpace(queryTitle) && !string.IsNullOrWhiteSpace(c.Title))
                score += TokenOverlap(queryTitle, c.Title) * 20.0;

            if (!string.IsNullOrWhiteSpace(queryArtist) && !string.IsNullOrWhiteSpace(c.Artist))
                score += TokenOverlap(queryArtist, c.Artist) * 15.0;

            if (!string.IsNullOrEmpty(c.ArtUrl)) score += 3.0;
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
        CancellationToken ct)
    {
        var list = new List<AutoTagCandidate>();
        string cleanAlbum = CleanSearchAlbum(albumHint);
        string shortTitle = StripTrailingParenthetical(title);
        string baseTerm = string.IsNullOrWhiteSpace(artist) ? shortTitle : $"{artist} {shortTitle}";
        if (string.IsNullOrWhiteSpace(baseTerm) && string.IsNullOrWhiteSpace(cleanAlbum)) return list;

        try
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

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string term in terms)
            {
                string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=song&limit=8";
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

                    list.Add(new AutoTagCandidate
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
                    });
                }
            }

            // Apple's song search frequently puts the original single/recording year on compilation tracks.
            // Look up the collection (album) objects once in batch so album compilation years are accurate.
            var collIds = list.Select(c => c.CollectionId).Where(id => id > 0).Distinct().Take(12).ToList();
            if (collIds.Count > 0)
            {
                var albumYears = await LookupItunesAlbumYearsAsync(collIds, ct);
                foreach (var c in list)
                {
                    if (c.CollectionId > 0 && albumYears.TryGetValue(c.CollectionId, out int albumYear) && albumYear > 0)
                    {
                        c.Year = albumYear;
                        c.DisplayLabel = FormatDisplayLabel("iTunes", c.Artist, c.Title, c.Album, c.Year);
                    }
                }
            }
        }
        catch { }
        return list;
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

    private static async Task<List<AutoTagCandidate>> QueryMusicBrainzAsync(
        string title,
        string artist,
        string albumHint,
        CancellationToken ct)
    {
        var list = new List<AutoTagCandidate>();
        string safeTitle = SanitizeLucenePhrase(title);
        string shortTitle = SanitizeLucenePhrase(StripTrailingParenthetical(title));
        string safeArtist = SanitizeLucenePhrase(artist);
        string safeAlbum = SanitizeLucenePhrase(CleanSearchAlbum(albumHint));
        if (string.IsNullOrWhiteSpace(safeTitle) && string.IsNullOrWhiteSpace(safeAlbum)) return list;

        try
        {
            string MakeRecQuery(string t) =>
                string.IsNullOrWhiteSpace(safeArtist)
                    ? $"recording:\"{t}\" AND status:official"
                    : $"recording:\"{t}\" AND artist:\"{safeArtist}\" AND status:official";

            var queries = new List<string>();
            void AddQuery(string q)
            {
                if (!queries.Contains(q, StringComparer.Ordinal)) queries.Add(q);
            }

            if (!string.IsNullOrWhiteSpace(shortTitle) && !string.IsNullOrWhiteSpace(safeAlbum))
                AddQuery($"{MakeRecQuery(shortTitle)} AND release:\"{safeAlbum}\"");
            if (!string.IsNullOrWhiteSpace(safeTitle) && !string.IsNullOrWhiteSpace(safeAlbum))
                AddQuery($"{MakeRecQuery(safeTitle)} AND release:\"{safeAlbum}\"");
            if (!string.IsNullOrWhiteSpace(shortTitle))
                AddQuery(MakeRecQuery(shortTitle));
            if (!string.IsNullOrWhiteSpace(safeTitle))
                AddQuery(MakeRecQuery(safeTitle));
            if (queries.Count == 0 && !string.IsNullOrWhiteSpace(safeAlbum))
                AddQuery(string.IsNullOrWhiteSpace(safeArtist)
                    ? $"release:\"{safeAlbum}\" AND status:official"
                    : $"release:\"{safeAlbum}\" AND artist:\"{safeArtist}\" AND status:official");

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string q in queries)
            {
                if (list.Count >= 3) break;
                using var doc = await SendMusicBrainzGetAsync(
                    $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(q)}&fmt=json&limit=10",
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
                    {
                        string key = $"{recArtist}|{recTitle}||0";
                        if (!seenKeys.Add(key)) continue;
                        list.Add(new AutoTagCandidate
                        {
                            Source = "MusicBrainz",
                            DisplayLabel = FormatDisplayLabel("MusicBrainz", recArtist, recTitle, string.Empty, 0),
                            Title = recTitle,
                            Artist = recArtist,
                            AlbumArtist = recArtist
                        });
                        continue;
                    }

                    var orderedRels = string.IsNullOrWhiteSpace(safeAlbum)
                        ? rels.EnumerateArray().ToList()
                        : rels.EnumerateArray()
                              .OrderByDescending(r =>
                              {
                                  string rtStr = r.TryGetProperty("title", out var rtEl) ? rtEl.GetString() ?? "" : "";
                                  return TokenOverlap(safeAlbum, rtStr);
                              })
                              .ToList();
                    int addedForRec = 0;
                    foreach (var rel in orderedRels)
                    {
                        if (addedForRec >= 3) break;
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
