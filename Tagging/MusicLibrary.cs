using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ZenMusic.Audio;
using ZenMusic.Library;

namespace ZenMusic.Tagging;

/// <summary>
/// Reads and writes audio metadata and half-star ratings compatible with ModernImageViewer and MediaMonkey.
/// </summary>
public static class MusicLibrary
{
    public static readonly string[] Extensions = { ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg", ".opus" };

    private static readonly (byte Value, double Stars)[] PopularimeterSteps =
    {
        (1, 1), (13, 0.5), (23, 1), (54, 1.5), (64, 2), (118, 2.5), (128, 3), (186, 3.5), (196, 4), (242, 4.5), (255, 5)
    };

    public static bool IsSupportedExtension(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (string candidate in Extensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool SupportsRatingTag(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wma", StringComparison.OrdinalIgnoreCase);
    }

    public static double? ResolveEffectiveRating(
        bool hasPendingWrite,
        double? pendingRating,
        double? diskRating,
        double? cachedRating)
    {
        if (hasPendingWrite) return pendingRating;
        return diskRating ?? cachedRating;
    }

    public static double? StarsFromPopularimeter(byte value)
    {
        if (value == 0) return null;
        double best = 0;
        int bestGap = int.MaxValue;
        foreach (var step in PopularimeterSteps)
        {
            int gap = Math.Abs(step.Value - value);
            if (gap < bestGap) { bestGap = gap; best = step.Stars; }
        }
        return best;
    }

    public static byte PopularimeterFromStars(double stars)
    {
        if (stars <= 0) return 0;
        double clamped = Math.Clamp(Math.Round(stars * 2.0) / 2.0, 0.5, 5.0);
        byte best = 255;
        double bestGap = double.MaxValue;
        foreach (var step in PopularimeterSteps)
        {
            double gap = Math.Abs(step.Stars - clamped);
            if (gap < bestGap) { bestGap = gap; best = step.Value; }
        }
        return best;
    }

    public static double? StarsFromVorbis(string key, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number <= 0) return null;
        if (key.Equals("FMPS_RATING", StringComparison.OrdinalIgnoreCase)) return Math.Min(5, number * 5);
        if (!key.Equals("RATING", StringComparison.OrdinalIgnoreCase)) return null;
        return Math.Min(5, number > 5 ? number / 20 : number);
    }

    public static string SongName(string? title, string? artist, string fileName)
    {
        title = title?.Trim();
        artist = artist?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            string name = Regex.Replace(fileName, @"^[\w.-]+\.(com|net|org)\s+-\s+", string.Empty, RegexOptions.IgnoreCase);
            return Regex.Replace(name, @"_\d+kbps$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }
        return string.IsNullOrEmpty(artist) ? title : $"{title} \u2013 {artist}";
    }

    public static TrackItem ReadTrackMetadata(string path, int index, bool isCloudOnly)
    {
        var info = new FileInfo(path);
        var item = new TrackItem
        {
            Index = index,
            Path = path,
            Title = Path.GetFileNameWithoutExtension(path),
            Codec = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            DateAddedUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
            IsCloudOnly = isCloudOnly,
            FileSize = info.Exists ? info.Length : 0,
            FileWriteUtcTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0
        };

        if (isCloudOnly) return item;

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            item.Title = string.IsNullOrWhiteSpace(tag.Title) ? item.Title : tag.Title.Trim();
            item.Artist = tag.JoinedPerformers?.Trim() ?? string.Empty;
            item.Album = tag.Album?.Trim() ?? string.Empty;
            item.AlbumArtist = tag.JoinedAlbumArtists?.Trim() ?? string.Empty;
            item.Genre = tag.JoinedGenres?.Trim() ?? string.Empty;
            item.Year = (int)tag.Year;
            item.TrackNumber = (int)tag.Track;
            item.DiscNumber = (int)tag.Disc;
            item.HasEmbeddedArt = tag.Pictures != null && tag.Pictures.Length > 0;

            if (file.Properties != null)
            {
                item.Duration = file.Properties.Duration;
                item.Bitrate = file.Properties.AudioBitrate;
                item.SampleRate = file.Properties.AudioSampleRate;
                item.Channels = Math.Max(1, file.Properties.AudioChannels);
            }

            item.Rating = ReadStarsFromTagLib(file, path);
            ReadReplayGainFromTagLib(file, item);
        }
        catch
        {
            // Fallback to basic filename item if tag is corrupt
        }

        return item;
    }

    public static byte[]? ReadEmbeddedArtBytes(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var pics = file.Tag.Pictures;
            if (pics != null && pics.Length > 0)
                return pics[0].Data?.Data;
        }
        catch { }
        return null;
    }

    public static string ReadLyrics(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Tag.Lyrics?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string ReadAlbumArtist(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Tag.JoinedAlbumArtists?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public sealed class TrackDetails
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public int Year { get; set; }
        public int TrackNumber { get; set; }
        public int DiscNumber { get; set; }
        public double? Rating { get; set; }
        public string Lyrics { get; set; } = string.Empty;
        public byte[]? ArtBytes { get; set; }
        public string ArtMimeType { get; set; } = "image/jpeg";
        public bool ArtChanged { get; set; }
    }

    public static TrackDetails ReadFullDetails(string path)
    {
        TryReadFullDetails(path, out var details, out _);
        return details;
    }

    public static bool TryReadFullDetails(string path, out TrackDetails details, out string? error)
    {
        details = new TrackDetails();
        error = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var d = new TrackDetails();
            try
            {
                using var file = TagLib.File.Create(path);
                var tag = file.Tag;
                d.Title = tag.Title ?? string.Empty;
                d.Artist = tag.JoinedPerformers ?? string.Empty;
                d.Album = tag.Album ?? string.Empty;
                d.AlbumArtist = tag.JoinedAlbumArtists ?? string.Empty;
                d.Genre = tag.JoinedGenres ?? string.Empty;
                d.Year = (int)tag.Year;
                d.TrackNumber = (int)tag.Track;
                d.DiscNumber = (int)tag.Disc;
                d.Lyrics = tag.Lyrics ?? string.Empty;
                d.Rating = ReadStarsFromTagLib(file, path);
                if (tag.Pictures is { Length: > 0 } && tag.Pictures[0].Data?.Data is { Length: > 0 } picBytes)
                {
                    d.ArtBytes = picBytes;
                    d.ArtMimeType = string.IsNullOrWhiteSpace(tag.Pictures[0].MimeType) ? "image/jpeg" : tag.Pictures[0].MimeType;
                }
                details = d;
                error = null;
                return true;
            }
            catch (IOException ex) when (attempt < 2)
            {
                error = ex.Message;
                System.Threading.Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        return false;
    }

    public static bool TryWriteFullDetails(string path, TrackDetails d) =>
        TryWriteFullDetails(path, d, out _);

    public static bool TryWriteFullDetails(string path, TrackDetails d, out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                DateTime originalWriteUtc = File.GetLastWriteTimeUtc(path);
                using (var file = TagLib.File.Create(path))
                {
                    if (Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase))
                        StripLegacyFlacId3Tags(file, path);

                    var tag = file.Tag;
                    tag.Title = d.Title;
                    tag.Performers = string.IsNullOrWhiteSpace(d.Artist) ? Array.Empty<string>() : new[] { d.Artist.Trim() };
                    tag.Album = d.Album;
                    tag.AlbumArtists = string.IsNullOrWhiteSpace(d.AlbumArtist) ? Array.Empty<string>() : new[] { d.AlbumArtist.Trim() };
                    tag.Genres = string.IsNullOrWhiteSpace(d.Genre) ? Array.Empty<string>() : new[] { d.Genre.Trim() };
                    tag.Year = d.Year > 0 ? (uint)d.Year : 0;
                    tag.Track = d.TrackNumber > 0 ? (uint)d.TrackNumber : 0;
                    tag.Disc = d.DiscNumber > 0 ? (uint)d.DiscNumber : 0;
                    tag.Lyrics = d.Lyrics;
                    if (d.ArtChanged)
                    {
                        if (d.ArtBytes is { Length: > 0 })
                        {
                            var pic = new TagLib.Picture(new TagLib.ByteVector(d.ArtBytes))
                            {
                                Type = TagLib.PictureType.FrontCover,
                                MimeType = string.IsNullOrWhiteSpace(d.ArtMimeType) ? "image/jpeg" : d.ArtMimeType
                            };
                            tag.Pictures = new TagLib.IPicture[] { pic };
                        }
                        else
                        {
                            tag.Pictures = Array.Empty<TagLib.IPicture>();
                        }
                    }

                    ApplyStarsToTagLib(file, path, d.Rating);
                    file.Save();
                }

                try { File.SetLastWriteTimeUtc(path, originalWriteUtc); } catch { }
                return true;
            }
            catch (IOException ex) when (attempt < 2)
            {
                error = ex.Message;
                System.Threading.Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        return false;
    }

    public static bool TryWriteStars(string path, double? stars) =>
        TryWriteStars(path, stars, out _);

    public static bool TryWriteStars(string path, double? stars, out string? error)
    {
        error = null;
        if (!SupportsRatingTag(path)) return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                DateTime originalWriteUtc = File.GetLastWriteTimeUtc(path);
                bool modified;
                using (var file = TagLib.File.Create(path))
                {
                    modified = ApplyStarsToTagLib(file, path, stars);
                    if (modified)
                        file.Save();
                }
                if (modified)
                {
                    try { File.SetLastWriteTimeUtc(path, originalWriteUtc); } catch { }
                }
                error = null;
                return true;
            }
            catch (IOException ex) when (attempt < 2)
            {
                error = ex.Message;
                System.Threading.Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        return false;
    }

    private static bool ApplyStarsToTagLib(TagLib.File file, string path, double? stars)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".mp3")
        {
            if (stars is double s && s > 0)
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
                byte value = PopularimeterFromStars(s);
                TagLib.Id3v2.PopularimeterFrame.Get(id3, "Windows Media Player 9 Series", true).Rating = value;
                // A file rated by another tagger carries that tagger's own rating frame. Every frame gets
                // the new value: left behind, the old one is what the file reads back as on the next scan.
                foreach (var other in id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>())
                    other.Rating = value;
                return true;
            }

            if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag existingId3)
            {
                bool anyChanged = false;
                foreach (var other in existingId3.GetFrames<TagLib.Id3v2.PopularimeterFrame>())
                {
                    if (other.Rating != 0)
                    {
                        other.Rating = 0;
                        anyChanged = true;
                    }
                }
                return anyChanged;
            }
            return false;
        }

        if (ext is ".flac" or ".ogg" or ".opus")
        {
            bool stripped = ext == ".flac" && StripLegacyFlacId3Tags(file, path);
            if (stars is double s && s > 0)
            {
                var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);
                int val100 = (int)Math.Clamp(Math.Round(s * 20), 10, 100);
                xiph.SetField("RATING", val100.ToString(CultureInfo.InvariantCulture));
                if (xiph.GetField("FMPS_RATING").Length > 0)
                {
                    double clamped = Math.Clamp(Math.Round(s * 2.0) / 2.0, 0.5, 5.0);
                    xiph.SetField("FMPS_RATING", (clamped / 5.0).ToString("0.##", CultureInfo.InvariantCulture));
                }
                return true;
            }

            if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment existingXiph &&
                (existingXiph.GetField("RATING").Length > 0 || existingXiph.GetField("FMPS_RATING").Length > 0))
            {
                existingXiph.RemoveField("RATING");
                existingXiph.RemoveField("FMPS_RATING");
                return true;
            }
            return stripped;
        }

        if (ext is ".m4a" or ".mp4" or ".aac")
        {
            if (stars is double s && s > 0)
            {
                if (file.GetTag(TagLib.TagTypes.Apple, true) is TagLib.Mpeg4.AppleTag apple)
                {
                    int val100 = (int)Math.Clamp(Math.Round(s * 20), 10, 100);
                    apple.SetDashBox("com.apple.iTunes", "RATING", val100.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
            }
            else if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag existingApple &&
                     !string.IsNullOrEmpty(existingApple.GetDashBox("com.apple.iTunes", "RATING")))
            {
                existingApple.SetDashBox("com.apple.iTunes", "RATING", string.Empty);
                return true;
            }
            return false;
        }

        if (ext == ".wma")
        {
            if (stars is double s && s > 0)
            {
                if (file.GetTag(TagLib.TagTypes.Asf, true) is TagLib.Asf.Tag asf)
                {
                    uint val99 = (uint)Math.Clamp(Math.Round((s - 0.5) / 4.5 * 98.0 + 1.0), 1, 99);
                    if (s >= 4.9) val99 = 99;
                    asf.SetDescriptorString(val99.ToString(CultureInfo.InvariantCulture), "WM/SharedUserRating");
                    return true;
                }
            }
            else if (file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag existingAsf &&
                     existingAsf.GetDescriptors("WM/SharedUserRating").Any())
            {
                existingAsf.RemoveDescriptors("WM/SharedUserRating");
                return true;
            }
            return false;
        }

        return false;
    }

    private static bool StripLegacyFlacId3Tags(TagLib.File file, string path)
    {
        const TagLib.TagTypes legacy = TagLib.TagTypes.Id3v2 | TagLib.TagTypes.Id3v1;
        if ((file.TagTypes & legacy) == 0) return false;

        // Snapshot combined tag values (including rating/art from legacy ID3v2) before stripping ID3 wrapper,
        // otherwise TagLibSharp embeds cover art inside the prepended ID3v2 block and breaks Media Foundation FLAC sniffing.
        var comb = file.Tag;
        string title = comb.Title ?? string.Empty;
        string[] performers = comb.Performers ?? Array.Empty<string>();
        string album = comb.Album ?? string.Empty;
        string[] albumArtists = comb.AlbumArtists ?? Array.Empty<string>();
        string[] genres = comb.Genres ?? Array.Empty<string>();
        uint year = comb.Year;
        uint track = comb.Track;
        uint disc = comb.Disc;
        string lyrics = comb.Lyrics ?? string.Empty;
        var pics = comb.Pictures ?? Array.Empty<TagLib.IPicture>();
        double? rating = ReadStarsFromTagLib(file, path);

        file.RemoveTags(legacy);

        var xiph = file.GetTag(TagLib.TagTypes.Xiph, true);
        if (!string.IsNullOrEmpty(title) && string.IsNullOrEmpty(xiph.Title)) xiph.Title = title;
        if (performers.Length > 0 && (xiph.Performers == null || xiph.Performers.Length == 0)) xiph.Performers = performers;
        if (!string.IsNullOrEmpty(album) && string.IsNullOrEmpty(xiph.Album)) xiph.Album = album;
        if (albumArtists.Length > 0 && (xiph.AlbumArtists == null || xiph.AlbumArtists.Length == 0)) xiph.AlbumArtists = albumArtists;
        if (genres.Length > 0 && (xiph.Genres == null || xiph.Genres.Length == 0)) xiph.Genres = genres;
        if (year > 0 && xiph.Year == 0) xiph.Year = year;
        if (track > 0 && xiph.Track == 0) xiph.Track = track;
        if (disc > 0 && xiph.Disc == 0) xiph.Disc = disc;
        if (!string.IsNullOrEmpty(lyrics) && string.IsNullOrEmpty(xiph.Lyrics)) xiph.Lyrics = lyrics;

        var flacTag = file.GetTag(TagLib.TagTypes.FlacMetadata, true);
        if (pics.Length > 0 && (flacTag.Pictures == null || flacTag.Pictures.Length == 0))
            flacTag.Pictures = pics;

        if (rating is double s && s > 0 && xiph is TagLib.Ogg.XiphComment xc && xc.GetField("RATING").Length == 0)
        {
            int val100 = (int)Math.Clamp(Math.Round(s * 20), 10, 100);
            xc.SetField("RATING", val100.ToString(CultureInfo.InvariantCulture));
        }
        return true;
    }

    private static double? ReadStarsFromTagLib(TagLib.File file, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".mp3" && file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>())
            {
                if (StarsFromPopularimeter(frame.Rating) is double s) return s;
            }
        }
        else if (ext is ".flac" or ".ogg" or ".opus")
        {
            if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            {
                foreach (string key in new[] { "RATING", "FMPS_RATING" })
                {
                    string[] vals = xiph.GetField(key);
                    if (vals.Length > 0 && StarsFromVorbis(key, vals[0]) is double s) return s;
                }
            }
            if (ext == ".flac" && file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag flacId3)
            {
                foreach (var frame in flacId3.GetFrames<TagLib.Id3v2.PopularimeterFrame>())
                {
                    if (StarsFromPopularimeter(frame.Rating) is double s) return s;
                }
            }
        }
        else if (ext is ".m4a" or ".mp4" or ".aac" && file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
        {
            string? val = apple.GetDashBox("com.apple.iTunes", "RATING");
            if (!string.IsNullOrWhiteSpace(val) &&
                double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                num > 0)
            {
                return Math.Clamp(Math.Round((num > 5 ? num / 20.0 : num) * 2.0) / 2.0, 0.5, 5.0);
            }
        }
        else if (ext == ".wma" && file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag asf)
        {
            var descs = asf.GetDescriptors("WM/SharedUserRating");
            foreach (var desc in descs)
            {
                string raw = desc.ToString();
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) && num > 0)
                {
                    if (num >= 99) return 5.0;
                    double mapped = (num - 1.0) / 98.0 * 4.5 + 0.5;
                    return Math.Clamp(Math.Round(mapped * 2.0) / 2.0, 0.5, 5.0);
                }
            }
        }
        return null;
    }

    private static void ReadReplayGainFromTagLib(TagLib.File file, TrackItem item)
    {
        var tag = file.Tag;
        if (!double.IsNaN(tag.ReplayGainTrackGain) && double.IsFinite(tag.ReplayGainTrackGain))
        {
            item.ReplayGainTrackDb = Math.Round(tag.ReplayGainTrackGain, 2);
            item.ReplayGainFromTag = true;
        }
        if (!double.IsNaN(tag.ReplayGainAlbumGain) && double.IsFinite(tag.ReplayGainAlbumGain))
        {
            item.ReplayGainAlbumDb = Math.Round(tag.ReplayGainAlbumGain, 2);
            item.ReplayGainFromTag = true;
        }
        if (!double.IsNaN(tag.ReplayGainTrackPeak) && double.IsFinite(tag.ReplayGainTrackPeak) && tag.ReplayGainTrackPeak > 0)
            item.ReplayGainTrackPeak = (float)tag.ReplayGainTrackPeak;
        if (!double.IsNaN(tag.ReplayGainAlbumPeak) && double.IsFinite(tag.ReplayGainAlbumPeak) && tag.ReplayGainAlbumPeak > 0)
            item.ReplayGainAlbumPeak = (float)tag.ReplayGainAlbumPeak;

        if (item.ReplayGainTrackDb.HasValue) return;

        // Fall back to explicit custom tag frames (MP4 iTunes freeform, ID3v2 TXXX, Vorbis Xiph)
        void ApplyRaw(string? tg, string? tp, string? ag, string? ap)
        {
            if (ReplayGainAnalyzer.ParseGainDb(tg) is double tDb) { item.ReplayGainTrackDb = tDb; item.ReplayGainFromTag = true; }
            if (ReplayGainAnalyzer.ParsePeak(tp) is float tPk) item.ReplayGainTrackPeak = tPk;
            if (ReplayGainAnalyzer.ParseGainDb(ag) is double aDb) { item.ReplayGainAlbumDb = aDb; item.ReplayGainFromTag = true; }
            if (ReplayGainAnalyzer.ParsePeak(ap) is float aPk) item.ReplayGainAlbumPeak = aPk;
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            string? tg = null, tp = null, ag = null, ap = null;
            foreach (var f in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
            {
                string val = f.Text.Length > 0 ? f.Text[0] : string.Empty;
                if (f.Description.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase)) tg = val;
                else if (f.Description.Equals("REPLAYGAIN_TRACK_PEAK", StringComparison.OrdinalIgnoreCase)) tp = val;
                else if (f.Description.Equals("REPLAYGAIN_ALBUM_GAIN", StringComparison.OrdinalIgnoreCase)) ag = val;
                else if (f.Description.Equals("REPLAYGAIN_ALBUM_PEAK", StringComparison.OrdinalIgnoreCase)) ap = val;
            }
            ApplyRaw(tg, tp, ag, ap);
        }
        else if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            ApplyRaw(
                xiph.GetFirstField("REPLAYGAIN_TRACK_GAIN"),
                xiph.GetFirstField("REPLAYGAIN_TRACK_PEAK"),
                xiph.GetFirstField("REPLAYGAIN_ALBUM_GAIN"),
                xiph.GetFirstField("REPLAYGAIN_ALBUM_PEAK"));
        }
        else if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
        {
            ApplyRaw(
                apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_TRACK_GAIN"),
                apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_TRACK_PEAK"),
                apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_ALBUM_GAIN"),
                apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_ALBUM_PEAK"));
        }
    }
}
