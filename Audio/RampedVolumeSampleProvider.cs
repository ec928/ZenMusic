using NAudio.Wave;
using System;

namespace ZenMusic.Audio;

/// <summary>
/// Applies playback volume with a short ramp so a burst of changes (a volume wheel,
/// or a fast drag of the slider) does not step the waveform hard enough to click.
/// </summary>
internal sealed class RampedVolumeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _maxStepPerFrame;
    private volatile float _target;
    private float _current;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float Volume
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 1f);
    }

    public RampedVolumeSampleProvider(ISampleProvider source, float initial)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        int rampFrames = Math.Max(1, source.WaveFormat.SampleRate / 20); // ~50 ms across the full range
        _maxStepPerFrame = 1f / rampFrames;
        _target = Math.Clamp(initial, 0f, 1f);
        _current = _target;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        float target = _target;
        float gain = _current;
        int frames = read / _channels;
        int framePos = 0;
        for (int i = 0; i < frames * _channels; i++)
        {
            if (framePos == 0 && gain != target)
            {
                float diff = target - gain;
                gain += MathF.Abs(diff) <= _maxStepPerFrame ? diff : MathF.CopySign(_maxStepPerFrame, diff);
            }
            buffer[offset + i] *= gain;
            framePos++;
            if (framePos >= _channels) framePos = 0;
        }

        for (int i = frames * _channels; i < read; i++)
            buffer[offset + i] *= gain;

        _current = gain;
        return read;
    }
}
