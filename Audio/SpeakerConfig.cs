using NAudio.Wave;
using System;

namespace ZenMusic.Audio;

public enum SpeakerLayout
{
    Stereo20 = 0,
    Stereo21 = 1,
    Surround51Upmix = 2,
    Surround51Direct = 3
}

public sealed class SpeakerRoutingOptions
{
    public SpeakerLayout Layout { get; set; } = SpeakerLayout.Stereo20;
    public float CenterLevel { get; set; } = 0.55f;
    public float LfeGain { get; set; } = 0.85f;
    public float RearLevel { get; set; } = 0.60f;
}

/// <summary>
/// Routes and matrixes float32 PCM samples into 2.0 Stereo, 2.1 Stereo + LFE,
/// 5.1 Surround (Smart Stereo Upmix), or 5.1 Direct multichannel output.
/// Uses WaveFormatExtensible (5.1 channel mask 0x3F) when outputting 6 channels to a 5.1 WASAPI endpoint,
/// and gracefully folds to 2-channel spatial stereo if the Windows endpoint only supports 2 channels.
/// </summary>
public sealed class SpeakerMatrixProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inChannels;
    private readonly int _outChannels;
    private readonly int _sampleRate;
    private float[] _sourceBuffer = Array.Empty<float>();

    private float _lfeLp1;
    private float _lfeLp2;
    private readonly float _lfeAlpha;

    private readonly float[] _rearDelayL;
    private readonly float[] _rearDelayR;
    private int _rearDelayPos;

    private volatile SpeakerRoutingOptions _options;

    public WaveFormat WaveFormat { get; }
    public bool EndpointIsMultichannel { get; }

    public SpeakerMatrixProvider(ISampleProvider source, SpeakerRoutingOptions options, int endpointChannels = 2)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _inChannels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _options = options ?? new SpeakerRoutingOptions();

        bool wantsSix = _options.Layout != SpeakerLayout.Stereo20;
        EndpointIsMultichannel = endpointChannels >= 6;
        _outChannels = wantsSix && EndpointIsMultichannel ? 6 : 2;

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _outChannels);

        double rc = 1.0 / (2.0 * Math.PI * 80.0);
        double dt = 1.0 / Math.Max(8000, _sampleRate);
        _lfeAlpha = (float)(dt / (rc + dt));

        int delaySamples = Math.Max(1, (int)(_sampleRate * 0.012));
        _rearDelayL = new float[delaySamples];
        _rearDelayR = new float[delaySamples];
    }

    public void UpdateOptions(SpeakerRoutingOptions options)
    {
        _options = options ?? new SpeakerRoutingOptions();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / _outChannels;
        if (framesRequested <= 0) return 0;

        int sourceSamplesNeeded = framesRequested * _inChannels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
            _sourceBuffer = new float[sourceSamplesNeeded];

        int sourceRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceRead / _inChannels;
        var opts = _options;

        for (int f = 0; f < framesRead; f++)
        {
            int srcIdx = f * _inChannels;
            int dstIdx = offset + f * _outChannels;

            if (_outChannels == 2)
            {
                WriteStereoFrame(_sourceBuffer, srcIdx, buffer, dstIdx, opts);
            }
            else
            {
                WriteSixChannelFrame(_sourceBuffer, srcIdx, buffer, dstIdx, opts);
            }
        }

        return framesRead * _outChannels;
    }

    public static float SoftClip(float x)
    {
        float abs = MathF.Abs(x);
        if (abs <= 0.85f) return x;
        return MathF.Sign(x) * (0.85f + 0.15f * MathF.Tanh((abs - 0.85f) / 0.15f));
    }

    private void WriteStereoFrame(float[] src, int s, float[] dst, int d, SpeakerRoutingOptions opts)
    {
        float l, r;
        if (_inChannels == 1)
        {
            l = src[s];
            r = src[s];
        }
        else if (_inChannels >= 6)
        {
            float fl = src[s];
            float fr = src[s + 1];
            float fc = src[s + 2] * 0.707f;
            float bl = src[s + 4] * 0.707f;
            float br = src[s + 5] * 0.707f;
            l = SoftClip(fl + fc + bl);
            r = SoftClip(fr + fc + br);
        }
        else
        {
            l = src[s];
            r = src[s + 1];
        }

        // If user selected 5.1 Upmix / 2.1 on a 2-channel Windows endpoint, apply subtle spatial & LFE warmth with soft limiting
        if (opts.Layout is SpeakerLayout.Surround51Upmix or SpeakerLayout.Stereo21)
        {
            float mono = 0.5f * (l + r);
            _lfeLp1 += _lfeAlpha * (mono - _lfeLp1);
            _lfeLp2 += _lfeAlpha * (_lfeLp1 - _lfeLp2);
            float lfe = _lfeLp2 * (opts.LfeGain * 0.25f);
            float delayedL = 0f;
            float delayedR = 0f;
            if (opts.Layout == SpeakerLayout.Surround51Upmix)
            {
                float diff = 0.5f * (l - r) * (opts.RearLevel * 0.35f);
                delayedL = _rearDelayL[_rearDelayPos];
                delayedR = _rearDelayR[_rearDelayPos];
                _rearDelayL[_rearDelayPos] = diff;
                _rearDelayR[_rearDelayPos] = -diff;
                _rearDelayPos = (_rearDelayPos + 1) % _rearDelayL.Length;
            }

            dst[d] = SoftClip(l + lfe + delayedL);
            dst[d + 1] = SoftClip(r + lfe + delayedR);
            return;
        }

        dst[d] = l;
        dst[d + 1] = r;
    }

    private void WriteSixChannelFrame(float[] src, int s, float[] dst, int d, SpeakerRoutingOptions opts)
    {
        if (opts.Layout == SpeakerLayout.Surround51Direct && _inChannels >= 6)
        {
            dst[d] = src[s];
            dst[d + 1] = src[s + 1];
            dst[d + 2] = src[s + 2];
            dst[d + 3] = src[s + 3];
            dst[d + 4] = src[s + 4];
            dst[d + 5] = src[s + 5];
            return;
        }

        float l = src[s];
        float r = _inChannels >= 2 ? src[s + 1] : l;
        float mono = 0.5f * (l + r);

        _lfeLp1 += _lfeAlpha * (mono - _lfeLp1);
        _lfeLp2 += _lfeAlpha * (_lfeLp1 - _lfeLp2);
        float lfe = SoftClip(_lfeLp2 * opts.LfeGain);

        if (opts.Layout == SpeakerLayout.Stereo21)
        {
            dst[d] = l;
            dst[d + 1] = r;
            dst[d + 2] = 0f;
            dst[d + 3] = lfe;
            dst[d + 4] = 0f;
            dst[d + 5] = 0f;
            return;
        }

        float center = SoftClip(mono * opts.CenterLevel);
        float diff = 0.5f * (l - r);

        float delayedL = _rearDelayL[_rearDelayPos];
        float delayedR = _rearDelayR[_rearDelayPos];
        _rearDelayL[_rearDelayPos] = (diff + 0.25f * l) * opts.RearLevel;
        _rearDelayR[_rearDelayPos] = (-diff + 0.25f * r) * opts.RearLevel;
        _rearDelayPos = (_rearDelayPos + 1) % _rearDelayL.Length;

        dst[d] = l;
        dst[d + 1] = r;
        dst[d + 2] = center;
        dst[d + 3] = lfe;
        dst[d + 4] = SoftClip(delayedL);
        dst[d + 5] = SoftClip(delayedR);
    }
}
