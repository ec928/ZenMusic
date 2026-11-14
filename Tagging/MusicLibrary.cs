using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
            IsCloudOnly = isCloudOnly
        };

        if (isCloudOnly) return item;

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            item.Title = string.IsNullOrWhiteSpace(tag.Title) ? item.Title : tag.Title.Trim();
            item.Artist = tag.JoinedPerformers?.Trim() ?? string.Empty;
            item.Album = tag.Album?.Trim() ?? string.Empty;
            item.Genre = tag.JoinedGenres?.Trim() ?? string.Empty;
            item.Year = (int)tag.Year;
            item.HasEmbeddedArt = tag.Pictures != null && tag.Pictures.Length > 0;

            if (file.Properties != null)
            {
                item.Duration = file.Properties.Duration;
                item.Bitrate = file.Properties.AudioBitrate;
                item.SampleRate = file.Properties.AudioSampleRate;
                item.Channels = Math.Max(1, file.Properties.AudioChannels);
            }

            item.Rating = ReadStarsFromTagLib(file, path);
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

    public sealed class TrackDetails
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public int Year { get; set; }
        public int TrackNumber { get; set; }
        public double? Rating { get; set; }
        public string Lyrics { get; set; } = string.Empty;
        public byte[]? ArtBytes { get; set; }
        public string ArtMimeType { get; set; } = "image/jpeg";
        public bool ArtChanged { get; set; }
    }

    public static TrackDetails ReadFullDetails(string path)
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
            d.Lyrics = tag.Lyrics ?? string.Empty;
            d.Rating = ReadStarsFromTagLib(file, path);
            if (tag.Pictures is { Length: > 0 } && tag.Pictures[0].Data?.Data is { Length: > 0 } picBytes)
            {
                d.ArtBytes = picBytes;
                d.ArtMimeType = string.IsNullOrWhiteSpace(tag.Pictures[0].MimeType) ? "image/jpeg" : tag.Pictures[0].MimeType;
            }
            else
            {
                d.ArtBytes = ReadEmbeddedArtBytes(path);
            }
        }
        catch { }
        return d;
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
                    var tag = file.Tag;
                    tag.Title = d.Title;
                    tag.Performers = string.IsNullOrWhiteSpace(d.Artist) ? Array.Empty<string>() : new[] { d.Artist.Trim() };
                    tag.Album = d.Album;
                    tag.AlbumArtists = string.IsNullOrWhiteSpace(d.AlbumArtist) ? Array.Empty<string>() : new[] { d.AlbumArtist.Trim() };
                    tag.Genres = string.IsNullOrWhiteSpace(d.Genre) ? Array.Empty<string>() : new[] { d.Genre.Trim() };
                    tag.Year = d.Year > 0 ? (uint)d.Year : 0;
                    tag.Track = d.TrackNumber > 0 ? (uint)d.TrackNumber : 0;
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
                    file.Save();
                }

                try { File.SetLastWriteTimeUtc(path, originalWriteUtc); } catch { }
                TryWriteStars(path, d.Rating);
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

    public static bool TryWriteStars(string path, double? stars)
    {
        try
        {
            DateTime originalWriteUtc = File.GetLastWriteTimeUtc(path);
            bool saved = false;
            using (var file = TagLib.File.Create(path))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".mp3")
                {
                    var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
                    var popm = TagLib.Id3v2.PopularimeterFrame.Get(id3, "Windows Media Player 9 Series", true);
                    popm.Rating = stars is double s && s > 0 ? PopularimeterFromStars(s) : (byte)0;
                    file.Save();
                    saved = true;
                }
                else if (ext is ".flac" or ".ogg" or ".opus")
                {
                    var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);
                    if (stars is double s && s > 0)
                    {
                        int val100 = (int)Math.Clamp(Math.Round(s * 20), 10, 100);
                        xiph.SetField("RATING", val100.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        xiph.RemoveField("RATING");
                        xiph.RemoveField("FMPS_RATING");
                    }
                    file.Save();
                    saved = true;
                }
            }
            if (saved)
            {
                try { File.SetLastWriteTimeUtc(path, originalWriteUtc); } catch { }
            }
            return saved;
        }
        catch { }
        return false;
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
        else if (ext is ".flac" or ".ogg" or ".opus" && file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            foreach (string key in new[] { "RATING", "FMPS_RATING" })
            {
                string[] vals = xiph.GetField(key);
                if (vals.Length > 0 && StarsFromVorbis(key, vals[0]) is double s) return s;
            }
        }
        return null;
    }
}
