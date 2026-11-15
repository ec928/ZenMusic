using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ZenMusic.Library;

public sealed class TrackItem : INotifyPropertyChanged
{
    private double? _rating;
    private bool _isNowPlaying;
    private bool _isEvenRow;
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
            OnPropertyChanged(nameof(HasDirtyFilename));
        }
    }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public TimeSpan Duration { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; } = 2;
    public string Codec { get; set; } = string.Empty;
    public DateTime DateAddedUtc
    {
        get => _dateAddedUtc;
        set
        {
            if (_dateAddedUtc == value) return;
            _dateAddedUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateAddedText));
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
    public string ChannelsText => Channels >= 6 ? "5.1" : Channels == 1 ? "Mono" : "2.0";
    public string DateAddedText => DateAddedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    public void CopyMetadataFrom(TrackItem other)
    {
        if (ReferenceEquals(this, other)) return;

        bool changed = Index != other.Index
            || !string.Equals(Title, other.Title, StringComparison.Ordinal)
            || !string.Equals(Artist, other.Artist, StringComparison.Ordinal)
            || !string.Equals(Album, other.Album, StringComparison.Ordinal)
            || !string.Equals(Genre, other.Genre, StringComparison.Ordinal)
            || Year != other.Year
            || TrackNumber != other.TrackNumber
            || Duration != other.Duration
            || Bitrate != other.Bitrate
            || SampleRate != other.SampleRate
            || Channels != other.Channels
            || !string.Equals(Codec, other.Codec, StringComparison.Ordinal)
            || DateAddedUtc != other.DateAddedUtc
            || IsCloudOnly != other.IsCloudOnly
            || HasEmbeddedArt != other.HasEmbeddedArt
            || (other.Rating.HasValue && !Nullable.Equals(_rating, other.Rating));

        Index = other.Index;
        Title = other.Title;
        Artist = other.Artist;
        Album = other.Album;
        Genre = other.Genre;
        Year = other.Year;
        TrackNumber = other.TrackNumber;
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
