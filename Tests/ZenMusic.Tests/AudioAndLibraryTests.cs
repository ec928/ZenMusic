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

        // Track mode attenuates hot tracks (~ -6 dB -> 0.501)
        float trackFactor = ReplayGainAnalyzer.ComputeLinearFactor(-6.0, 0.9f, -3.0, 0.8f, "Track");
        Assert.InRange(trackFactor, 0.49f, 0.51f);

        // Album mode prefers album gain when present
        float albumFactor = ReplayGainAnalyzer.ComputeLinearFactor(-6.0, 0.9f, -3.0, 0.8f, "Album");
        Assert.InRange(albumFactor, 0.70f, 0.72f);

        // High boost + peak protection prevents clipping (> 0.98 / peak)
        float capped = ReplayGainAnalyzer.ComputeLinearFactor(10.0, 0.6f, null, null, "Track");
        Assert.Equal(0.98f / 0.6f, capped, 3);
    }

    [Fact]
    public void ReplayGain_AnalyzeSampleProvider_MeasuresHotAndQuietSignals()
    {
        var hotSrc = new BoundedSampleProvider(new ConstantSampleProvider(44100, 0.8f, 0.8f), 44100);
        var hot = ReplayGainAnalyzer.AnalyzeSampleProvider(hotSrc);
        Assert.NotNull(hot);
        Assert.True(hot!.Value.TrackGainDb < -10.0);
        Assert.Equal(0.8f, hot.Value.TrackPeak, 3);

        var quietSrc = new BoundedSampleProvider(new ConstantSampleProvider(44100, 0.02f, 0.02f), 44100);
        var quiet = ReplayGainAnalyzer.AnalyzeSampleProvider(quietSrc);
        Assert.NotNull(quiet);
        Assert.True(quiet!.Value.TrackGainDb > 10.0);
        Assert.Equal(0.02f, quiet.Value.TrackPeak, 3);
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
}
