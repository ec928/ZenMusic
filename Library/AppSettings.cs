using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZenMusic.Audio;

namespace ZenMusic.Library;

public sealed class SavedPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<string> TrackPaths { get; set; } = new();
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
    public SpeakerLayout SpeakerLayout { get; set; } = SpeakerLayout.Stereo20;
    public float CenterLevel { get; set; } = 0.55f;
    public float LfeGain { get; set; } = 0.85f;
    public float RearLevel { get; set; } = 0.60f;
    public string VisualizerMode { get; set; } = "Spectrum"; // Spectrum | Waveform | Off
    public bool VisualizerEnabled { get; set; } = true;
    public string Theme { get; set; } = "Dark"; // Dark | Light
    public double Volume { get; set; } = 70;
    public bool Shuffle { get; set; }
    public bool RepeatAll { get; set; } = true;

    // Persistent sort state
    public string SortColumn { get; set; } = "Index";
    public bool SortAscending { get; set; } = true;

    // Persistent window geometry
    public int WindowWidth { get; set; } = 1480;
    public int WindowHeight { get; set; } = 900;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool IsMaximized { get; set; }

    public List<SavedPlaylist> Playlists { get; set; } = new();
    public List<CustomFilterCriteria> CustomFilters { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public void ResetAudioAndUiToDefaults()
    {
        OutputDeviceId = string.Empty;
        SpeakerLayout = SpeakerLayout.Stereo20;
        CenterLevel = 0.55f;
        LfeGain = 0.85f;
        RearLevel = 0.60f;
        VisualizerMode = "Spectrum";
        VisualizerEnabled = true;
        Theme = "Dark";
        WindowWidth = 1480;
        WindowHeight = 900;
        WindowX = null;
        WindowY = null;
        IsMaximized = false;
    }

    public static AppSettings Load()
    {
        string path = AppPaths.SettingsPath;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            try
            {
                File.Copy(path, path + ".unreadable", overwrite: true);
            }
            catch { }
            return new AppSettings();
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
