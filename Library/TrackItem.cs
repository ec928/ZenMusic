using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ZenMusic.Library;

public sealed class TrackItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _albumArtist = string.Empty;
    private string _genre = string.Empty;
    private int _year;
    private int _trackNumber;
    private int _discNumber;
    private double? _rating;
    private bool _isNowPlaying;
    private bool _isEvenRow;
    private double _queueRowOpacity = 1.0;
    private string _path = string.Empty;
    private DateTime _dateAddedUtc;
    private int _playCount;
    private DateTime? _lastPlayedUtc;
    private object? _coverThumbnail;

    public int Index { get; set; }
    public string Path
    {
        get => _path;
        set
        {
            if (string.Equals(_path, value, StringComparison.Ordinal)) return;
            _path = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(ArtInitial));
            OnPropertyChanged(nameof(HasDirtyFilename));
            OnPropertyChanged(nameof(TechHoverText));
        }
    }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Title
    {
        get => _title;
        set
        {
            if (string.Equals(_title, value, StringComparison.Ordinal)) return;
            _title = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(ArtInitial));
        }
    }
    public string Artist
    {
        get => _artist;
        set
        {
            if (string.Equals(_artist, value, StringComparison.Ordinal)) return;
            _artist = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayArtist));
            OnPropertyChanged(nameof(ArtInitial));
        }
    }
    public string Album
    {
        get => _album;
        set
        {
            if (string.Equals(_album, value, StringComparison.Ordinal)) return;
            _album = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayAlbum));
            OnPropertyChanged(nameof(ArtInitial));
            OnPropertyChanged(nameof(AlbumHoverText));
        }
    }
    public string AlbumArtist
    {
        get => _albumArtist;
        set
        {
            if (string.Equals(_albumArtist, value, StringComparison.Ordinal)) return;
            _albumArtist = value;
            OnPropertyChanged();
        }
    }
    public string Genre
    {
        get => _genre;
        set
        {
            if (string.Equals(_genre, value, StringComparison.Ordinal)) return;
            _genre = value;
            OnPropertyChanged();
        }
    }
    public int Year
    {
        get => _year;
        set
        {
            if (_year == value) return;
            _year = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(YearText));
            OnPropertyChanged(nameof(TileMetaText));
        }
    }
    public int TrackNumber
    {
        get => _trackNumber;
        set
        {
            if (_trackNumber == value) return;
            _trackNumber = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AlbumHoverText));
            OnPropertyChanged(nameof(DetailText));
        }
    }
    public int DiscNumber
    {
        get => _discNumber;
        set
        {
            if (_discNumber == value) return;
            _discNumber = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AlbumHoverText));
            OnPropertyChanged(nameof(DetailText));
        }
    }
    public TimeSpan Duration { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; } = 2;
    public string Codec { get; set; } = string.Empty;
    public double? ReplayGainTrackDb { get; set; }
    public double? ReplayGainAlbumDb { get; set; }
    public float? ReplayGainTrackPeak { get; set; }
    public float? ReplayGainAlbumPeak { get; set; }
    public bool ReplayGainFromTag { get; set; }
    /// <summary>Which loudness meter produced a measured (not tag) gain; see ReplayGainAnalyzer.MeterVersion.</summary>
    public int ReplayGainMeterVersion { get; set; }
    /// <summary>True when the gain was measured by an older meter and should be measured again.</summary>
    public bool ReplayGainIsStale => ReplayGainTrackDb.HasValue && !ReplayGainFromTag
        && ReplayGainMeterVersion < ZenMusic.Audio.ReplayGainAnalyzer.MeterVersion;
    public DateTime DateAddedUtc
    {
        get => _dateAddedUtc;
        set
        {
            if (_dateAddedUtc == value) return;
            _dateAddedUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateAddedText));
            OnPropertyChanged(nameof(DateAddedFullText));
            OnPropertyChanged(nameof(DateAddedHoverText));
        }
    }
    public bool IsCloudOnly { get; set; }
    public bool HasEmbeddedArt { get; set; }
    public long FileSize { get; set; }
    public long FileWriteUtcTicks { get; set; }
    public bool HasDirtyFilename => Tagging.AutoTagger.HasDirtyFilename(Path);

    public int PlayCount
    {
        get => _playCount;
        set
        {
            if (_playCount == value) return;
            _playCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateAddedHoverText));
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public DateTime? LastPlayedUtc
    {
        get => _lastPlayedUtc;
        set
        {
            if (_lastPlayedUtc == value) return;
            _lastPlayedUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateAddedHoverText));
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public double? Rating
    {
        get => _rating;
        set
        {
            if (Nullable.Equals(_rating, value)) return;
            _rating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RatingDisplay));
        }
    }

    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set
        {
            if (_isNowPlaying == value) return;
            _isNowPlaying = value;
            OnPropertyChanged();
        }
    }

    public bool IsEvenRow
    {
        get => _isEvenRow;
        set
        {
            if (_isEvenRow == value) return;
            _isEvenRow = value;
            OnPropertyChanged();
        }
    }

    public static bool ShowAlbumTrackNumbers { get; set; }

    public double QueueRowOpacity
    {
        get => _queueRowOpacity;
        set
        {
            if (Math.Abs(_queueRowOpacity - value) < 0.01) return;
            _queueRowOpacity = value;
            OnPropertyChanged();
        }
    }

    public string TrackNumberBadge => TrackNumber > 0 ? TrackNumber.ToString("D2") : string.Empty;
    public bool IsTrackBadgeVisible => ShowAlbumTrackNumbers && TrackNumber > 0;
    public string ArtistHoverText => string.IsNullOrWhiteSpace(Artist) ? string.Empty : $"{Artist}\nCtrl+Click to filter by artist";
    public string AlbumCellHoverText => string.IsNullOrWhiteSpace(Album) ? string.Empty : $"{AlbumHoverText}\nCtrl+Click to filter by album";

    public void NotifyTrackBadgeChanged()
    {
        OnPropertyChanged(nameof(TrackNumberBadge));
        OnPropertyChanged(nameof(IsTrackBadgeVisible));
    }

    public bool ArtLoadAttempted { get; set; }

    public object? CoverThumbnail
    {
        get => _coverThumbnail;
        set
        {
            if (ReferenceEquals(_coverThumbnail, value)) return;
            _coverThumbnail = value;
            OnPropertyChanged();
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path).Trim() : Title.Trim();
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown Artist" : Artist.Trim();
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown Album" : Album.Trim();
    /// <summary>First character of the artist (else the title), shown on the generated cover of tracks without art.</summary>
    public string ArtInitial
    {
        get
        {
            string source = string.IsNullOrWhiteSpace(Artist) ? DisplayTitle : Artist.Trim();
            foreach (char ch in source)
            {
                if (char.IsLetterOrDigit(ch) && !char.IsSurrogate(ch))
                    return char.ToUpperInvariant(ch).ToString();
            }
            return source.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(source) : "♪";
        }
    }

    public string YearText => Year > 0 ? Year.ToString() : string.Empty;
    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}"
        : $"{Duration.Minutes:D2}:{Duration.Seconds:D2}";
    public string TileMetaText => Year > 0 ? $"{Year} · {DurationText}" : DurationText;
    public string BitrateText => Bitrate > 0 ? $"{Bitrate} kbps" : string.Empty;
    public string BitrateNumberText => Bitrate > 0 ? Bitrate.ToString() : string.Empty;
    public string ChannelsText => Channels >= 6 ? "5.1" : Channels == 1 ? "Mono" : "2.0";
    public string DateAddedText => DateAddedUtc.ToLocalTime().ToString("MM'/'yyyy");
    public string DateAddedFullText => DateAddedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    public static string ActiveDetailField { get; set; } = "Bitrate";

    public string DetailText => GetDetailText(ActiveDetailField);

    public string GetDetailText(string field) => field switch
    {
        "Track" => DiscNumber > 1 && TrackNumber > 0
            ? $"{DiscNumber}.{TrackNumber:D2}"
            : TrackNumber > 0 ? TrackNumber.ToString("D2") : string.Empty,
        "Codec" => Codec,
        "SampleRate" => SampleRate > 0
            ? (SampleRate % 1000 == 0
                ? $"{SampleRate / 1000}k"
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#}k", SampleRate / 1000.0))
            : string.Empty,
        "Channels" => (SampleRate > 0 || Bitrate > 0 || !string.IsNullOrWhiteSpace(Codec)) ? ChannelsText : string.Empty,
        "ReplayGain" => ReplayGainTrackDb.HasValue
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:+0.0;-0.0;0.0}", ReplayGainTrackDb.Value)
            : string.Empty,
        "Plays" => PlayCount.ToString(),
        "LastPlayed" => LastPlayedUtc.HasValue ? LastPlayedUtc.Value.ToLocalTime().ToString("MM'/'yy") : "—",
        _ => BitrateNumberText
    };

    public string? AlbumHoverText
    {
        get
        {
            string album = Album?.Trim() ?? string.Empty;
            string trackLabel = TrackNumber > 0
                ? (DiscNumber > 1 ? $"Disc {DiscNumber}, Track {TrackNumber}" : $"Track {TrackNumber}")
                : (DiscNumber > 1 ? $"Disc {DiscNumber}" : string.Empty);

            if (album.Length > 0 && trackLabel.Length > 0) return $"{album}\n{trackLabel}";
            if (album.Length > 0) return album;
            if (trackLabel.Length > 0) return trackLabel;
            return null;
        }
    }

    public string DateAddedHoverText
    {
        get
        {
            if (PlayCount <= 0 && !LastPlayedUtc.HasValue)
                return DateAddedFullText;

            var sb = new System.Text.StringBuilder(DateAddedFullText);
            if (PlayCount > 0)
                sb.Append($"\nPlays: {PlayCount}");
            if (LastPlayedUtc.HasValue)
                sb.Append($"\nLast played: {LastPlayedUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}");
            return sb.ToString();
        }
    }

    public string? TechHoverText
    {
        get
        {
            var lines = new System.Collections.Generic.List<string>(3);

            var specs = new System.Collections.Generic.List<string>(4);
            if (!string.IsNullOrWhiteSpace(Codec)) specs.Add(Codec.Trim());
            if (Bitrate > 0) specs.Add(BitrateText);
            if (SampleRate > 0)
                specs.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} kHz", SampleRate / 1000.0));
            if (specs.Count > 0 && Channels > 0)
                specs.Add(ChannelsText);
            if (specs.Count > 0)
                lines.Add(string.Join(" · ", specs));

            if (ReplayGainTrackDb.HasValue || ReplayGainAlbumDb.HasValue)
            {
                var rg = new System.Collections.Generic.List<string>(2);
                if (ReplayGainTrackDb.HasValue)
                    rg.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Track {0:+0.0;-0.0;0.0} dB", ReplayGainTrackDb.Value));
                if (ReplayGainAlbumDb.HasValue)
                    rg.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Album {0:+0.0;-0.0;0.0} dB", ReplayGainAlbumDb.Value));
                lines.Add($"ReplayGain: {string.Join(" · ", rg)}");
            }

            if (!string.IsNullOrWhiteSpace(FileName))
                lines.Add(FileName);

            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
    }

    public void NotifyDetailChanged()
    {
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(TechHoverText));
    }

    public void CopyMetadataFrom(TrackItem other)
    {
        if (ReferenceEquals(this, other)) return;

        bool changed = Index != other.Index
            || !string.Equals(Title, other.Title, StringComparison.Ordinal)
            || !string.Equals(Artist, other.Artist, StringComparison.Ordinal)
            || !string.Equals(Album, other.Album, StringComparison.Ordinal)
            || !string.Equals(AlbumArtist, other.AlbumArtist, StringComparison.Ordinal)
            || !string.Equals(Genre, other.Genre, StringComparison.Ordinal)
            || Year != other.Year
            || TrackNumber != other.TrackNumber
            || DiscNumber != other.DiscNumber
            || Duration != other.Duration
            || Bitrate != other.Bitrate
            || SampleRate != other.SampleRate
            || Channels != other.Channels
            || !string.Equals(Codec, other.Codec, StringComparison.Ordinal)
            || DateAddedUtc != other.DateAddedUtc
            || IsCloudOnly != other.IsCloudOnly
            || HasEmbeddedArt != other.HasEmbeddedArt
            || ReplayGainTrackDb != other.ReplayGainTrackDb
            || ReplayGainAlbumDb != other.ReplayGainAlbumDb
            || (other.Rating.HasValue && !Nullable.Equals(_rating, other.Rating));

        Index = other.Index;
        Title = other.Title;
        Artist = other.Artist;
        Album = other.Album;
        AlbumArtist = other.AlbumArtist;
        Genre = other.Genre;
        Year = other.Year;
        TrackNumber = other.TrackNumber;
        DiscNumber = other.DiscNumber;
        Duration = other.Duration;
        Bitrate = other.Bitrate;
        SampleRate = other.SampleRate;
        Channels = other.Channels;
        Codec = other.Codec;
        DateAddedUtc = other.DateAddedUtc;
        IsCloudOnly = other.IsCloudOnly;
        if (FileWriteUtcTicks != other.FileWriteUtcTicks || FileSize != other.FileSize || HasEmbeddedArt != other.HasEmbeddedArt)
        {
            ArtLoadAttempted = false;
            _coverThumbnail = null;
        }
        HasEmbeddedArt = other.HasEmbeddedArt;
        FileSize = other.FileSize;
        FileWriteUtcTicks = other.FileWriteUtcTicks;
        if (other.Rating.HasValue) _rating = other.Rating;
        if (other.ReplayGainTrackDb.HasValue)
        {
            ReplayGainTrackDb = other.ReplayGainTrackDb;
            ReplayGainTrackPeak = other.ReplayGainTrackPeak;
            ReplayGainFromTag = other.ReplayGainFromTag;
            ReplayGainMeterVersion = other.ReplayGainMeterVersion;
        }
        if (other.ReplayGainAlbumDb.HasValue)
        {
            ReplayGainAlbumDb = other.ReplayGainAlbumDb;
            ReplayGainAlbumPeak = other.ReplayGainAlbumPeak;
        }

        if (changed) OnPropertyChanged(string.Empty);
    }

    public string RatingDisplay
    {
        get
        {
            if (Rating is not double r || r <= 0) return string.Empty;
            int full = (int)Math.Floor(r);
            bool half = (r - full) >= 0.4;
            return new string('\u2605', full) + (half ? "\u00BD" : string.Empty);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
