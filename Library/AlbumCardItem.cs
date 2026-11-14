using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZenMusic.Library;

public sealed class AlbumCardItem : INotifyPropertyChanged
{
    private ImageSource? _coverImage;
    private bool _artLoadAttempted;

    public string Key { get; set; } = string.Empty;
    public string AlbumName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TrackCount { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public string? CoverTrackPath { get; set; }
    public List<TrackItem> Tracks { get; } = new();

    public bool ArtLoadAttempted
    {
        get => _artLoadAttempted;
        set => _artLoadAttempted = value;
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
    public string MetaText => Year > 0
        ? $"{Year} · {TrackCount} {(TrackCount == 1 ? "track" : "tracks")}"
        : $"{TrackCount} {(TrackCount == 1 ? "track" : "tracks")}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
