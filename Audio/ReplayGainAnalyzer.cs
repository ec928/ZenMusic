using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZenMusic.Audio;

public readonly record struct ReplayGainAnalysis(double TrackGainDb, float TrackPeak);

/// <summary>
/// Reads ReplayGain gain/peak strings, computes safe anti-clipping linear multipliers,
/// measures perceived track loudness without modifying files, and smoothly ramps gain
/// so transitions never click or pop.
/// </summary>
public static class ReplayGainAnalyzer
{
    // Slightly hotter than strict -18 LUFS (-15.5 dBFS RMS) so leveled tracks match desktop/browser
    // loudness comfortably on Windows while still taming clipped modern masters and lifting quiet ones.
    public const double TargetRmsDbfs = -15.5;
    public const double MinGainDb = -15.0;
    public const double MaxGainDb = 12.0;

    public static double? ParseGainDb(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        int dbIdx = s.IndexOf("db", StringComparison.OrdinalIgnoreCase);
        if (dbIdx >= 0) s = s[..dbIdx].Trim();
        if (s.StartsWith('+')) s = s[1..].Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) &&
            double.IsFinite(val))
        {
            return Math.Round(Math.Clamp(val, -30.0, 30.0), 2);
        }
        return null;
    }

    public static float? ParsePeak(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float val) &&
            float.IsFinite(val) && val > 0f && val <= 10f)
        {
            return val;
        }
        return null;
    }

    public static float ComputeLinearFactor(
        double? trackGainDb,
        float? trackPeak,
        double? albumGainDb,
        float? albumPeak,
        string? mode,
        double preampDb = 0.0)
    {
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            return 1.0f;

        bool useAlbum = string.Equals(mode, "Album", StringComparison.OrdinalIgnoreCase);
        double? chosenDb = useAlbum && albumGainDb.HasValue ? albumGainDb : trackGainDb;
        float? chosenPeak = useAlbum && albumGainDb.HasValue ? (albumPeak ?? trackPeak) : trackPeak;

        if (!chosenDb.HasValue || !double.IsFinite(chosenDb.Value))
            return 1.0f;

        double clampedDb = Math.Clamp(chosenDb.Value + preampDb, MinGainDb, MaxGainDb);
        double linear = Math.Pow(10.0, clampedDb / 20.0);

        if (chosenPeak is float peak && float.IsFinite(peak) && peak > 0.001f)
        {
            double maxNoClip = 0.98 / peak;
            if (linear > maxNoClip) linear = maxNoClip;
        }

        return (float)Math.Clamp(linear, 0.05, 4.0);
    }

    public static ReplayGainAnalysis? AnalyzeFile(string path)
    {
        try
        {
            using var reader = AudioFileOpener.OpenWaveStream(path);
            return AnalyzeSampleProvider(reader.ToSampleProvider());
        }
        catch
        {
            return null;
        }
    }

    public static ReplayGainAnalysis? AnalyzeSampleProvider(ISampleProvider source)
    {
        int channels = Math.Max(1, source.WaveFormat.Channels);
        int sampleRate = Math.Max(8000, source.WaveFormat.SampleRate);
        int blockFrames = Math.Max(1, sampleRate / 20); // 50 ms windows

        var buffer = new float[channels * 4096];
        var activeBlockDb = new List<double>(4096);

        double blockSumSq = 0;
        int blockSamples = 0;
        int targetSamples = blockFrames * channels;
        float peak = 0f;

        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            for (int i = 0; i < read; i++)
            {
                float s = buffer[i];
                float abs = Math.Abs(s);
                if (abs > peak) peak = abs;

                blockSumSq += (double)s * s;
                blockSamples++;

                if (blockSamples >= targetSamples)
                {
                    double meanSq = blockSumSq / blockSamples;
                    if (meanSq > 1e-7) // above -70 dBFS noise floor
                    {
                        activeBlockDb.Add(10.0 * Math.Log10(meanSq));
                    }
                    blockSumSq = 0;
                    blockSamples = 0;
                }
            }
        }

        if (blockSamples > 0)
        {
            double meanSq = blockSumSq / blockSamples;
            if (meanSq > 1e-7)
                activeBlockDb.Add(10.0 * Math.Log10(meanSq));
        }

        if (activeBlockDb.Count == 0 || peak <= 1e-6f)
            return null;

        // 95th-percentile active 50 ms window matches ReplayGain perceptual loudness gating.
        activeBlockDb.Sort();
        int pctIndex = Math.Clamp((int)Math.Floor((activeBlockDb.Count - 1) * 0.95), 0, activeBlockDb.Count - 1);
        double refLevelDb = activeBlockDb[pctIndex];
        double gainDb = Math.Round(Math.Clamp(TargetRmsDbfs - refLevelDb, MinGainDb, MaxGainDb), 2);
        return new ReplayGainAnalysis(gainDb, peak);
    }
}

/// <summary>
/// Thread-safe sample provider that smoothly glides loudness toward TargetGain over ~100 ms
/// so background loudness analysis or Settings toggles never click during playback.
/// </summary>
public sealed class SmoothGainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _maxStepPerFrame;
    private volatile float _targetGain;
    private float _currentGain;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float TargetGain
    {
        get => _targetGain;
        set => _targetGain = Math.Clamp(value, 0.05f, 4.0f);
    }

    public SmoothGainSampleProvider(ISampleProvider source, float initialGain = 1.0f)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        int rampFrames = Math.Max(1, source.WaveFormat.SampleRate / 10); // ~100 ms ramp
        _maxStepPerFrame = 1.0f / rampFrames;
        _currentGain = Math.Clamp(initialGain, 0.05f, 4.0f);
        _targetGain = _currentGain;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        float target = _targetGain;
        float gain = _currentGain;

        if (Math.Abs(gain - target) < 1e-5f)
        {
            if (Math.Abs(gain - 1.0f) < 1e-5f) return read;
            for (int i = 0; i < read; i++)
                buffer[offset + i] *= gain;
            return read;
        }

        int framePos = 0;
        for (int i = 0; i < read; i++)
        {
            if (framePos == 0 && Math.Abs(gain - target) >= 1e-5f)
            {
                float diff = target - gain;
                float step = MathF.Abs(diff) <= _maxStepPerFrame ? diff : (diff >= 0f ? _maxStepPerFrame : -_maxStepPerFrame);
                gain += step;
            }
            buffer[offset + i] *= gain;
            framePos++;
            if (framePos >= _channels) framePos = 0;
        }

        _currentGain = gain;
        return read;
    }
}
