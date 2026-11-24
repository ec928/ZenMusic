using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ZenMusic.Library;

public sealed class AlbumCardItem : INotifyPropertyChanged
{
    private ImageSource? _coverImage;
    private bool _artLoadAttempted;
    private bool _isNowPlaying;
    private double? _averageRating;

    public string Key { get; set; } = string.Empty;
    public string AlbumName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TrackCount { get; set; }
    public int AverageBitrate { get; set; }
    public DateTime LatestAddedUtc { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public string? CoverTrackPath { get; set; }
    public bool IsSingle { get; set; }
    public List<TrackItem> Tracks { get; } = new();

    public bool ArtLoadAttempted
    {
        get => _artLoadAttempted;
        set => _artLoadAttempted = value;
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

    public double? AverageRating
    {
        get => _averageRating;
        set
        {
            if (Nullable.Equals(_averageRating, value)) return;
            _averageRating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RatingBadgeText));
            OnPropertyChanged(nameof(RatingBadgeVis));
        }
    }

    public ImageSource? CoverImage
    {
        get => _coverImage;
        set
        {
            if (ReferenceEquals(_coverImage, value)) return;
            _coverImage = value;
            OnPropertyChanged();
        }
    }

    public string DisplayAlbum => string.IsNullOrWhiteSpace(AlbumName) ? "Unknown Album" : AlbumName;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown Artist" : Artist;

    public string ArtInitial
    {
        get
        {
            string src = !string.IsNullOrWhiteSpace(AlbumName) ? AlbumName : Artist;
            foreach (char c in src)
            {
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            }
            return "♪";
        }
    }

    public string DurationShortText => TotalDuration.TotalHours >= 1
        ? $"{(int)TotalDuration.TotalHours}h {TotalDuration.Minutes}m"
        : TotalDuration.TotalMinutes >= 1
            ? $"{(int)Math.Round(TotalDuration.TotalMinutes)}m"
            : Tracks.Count > 0 ? Tracks[0].DurationText : string.Empty;

    public string MetaText
    {
        get
        {
            if (IsSingle)
            {
                string dur = Tracks.Count > 0 ? Tracks[0].DurationText : string.Empty;
                return Year > 0
                    ? $"{Year} · Single{(string.IsNullOrEmpty(dur) ? "" : $" · {dur}")}"
                    : $"Single{(string.IsNullOrEmpty(dur) ? "" : $" · {dur}")}";
            }
            return Year > 0
                ? $"{Year} · {TrackCount} {(TrackCount == 1 ? "track" : "tracks")}"
                : $"{TrackCount} {(TrackCount == 1 ? "track" : "tracks")}";
        }
    }

    public string BrowseSubtitleText
    {
        get
        {
            string dur = DurationShortText;
            string durPart = string.IsNullOrEmpty(dur) ? string.Empty : $" · {dur}";
            return Year > 0
                ? $"{DisplayArtist} · {Year} · {TrackCount} {(TrackCount == 1 ? "track" : "tracks")}{durPart}"
                : $"{DisplayArtist} · {TrackCount} {(TrackCount == 1 ? "track" : "tracks")}{durPart}";
        }
    }

    public string PlayTooltipText => IsSingle ? "Play track" : $"Play album ({TrackCount} tracks)";

    public string RatingBadgeText => AverageRating is double r && r > 0
        ? string.Format(CultureInfo.InvariantCulture, "★ {0:0.0}", r)
        : string.Empty;

    public Visibility RatingBadgeVis => AverageRating is double r && r > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
