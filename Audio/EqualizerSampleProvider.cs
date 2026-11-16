using NAudio.Dsp;
using NAudio.Wave;
using System;
using System.Collections.Generic;

namespace ZenMusic.Audio;

/// <summary>
/// 10-band stereo/multichannel peaking EQ sample provider with zero-overhead bypass when flat.
/// </summary>
public sealed class EqualizerSampleProvider : ISampleProvider
{
    public static readonly float[] BandFrequencies =
    {
        32f, 64f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f
    };

    public static readonly string[] BandLabels =
    {
        "32", "64", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"
    };

    public static IReadOnlyDictionary<string, double[]> Presets { get; } = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Flat"]       = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["Rock"]       = new double[] { 4.5, 3.5, 2.0, 0.5, -1.0, -0.5, 1.5, 3.5, 4.5, 4.5 },
        ["Pop"]        = new double[] { -1.5, 1.0, 3.0, 4.0, 3.0, 0.5, -1.0, -1.0, 1.0, 2.0 },
        ["Jazz"]       = new double[] { 3.0, 2.0, 1.0, 1.5, -1.5, -1.5, 0.0, 1.5, 2.5, 3.0 },
        ["Classical"]  = new double[] { 3.5, 2.5, 1.5, 0.5, -1.0, -1.0, 0.0, 1.5, 2.5, 3.5 },
        ["Bass Boost"] = new double[] { 6.0, 5.0, 3.5, 1.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
        ["Vocal"]      = new double[] { -2.5, -2.0, -1.0, 1.5, 3.5, 4.0, 3.5, 1.5, 0.0, -1.0 },
        ["Acoustic"]   = new double[] { 3.0, 2.5, 1.0, 0.5, 1.5, 1.5, 2.5, 3.0, 2.5, 1.5 },
        ["Electronic"] = new double[] { 4.5, 4.0, 1.5, 0.0, -1.5, 1.0, 1.5, 3.5, 4.0, 4.5 }
    };

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly object _filterLock = new();
    private readonly BiQuadFilter[,] _filters;
    private readonly float[] _gains = new float[BandFrequencies.Length];
    private volatile bool _bypass = true;

    public EqualizerSampleProvider(ISampleProvider source, double[]? initialGains = null)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _filters = new BiQuadFilter[_channels, BandFrequencies.Length];
        UpdateGains(initialGains);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public void UpdateGains(double[]? gains)
    {
        bool allFlat = true;
        lock (_filterLock)
        {
            for (int b = 0; b < BandFrequencies.Length; b++)
            {
                float g = gains != null && b < gains.Length
                    ? Math.Clamp((float)gains[b], -12f, 12f)
                    : 0f;
                _gains[b] = g;
                if (MathF.Abs(g) >= 0.05f) allFlat = false;

                float freq = Math.Min(BandFrequencies[b], _sampleRate * 0.45f);
                for (int ch = 0; ch < _channels; ch++)
                {
                    _filters[ch, b] = BiQuadFilter.PeakingEQ(_sampleRate, freq, 1.41f, g);
                }
            }
            _bypass = allFlat;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0 || _bypass) return read;

        lock (_filterLock)
        {
            if (_bypass) return read;
            for (int i = 0; i < read; i++)
            {
                int ch = i % _channels;
                float sample = buffer[offset + i];
                for (int b = 0; b < BandFrequencies.Length; b++)
                {
                    if (MathF.Abs(_gains[b]) >= 0.05f)
                        sample = _filters[ch, b].Transform(sample);
                }
                buffer[offset + i] = sample;
            }
        }
        return read;
    }
}
