using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ZenMusic.Library;

public sealed class TrackItem : INotifyPropertyChanged
{
    private double? _rating;
    private bool _isNowPlaying;
    private string _path = string.Empty;
    private DateTime _dateAddedUtc;

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
        }
    }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }
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

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Title;
    public string YearText => Year > 0 ? Year.ToString() : string.Empty;
    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}"
        : $"{Duration.Minutes:D2}:{Duration.Seconds:D2}";
    public string BitrateText => Bitrate > 0 ? $"{Bitrate} kbps" : string.Empty;
    public string ChannelsText => Channels >= 6 ? "5.1" : Channels == 1 ? "Mono" : "2.0";
    public string DateAddedText => DateAddedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

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
