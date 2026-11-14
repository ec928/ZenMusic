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

    [Fact]
    public void LrcParser_ParsesSortedTimestampsAndResolvesActiveLine()
    {
        string raw = """
            [ar:Test Artist]
            [00:12.50]Second line
            [00:05.100]First line
            [00:25.00]Third line
            """;

        var lines = ZenMusic.Tagging.LrcParser.Parse(raw);
        Assert.Equal(3, lines.Count);
        Assert.Equal("First line", lines[0].Text);
        Assert.Equal(System.TimeSpan.FromMilliseconds(5100), lines[0].Timestamp);
        Assert.Equal("Second line", lines[1].Text);
        Assert.Equal("Third line", lines[2].Text);

        Assert.Equal(-1, ZenMusic.Tagging.LrcParser.GetActiveLineIndex(lines, System.TimeSpan.FromSeconds(2)));
        Assert.Equal(0, ZenMusic.Tagging.LrcParser.GetActiveLineIndex(lines, System.TimeSpan.FromMilliseconds(5100)));
        Assert.Equal(1, ZenMusic.Tagging.LrcParser.GetActiveLineIndex(lines, System.TimeSpan.FromSeconds(18)));
        Assert.Equal(2, ZenMusic.Tagging.LrcParser.GetActiveLineIndex(lines, System.TimeSpan.FromSeconds(40)));
    }

    [Fact]
    public void SpeakerMatrixProvider_SoftClip_PreservesLinearAndBoundsOverdrive()
    {
        Assert.Equal(0.5f, ZenMusic.Audio.SpeakerMatrixProvider.SoftClip(0.5f), precision: 5);
        Assert.Equal(-0.85f, ZenMusic.Audio.SpeakerMatrixProvider.SoftClip(-0.85f), precision: 5);

        float knee = ZenMusic.Audio.SpeakerMatrixProvider.SoftClip(1.05f);
        Assert.InRange(knee, 0.85f, 0.9999f);

        float high = ZenMusic.Audio.SpeakerMatrixProvider.SoftClip(3.0f);
        float low = ZenMusic.Audio.SpeakerMatrixProvider.SoftClip(-3.0f);
        Assert.InRange(high, 0.99f, 1.0f);
        Assert.InRange(low, -1.0f, -0.99f);
        Assert.Equal(high, -low, precision: 5);
    }

    [Theory]
    [InlineData(@"C:\Music\01 - Pink Floyd - Time [Official Audio].mp3", true)]
    [InlineData(@"C:\Music\Track_Name_(Official Video).mp3", true)]
    [InlineData(@"C:\Music\01 - Time.flac", false)]
    public void AutoTagger_HasDirtyFilename_DetectsNoise(string path, bool expectedDirty)
    {
        Assert.Equal(expectedDirty, AutoTagger.HasDirtyFilename(path));
    }

    [Fact]
    public void LibraryCache_RoundTripsFolderTracksAndFiltersUnrelatedFolders()
    {
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZenMusicCacheTest_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            string cachePath = System.IO.Path.Combine(tempDir, "library-cache.json");
            string musicFolder = System.IO.Path.Combine(tempDir, "Library");
            string otherFolder = System.IO.Path.Combine(tempDir, "Other");

            var tracks = new[]
            {
                new ZenMusic.Library.TrackItem
                {
                    Path = System.IO.Path.Combine(musicFolder, "01 - Track One.flac"),
                    Title = "Track One",
                    Artist = "Artist A",
                    Album = "Album A",
                    Genre = "Ambient",
                    Year = 2024,
                    TrackNumber = 1,
                    Rating = 4.5,
                    PlayCount = 7,
                    FileSize = 123456,
                    FileWriteUtcTicks = 638000000000000000L
                },
                new ZenMusic.Library.TrackItem
                {
                    Path = System.IO.Path.Combine(otherFolder, "02 - Outside.mp3"),
                    Title = "Outside",
                    Artist = "Artist B"
                }
            };

            ZenMusic.Library.LibraryCache.Save(cachePath, tracks);
            var restored = ZenMusic.Library.LibraryCache.LoadInstantTracks(cachePath, musicFolder);

            Assert.Single(restored);
            Assert.Equal("Track One", restored[0].Title);
            Assert.Equal("Artist A", restored[0].Artist);
            Assert.Equal(4.5, restored[0].Rating);
            Assert.Equal(7, restored[0].PlayCount);
            Assert.Equal(123456, restored[0].FileSize);
            Assert.Equal(638000000000000000L, restored[0].FileWriteUtcTicks);
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TrackItem_TrimsDisplayPropertiesAndCopiesMetadataInPlace()
    {
        var existing = new ZenMusic.Library.TrackItem
        {
            Index = 1,
            Path = @"C:\Music\Song.flac",
            Title = "  My Song  ",
            Artist = "  Artist X ",
            Album = "",
            Rating = 4.0,
            IsNowPlaying = true
        };

        Assert.Equal("My Song", existing.DisplayTitle);
        Assert.Equal("Artist X", existing.DisplayArtist);
        Assert.Equal("Unknown Album", existing.DisplayAlbum);

        int notifyCount = 0;
        existing.PropertyChanged += (_, _) => notifyCount++;

        var identical = new ZenMusic.Library.TrackItem
        {
            Index = 1,
            Path = @"C:\Music\Song.flac",
            Title = "  My Song  ",
            Artist = "  Artist X ",
            Album = "",
            Rating = 4.0
        };
        existing.CopyMetadataFrom(identical);
        Assert.Equal(0, notifyCount);
        Assert.True(existing.IsNowPlaying);

        var updated = new ZenMusic.Library.TrackItem
        {
            Index = 2,
            Path = @"C:\Music\Song.flac",
            Title = "Renamed Song",
            Artist = "Artist X",
            Album = "New Album",
            Year = 2025,
            Duration = System.TimeSpan.FromSeconds(185),
            Rating = 5.0
        };
        existing.CopyMetadataFrom(updated);
        Assert.True(notifyCount > 0);
        Assert.Equal(2, existing.Index);
        Assert.Equal("Renamed Song", existing.DisplayTitle);
        Assert.Equal("New Album", existing.DisplayAlbum);
        Assert.Equal("2025 · 03:05", existing.TileMetaText);
        Assert.Equal(5.0, existing.Rating);
        Assert.True(existing.IsNowPlaying);
    }
}
