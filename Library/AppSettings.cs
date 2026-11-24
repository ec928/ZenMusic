using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZenMusic.Audio;

namespace ZenMusic.Library;

public sealed class SavedPlaylist : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal)) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public List<string> TrackPaths { get; set; } = new();

    [JsonIgnore]
    public int TrackCount => TrackPaths.Count;

    public void NotifyTracksChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrackCount)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CustomFilterCriteria
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public double? MinRating { get; set; }
    public string? TextContains { get; set; }
}

public sealed class AppSettings
{
    public string? MusicFolder { get; set; }
    public string OutputDeviceId { get; set; } = string.Empty;
    public bool WasapiExclusive { get; set; } = false;
    public SpeakerLayout SpeakerLayout { get; set; } = SpeakerLayout.Stereo20;
    public float CenterLevel { get; set; } = 0.55f;
    public float LfeGain { get; set; } = 0.85f;
    public float RearLevel { get; set; } = 0.60f;
    public string ReplayGainMode { get; set; } = "Track"; // Off | Track | Album
    public double ReplayGainPreampDb { get; set; } = 0.0;
    public string EqualizerPreset { get; set; } = "Flat";
    public double[] EqualizerGains { get; set; } = new double[10];
    public string VisualizerMode { get; set; } = "Spectrum"; // Spectrum | Waveform | Off
    public bool VisualizerEnabled { get; set; } = true;
    public bool PlaybarVisualizerEnabled { get; set; } = true;
    public bool WindowVisualizerEnabled { get; set; } = true;
    public string Theme { get; set; } = "Dark"; // Dark | Light | System
    public int CoverTint { get; set; } = 100; // strength of the cover-colour wash, percent: 0 = off, 100 = standard, 200 = vivid
    public double Volume { get; set; } = 70;
    public bool Shuffle { get; set; }
    public bool RepeatAll { get; set; } = true;
    public int RepeatMode { get; set; } = 1; // 0 = Off, 1 = All, 2 = One

    // Persistent workspace layout & view mode
    public int LayoutSchemaVersion { get; set; } = 6;
    public bool LeftPaneOpen { get; set; } = false;
    public bool RightPaneOpen { get; set; } = true;
    public double RightPaneWidth { get; set; } = 242;
    public double InspectorHeight { get; set; } = 212;
    public bool InspectorCollapsed { get; set; } = false;
    public int InspectorArtMode { get; set; } = 2;
    public string ViewMode { get; set; } = "Table"; // Table | Grid

    // Persistent sort state
    public string SortColumn { get; set; } = "Title";
    public bool SortAscending { get; set; } = true;
    public string DetailColumnField { get; set; } = "Bitrate";
    public double? CustomTitleStar { get; set; }
    public double? CustomArtistStar { get; set; }
    public double? CustomAlbumStar { get; set; }

    // Persistent window geometry
    public int WindowWidth { get; set; } = 1368;
    public int WindowHeight { get; set; } = 900;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool IsMaximized { get; set; }

    public List<SavedPlaylist> Playlists { get; set; } = new();
    public List<CustomFilterCriteria> CustomFilters { get; set; } = new();
    public List<string> FavouriteFolders { get; set; } = new();
    public List<string> RecentFolders { get; set; } = new();
    public List<string> SavedQueuePaths { get; set; } = new();
    public int SavedQueueIndex { get; set; } = -1;
    public double SavedPositionSeconds { get; set; }
    public Dictionary<string, bool> ColumnVisibility { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public void ResetAudioAndUiToDefaults()
    {
        OutputDeviceId = string.Empty;
        WasapiExclusive = false;
        SpeakerLayout = WasapiAudioEngine.GetRecommendedDirectLayout(OutputDeviceId);
        CenterLevel = 0.55f;
        LfeGain = 0.85f;
        RearLevel = 0.60f;
        ReplayGainMode = "Track";
        ReplayGainPreampDb = 0.0;
        EqualizerPreset = "Flat";
        EqualizerGains = new double[10];
        VisualizerMode = "Spectrum";
        VisualizerEnabled = true;
        PlaybarVisualizerEnabled = true;
        WindowVisualizerEnabled = true;
        Theme = "Dark";
        CoverTint = 100;
        LeftPaneOpen = false;
        RightPaneOpen = true;
        RightPaneWidth = 242;
        InspectorHeight = 212;
        InspectorCollapsed = false;
        InspectorArtMode = 2;
        ViewMode = "Table";
        DetailColumnField = "Bitrate";
        CustomTitleStar = null;
        CustomArtistStar = null;
        CustomAlbumStar = null;
        WindowWidth = 1368;
        WindowHeight = 900;
        WindowX = null;
        WindowY = null;
        IsMaximized = false;
    }

    public static AppSettings Load()
    {
        string path = AppPaths.SettingsPath;
        if (!File.Exists(path))
        {
            return new AppSettings
            {
                SpeakerLayout = WasapiAudioEngine.GetRecommendedDirectLayout(string.Empty)
            };
        }
        try
        {
            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            bool migrated = false;
            if (settings.LayoutSchemaVersion < 2)
            {
                settings.LeftPaneOpen = false;
                settings.LayoutSchemaVersion = 2;
                migrated = true;
            }
            if (settings.LayoutSchemaVersion < 3)
            {
                if (settings.SpeakerLayout == SpeakerLayout.Stereo20)
                {
                    settings.SpeakerLayout = WasapiAudioEngine.GetRecommendedDirectLayout(settings.OutputDeviceId);
                }
                settings.LayoutSchemaVersion = 3;
                migrated = true;
            }
            if (settings.LayoutSchemaVersion < 5)
            {
                settings.RightPaneWidth = 238;
                settings.InspectorHeight = 212;
                settings.LayoutSchemaVersion = 5;
                migrated = true;
            }
            if (settings.LayoutSchemaVersion < 6)
            {
                settings.RightPaneWidth = 242;
                settings.InspectorHeight = 212;
                if (settings.WindowWidth > 1368)
                    settings.WindowWidth = Math.Max(1120, settings.WindowWidth - 112);
                settings.LayoutSchemaVersion = 6;
                migrated = true;
            }
            if (string.Equals(settings.SortColumn, "Index", StringComparison.OrdinalIgnoreCase))
            {
                settings.SortColumn = "Title";
                migrated = true;
            }
            if (migrated)
            {
                settings.Save();
            }
            return settings;
        }
        catch
        {
            try
            {
                File.Copy(path, path + ".unreadable", overwrite: true);
            }
            catch { }
            return new AppSettings
            {
                SpeakerLayout = WasapiAudioEngine.GetRecommendedDirectLayout(string.Empty)
            };
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, JsonOptions);
            AtomicFile.WriteAllText(AppPaths.SettingsPath, json);
        }
        catch { }
    }
}
