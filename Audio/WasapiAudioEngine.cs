using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Media;
using Windows.Media.Playback;

namespace ZenMusic.Audio;

public sealed record AudioOutputDeviceInfo(string Id, string FriendlyName, bool IsDefault);

/// <summary>
/// Primary WASAPI audio engine supporting 2.0 Stereo, 2.1, and 5.1 Surround routing,
/// live device/channel hot-swap, real-time FFT visualization tap, and Windows SMTC media keys.
/// </summary>
public sealed class WasapiAudioEngine : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly object _lock = new();

    private WasapiOut? _output;
    private SeamlessStreamProvider? _seamless;
    private EqualizerSampleProvider? _equalizerProvider;
    private RampedVolumeSampleProvider? _volumeProvider;
    private SpeakerMatrixProvider? _matrixProvider;

    private string? _currentPath;
    private string? _queuedNextPath;
    private string? _queuedAfterPath;
    private float _queuedNextGain = 1.0f;
    private double _crossfadeSeconds;
    private string? _deviceId;
    private bool _exclusiveMode;
    private SpeakerRoutingOptions _speakerOptions = new();
    private float _volume = 0.7f;
    private float _replayGainFactor = 1.0f;
    private double[] _equalizerGains = new double[10];
    private bool _isPlaying;
    private bool _suppressStoppedEvent;
    private bool _disposed;

    // Audible position. The reader cursor leads the speakers by the device buffer, so the
    // scrubber and a tag-write resume both follow the render clock instead of that cursor.
    private long _lastClockBytes;
    private int _outputBytesPerFrame = 8;
    private int _outputSampleRate = 48000;
    private int _streamSampleRate = 48000;

    /// <summary>
    /// Frames the speakers have played, counted at the joined stream's rate (the device clock runs at
    /// the device rate, which differs when the stream is resampled after the join).
    /// </summary>
    private long SpeakerFramesLocked()
    {
        if (_output == null) return 0;
        long deviceFrames = ReadClockBytesLocked() / Math.Max(1, _outputBytesPerFrame);
        if (_outputSampleRate == _streamSampleRate) return deviceFrames;
        return (long)Math.Round(deviceFrames * (double)_streamSampleRate / _outputSampleRate);
    }
    private bool _holding;
    private TimeSpan _heldPosition;
    private TimeSpan _heldDuration;

    // SMTC (Windows media keys & lock-screen overlay) via a silent dummy MediaPlayer
    private MediaPlayer? _smtcHost;
    private SystemMediaTransportControls? _smtc;
    private Windows.Storage.Streams.InMemoryRandomAccessStream? _smtcArtStream;

    public FftAnalyzer Fft { get; } = new();

    public bool IsPlaying => _isPlaying;
    public string? CurrentPath => _currentPath;

    public TimeSpan Position
    {
        get
        {
            lock (_lock) return HeardLocked();
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_lock)
            {
                if (_seamless != null) return _seamless.AudibleDuration;
                return _holding ? _heldDuration : TimeSpan.Zero;
            }
        }
    }

    public double CrossfadeSeconds
    {
        get { lock (_lock) return _crossfadeSeconds; }
        set
        {
            double clamped = Math.Clamp(value, 0.0, 12.0);
            lock (_lock)
            {
                _crossfadeSeconds = clamped;
                if (_seamless != null) _seamless.CrossfadeSeconds = clamped;
            }
        }
    }

    public event Action? TrackEnded;
    public event Action<string>? SeamlessTrackTransitioned;
    public event Action<bool>? PlaybackStateChanged;
    public event Action<string>? PlaybackError;
    public event Action? SmtcNextRequested;
    public event Action? SmtcPreviousRequested;

    public WasapiAudioEngine(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        InitSmtc();
    }

    private void InitSmtc()
    {
        try
        {
            _smtcHost = new MediaPlayer();
            _smtcHost.CommandManager.IsEnabled = false;
            _smtc = _smtcHost.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += Smtc_ButtonPressed;
        }
        catch
        {
            // SMTC unavailable in headless/test environments
        }
    }

    private void Smtc_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Resume();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    Pause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    SmtcNextRequested?.Invoke();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    SmtcPreviousRequested?.Invoke();
                    break;
            }
        });
    }

    public async Task UpdateSmtcMetadata(string title, string artist, string album, byte[]? artBytes = null)
    {
        if (_smtc == null) return;
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;

            _smtcArtStream?.Dispose();
            _smtcArtStream = null;
            if (artBytes is { Length: > 0 })
            {
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(artBytes);
                    await writer.StoreAsync().AsTask();
                    writer.DetachStream();
                }
                stream.Seek(0);
                _smtcArtStream = stream;
                updater.Thumbnail = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream);
            }
            else
            {
                updater.Thumbnail = null;
            }

            updater.Update();
        }
        catch { }
    }

    public static int GetEndpointChannelCount(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            return dev.AudioClient.MixFormat.Channels;
        }
        catch
        {
            return 2;
        }
    }

    public static SpeakerLayout GetRecommendedDirectLayout(string? deviceId = null)
    {
        return GetEndpointChannelCount(deviceId) >= 6
            ? SpeakerLayout.Surround51Direct
            : SpeakerLayout.Stereo20;
    }

    public static IReadOnlyList<AudioOutputDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioOutputDeviceInfo>
        {
            new(string.Empty, "Default Windows Audio Device", true)
        };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioOutputDeviceInfo(dev.ID, dev.FriendlyName, false));
            }
        }
        catch { }
        return list;
    }

    public void ConfigureAudio(string? deviceId, SpeakerRoutingOptions options, bool exclusiveMode = false)
    {
        bool needsRebuild;
        lock (_lock)
        {
            bool deviceChanged = !string.Equals(_deviceId ?? string.Empty, deviceId ?? string.Empty, StringComparison.Ordinal);
            bool channelCountChanged = (_speakerOptions.Layout == SpeakerLayout.Stereo20) != (options.Layout == SpeakerLayout.Stereo20);
            bool exclusiveChanged = _exclusiveMode != exclusiveMode;
            _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            _exclusiveMode = exclusiveMode;
            _speakerOptions = new SpeakerRoutingOptions
            {
                Layout = options.Layout,
                CenterLevel = options.CenterLevel,
                LfeGain = options.LfeGain,
                RearLevel = options.RearLevel
            };
            _matrixProvider?.UpdateOptions(_speakerOptions);
            needsRebuild = _seamless != null && (deviceChanged || channelCountChanged || exclusiveChanged);
        }

        if (needsRebuild)
        {
            RebuildGraphPreservingPosition();
        }
    }

    public bool PlayFile(string path, float replayGainFactor = 1.0f)
    {
        if (_disposed) return false;
        if (!File.Exists(path))
        {
            PlaybackError?.Invoke($"File not found: {path}");
            return false;
        }

        try
        {
            lock (_lock)
            {
                StopInternal();
                _currentPath = path;
                _replayGainFactor = Math.Clamp(replayGainFactor, 0.05f, 4.0f);
                OpenAndStartGraph(path, TimeSpan.Zero, autoPlay: true);
            }
            UpdatePlaybackState(true);
            return true;
        }
        catch (Exception ex)
        {
            Stop();
            PlaybackError?.Invoke($"Cannot play {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    public void LoadPausedFile(string path, TimeSpan position, float replayGainFactor = 1.0f)
    {
        if (_disposed || !File.Exists(path)) return;
        try
        {
            lock (_lock)
            {
                StopInternal();
                _currentPath = path;
                _replayGainFactor = Math.Clamp(replayGainFactor, 0.05f, 4.0f);
                OpenAndStartGraph(path, position, autoPlay: false);
            }
            UpdatePlaybackState(false);
        }
        catch { }
    }

    /// <summary>
    /// Queues <paramref name="path"/> to follow <paramref name="afterPath"/> with no gap. A request made
    /// for a track the stream has already left is ignored (see <see cref="SeamlessStreamProvider.SetQueuedNext"/>).
    /// </summary>
    public void SetQueuedNextTrack(string? path, float replayGainFactor = 1.0f, string? afterPath = null)
    {
        float clamped = Math.Clamp(replayGainFactor, 0.05f, 4.0f);
        SeamlessStreamProvider? seamless;
        lock (_lock)
        {
            _queuedNextPath = string.IsNullOrWhiteSpace(path) ? null : path;
            _queuedAfterPath = afterPath;
            _queuedNextGain = clamped;
            seamless = _seamless;
        }
        if (seamless == null) return;
        seamless.SetQueuedNext(path, clamped, afterPath);
        PreopenInBackground(seamless);
    }

    private static void PreopenInBackground(SeamlessStreamProvider seamless)
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { seamless.TryPreopenNextIfClose(); } catch { }
        });
    }

    public void PreopenNextTrackIfClose()
    {
        SeamlessStreamProvider? seamless;
        lock (_lock)
        {
            seamless = _seamless;
        }
        seamless?.TryPreopenNextIfClose();
    }

    private void OnSeamlessTrackTransitioned(string newPath)
    {
        lock (_lock)
        {
            _currentPath = newPath;
        }
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
                SeamlessTrackTransitioned?.Invoke(newPath);
        });
    }

    private void RebuildGraphPreservingPosition()
    {
        try
        {
            bool wasPlaying;
            lock (_lock)
            {
                if (_currentPath == null || _seamless == null) return;
                wasPlaying = _isPlaying;
                BeginHoldLocked();
                var pos = _heldPosition;
                string path = _currentPath;
                StopInternal();
                _currentPath = path;
                OpenAndStartGraph(path, pos, wasPlaying);
            }
            UpdatePlaybackState(wasPlaying);
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke($"Audio device switch failed: {ex.Message}");
        }
    }

    private void OpenAndStartGraph(string path, TimeSpan startAt, bool autoPlay)
    {
        _lastClockBytes = 0;
        _holding = false;

        var mixFormat = GetEndpointMixFormat(_deviceId);
        int endpointChannels = mixFormat?.Channels ?? 2;
        int endpointSampleRate = mixFormat?.SampleRate ?? 0;

        ISampleProvider BuildPipeline(bool allowResample)
        {
            if (_seamless != null)
            {
                _seamless.TrackTransitioned -= OnSeamlessTrackTransitioned;
                _seamless.Dispose();
                _seamless = null;
            }

            var initialReader = CreateReader(path);
            TimeSpan applied = PlaybackClock.SeekTarget(startAt, initialReader.TotalTime);
            AudioFileOpener.PositionFreshReader(initialReader, path, applied);

            // Tracks are joined at the first track's own rate and layout; a following track with
            // another rate or channel count is not joined and starts the ordinary way. Resampling to
            // the device happens once, after the join: a resampler per track drops its buffered tail
            // at the end of each track and starts cold on the next, which is a click at every join.
            int sourceSr = initialReader.WaveFormat.SampleRate;
            int targetCh = initialReader.WaveFormat.Channels;

            _seamless = new SeamlessStreamProvider(
                path,
                initialReader,
                applied,
                _replayGainFactor,
                sourceSr,
                targetCh,
                allowResample: false,
                _crossfadeSeconds);
            _seamless.TrackTransitioned += OnSeamlessTrackTransitioned;
            if (!string.IsNullOrEmpty(_queuedNextPath) &&
                (_queuedAfterPath == null || string.Equals(_queuedAfterPath, path, StringComparison.OrdinalIgnoreCase)))
                _seamless.SetQueuedNext(_queuedNextPath, _queuedNextGain, path);

            ISampleProvider joined = _seamless;
            if (allowResample && endpointSampleRate > 0 && endpointSampleRate != sourceSr)
                joined = new WdlResamplingSampleProvider(_seamless, endpointSampleRate);
            _streamSampleRate = sourceSr;

            _equalizerProvider = new EqualizerSampleProvider(joined, _equalizerGains);
            var fftTap = new FftTapSampleProvider(_equalizerProvider, Fft);
            _volumeProvider = new RampedVolumeSampleProvider(fftTap, _volume);
            _matrixProvider = new SpeakerMatrixProvider(_volumeProvider, _speakerOptions, endpointChannels);
            return _matrixProvider;
        }

        void InitOutput(WasapiOut output, ISampleProvider matrix)
        {
            if (matrix.WaveFormat.Channels >= 6 && mixFormat != null)
                output.Init(new MultichannelFloatWaveProvider(matrix, mixFormat));
            else
                output.Init(matrix);
        }

        if (_exclusiveMode)
        {
            try
            {
                // First try native sample rate in Exclusive mode (skipping resampler)
                var matrix = BuildPipeline(allowResample: false);
                _output = CreateWasapiOut(_deviceId, exclusive: true);
                InitOutput(_output, matrix);
            }
            catch
            {
                _output?.Dispose();
                _output = null;
                try
                {
                    // Retry Exclusive at endpoint's mix sample rate if DAC rejected native rate
                    var matrix = BuildPipeline(allowResample: true);
                    _output = CreateWasapiOut(_deviceId, exclusive: true);
                    InitOutput(_output, matrix);
                }
                catch
                {
                    _output?.Dispose();
                    _output = null;
                    var matrix = BuildPipeline(allowResample: true);
                    _output = CreateWasapiOut(_deviceId, exclusive: false);
                    InitOutput(_output, matrix);
                    PlaybackError?.Invoke("WASAPI Exclusive mode was rejected by this audio device; using Shared mode.");
                }
            }
        }
        else
        {
            var matrix = BuildPipeline(allowResample: true);
            _output = CreateWasapiOut(_deviceId, exclusive: false);
            InitOutput(_output, matrix);
        }

        _output.PlaybackStopped += Output_PlaybackStopped;
        _outputBytesPerFrame = Math.Max(4, _output.OutputWaveFormat.BlockAlign);
        _outputSampleRate = Math.Max(1, _output.OutputWaveFormat.SampleRate);

        if (autoPlay) _output.Play();
    }

    private static WaveFormat? GetEndpointMixFormat(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            return dev.AudioClient.MixFormat;
        }
        catch
        {
            return null;
        }
    }

    private sealed class MultichannelFloatWaveProvider : IWaveProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _srcChannels;
        private readonly int _dstChannels;
        private float[] _srcBuf = Array.Empty<float>();
        private float[] _dstBuf = Array.Empty<float>();

        public WaveFormat WaveFormat { get; }

        public MultichannelFloatWaveProvider(ISampleProvider source, WaveFormat endpointMixFormat)
        {
            _source = source;
            _srcChannels = source.WaveFormat.Channels;
            _dstChannels = endpointMixFormat.Channels;
            WaveFormat = endpointMixFormat;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int dstSamples = count / 4;
            int frames = dstSamples / _dstChannels;
            if (frames <= 0) return 0;

            int srcSamples = frames * _srcChannels;
            if (_srcBuf.Length < srcSamples) _srcBuf = new float[srcSamples];

            int readSrc = _source.Read(_srcBuf, 0, srcSamples);
            int readFrames = readSrc / _srcChannels;
            if (readFrames <= 0) return 0;

            if (_dstChannels == _srcChannels)
            {
                Buffer.BlockCopy(_srcBuf, 0, buffer, offset, readFrames * _dstChannels * 4);
                return readFrames * _dstChannels * 4;
            }

            int outSamples = readFrames * _dstChannels;
            if (_dstBuf.Length < outSamples) _dstBuf = new float[outSamples];
            Array.Clear(_dstBuf, 0, outSamples);

            int copyCh = Math.Min(_srcChannels, _dstChannels);
            for (int f = 0; f < readFrames; f++)
            {
                for (int c = 0; c < copyCh; c++)
                {
                    _dstBuf[f * _dstChannels + c] = _srcBuf[f * _srcChannels + c];
                }
            }
            Buffer.BlockCopy(_dstBuf, 0, buffer, offset, outSamples * 4);
            return outSamples * 4;
        }
    }

    private static WaveStream CreateReader(string path) => AudioFileOpener.OpenWaveStream(path);

    private static WasapiOut CreateWasapiOut(string? deviceId, bool exclusive)
    {
        var shareMode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
        // Shared mode is polled, with a deeper buffer than the old 60 ms event-sync stream.
        // A hardware volume wheel steps the endpoint volume many times a second, and that
        // event-sync stream underruns on each step.
        int latency = exclusive ? 100 : 120;
        bool eventSync = exclusive;
        if (!string.IsNullOrEmpty(deviceId))
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            return new WasapiOut(device, shareMode, eventSync, latency: latency);
        }
        return new WasapiOut(shareMode, eventSync, latency: latency);
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_suppressStoppedEvent || _disposed) return;

        bool reachedEnd;
        lock (_lock)
        {
            if (_seamless != null && _output != null)
                _seamless.PollAudibleTransition(long.MaxValue / 4);
            // Only a stream that ran out of audio is the end of the track; any other stop is not.
            reachedEnd = _seamless != null && _seamless.ReachedEnd;
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (e.Exception != null)
            {
                UpdatePlaybackState(false);
                PlaybackError?.Invoke(e.Exception.Message);
            }
            else if (reachedEnd)
            {
                UpdatePlaybackState(false);
                TrackEnded?.Invoke();
            }
        });
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_output == null) return;
            _output.Pause();
        }
        UpdatePlaybackState(false);
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_output == null) return;
            _output.Play();
        }
        UpdatePlaybackState(true);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _holding = false;
            _heldPosition = TimeSpan.Zero;
            _heldDuration = TimeSpan.Zero;
            StopInternal();
            _currentPath = null;
            _queuedNextPath = null;
            _queuedAfterPath = null;
            _lastClockBytes = 0;
        }
        Fft.Clear();
        UpdatePlaybackState(false);
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_holding || _seamless == null) return;
            long speakerFrames = SpeakerFramesLocked();
            _seamless.Seek(position, speakerFrames);
            _currentPath = _seamless.AudiblePath;
            PreopenInBackground(_seamless);
        }
    }

    /// <summary>
    /// Temporarily releases the audio file handle if <paramref name="path"/> is currently loaded,
    /// executes <paramref name="fileAction"/> so tags can be written without file-lock errors,
    /// and then restores playback at the previous position and play/pause state.
    /// <paramref name="resumePath"/> is the file to reopen when the action renamed it.
    /// </summary>
    public async Task<T> WithFileReleasedAsync<T>(
        string path,
        Func<Task<T>> fileAction,
        Func<T, string?>? resumePath = null)
    {
        bool released = false;
        bool wasPlaying = false;
        TimeSpan pos = TimeSpan.Zero;

        lock (_lock)
        {
            _seamless?.ReleasePreopenedIfMatches(path);
            if (!_disposed &&
                _currentPath != null &&
                _seamless != null &&
                (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase) || _seamless.UsesFilePath(path)))
            {
                released = true;
                wasPlaying = _isPlaying;
                BeginHoldLocked();
                pos = _heldPosition;
                path = _currentPath;
                StopInternal();
            }
        }

        T result = default!;
        bool haveResult = false;
        try
        {
            result = await fileAction();
            haveResult = true;
            return result;
        }
        finally
        {
            if (released && !_disposed)
            {
                string reopen = path;
                if (haveResult && resumePath != null)
                {
                    try
                    {
                        string? chosen = resumePath(result);
                        if (!string.IsNullOrWhiteSpace(chosen))
                            reopen = chosen;
                    }
                    catch
                    {
                        reopen = path;
                    }
                }

                try
                {
                    lock (_lock)
                    {
                        if (!_disposed &&
                            _currentPath != null &&
                            string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            _currentPath = reopen;
                            OpenAndStartGraph(reopen, pos, wasPlaying);
                        }
                    }
                    UpdatePlaybackState(wasPlaying);
                }
                catch (Exception ex)
                {
                    PlaybackError?.Invoke($"Cannot resume {Path.GetFileName(reopen)}: {ex.Message}");
                }
            }
        }
    }

    public async Task<(bool Ok, string? Error)> RenameFileAsync(string oldPath, string newPath)
    {
        bool released = false;
        bool wasPlaying = false;
        TimeSpan pos = TimeSpan.Zero;

        lock (_lock)
        {
            _seamless?.ReleasePreopenedIfMatches(oldPath);
            if (!_disposed &&
                _currentPath != null &&
                _seamless != null &&
                (string.Equals(_currentPath, oldPath, StringComparison.OrdinalIgnoreCase) || _seamless.UsesFilePath(oldPath)))
            {
                released = true;
                wasPlaying = _isPlaying;
                BeginHoldLocked();
                pos = _heldPosition;
                StopInternal();
            }
        }

        string activePath = oldPath;
        try
        {
            await Task.Run(() => File.Move(oldPath, newPath));
            activePath = newPath;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (released && !_disposed)
            {
                try
                {
                    lock (_lock)
                    {
                        if (!_disposed && _currentPath != null &&
                            string.Equals(_currentPath, oldPath, StringComparison.OrdinalIgnoreCase))
                        {
                            _currentPath = activePath;
                            OpenAndStartGraph(activePath, pos, wasPlaying);
                        }
                    }
                    UpdatePlaybackState(wasPlaying);
                }
                catch (Exception ex)
                {
                    PlaybackError?.Invoke($"Cannot resume {Path.GetFileName(activePath)}: {ex.Message}");
                }
            }
        }
    }

    public void SetReplayGainFactor(float factor)
    {
        float clamped = Math.Clamp(factor, 0.05f, 4.0f);
        _replayGainFactor = clamped;
        lock (_lock)
        {
            _seamless?.SetReplayGainForCurrent(clamped);
        }
    }

    public void SetEqualizerGains(double[]? gains)
    {
        var copy = new double[10];
        if (gains != null)
        {
            for (int i = 0; i < Math.Min(10, gains.Length); i++)
                copy[i] = Math.Clamp(gains[i], -12.0, 12.0);
        }
        _equalizerGains = copy;
        lock (_lock)
        {
            _equalizerProvider?.UpdateGains(copy);
        }
    }

    public void SetVolume(double percent)
    {
        float normalized = (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
        // Perceptual audio taper (exponent 1.6) so low/mid slider positions feel natural.
        _volume = MathF.Pow(normalized, 1.6f);
        lock (_lock)
        {
            if (_volumeProvider != null) _volumeProvider.Volume = _volume;
        }
    }

    private TimeSpan HeardLocked()
    {
        if (_seamless == null)
            return _holding ? _heldPosition : TimeSpan.Zero;

        long speakerFrames = SpeakerFramesLocked();
        var pos = _seamless.GetAudiblePosition(speakerFrames);
        // The heard track, not the reader's: during a crossfade or the device buffer they differ, and
        // a tag-write resume reopens _currentPath at this position.
        _currentPath = _seamless.AudiblePath;
        return pos;
    }

    private void BeginHoldLocked()
    {
        _heldPosition = HeardLocked();
        _heldDuration = _seamless?.AudibleDuration ?? _heldDuration;
        _holding = true;
    }

    private long ReadClockBytesLocked()
    {
        try
        {
            if (_output == null) return _lastClockBytes;
            long clock = Math.Max(0, _output.GetPosition());
            if (clock == 0 && _lastClockBytes > 0)
                return _lastClockBytes;
            _lastClockBytes = clock;
            return clock;
        }
        catch
        {
            return _lastClockBytes;
        }
    }

    private void UpdatePlaybackState(bool playing)
    {
        _isPlaying = playing;
        if (_smtc != null)
        {
            try
            {
                _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : (_currentPath != null ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Stopped);
            }
            catch { }
        }
        PlaybackStateChanged?.Invoke(playing);
    }

    private void StopInternal()
    {
        _suppressStoppedEvent = true;
        try
        {
            if (_output != null)
            {
                _output.PlaybackStopped -= Output_PlaybackStopped;
                _output.Stop();
                _output.Dispose();
                _output = null;
            }
            if (_seamless != null)
            {
                _seamless.TrackTransitioned -= OnSeamlessTrackTransitioned;
                _seamless.Dispose();
                _seamless = null;
            }
            _equalizerProvider = null;
            _volumeProvider = null;
            _matrixProvider = null;
        }
        finally
        {
            _suppressStoppedEvent = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _smtcArtStream?.Dispose();
        _smtcArtStream = null;
        if (_smtc != null) _smtc.ButtonPressed -= Smtc_ButtonPressed;
        _smtcHost?.Dispose();
        _smtcHost = null;
    }
}
