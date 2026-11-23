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
    // Stored gains follow ReplayGain 2.0: relative to -18 LUFS, so values measured here and
    // values read from tags written by other taggers mean the same thing.
    public const double ReferenceLufs = -18.0;
    // Playback levels to -14 LUFS (the level streaming services play at), 4 dB above the reference.
    public const double PlaybackTargetLufs = -14.0;
    public const double TargetOffsetDb = PlaybackTargetLufs - ReferenceLufs;
    public const double MinGainDb = -15.0;
    public const double MaxGainDb = 12.0;
    // 1 = unweighted RMS of the loudest 50 ms windows (to 0.7.14). 2 = EBU R128 integrated loudness.
    public const int MeterVersion = 2;

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

        double clampedDb = Math.Clamp(chosenDb.Value + TargetOffsetDb + preampDb, MinGainDb, MaxGainDb);
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
        double? lufs = MeasureIntegratedLoudness(source, out float peak);
        if (lufs is not double l || peak <= 1e-6f)
            return null;
        double gainDb = Math.Round(Math.Clamp(ReferenceLufs - l, -30.0, 30.0), 2);
        return new ReplayGainAnalysis(gainDb, peak);
    }

    /// <summary>
    /// EBU R128 / ITU-R BS.1770-4 integrated loudness in LUFS: K-weighting, 400 ms blocks
    /// every 100 ms, absolute gate at -70 LUFS and relative gate 10 LU under the ungated mean.
    /// Returns null for silence or audio shorter than one block.
    /// </summary>
    public static double? MeasureIntegratedLoudness(ISampleProvider source, out float peak)
    {
        int channels = Math.Max(1, source.WaveFormat.Channels);
        double fs = Math.Max(8000, source.WaveFormat.SampleRate);

        // Channel weights: L, R, C = 1; LFE excluded; surrounds = 1.41 (5.1 order L R C LFE Ls Rs).
        var weight = new double[channels];
        for (int c = 0; c < channels; c++)
            weight[c] = channels >= 6 ? c switch { 3 => 0.0, 4 or 5 => 1.41, _ => 1.0 } : 1.0;

        // K-weighting: high shelf then high pass, coefficients for any sample rate (BS.1770 / libebur128).
        double K = Math.Tan(Math.PI * 1681.974450955533 / fs);
        double Q = 0.7071752369554196;
        double Vh = Math.Pow(10.0, 3.999843853973347 / 20.0);
        double Vb = Math.Pow(Vh, 0.4996667741545416);
        double a0 = 1.0 + K / Q + K * K;
        double sb0 = (Vh + Vb * K / Q + K * K) / a0;
        double sb1 = 2.0 * (K * K - Vh) / a0;
        double sb2 = (Vh - Vb * K / Q + K * K) / a0;
        double sa1 = 2.0 * (K * K - 1.0) / a0;
        double sa2 = (1.0 - K / Q + K * K) / a0;

        K = Math.Tan(Math.PI * 38.13547087602444 / fs);
        Q = 0.5003270373238773;
        a0 = 1.0 + K / Q + K * K;
        double ha1 = 2.0 * (K * K - 1.0) / a0;
        double ha2 = (1.0 - K / Q + K * K) / a0;

        var s1 = new double[channels]; var s2 = new double[channels];   // shelf state (direct form II)
        var h1 = new double[channels]; var h2 = new double[channels];   // high-pass state

        int subFrames = Math.Max(1, (int)Math.Round(fs / 10.0));        // 100 ms
        var subPower = new List<double>(8192);
        double subSum = 0;
        int subCount = 0;
        peak = 0f;

        var buffer = new float[channels * 4096];
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                double frameSum = 0;
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    float x = buffer[baseIdx + c];
                    float ax = Math.Abs(x);
                    if (ax > peak) peak = ax;
                    if (weight[c] == 0.0) continue;

                    double w = x - sa1 * s1[c] - sa2 * s2[c];
                    double y = sb0 * w + sb1 * s1[c] + sb2 * s2[c];
                    s2[c] = s1[c]; s1[c] = w;

                    double v = y - ha1 * h1[c] - ha2 * h2[c];
                    double z = v - 2.0 * h1[c] + h2[c];
                    h2[c] = h1[c]; h1[c] = v;

                    frameSum += weight[c] * z * z;
                }
                subSum += frameSum;
                if (++subCount >= subFrames)
                {
                    subPower.Add(subSum / subCount);
                    subSum = 0;
                    subCount = 0;
                }
            }
        }

        if (subPower.Count < 4) return null;

        const double absGate = 1.1724653e-7; // power of -70 LUFS: 10^((-70 + 0.691) / 10)
        var blocks = new List<double>(subPower.Count);
        for (int i = 3; i < subPower.Count; i++)
        {
            double p = (subPower[i] + subPower[i - 1] + subPower[i - 2] + subPower[i - 3]) * 0.25;
            if (p > absGate) blocks.Add(p);
        }
        if (blocks.Count == 0) return null;

        double mean = 0;
        foreach (double p in blocks) mean += p;
        mean /= blocks.Count;
        double relGate = mean * 0.1; // -10 LU

        double gated = 0;
        int n = 0;
        foreach (double p in blocks)
        {
            if (p > relGate) { gated += p; n++; }
        }
        if (n == 0) return null;
        return -0.691 + 10.0 * Math.Log10(gated / n);
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
