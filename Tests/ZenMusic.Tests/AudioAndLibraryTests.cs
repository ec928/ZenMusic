using NAudio.Wave;
using System;
using System.Collections.ObjectModel;
using System.Text;
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
    public void SpeakerMatrix_Surround51Direct_PassesStereoUnalteredWithoutUpmixOnSixChannelEndpoint()
    {
        var src = new ConstantSampleProvider(44100, 0.8f, 0.2f);
        var matrix = new SpeakerMatrixProvider(src, new SpeakerRoutingOptions
        {
            Layout = SpeakerLayout.Surround51Direct,
            CenterLevel = 0.6f,
            LfeGain = 1.0f,
            RearLevel = 0.5f
        }, endpointChannels: 6);

        Assert.Equal(6, matrix.WaveFormat.Channels);
        var buf = new float[6 * 4];
        int read = matrix.Read(buf, 0, buf.Length);
        Assert.Equal(24, read);
        for (int f = 0; f < 4; f++)
        {
            int o = f * 6;
            Assert.Equal(0.8f, buf[o], 4);     // FL
            Assert.Equal(0.2f, buf[o + 1], 4); // FR
            Assert.Equal(0f, buf[o + 2], 4);   // C
            Assert.Equal(0f, buf[o + 3], 4);   // LFE
            Assert.Equal(0f, buf[o + 4], 4);   // RL
            Assert.Equal(0f, buf[o + 5], 4);   // RR
        }
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

    [Fact]
    public void FftAnalyzer_ReusesFrameUntilCleared()
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

        var first = new float[FftAnalyzer.BandCount];
        var second = new float[FftAnalyzer.BandCount];
        var peaks = new float[FftAnalyzer.BandCount];
        var wave = new float[FftAnalyzer.WaveformPoints];
        fft.ComputeFrame(first, peaks, wave, smoothing: 0.5f);
        fft.ComputeFrame(second, peaks, wave, smoothing: 0.5f);
        Assert.Equal(first, second);

        fft.Clear();
        Array.Clear(samples);
        fft.PushSamples(samples, 0, samples.Length, 2);
        var after = new float[FftAnalyzer.BandCount];
        fft.ComputeFrame(after, peaks, wave, smoothing: 0.5f);
        float maxAfter = 0f;
        for (int i = 0; i < after.Length; i++)
            if (after[i] > maxAfter) maxAfter = after[i];
        Assert.True(maxAfter < 0.2f);
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

    // Writing tags to the playing file reopens it at the held position. Media Foundation's FLAC source
    // ignored that seek and played from 0 while the clock, scrubber and lyrics stayed at the position.
    [Fact]
    public void AudioFileOpener_PositionFreshReader_StartsAFlacAtTheTarget()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rising-tone-10s.flac");
        using var reader = AudioFileOpener.OpenWaveStream(path);
        AudioFileOpener.PositionFreshReader(reader, path, TimeSpan.FromSeconds(6));

        var sp = reader.ToSampleProvider();
        var buf = new float[sp.WaveFormat.SampleRate / 10 * sp.WaveFormat.Channels];
        int got = 0;
        while (got < buf.Length) { int n = sp.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
        double rms = Math.Sqrt(buf.Take(got).Sum(x => (double)x * x) / Math.Max(1, got));

        // Amplitude at t is 0.9 * t / 10; the RMS of 6.0-6.1 s is about 0.385. From 0 it would be about 0.003.
        Assert.InRange(rms, 0.36, 0.41);
    }

    // Play next / Add to end on a track already in the queue put the same object in the queue
    // ListView twice, which hung ZenMusic's UI thread. It must move instead.
    [Fact]
    public void ListMerge_Enqueue_MovesQueuedTracksInsteadOfDuplicating()
    {
        Library.TrackItem T(string n) => new Library.TrackItem { Path = n + ".mp3", Title = n };
        var a = T("a"); var b = T("b"); var c = T("c"); var d = T("d"); var e = T("e");
        var q = new ObservableCollection<Library.TrackItem> { a, b, c, d };

        // b plays; Play next on d (queued) and e (not queued), plus b itself, which stays put.
        int idx = ListMerge.Enqueue(q, 1, new[] { d, e, b, d }, playNext: true);
        Assert.Equal(new[] { a, b, d, e, c }, q);
        Assert.Equal(1, idx);

        // Add to end of queue on a, which sits before the playing entry: the index follows b.
        idx = ListMerge.Enqueue(q, idx, new[] { a }, playNext: false);
        Assert.Equal(new[] { b, d, e, c, a }, q);
        Assert.Equal(0, idx);

        // Nothing playing: Play next goes to the front.
        idx = ListMerge.Enqueue(q, -1, new[] { c }, playNext: true);
        Assert.Equal(new[] { c, b, d, e, a }, q);
        Assert.Equal(-1, idx);
        Assert.Equal(q.Count, q.Distinct().Count());
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

    [Fact]
    public void ReplayGain_ParseAndComputeFactor_RespectsModeAndAntiClipping()
    {
        Assert.Equal(-6.42, ReplayGainAnalyzer.ParseGainDb("-6.42 dB"));
        Assert.Equal(2.15, ReplayGainAnalyzer.ParseGainDb("+2.15 dB"));
        Assert.Null(ReplayGainAnalyzer.ParseGainDb("not a gain"));
        Assert.Equal(0.95f, ReplayGainAnalyzer.ParsePeak("0.95")!.Value, 3);

        // Off mode always returns unity
        Assert.Equal(1.0f, ReplayGainAnalyzer.ComputeLinearFactor(-6.0, 0.9f, -4.0, 0.8f, "Off"), 4);

        // Stored gains are relative to -18 LUFS; playback adds 4 dB to level at -14 LUFS.
        // Track mode: -6 + 4 = -2 dB -> 0.794
        float trackFactor = ReplayGainAnalyzer.ComputeLinearFactor(-6.0, 0.9f, -3.0, 0.8f, "Track");
        Assert.InRange(trackFactor, 0.78f, 0.80f);

        // Album mode prefers album gain when present: -3 + 4 = +1 dB -> 1.122
        float albumFactor = ReplayGainAnalyzer.ComputeLinearFactor(-6.0, 0.9f, -3.0, 0.8f, "Album");
        Assert.InRange(albumFactor, 1.11f, 1.13f);

        // High boost + peak protection prevents clipping (> 0.98 / peak)
        float capped = ReplayGainAnalyzer.ComputeLinearFactor(10.0, 0.6f, null, null, "Track");
        Assert.Equal(0.98f / 0.6f, capped, 3);
    }

    [Fact]
    public void ReplayGain_AnalyzeSampleProvider_MeasuresHotAndQuietSignals()
    {
        // EBU Tech 3341: a stereo 1 kHz sine at -23 dBFS peak reads -23 LUFS.
        static ISampleProvider Sine(int rate, double amplitude, double seconds) =>
            new NAudio.Wave.SampleProviders.SignalGenerator(rate, 2)
            {
                Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin,
                Frequency = 1000,
                Gain = amplitude
            }.Take(TimeSpan.FromSeconds(seconds));

        double? ref23 = ReplayGainAnalyzer.MeasureIntegratedLoudness(Sine(48000, Math.Pow(10, -23 / 20.0), 5), out _);
        Assert.NotNull(ref23);
        Assert.InRange(ref23!.Value, -23.1, -22.9);
        double? ref23At44 = ReplayGainAnalyzer.MeasureIntegratedLoudness(Sine(44100, Math.Pow(10, -23 / 20.0), 5), out _);
        Assert.InRange(ref23At44!.Value, -23.1, -22.9);

        var hot = ReplayGainAnalyzer.AnalyzeSampleProvider(Sine(44100, 0.8, 3));
        Assert.NotNull(hot);
        Assert.InRange(hot!.Value.TrackGainDb, -16.2, -15.9); // -18 - (-1.94 LUFS)
        Assert.Equal(0.8f, hot.Value.TrackPeak, 2);

        var quiet = ReplayGainAnalyzer.AnalyzeSampleProvider(Sine(44100, 0.02, 3));
        Assert.NotNull(quiet);
        Assert.InRange(quiet!.Value.TrackGainDb, 15.8, 16.1);  // -18 - (-33.98 LUFS)

        // Silence and sub-block audio give no reading rather than a wild gain.
        Assert.Null(ReplayGainAnalyzer.AnalyzeSampleProvider(Sine(44100, 0, 3)));
        Assert.Null(ReplayGainAnalyzer.AnalyzeSampleProvider(Sine(44100, 0.5, 0.2)));
    }

    [Fact]
    public void SmoothGainSampleProvider_GlidesSmoothlyTowardTargetWithoutStep()
    {
        var src = new ConstantSampleProvider(40000, 1.0f, 1.0f);
        var provider = new SmoothGainSampleProvider(src, 1.0f) { TargetGain = 0.5f };
        var buf = new float[800]; // 400 stereo frames (< 4000 ramp frames)
        int read = provider.Read(buf, 0, buf.Length);
        Assert.Equal(800, read);
        Assert.True(buf[0] > 0.99f);
        Assert.True(buf[^1] < buf[0]);
        Assert.True(buf[^1] > 0.5f);
    }

    [Fact]
    public void SmartPlaylistGenerator_FiltersAndOrdersCorrectly()
    {
        var now = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        var tracks = new[]
        {
            new ZenMusic.Library.TrackItem { Path = @"C:\1.mp3", Title = "Old Hit", Artist = "A", Genre = "Rock", Year = 1998, Rating = 4.5, PlayCount = 2, LastPlayedUtc = now.AddDays(-40) },
            new ZenMusic.Library.TrackItem { Path = @"C:\2.mp3", Title = "Recent Play", Artist = "B", Genre = "Rock", Year = 2022, Rating = 4.5, PlayCount = 15, LastPlayedUtc = now.AddHours(-2) },
            new ZenMusic.Library.TrackItem { Path = @"C:\3.mp3", Title = "Unrated Track", Artist = "C", Genre = "Jazz", Year = 2015, Rating = null, PlayCount = 0 },
            new ZenMusic.Library.TrackItem { Path = @"C:\4.mp3", Title = "Cloud Song", Artist = "A", Genre = "Rock", Year = 2010, Rating = 5.0, IsCloudOnly = true }
        };

        var forgotten = ZenMusic.Library.SmartPlaylistGenerator.Generate(
            tracks,
            new ZenMusic.Library.SmartMixRule
            {
                Name = "Forgotten Gems",
                MaxTracks = 10,
                MinRating = 4.0,
                History = ZenMusic.Library.SmartPlayHistoryFilter.NotPlayedRecently,
                StaleDays = 14,
                Order = ZenMusic.Library.SmartMixOrder.Random
            },
            now,
            new Random(42));

        Assert.Single(forgotten);
        Assert.Equal("Old Hit", forgotten[0].Title);

        var heavy = ZenMusic.Library.SmartPlaylistGenerator.Generate(
            tracks,
            new ZenMusic.Library.SmartMixRule
            {
                MaxTracks = 5,
                GenreContains = "rock",
                Order = ZenMusic.Library.SmartMixOrder.MostPlayed
            },
            now,
            new Random(42));

        Assert.Equal(2, heavy.Count);
        Assert.Equal("Recent Play", heavy[0].Title);
    }

    [Fact]
    public void EqualizerSampleProvider_BypassesWhenFlatAndShapesWhenBoosted()
    {
        var flatSrc = new NAudio.Wave.SampleProviders.SignalGenerator(44100, 2) { Frequency = 100.0, Gain = 0.5, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin };
        var eq = new EqualizerSampleProvider(flatSrc);
        var bufFlat = new float[2048];
        eq.Read(bufFlat, 0, bufFlat.Length);

        var refSrc = new NAudio.Wave.SampleProviders.SignalGenerator(44100, 2) { Frequency = 100.0, Gain = 0.5, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin };
        var bufRef = new float[2048];
        refSrc.Read(bufRef, 0, bufRef.Length);
        Assert.Equal(bufRef[500], bufFlat[500], 5);

        eq.UpdateGains(EqualizerSampleProvider.Presets["Bass Boost"]);
        var boostedSrc = new NAudio.Wave.SampleProviders.SignalGenerator(44100, 2) { Frequency = 64.0, Gain = 0.3, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin };
        var eqBoost = new EqualizerSampleProvider(boostedSrc, EqualizerSampleProvider.Presets["Bass Boost"]);
        var bufBoost = new float[4096];
        eqBoost.Read(bufBoost, 0, bufBoost.Length);

        float peakBoost = 0f;
        for (int i = 2048; i < bufBoost.Length; i++) peakBoost = Math.Max(peakBoost, MathF.Abs(bufBoost[i]));
        Assert.True(peakBoost > 0.35f);
    }

    [Fact]
    public void AutoTagger_CleanQueryTitleAndRankCandidates_PreferMatchingAlbumAndYear()
    {
        Assert.Equal("Sultans Of Swing", ZenMusic.Tagging.AutoTagger.CleanQueryTitle("01 - Sultans Of Swing [Official Audio]"));
        Assert.Equal("Lady Writer", ZenMusic.Tagging.AutoTagger.CleanQueryTitle("02. Lady Writer"));
        Assert.Equal("The Platinum Collection", ZenMusic.Tagging.AutoTagger.CleanSearchAlbum("The Platinum Collection (Disc 1)"));
        Assert.Equal("Il Buono, Il Brutto, Il Cattivo", ZenMusic.Tagging.AutoTagger.StripTrailingParenthetical("Il Buono, Il Brutto, Il Cattivo (The Good, The Bad, The Ugly)"));
        Assert.Equal(
            "[iTunes] Dire Straits — Sultans of Swing: The Very Best of Dire Straits, 1998",
            ZenMusic.Tagging.AutoTagger.FormatDisplayLabel("iTunes", "Dire Straits", "Sultans of Swing", "Sultans of Swing: The Very Best of Dire Straits", 1998));
        Assert.Equal(
            "[iTunes] Dire Straits — Sultans of Swing · Private Investigations, 2005",
            ZenMusic.Tagging.AutoTagger.FormatDisplayLabel("iTunes", "Dire Straits", "Sultans of Swing", "Private Investigations", 2005));

        var candidates = new[]
        {
            new ZenMusic.Tagging.AutoTagCandidate
            {
                Source = "iTunes",
                Title = "Sultans of Swing",
                Artist = "Dire Straits",
                Album = "Dire Straits",
                Year = 1978
            },
            new ZenMusic.Tagging.AutoTagCandidate
            {
                Source = "iTunes",
                Title = "Sultans of Swing",
                Artist = "Dire Straits",
                Album = "Sultans of Swing: The Very Best of Dire Straits",
                Year = 1998
            },
            new ZenMusic.Tagging.AutoTagCandidate
            {
                Source = "MusicBrainz",
                Title = "Sultans of Swing",
                Artist = "Dire Straits",
                Album = "Private Investigations",
                Year = 2005
            }
        };

        var ranked = ZenMusic.Tagging.AutoTagger.RankCandidates(
            candidates,
            "Sultans Of Swing",
            "Dire Straits",
            albumHint: "Sultans of Swing: The Very Best of Dire Straits",
            yearHint: 1998);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(1998, ranked[0].Year);
        Assert.Equal("Sultans of Swing: The Very Best of Dire Straits", ranked[0].Album);
    }

    [Fact]
    public void AutoTagger_AlbumFirstRanking_PrefersRequestedAlbumOverMatchingYearLiveRelease()
    {
        double matchedSim = ZenMusic.Tagging.AutoTagger.AlbumSimilarity(
            "The Very Best Of 1990 - 2000",
            "The Very Best of Sarah Brightman 1990–2000");
        double wrongLiveSim = ZenMusic.Tagging.AutoTagger.AlbumSimilarity(
            "The Very Best Of 1990 - 2000",
            "La Luna (Live in Concert)");

        Assert.True(matchedSim >= 0.85, $"Expected high similarity for matching album variant, got {matchedSim:0.###}");
        Assert.Equal(0.0, wrongLiveSim);

        Assert.Equal(
            "[MusicBrainz Album] Sarah Brightman — The Very Best of 1990–2000, 2001",
            ZenMusic.Tagging.AutoTagger.FormatDisplayLabel("MusicBrainz Album", "Sarah Brightman", "", "The Very Best of 1990–2000", 2001));

        var candidates = new[]
        {
            new ZenMusic.Tagging.AutoTagCandidate
            {
                Source = "MusicBrainz",
                Title = "A Question of Honour",
                Artist = "Sarah Brightman",
                Album = "La Luna (Live in Concert)",
                Year = 2001,
                TrackNumber = 11
            },
            new ZenMusic.Tagging.AutoTagCandidate
            {
                Source = "MusicBrainz",
                Title = "A Question of Honour",
                Artist = "Sarah Brightman",
                Album = "The Very Best of Sarah Brightman 1990–2000",
                Year = 2002,
                TrackNumber = 1
            }
        };

        var ranked = ZenMusic.Tagging.AutoTagger.RankCandidates(
            candidates,
            queryTitle: "A Question of Honour",
            queryArtist: "Sarah Brightman",
            albumHint: "The Very Best Of 1990 - 2000",
            yearHint: 2001);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("The Very Best of Sarah Brightman 1990–2000", ranked[0].Album);
        Assert.Equal(1, ranked[0].TrackNumber);

        // "1990 - 2000" and "1990-2000" are the same MusicBrainz token. Splitting
        // them into 1990 AND 2000 returned no release for this album.
        Assert.Equal(
            "release:(very AND best AND 1990-2000)",
            ZenMusic.Tagging.AutoTagger.BuildLuceneAlbumClause("The Very Best Of 1990 - 2000"));
        Assert.Equal(
            "release:(very AND best AND 1990-2000)",
            ZenMusic.Tagging.AutoTagger.BuildLuceneAlbumClause("The Very Best of 1990-2000"));
    }

    [Fact]
    public void AudioFileOpener_FindFlacMagicAfterId3_DetectsSynchsafeOffset()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"zen_id3flac_{Guid.NewGuid():N}.flac");
        try
        {
            // Minimal ID3v2.3 header claiming 128 bytes (synchsafe 0x00, 0x00, 0x01, 0x00) followed by fLaC
            var bytes = new byte[10 + 128 + 8];
            bytes[0] = (byte)'I'; bytes[1] = (byte)'D'; bytes[2] = (byte)'3'; bytes[3] = 3;
            bytes[6] = 0; bytes[7] = 0; bytes[8] = 1; bytes[9] = 0;
            bytes[138] = (byte)'f'; bytes[139] = (byte)'L'; bytes[140] = (byte)'a'; bytes[141] = (byte)'C';
            File.WriteAllBytes(tmp, bytes);

            Assert.Equal(138, AudioFileOpener.FindFlacMagicAfterId3(tmp));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void TrackItem_RaisesPropertyChangedForMetadataAndDisplayProperties()
    {
        var item = new ZenMusic.Library.TrackItem { Path = @"C:\Music\song.mp3" };
        var changed = new HashSet<string>();
        item.PropertyChanged += (_, e) => { if (e.PropertyName != null) changed.Add(e.PropertyName); };

        item.Title = "New Title";
        item.Artist = "New Artist";
        item.Album = "New Album";
        item.Genre = "Rock";
        item.Year = 1998;

        Assert.Contains("Title", changed);
        Assert.Contains("DisplayTitle", changed);
        Assert.Contains("ArtInitial", changed);
        Assert.Contains("Artist", changed);
        Assert.Contains("DisplayArtist", changed);
        Assert.Contains("Album", changed);
        Assert.Contains("DisplayAlbum", changed);
        Assert.Contains("Genre", changed);
        Assert.Contains("Year", changed);
        Assert.Contains("YearText", changed);
        Assert.Contains("TileMetaText", changed);
        Assert.Contains("AlbumHoverText", changed);
    }

    [Fact]
    public void TrackItem_DetailAndHoverTexts_FormatExpectedFields()
    {
        var item = new ZenMusic.Library.TrackItem
        {
            Path = @"C:\Music\04 - Midnight.flac",
            Album = "Night Drive",
            DiscNumber = 2,
            TrackNumber = 4,
            Codec = "FLAC",
            Bitrate = 980,
            SampleRate = 44100,
            Channels = 6,
            ReplayGainTrackDb = -4.2,
            ReplayGainAlbumDb = -3.5,
            PlayCount = 7,
            DateAddedUtc = new DateTime(2026, 11, 10, 12, 30, 0, DateTimeKind.Utc),
            LastPlayedUtc = new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc)
        };

        Assert.Equal("Night Drive\nDisc 2, Track 4", item.AlbumHoverText);
        Assert.Contains("FLAC · 980 kbps · 44.1 kHz · 5.1", item.TechHoverText);
        Assert.Contains("ReplayGain: Track -4.2 dB · Album -3.5 dB", item.TechHoverText);
        Assert.Contains("04 - Midnight.flac", item.TechHoverText);
        Assert.Contains("Plays: 7", item.DateAddedHoverText);
        Assert.Contains("Last played:", item.DateAddedHoverText);

        Assert.Equal("980", item.GetDetailText("Bitrate"));
        Assert.Equal("2.04", item.GetDetailText("Track"));
        Assert.Equal("FLAC", item.GetDetailText("Codec"));
        Assert.Equal("44.1k", item.GetDetailText("SampleRate"));
        Assert.Equal("5.1", item.GetDetailText("Channels"));
        Assert.Equal("-4.2", item.GetDetailText("ReplayGain"));
        Assert.Equal("7", item.GetDetailText("Plays"));
        Assert.False(string.IsNullOrEmpty(item.GetDetailText("LastPlayed")));

        item.Album = string.Empty;
        item.DiscNumber = 1;
        Assert.Equal("Track 4", item.AlbumHoverText);
        Assert.Equal("04", item.GetDetailText("Track"));

        item.TrackNumber = 0;
        Assert.Null(item.AlbumHoverText);
    }

    [Fact]
    public void MusicLibrary_ResolveEffectiveRating_PrioritizesPendingThenDiskThenCached()
    {
        Assert.Equal(4.5, MusicLibrary.ResolveEffectiveRating(hasPendingWrite: true, pendingRating: 4.5, diskRating: 2.0, cachedRating: 3.0));
        Assert.Null(MusicLibrary.ResolveEffectiveRating(hasPendingWrite: true, pendingRating: null, diskRating: 4.0, cachedRating: 4.0));
        Assert.Equal(3.5, MusicLibrary.ResolveEffectiveRating(hasPendingWrite: false, pendingRating: null, diskRating: 3.5, cachedRating: 2.0));
        Assert.Equal(4.0, MusicLibrary.ResolveEffectiveRating(hasPendingWrite: false, pendingRating: null, diskRating: null, cachedRating: 4.0));
        Assert.Null(MusicLibrary.ResolveEffectiveRating(hasPendingWrite: false, pendingRating: null, diskRating: null, cachedRating: null));
    }

    [Theory]
    [InlineData(@"C:\Music\track.mp3", true)]
    [InlineData(@"C:\Music\track.FLAC", true)]
    [InlineData(@"C:\Music\track.m4a", true)]
    [InlineData(@"C:\Music\track.ogg", true)]
    [InlineData(@"C:\Music\track.opus", true)]
    [InlineData(@"C:\Music\track.wma", true)]
    [InlineData(@"C:\Music\track.wav", false)]
    public void MusicLibrary_SupportsRatingTag_IdentifiesSupportedFormats(string path, bool expected)
    {
        Assert.Equal(expected, MusicLibrary.SupportsRatingTag(path));
    }

    [Fact]
    public void MusicLibrary_TryWriteFullDetails_PreservesUpdatesAndClearsMp3Rating()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"zen_mp3_props_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string ratedPath = Path.Combine(tmpDir, "rated.mp3");
            WriteMinimalMp3(ratedPath);

            Assert.True(MusicLibrary.TryWriteStars(ratedPath, 4.0));
            Assert.True(MusicLibrary.TryReadFullDetails(ratedPath, out var initial, out _));
            Assert.Equal(4.0, initial.Rating);

            initial.Title = "Edited Title";
            initial.Artist = "Edited Artist";
            initial.Album = "Edited Album";
            initial.Year = 2024;
            Assert.True(MusicLibrary.TryWriteFullDetails(ratedPath, initial, out string? err), err);

            Assert.True(MusicLibrary.TryReadFullDetails(ratedPath, out var afterEdit, out _));
            Assert.Equal("Edited Title", afterEdit.Title);
            Assert.Equal("Edited Artist", afterEdit.Artist);
            Assert.Equal("Edited Album", afterEdit.Album);
            Assert.Equal(2024, afterEdit.Year);
            Assert.Equal(4.0, afterEdit.Rating);

            afterEdit.Rating = 4.5;
            Assert.True(MusicLibrary.TryWriteFullDetails(ratedPath, afterEdit, out err), err);
            Assert.True(MusicLibrary.TryReadFullDetails(ratedPath, out var afterHalfStar, out _));
            Assert.Equal(4.5, afterHalfStar.Rating);

            afterHalfStar.Rating = null;
            Assert.True(MusicLibrary.TryWriteFullDetails(ratedPath, afterHalfStar, out err), err);
            Assert.True(MusicLibrary.TryReadFullDetails(ratedPath, out var afterClear, out _));
            Assert.Null(afterClear.Rating);

            // Editing properties of an unrated MP3 should not synthesize a zero POPM frame.
            string unratedPath = Path.Combine(tmpDir, "unrated.mp3");
            WriteMinimalMp3(unratedPath);
            Assert.True(MusicLibrary.TryWriteFullDetails(unratedPath, new MusicLibrary.TrackDetails
            {
                Title = "No Rating Track",
                Rating = null
            }, out err), err);
            using (var tf = TagLib.File.Create(unratedPath))
            {
                var id3 = tf.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
                Assert.True(id3 == null || !id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>().Any());
            }
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void MusicLibrary_TryWriteFullDetails_PreservesFlacXiphAndMigratesLegacyId3PopmRating()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"zen_flac_props_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string flacPath = Path.Combine(tmpDir, "xiph.flac");
            WriteMinimalFlac(flacPath);

            Assert.True(MusicLibrary.TryWriteStars(flacPath, 3.5));
            Assert.True(MusicLibrary.TryReadFullDetails(flacPath, out var d1, out _));
            Assert.Equal(3.5, d1.Rating);

            d1.Title = "FLAC Title";
            d1.Album = "FLAC Album";
            Assert.True(MusicLibrary.TryWriteFullDetails(flacPath, d1, out string? err), err);
            Assert.True(MusicLibrary.TryReadFullDetails(flacPath, out var d2, out _));
            Assert.Equal("FLAC Title", d2.Title);
            Assert.Equal("FLAC Album", d2.Album);
            Assert.Equal(3.5, d2.Rating);

            string legacyPath = Path.Combine(tmpDir, "legacy.flac");
            WriteMinimalFlac(legacyPath);
            using (var tf = TagLib.File.Create(legacyPath))
            {
                var id3 = (TagLib.Id3v2.Tag)tf.GetTag(TagLib.TagTypes.Id3v2, true);
                TagLib.Id3v2.PopularimeterFrame.Get(id3, "Windows Media Player 9 Series", true).Rating = 196;
                tf.Save();
            }

            Assert.True(MusicLibrary.TryReadFullDetails(legacyPath, out var legacyDetails, out _));
            Assert.Equal(4.0, legacyDetails.Rating);

            legacyDetails.Title = "Migrated FLAC";
            Assert.True(MusicLibrary.TryWriteFullDetails(legacyPath, legacyDetails, out err), err);
            Assert.True(MusicLibrary.TryReadFullDetails(legacyPath, out var migrated, out _));
            Assert.Equal("Migrated FLAC", migrated.Title);
            Assert.Equal(4.0, migrated.Rating);
            using (var tf = TagLib.File.Create(legacyPath))
            {
                Assert.Equal(TagLib.TagTypes.None, tf.TagTypes & (TagLib.TagTypes.Id3v2 | TagLib.TagTypes.Id3v1));
            }
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void PlaybackClock_HeardFollowsRenderClockFromTheSeekOrigin()
    {
        var origin = TimeSpan.FromSeconds(90);
        int bytesPerSecond = 176400;
        var heard = PlaybackClock.Heard(origin, clockBytes: bytesPerSecond * 2, clockBytesAtOrigin: 0, bytesPerSecond, TimeSpan.FromMinutes(4));
        Assert.Equal(92, heard.TotalSeconds, 3);
    }

    [Fact]
    public void PlaybackClock_HeardIgnoresAClockThatRunsBackwards()
    {
        var origin = TimeSpan.FromSeconds(40);
        var heard = PlaybackClock.Heard(origin, clockBytes: 100, clockBytesAtOrigin: 5000, bytesPerSecond: 176400, TimeSpan.FromMinutes(3));
        Assert.Equal(40, heard.TotalSeconds, 3);
    }

    [Fact]
    public void PlaybackClock_HeardClampsAtTheEndOfTheSong()
    {
        var heard = PlaybackClock.Heard(TimeSpan.FromSeconds(10), clockBytes: 176400 * 100, clockBytesAtOrigin: 0, 176400, TimeSpan.FromSeconds(30));
        Assert.Equal(30, heard.TotalSeconds, 3);
    }

    [Fact]
    public void PlaybackClock_SeekTargetStillSeeksWhenDurationIsUnknown()
    {
        var requested = TimeSpan.FromSeconds(75);
        Assert.Equal(requested, PlaybackClock.SeekTarget(requested, TimeSpan.Zero));
    }

    [Fact]
    public void PlaybackClock_SeekTargetClampsInsideAShorterReopenedFile()
    {
        var target = PlaybackClock.SeekTarget(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(30));
        Assert.InRange(target.TotalSeconds, 29.9, 29.96);
    }

    [Fact]
    public void RampedVolume_FullScaleChangeDoesNotStepHarderThanTheRamp()
    {
        var src = new ConstantSampleProvider(1000, 1f);
        var volume = new RampedVolumeSampleProvider(src, 0f);
        volume.Volume = 1f;

        var buf = new float[200];
        int read = volume.Read(buf, 0, buf.Length);
        Assert.Equal(200, read);

        float maxStep = 1f / (1000 / 20);
        for (int i = 1; i < buf.Length; i++)
            Assert.True(buf[i] - buf[i - 1] <= maxStep + 1e-4, $"step {buf[i] - buf[i - 1]} at {i}");
        Assert.Equal(1f, buf[^1], 3);
        Assert.True(buf[0] < 0.1f);
    }

    [Fact]
    public void PropertiesFileCommit_WritesTagsThenDateThenRename()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"zen_props_once_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string path = Path.Combine(tmpDir, "song.mp3");
            WriteMinimalMp3(path);
            var original = new DateTime(2019, 1, 2, 3, 4, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, original);
            var wanted = new DateTime(2021, 6, 7, 8, 9, 0, DateTimeKind.Utc);
            string renamed = Path.Combine(tmpDir, "renamed.mp3");

            var result = PropertiesFileCommit.Apply(
                path,
                () =>
                {
                    bool ok = MusicLibrary.TryWriteFullDetails(path, new MusicLibrary.TrackDetails { Title = "Once" }, out string? err);
                    // The tag writer puts the pre-edit write time back. The new Date Added has to follow that.
                    File.SetLastWriteTimeUtc(path, original);
                    return (ok, err);
                },
                wanted,
                renamed);

            Assert.True(result.TagsSaved, result.TagError);
            Assert.True(result.DateWritten, result.DateError);
            Assert.True(result.Renamed, result.RenameError);
            Assert.Equal(renamed, result.ResumePath);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(renamed));
            Assert.Equal(wanted, File.GetLastWriteTimeUtc(renamed));
            Assert.True(MusicLibrary.TryReadFullDetails(renamed, out var details, out _));
            Assert.Equal("Once", details.Title);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void PropertiesFileCommit_FailedTagsLeaveTheFileAlone_FailedRenameKeepsTheNewDate()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"zen_props_fail_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string path = Path.Combine(tmpDir, "song.mp3");
            WriteMinimalMp3(path);
            var original = new DateTime(2018, 3, 4, 5, 6, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, original);
            string renamed = Path.Combine(tmpDir, "renamed.mp3");

            var rejected = PropertiesFileCommit.Apply(
                path,
                () => (false, "locked"),
                new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                renamed);

            Assert.False(rejected.TagsSaved);
            Assert.Equal("locked", rejected.TagError);
            Assert.False(rejected.DateWritten);
            Assert.False(rejected.Renamed);
            Assert.Equal(path, rejected.ResumePath);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(renamed));
            Assert.Equal(original, File.GetLastWriteTimeUtc(path));

            var wanted = new DateTime(2020, 5, 6, 7, 8, 0, DateTimeKind.Utc);
            string missing = Path.Combine(tmpDir, "missing-dir", "x.mp3");
            var kept = PropertiesFileCommit.Apply(
                path,
                () =>
                {
                    bool ok = MusicLibrary.TryWriteFullDetails(path, new MusicLibrary.TrackDetails { Title = "Stayed" }, out string? err);
                    File.SetLastWriteTimeUtc(path, original);
                    return (ok, err);
                },
                wanted,
                missing);

            Assert.True(kept.TagsSaved, kept.TagError);
            Assert.True(kept.DateWritten, kept.DateError);
            Assert.False(kept.Renamed);
            Assert.False(string.IsNullOrEmpty(kept.RenameError));
            Assert.Equal(path, kept.ResumePath);
            Assert.Equal(wanted, File.GetLastWriteTimeUtc(path));
            Assert.False(File.Exists(missing));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void MusicLibrary_ShortPictureFrame_DropsTheFrameAndKeepsTheOtherTags()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"zen_short_apic_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string path = Path.Combine(tmpDir, "short-apic.mp3");
            WriteMp3WithShortPictureFrame(path, "KeepTitle", "KeepAlbum");

            var thrown = Assert.ThrowsAny<Exception>(() =>
            {
                using var broken = TagLib.File.Create(path);
            });
            Assert.Contains("at least 5 bytes", thrown.ToString(), StringComparison.OrdinalIgnoreCase);

            Assert.True(MusicLibrary.TryReadFullDetails(path, out var details, out string? err), err);
            Assert.Equal("KeepTitle", details.Title);
            Assert.Equal("KeepAlbum", details.Album);
            Assert.Null(details.ArtBytes);

            Assert.True(MusicLibrary.TryReadFullDetails(path, out var again, out err), err);
            Assert.Equal("KeepTitle", again.Title);
            Assert.Equal("KeepAlbum", again.Album);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static void WriteMp3WithShortPictureFrame(string path, string title, string album)
    {
        byte[] titleFrame = Id3TextFrame("TIT2", title);
        byte[] pictureFrame = Id3Frame("APIC", new byte[] { 0x00, 0x00, 0x03, 0x00 });
        byte[] albumFrame = Id3TextFrame("TALB", album);
        int tagSize = titleFrame.Length + pictureFrame.Length + albumFrame.Length;
        var file = new byte[10 + tagSize + 417 * 2];
        file[0] = (byte)'I';
        file[1] = (byte)'D';
        file[2] = (byte)'3';
        file[3] = 3;
        file[6] = (byte)((tagSize >> 21) & 0x7f);
        file[7] = (byte)((tagSize >> 14) & 0x7f);
        file[8] = (byte)((tagSize >> 7) & 0x7f);
        file[9] = (byte)(tagSize & 0x7f);
        int o = 10;
        titleFrame.CopyTo(file, o);
        o += titleFrame.Length;
        pictureFrame.CopyTo(file, o);
        o += pictureFrame.Length;
        albumFrame.CopyTo(file, o);
        o += albumFrame.Length;
        for (int frame = 0; frame < 2; frame++)
        {
            file[o] = 0xFF;
            file[o + 1] = 0xFB;
            file[o + 2] = 0x90;
            o += 417;
        }
        File.WriteAllBytes(path, file);
    }

    private static byte[] Id3TextFrame(string id, string text)
    {
        var body = new byte[1 + text.Length];
        Encoding.ASCII.GetBytes(text, 0, text.Length, body, 1);
        return Id3Frame(id, body);
    }

    private static byte[] Id3Frame(string id, byte[] body)
    {
        var frame = new byte[10 + body.Length];
        Encoding.ASCII.GetBytes(id, 0, 4, frame, 0);
        frame[4] = (byte)(body.Length >> 24);
        frame[5] = (byte)(body.Length >> 16);
        frame[6] = (byte)(body.Length >> 8);
        frame[7] = (byte)body.Length;
        body.CopyTo(frame, 10);
        return frame;
    }

    private static void WriteMinimalMp3(string path)
    {
        // Two MPEG-1 Layer III 128 kbps 44.1 kHz frames (417 bytes each)
        var bytes = new byte[417 * 2];
        for (int frame = 0; frame < 2; frame++)
        {
            int o = frame * 417;
            bytes[o] = 0xFF;
            bytes[o + 1] = 0xFB;
            bytes[o + 2] = 0x90;
            bytes[o + 3] = 0x00;
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteMinimalFlac(string path)
    {
        var bytes = new byte[4 + 4 + 34];
        bytes[0] = (byte)'f';
        bytes[1] = (byte)'L';
        bytes[2] = (byte)'a';
        bytes[3] = (byte)'C';
        bytes[4] = 0x80; // Last metadata block, type 0 (STREAMINFO)
        bytes[5] = 0x00;
        bytes[6] = 0x00;
        bytes[7] = 0x22; // Length 34
        int s = 8;
        bytes[s] = 0x10; bytes[s + 1] = 0x00; // min block 4096
        bytes[s + 2] = 0x10; bytes[s + 3] = 0x00; // max block 4096
        bytes[s + 10] = 0x0A; // 44100 Hz, 2ch, 16-bit
        bytes[s + 11] = 0xC4;
        bytes[s + 12] = 0x42;
        bytes[s + 13] = 0xF0;
        bytes[s + 17] = 0x01; // 1 sample
        File.WriteAllBytes(path, bytes);
    }

    private sealed class BoundedSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        private int _remainingFrames;
        public WaveFormat WaveFormat => _inner.WaveFormat;

        public BoundedSampleProvider(ISampleProvider inner, int totalFrames)
        {
            _inner = inner;
            _remainingFrames = totalFrames;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_remainingFrames <= 0) return 0;
            int channels = _inner.WaveFormat.Channels;
            int maxSamples = Math.Min(count, _remainingFrames * channels);
            int read = _inner.Read(buffer, offset, maxSamples);
            _remainingFrames -= read / channels;
            return read;
        }
    }

    private static RawSourceWaveStream CreateConstantFloatWave(int sampleRate, int channels, int frames, float value)
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        byte[] bytes = new byte[frames * channels * 4];
        var floats = new float[frames * channels];
        Array.Fill(floats, value);
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return new RawSourceWaveStream(new MemoryStream(bytes, writable: false), fmt);
    }

    [Fact]
    public void SeamlessStreamProvider_ZeroCrossfade_ProducesSampleAccurateZeroGapHandoff()
    {
        const int sr = 10000;
        using var track1 = CreateConstantFloatWave(sr, 2, frames: 250, value: 0.25f);
        string? transitionedTo = null;

        using var seamless = new SeamlessStreamProvider(
            initialPath: "mem://track1.wav",
            initialReader: track1,
            startAt: TimeSpan.Zero,
            initialReplayGain: 1.0f,
            targetSampleRate: sr,
            targetChannels: 2,
            allowResample: true,
            crossfadeSeconds: 0.0,
            streamFactory: path => path == "mem://track2.wav"
                ? CreateConstantFloatWave(sr, 2, frames: 300, value: 0.75f)
                : throw new FileNotFoundException(path));

        seamless.TrackTransitioned += p => transitionedTo = p;
        seamless.SetQueuedNext("mem://track2.wav", 1.0f);
        seamless.TryPreopenNextIfClose();

        // Request 400 stereo frames across the 250-frame track boundary in a single Read()
        var outBuf = new float[400 * 2];
        int got = seamless.Read(outBuf, 0, outBuf.Length);

        Assert.Equal(400 * 2, got);
        Assert.Equal(0.25f, outBuf[249 * 2], 4);     // Last frame of track 1
        Assert.Equal(0.75f, outBuf[250 * 2], 4);     // Immediate frame 0 of track 2 with zero silence gap!

        seamless.PollAudibleTransition(speakerPlayedFrames: 250);
        Assert.Equal("mem://track2.wav", transitionedTo);
    }

    [Fact]
    public void SeamlessStreamProvider_EqualPowerCrossfade_BlendsOverlappingTracksSmoothly()
    {
        const int sr = 10000;
        // 3.0s tracks (30,000 frames each) with a 1.0s (10,000 frame) equal-power crossfade
        using var track1 = CreateConstantFloatWave(sr, 2, frames: 30000, value: 1.0f);

        using var seamless = new SeamlessStreamProvider(
            initialPath: "mem://xfade1.wav",
            initialReader: track1,
            startAt: TimeSpan.Zero,
            initialReplayGain: 1.0f,
            targetSampleRate: sr,
            targetChannels: 2,
            allowResample: true,
            crossfadeSeconds: 1.0,
            streamFactory: _ => CreateConstantFloatWave(sr, 2, frames: 30000, value: 1.0f));

        seamless.SetQueuedNext("mem://xfade2.wav", 1.0f);
        seamless.TryPreopenNextIfClose();

        // Total output across two 3.0s tracks with 1.0s overlap = 5.0s (50,000 frames)
        var all = new float[55000 * 2];
        int totalSamples = 0;
        var chunk = new float[2048];
        while (true)
        {
            int n = seamless.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            Array.Copy(chunk, 0, all, totalSamples, n);
            totalSamples += n;
        }

        int totalFrames = totalSamples / 2;
        Assert.Equal(50000, totalFrames);

        // Midpoint of equal-power crossfade (frame 25,000 = 20,000 + 5,000): cos(pi/4) + sin(pi/4) ≈ 1.4142
        float midVal = all[25000 * 2];
        Assert.InRange(midVal, 1.38f, 1.45f);
        // After crossfade completes (frame 35,000): pure track 2 at 1.0f
        Assert.Equal(1.0f, all[35000 * 2], 3);
    }

    private static SeamlessStreamProvider CreateSeamless(string path, WaveStream first, Func<string, WaveStream> factory, double crossfade = 0.0) =>
        new SeamlessStreamProvider(path, first, TimeSpan.Zero, 1.0f, first.WaveFormat.SampleRate,
            first.WaveFormat.Channels, allowResample: true, crossfadeSeconds: crossfade, streamFactory: factory);

    private static int ReadAllFrames(SeamlessStreamProvider s, int channels)
    {
        var chunk = new float[1024 * channels];
        int frames = 0, n;
        while ((n = s.Read(chunk, 0, chunk.Length)) > 0) frames += n / channels;
        return frames;
    }

    /// <summary>A stream whose reported length is shorter than its audio, as Media Foundation reports some FLACs.</summary>
    private sealed class ShortReportedLengthStream : WaveStream
    {
        private readonly WaveStream _inner;
        private readonly long _reportedBytes;
        public ShortReportedLengthStream(WaveStream inner, int reportedFrames)
        {
            _inner = inner;
            _reportedBytes = (long)reportedFrames * inner.WaveFormat.BlockAlign;
        }
        public override WaveFormat WaveFormat => _inner.WaveFormat;
        public override long Length => _reportedBytes;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    }

    [Fact]
    public void SeamlessStreamProvider_Crossfade_WhenReportedLengthIsShort_KeepsTheWholeEnding()
    {
        const int sr = 10000;
        // a is 30,000 frames but reports 25,000; the fade must still end at a's real end.
        var a = new ShortReportedLengthStream(CreateConstantFloatWave(sr, 2, 30000, 1.0f), 25000);
        using var seamless = CreateSeamless("mem://a", a,
            _ => CreateConstantFloatWave(sr, 2, 30000, 0.5f), crossfade: 1.0);
        seamless.SetQueuedNext("mem://b");
        seamless.TryPreopenNextIfClose();

        var all = new float[60000 * 2];
        int total = 0, n;
        var chunk = new float[1000 * 2];
        while ((n = seamless.Read(chunk, 0, chunk.Length)) > 0) { Array.Copy(chunk, 0, all, total, n); total += n; }

        Assert.Equal(30000 + 30000 - 10000, total / 2);
        Assert.Equal(1.0f, all[19999 * 2], 4);      // a alone up to the fade
        Assert.InRange(all[25000 * 2], 1.04f, 1.08f); // fade midpoint: 1.0 x cos(pi/4) + 0.5 x sin(pi/4) = 1.061
        Assert.Equal(0.5f, all[49999 * 2], 4);
    }

    [Fact]
    public void SeamlessStreamProvider_QueueEditAfterPreopen_JoinsTheNewNextOrEnds()
    {
        // A queue edit near the end of a track, after the old next track was already opened.
        Func<string, WaveStream> factory = p => CreateConstantFloatWave(10000, 2, p == "mem://c" ? 400 : 300, p == "mem://c" ? 0.5f : 0.75f);

        using var replaced = CreateSeamless("mem://a", CreateConstantFloatWave(10000, 2, 250, 0.25f), factory);
        replaced.SetQueuedNext("mem://b", 1.0f, "mem://a");
        replaced.TryPreopenNextIfClose();
        replaced.SetQueuedNext("mem://c", 1.0f, "mem://a");   // b removed, c is next now
        replaced.TryPreopenNextIfClose();
        var buf = new float[1000 * 2];
        int frames = replaced.Read(buf, 0, buf.Length) / 2;
        Assert.Equal(250 + 400, frames);
        Assert.Equal(0.5f, buf[250 * 2], 4);

        using var removed = CreateSeamless("mem://a", CreateConstantFloatWave(10000, 2, 250, 0.25f), factory);
        removed.SetQueuedNext("mem://b", 1.0f, "mem://a");
        removed.TryPreopenNextIfClose();
        removed.SetQueuedNext(null, 1.0f, "mem://a");          // nothing after a any more
        Assert.Equal(250, ReadAllFrames(removed, 2));
        Assert.True(removed.ReachedEnd);
    }

    [Fact]
    public void SeamlessStreamProvider_NextNotPreopened_EndsWithoutOpeningOnTheRenderThread()
    {
        int opens = 0;
        using var seamless = CreateSeamless("mem://a", CreateConstantFloatWave(10000, 2, 250, 0.25f),
            _ => { opens++; return CreateConstantFloatWave(10000, 2, 300, 0.75f); });
        seamless.SetQueuedNext("mem://b");

        Assert.Equal(250, ReadAllFrames(seamless, 2));
        Assert.Equal(0, opens);
        Assert.True(seamless.ReachedEnd);
    }

    [Fact]
    public void SeamlessStreamProvider_ReachedEnd_IsFalseMidTrack()
    {
        using var seamless = CreateSeamless("mem://a", CreateConstantFloatWave(10000, 2, 250, 0.25f),
            _ => throw new FileNotFoundException());
        var buf = new float[100 * 2];
        Assert.Equal(200, seamless.Read(buf, 0, buf.Length));
        Assert.False(seamless.ReachedEnd);
    }

    [Fact]
    public void SeamlessStreamProvider_DifferentChannelCount_IsNotJoined()
    {
        using var seamless = CreateSeamless("mem://stereo", CreateConstantFloatWave(10000, 2, 250, 0.25f),
            _ => CreateConstantFloatWave(10000, 1, 300, 0.75f));
        seamless.SetQueuedNext("mem://mono");
        seamless.TryPreopenNextIfClose();

        Assert.Equal(250, ReadAllFrames(seamless, 2));
        Assert.True(seamless.ReachedEnd);
    }

    [Fact]
    public void SeamlessStreamProvider_QueueRequestForATrackAlreadyLeft_IsIgnored()
    {
        using var seamless = CreateSeamless("mem://a", CreateConstantFloatWave(10000, 2, 250, 0.25f),
            _ => CreateConstantFloatWave(10000, 2, 300, 0.75f));
        seamless.SetQueuedNext("mem://b", 1.0f, afterPath: "mem://z");
        seamless.TryPreopenNextIfClose();
        Assert.Equal(250, ReadAllFrames(seamless, 2));

        using var accepted = CreateSeamless("mem://a", CreateConstantFloatWave(10000, 2, 250, 0.25f),
            _ => CreateConstantFloatWave(10000, 2, 300, 0.75f));
        accepted.SetQueuedNext("mem://b", 1.0f, afterPath: "MEM://A");
        accepted.TryPreopenNextIfClose();
        Assert.Equal(550, ReadAllFrames(accepted, 2));
    }

    [Fact]
    public void SeamlessStreamProvider_SeekEarlyInCrossfade_StaysOnTheTrackBeingHeard()
    {
        const int sr = 10000;
        using var seamless = CreateSeamless("mem://a", CreateConstantFloatWave(sr, 2, 30000, 1.0f),
            _ => CreateConstantFloatWave(sr, 2, 30000, 0.5f), crossfade: 1.0);
        seamless.SetQueuedNext("mem://b");
        seamless.TryPreopenNextIfClose();

        // 500 frames into the 1 s fade that starts at 20,000: the switch to b is announced at ~23,333.
        var head = new float[20500 * 2];
        Assert.Equal(head.Length, seamless.Read(head, 0, head.Length));
        Assert.Equal("mem://a", seamless.AudiblePath);

        seamless.Seek(TimeSpan.FromSeconds(1), speakerPlayedFrames: 20500);
        Assert.Equal("mem://a", seamless.AudiblePath);
        var after = new float[2];
        seamless.Read(after, 0, 2);
        Assert.Equal(1.0f, after[0], 4); // a alone, no fade and no b

        // b was put back in the queue and still follows a.
        seamless.TryPreopenNextIfClose();
        var rest = new float[40000 * 2];
        int got = seamless.Read(rest, 0, rest.Length) / 2;
        Assert.Equal(0.5f, rest[(got - 1) * 2], 4);
    }
}
