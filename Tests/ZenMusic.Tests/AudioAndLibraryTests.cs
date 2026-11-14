using NAudio.Wave;
using System;
using System.Collections.ObjectModel;
using Xunit;
using ZenMusic.Audio;
using ZenMusic.Tagging;

namespace ZenMusic.Tests;

public sealed class AudioAndLibraryTests
{
    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float[] _frame;
        public WaveFormat WaveFormat { get; }

        public ConstantSampleProvider(int sampleRate, params float[] frame)
        {
            _frame = frame;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, frame.Length);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _frame.Length;
            for (int f = 0; f < frames; f++)
            {
                Array.Copy(_frame, 0, buffer, offset + f * _frame.Length, _frame.Length);
            }
            return frames * _frame.Length;
        }
    }

    [Fact]
    public void SpeakerMatrix_Stereo20_OutputsTwoChannels()
    {
        var src = new ConstantSampleProvider(44100, 0.8f, 0.4f);
        var matrix = new SpeakerMatrixProvider(src, new SpeakerRoutingOptions { Layout = SpeakerLayout.Stereo20 });

        Assert.Equal(2, matrix.WaveFormat.Channels);
        var buf = new float[8];
        int read = matrix.Read(buf, 0, buf.Length);
        Assert.Equal(8, read);
        Assert.Equal(0.8f, buf[0], 4);
        Assert.Equal(0.4f, buf[1], 4);
    }

    [Fact]
    public void SpeakerMatrix_Surround51Upmix_OutputsSixChannelsWithCenterAndRear()
    {
        var src = new ConstantSampleProvider(44100, 0.8f, 0.2f);
        var matrix = new SpeakerMatrixProvider(src, new SpeakerRoutingOptions
        {
            Layout = SpeakerLayout.Surround51Upmix,
            CenterLevel = 0.6f,
            LfeGain = 1.0f,
            RearLevel = 0.5f
        }, endpointChannels: 6);

        Assert.Equal(6, matrix.WaveFormat.Channels);
        var buf = new float[6 * 4];
        int read = matrix.Read(buf, 0, buf.Length);
        Assert.Equal(24, read);
        Assert.Equal(0.8f, buf[0], 4); // FL
        Assert.Equal(0.2f, buf[1], 4); // FR
        Assert.Equal(0.3f, buf[2], 4); // Center = 0.5*(0.8+0.2)*0.6
    }

    [Fact]
    public void SpeakerMatrix_Surround51Upmix_GracefullyFoldsToStereoOnTwoChannelEndpoint()
    {
        var src = new ConstantSampleProvider(44100, 0.8f, 0.2f);
        var matrix = new SpeakerMatrixProvider(src, new SpeakerRoutingOptions
        {
            Layout = SpeakerLayout.Surround51Upmix,
            LfeGain = 0.8f,
            RearLevel = 0.5f
        }, endpointChannels: 2);

        Assert.Equal(2, matrix.WaveFormat.Channels);
        var buf = new float[8];
        int read = matrix.Read(buf, 0, buf.Length);
        Assert.Equal(8, read);
    }

    [Fact]
    public void FftAnalyzer_Detects1kHzSineEnergy()
    {
        var fft = new FftAnalyzer();
        fft.SetSampleRate(44100);

        var samples = new float[FftAnalyzer.FftSize * 2];
        for (int i = 0; i < FftAnalyzer.FftSize; i++)
        {
            float v = MathF.Sin(2f * MathF.PI * 1000f * i / 44100f);
            samples[i * 2] = v;
            samples[i * 2 + 1] = v;
        }

        fft.PushSamples(samples, 0, samples.Length, 2);

        var bands = new float[FftAnalyzer.BandCount];
        var peaks = new float[FftAnalyzer.BandCount];
        var wave = new float[FftAnalyzer.WaveformPoints];
        fft.ComputeFrame(bands, peaks, wave, smoothing: 0.5f);

        float maxBand = 0f;
        int maxIdx = -1;
        for (int i = 0; i < bands.Length; i++)
        {
            if (bands[i] > maxBand) { maxBand = bands[i]; maxIdx = i; }
        }

        Assert.True(maxBand > 0.6f);
        Assert.InRange(maxIdx, 20, 35); // ~1 kHz lies in upper-mid log band
    }

    [Theory]
    [InlineData(13, 0.5)]
    [InlineData(64, 2.0)]
    [InlineData(196, 4.0)]
    [InlineData(242, 4.5)]
    [InlineData(255, 5.0)]
    public void MusicLibrary_HalfStarPopularimeterRoundTrip(byte popm, double expectedStars)
    {
        Assert.Equal(expectedStars, MusicLibrary.StarsFromPopularimeter(popm));
        Assert.Equal(popm, MusicLibrary.PopularimeterFromStars(expectedStars));
    }

    [Fact]
    public void ListMerge_PreservesMatchingItemInstances()
    {
        var a = new Library.TrackItem { Path = "a.mp3", Title = "A" };
        var b = new Library.TrackItem { Path = "b.mp3", Title = "B" };
        var current = new ObservableCollection<Library.TrackItem> { a, b };

        var bFresh = new Library.TrackItem { Path = "b.mp3", Title = "B" };
        var cFresh = new Library.TrackItem { Path = "c.mp3", Title = "C" };

        ListMerge.Apply(current, new[] { bFresh, cFresh }, t => t.Path, (_, _) => { });

        Assert.Equal(2, current.Count);
        Assert.Same(b, current[0]);
        Assert.Same(cFresh, current[1]);
    }

    [Theory]
    [InlineData(@"C:\Music\01 - Pink Floyd - Time [Official Audio].mp3", "Pink Floyd", "Time", 1)]
    [InlineData(@"C:\Music\One_Two_Three_-_Sinnerman[YoutubeConverter.Me].mp3", "One Two Three", "Sinnerman", 0)]
    [InlineData(@"C:\Music\Adele\21\02 Rumour Has It.flac", "", "Rumour Has It", 2)]
    public void AutoTagger_ParseFromFilename_ExtractsArtistTitleAndTrack(string path, string expectedArtist, string expectedTitle, int expectedTrack)
    {
        var parsed = AutoTagger.ParseFromFilename(path);
        Assert.Equal(expectedArtist, parsed.Artist);
        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedTrack, parsed.TrackNumber);
    }
}
