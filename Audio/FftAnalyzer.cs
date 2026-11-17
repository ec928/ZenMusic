using NAudio.Wave;
using System;
using System.Threading;

namespace ZenMusic.Audio;

/// <summary>
/// Lock-free audio tap + 2048-point Hann-windowed FFT analyzer producing logarithmic
/// spectrum bands (20 Hz - 16 kHz) with peak-hold and decimated waveform samples for Win2D.
/// </summary>
public sealed class FftAnalyzer
{
    public const int FftSize = 2048;
    public const int BandCount = 48;
    public const int WaveformPoints = 128;

    private readonly float[] _ring = new float[FftSize * 2];
    private int _writeIndex;

    private readonly float[] _hann = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2];

    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _peaks = new float[BandCount];
    private readonly float[] _waveform = new float[WaveformPoints];
    private readonly float[] _frameBands = new float[BandCount];
    private readonly float[] _framePeaks = new float[BandCount];
    private readonly float[] _frameWave = new float[WaveformPoints];
    private int _frameBandCount;
    private int _frameWaveCount;
    private long _lastFrameTimestamp;
    private readonly object _computeLock = new();

    private volatile int _sampleRate = 44100;
    private float _autoGain = 1.4f;
    private float _waveGain = 1.6f;

    public float BassEnergy { get; private set; }

    public FftAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            _hann[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
    }

    public void SetSampleRate(int sampleRate) => _sampleRate = Math.Max(8000, sampleRate);

    /// <summary>Called on the audio thread. Lock-free and allocation-free.</summary>
    public void PushSamples(float[] interleaved, int offset, int count, int channels)
    {
        if (channels <= 0) return;
        int idx = _writeIndex;
        int len = _ring.Length;
        for (int i = 0; i + channels - 1 < count; i += channels)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += interleaved[offset + i + c];
            _ring[idx] = sum / channels;
            idx = (idx + 1) % len;
        }
        Volatile.Write(ref _writeIndex, idx);
    }

    public void Clear()
    {
        lock (_computeLock)
        {
            _lastFrameTimestamp = 0;
            _frameBandCount = 0;
            _frameWaveCount = 0;
            Array.Clear(_frameBands);
            Array.Clear(_framePeaks);
            Array.Clear(_frameWave);
        }
        Array.Clear(_ring);
        Array.Clear(_bands);
        Array.Clear(_peaks);
        Array.Clear(_waveform);
        _autoGain = 1.4f;
        _waveGain = 1.6f;
        BassEnergy = 0f;
    }

    /// <summary>
    /// Computes latest smoothed spectrum bands (0..1), peak-hold caps (0..1), and waveform (-1..1).
    /// Safe to call from the UI/render thread at 60 fps.
    /// </summary>
    public void ComputeFrame(Span<float> outBands, Span<float> outPeaks, Span<float> outWaveform, float smoothing = 0.56f)
    {
        lock (_computeLock)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long last = _lastFrameTimestamp;
            if (last != 0 && now - last < System.Diagnostics.Stopwatch.Frequency / 50)
            {
                int cachedBands = Math.Min(_frameBandCount, Math.Min(outBands.Length, outPeaks.Length));
                for (int i = 0; i < cachedBands; i++)
                {
                    outBands[i] = _frameBands[i];
                    outPeaks[i] = _framePeaks[i];
                }
                int cachedWave = Math.Min(_frameWaveCount, outWaveform.Length);
                for (int i = 0; i < cachedWave; i++)
                    outWaveform[i] = _frameWave[i];
                return;
            }

            int writePos = Volatile.Read(ref _writeIndex);
            int len = _ring.Length;
            int start = (writePos - FftSize + len) % len;

            float waveFramePeak = 0.001f;
            for (int i = 0; i < FftSize; i++)
            {
                float sample = _ring[(start + i) % len];
                float abs = MathF.Abs(sample);
                if (abs > waveFramePeak) waveFramePeak = abs;
                _real[i] = sample * _hann[i];
                _imag[i] = 0f;
            }

            float targetWaveGain = Math.Clamp(0.75f / Math.Max(0.08f, waveFramePeak), 1.0f, 3.8f);
            _waveGain = _waveGain * 0.94f + targetWaveGain * 0.06f;

            // Decimated waveform from the most recent FftSize samples
            int waveStep = Math.Max(1, FftSize / WaveformPoints);
            int wCount = Math.Min(WaveformPoints, outWaveform.Length);
            for (int i = 0; i < wCount; i++)
            {
                float v = Math.Clamp(_ring[(start + i * waveStep) % len] * _waveGain, -1f, 1f);
                _waveform[i] = _waveform[i] * 0.30f + v * 0.70f;
                outWaveform[i] = _waveform[i];
            }

            FftInPlace(_real, _imag);

            int half = FftSize / 2;
            float norm = 2f / FftSize;
            float maxMag = 0.0005f;
            for (int i = 0; i < half; i++)
            {
                float mag = MathF.Sqrt(_real[i] * _real[i] + _imag[i] * _imag[i]) * norm;
                _magnitudes[i] = mag;
                if (mag > maxMag) maxMag = mag;
            }

            float targetAutoGain = Math.Clamp(0.28f / Math.Max(0.03f, maxMag), 0.9f, 3.2f);
            _autoGain = _autoGain * 0.94f + targetAutoGain * 0.06f;

            int sr = _sampleRate;
            double minFreq = 24.0;
            double maxFreq = Math.Min(16500.0, sr * 0.45);
            double logMin = Math.Log10(minFreq);
            double logMax = Math.Log10(maxFreq);
            double binHz = sr / (double)FftSize;

            int bCount = Math.Min(BandCount, Math.Min(outBands.Length, outPeaks.Length));
            float decay = Math.Clamp(smoothing, 0.3f, 0.92f);

            for (int b = 0; b < bCount; b++)
            {
                double f0 = Math.Pow(10.0, logMin + (logMax - logMin) * b / bCount);
                double f1 = Math.Pow(10.0, logMin + (logMax - logMin) * (b + 1) / bCount);
                int bin0 = Math.Clamp((int)Math.Floor(f0 / binHz), 1, half - 1);
                int bin1 = Math.Clamp((int)Math.Ceiling(f1 / binHz), bin0, half - 1);

                float peakMag = 0f;
                for (int k = bin0; k <= bin1; k++)
                {
                    if (_magnitudes[k] > peakMag) peakMag = _magnitudes[k];
                }

                // Convert to perceptual dB scale (-54 dB .. 0 dB -> 0 .. 1) with gentle auto-gain
                float db = 20f * MathF.Log10(peakMag * _autoGain + 1e-6f);
                float level = Math.Clamp((db + 54f) / 50f, 0f, 1f);

                float prev = _bands[b];
                _bands[b] = level > prev ? prev * 0.18f + level * 0.82f : prev * decay + level * (1f - decay);
                _peaks[b] = _bands[b] >= _peaks[b] ? _bands[b] : MathF.Max(0f, _peaks[b] - 0.014f);

                outBands[b] = _bands[b];
                outPeaks[b] = _peaks[b];
                _frameBands[b] = _bands[b];
                _framePeaks[b] = _peaks[b];
            }
            _frameBandCount = bCount;

            for (int i = 0; i < wCount; i++)
                _frameWave[i] = _waveform[i];
            _frameWaveCount = wCount;
            _lastFrameTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

            BassEnergy = Math.Clamp((_bands[0] + _bands[1] + _bands[2] + _bands[3] + _bands[4]) / 5f, 0f, 1f);
        }
    }

    public static void FftInPlace(float[] real, float[] imag)
    {
        int n = real.Length;
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int m = n >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }
            j += m;
        }

        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            double angle = -2.0 * Math.PI / size;
            float wStepR = (float)Math.Cos(angle);
            float wStepI = (float)Math.Sin(angle);

            for (int start = 0; start < n; start += size)
            {
                float wR = 1f;
                float wI = 0f;
                for (int k = 0; k < half; k++)
                {
                    int even = start + k;
                    int odd = even + half;
                    float tR = wR * real[odd] - wI * imag[odd];
                    float tI = wR * imag[odd] + wI * real[odd];
                    real[odd] = real[even] - tR;
                    imag[odd] = imag[even] - tI;
                    real[even] += tR;
                    imag[even] += tI;

                    float nextWR = wR * wStepR - wI * wStepI;
                    wI = wR * wStepI + wI * wStepR;
                    wR = nextWR;
                }
            }
        }
    }
}

internal sealed class FftTapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly FftAnalyzer _analyzer;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public FftTapSampleProvider(ISampleProvider source, FftAnalyzer analyzer)
    {
        _source = source;
        _analyzer = analyzer;
        _analyzer.SetSampleRate(source.WaveFormat.SampleRate);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0)
            _analyzer.PushSamples(buffer, offset, read, WaveFormat.Channels);
        return read;
    }
}
