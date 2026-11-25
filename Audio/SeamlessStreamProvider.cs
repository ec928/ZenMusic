using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.IO;

namespace ZenMusic.Audio;

/// <summary>
/// Continuous float32 sample provider that delivers 0 ms sample-accurate gapless transitions
/// and optional equal-power cosine/sine crossfading across consecutive tracks without tearing down WASAPI.
/// </summary>
public sealed class SeamlessStreamProvider : ISampleProvider, IDisposable
{
    private sealed class TrackSlot : IDisposable
    {
        public string Path { get; }
        public WaveStream Reader { get; }
        public SmoothGainSampleProvider GainStage { get; }
        public TimeSpan TotalDuration { get; }
        public long TotalFrames { get; }
        public long FramesRead { get; set; }

        public TrackSlot(string path, WaveStream reader, SmoothGainSampleProvider gainStage, int outputSampleRate)
        {
            Path = path;
            Reader = reader;
            GainStage = gainStage;
            TotalDuration = reader.TotalTime;
            TotalFrames = TotalDuration > TimeSpan.Zero
                ? Math.Max(1, (long)Math.Round(TotalDuration.TotalSeconds * outputSampleRate))
                : 0;
        }

        public void Dispose()
        {
            try { Reader.Dispose(); } catch { }
        }
    }

    private sealed record PendingAudibleTransition(long SpeakerFrame, string Path, TimeSpan Duration, long NewOriginFrame);

    private readonly object _sync = new();
    private readonly Func<string, WaveStream> _streamFactory;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly bool _allowResample;

    private TrackSlot? _current;
    private TrackSlot? _outgoingFade;
    private int _fadeTotalFrames;
    private int _fadeElapsedFrames;

    // Crossfade lookahead. A reported length (TotalTime) can be wrong by most of a second -- Media
    // Foundation reports some FLACs 0.8 s short -- so a fade timed from it cuts the end of the track.
    // Instead, while a crossfade is wanted, the current track is read one fade length ahead into
    // _ring; when its reader runs dry the ring holds exactly its last audio, and that is what fades out.
    private readonly FrameRing _ring;
    private bool _lookaheadArmed;
    private bool _lookaheadEof;
    private int _lookaheadFrames;
    private float[] _readScratch = Array.Empty<float>();

    private string? _queuedNextPath;
    private float _queuedNextGain = 1.0f;
    private TrackSlot? _preopenedNext;
    private bool _preopenFailed;

    private long _totalFramesEmitted;
    private long _audibleOriginFrame;
    private string _audiblePath = string.Empty;
    private TimeSpan _audibleDuration;
    private readonly Queue<PendingAudibleTransition> _pendingTransitions = new();

    private float[] _mixScratch = Array.Empty<float>();
    private bool _disposed;
    private bool _reachedEnd;

    public WaveFormat WaveFormat { get; }

    public double CrossfadeSeconds { get; set; }

    /// <summary>
    /// Raised from <see cref="PollAudibleTransition"/> (or immediately when no hardware buffer lag applies)
    /// when the next track becomes the audible foreground track.
    /// </summary>
    public event Action<string>? TrackTransitioned;

    public SeamlessStreamProvider(
        string initialPath,
        WaveStream initialReader,
        TimeSpan startAt,
        float initialReplayGain,
        int targetSampleRate,
        int targetChannels,
        bool allowResample,
        double crossfadeSeconds = 0.0,
        Func<string, WaveStream>? streamFactory = null)
    {
        _streamFactory = streamFactory ?? AudioFileOpener.OpenWaveStream;
        _sampleRate = Math.Max(8000, targetSampleRate);
        _channels = Math.Max(1, targetChannels);
        _allowResample = allowResample;
        CrossfadeSeconds = Math.Clamp(crossfadeSeconds, 0.0, 12.0);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
        _ring = new FrameRing(_channels);

        var conformed = BuildConformedChain(initialReader, _sampleRate, _channels, _allowResample, initialReplayGain)
            ?? throw new InvalidOperationException("Initial audio stream could not be conformed to output format.");

        _current = new TrackSlot(initialPath, initialReader, conformed, _sampleRate);
        long startFrames = (long)Math.Round(startAt.TotalSeconds * _sampleRate);
        _current.FramesRead = Math.Max(0, startFrames);
        _audibleOriginFrame = -_current.FramesRead;
        _audiblePath = initialPath;
        _audibleDuration = initialReader.TotalTime;
    }

    public string ActiveReaderPath
    {
        get { lock (_sync) return _current?.Path ?? _audiblePath; }
    }

    public TimeSpan AudibleDuration
    {
        get { lock (_sync) return _audibleDuration; }
    }

    /// <summary>The track the speakers are playing, which trails the render-side reader by the device buffer.</summary>
    public string AudiblePath
    {
        get { lock (_sync) return _audiblePath; }
    }

    /// <summary>
    /// True once the stream ran out of audio with no next track ready, so a stop that follows
    /// is the end of playback rather than an interruption.
    /// </summary>
    public bool ReachedEnd
    {
        get { lock (_sync) return _reachedEnd; }
    }

    public void SetReplayGainForCurrent(float factor)
    {
        float clamped = Math.Clamp(factor, 0.05f, 4.0f);
        lock (_sync)
        {
            if (_current != null)
                _current.GainStage.TargetGain = clamped;
        }
    }

    /// <summary>
    /// Queues the track that follows the current one. <paramref name="afterPath"/> names the track the
    /// caller believes is current; when the reader has already moved past it the request is stale
    /// (the caller will queue again once it sees the transition) and is ignored.
    /// </summary>
    public void SetQueuedNext(string? nextPath, float replayGainFactor = 1.0f, string? afterPath = null)
    {
        TrackSlot? stale = null;
        lock (_sync)
        {
            if (afterPath != null && _current != null &&
                !string.Equals(_current.Path, afterPath, StringComparison.OrdinalIgnoreCase))
                return;

            string? normalized = string.IsNullOrWhiteSpace(nextPath) ? null : nextPath;
            float clampedGain = Math.Clamp(replayGainFactor, 0.05f, 4.0f);
            bool samePath = string.Equals(_queuedNextPath, normalized, StringComparison.OrdinalIgnoreCase);

            _queuedNextPath = normalized;
            _queuedNextGain = clampedGain;
            if (!samePath)
            {
                _preopenFailed = false;
                stale = _preopenedNext;
                _preopenedNext = null;
            }
            else if (_preopenedNext != null)
            {
                _preopenedNext.GainStage.TargetGain = clampedGain;
            }
        }
        stale?.Dispose();
    }

    /// <summary>
    /// Pre-opens the queued next track on a background thread when the current track is within a few seconds of ending,
    /// avoiding both premature file locks during normal playback and disk-open latency on the audio render thread.
    /// </summary>
    public void TryPreopenNextIfClose()
    {
        string? pathToOpen = null;
        float gainToUse = 1.0f;

        lock (_sync)
        {
            if (_disposed || _current == null || _preopenedNext != null || _preopenFailed || string.IsNullOrEmpty(_queuedNextPath))
                return;

            if (_current.TotalFrames > 0)
            {
                double remSec = Math.Max(0, (_current.TotalFrames - _current.FramesRead) / (double)_sampleRate);
                double leadSec = Math.Max(3.5, CrossfadeSeconds + 2.5);
                if (remSec > leadSec)
                    return;
            }

            pathToOpen = _queuedNextPath;
            gainToUse = _queuedNextGain;
        }

        if (string.IsNullOrEmpty(pathToOpen)) return;

        TrackSlot? opened = TryCreateSlot(pathToOpen, gainToUse);
        TrackSlot? discard = null;
        lock (_sync)
        {
            if (_disposed || !string.Equals(_queuedNextPath, pathToOpen, StringComparison.OrdinalIgnoreCase) || _preopenedNext != null)
            {
                discard = opened;
            }
            else if (opened != null)
            {
                _preopenedNext = opened;
            }
            else
            {
                _preopenFailed = true;
            }
        }
        discard?.Dispose();
    }

    public bool UsesFilePath(string path)
    {
        lock (_sync)
        {
            return (_current != null && string.Equals(_current.Path, path, StringComparison.OrdinalIgnoreCase))
                || (_outgoingFade != null && string.Equals(_outgoingFade.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void ReleasePreopenedIfMatches(string path)
    {
        TrackSlot? toClose = null;
        lock (_sync)
        {
            if (_preopenedNext != null && string.Equals(_preopenedNext.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                toClose = _preopenedNext;
                _preopenedNext = null;
            }
        }
        toClose?.Dispose();
    }

    public void Seek(TimeSpan position, long speakerPlayedFrames)
    {
        TrackSlot? toDispose = null;
        List<string>? announce = null;
        lock (_sync)
        {
            if (_current == null) return;

            if (_outgoingFade != null && _pendingTransitions.Count > 0)
            {
                // Early in a crossfade the speakers (and the UI) are still on the outgoing track, so the
                // seek is meant for it: cancel the fade and put the incoming track back in the queue.
                if (!_outgoingFade.Reader.CanSeek) return;
                var incoming = _current;
                _current = _outgoingFade;
                _outgoingFade = null;
                _queuedNextPath = incoming.Path;
                _queuedNextGain = incoming.GainStage.TargetGain;
                _preopenFailed = false;
                toDispose = incoming;
            }
            else
            {
                if (!_current.Reader.CanSeek) return;
                toDispose = _outgoingFade;
                _outgoingFade = null;
                // A gapless handoff the speakers have not reached yet: announce it now, since the
                // seek lands in the new track.
                foreach (var tr in _pendingTransitions)
                    (announce ??= new List<string>(1)).Add(tr.Path);
            }

            _fadeTotalFrames = 0;
            _fadeElapsedFrames = 0;
            _ring.Clear();
            _lookaheadArmed = false;
            _lookaheadEof = false;
            _pendingTransitions.Clear();
            _reachedEnd = false;

            var target = PlaybackClock.SeekTarget(position, _current.TotalDuration);
            _current.Reader.CurrentTime = target;
            long seekFrames = (long)Math.Round(target.TotalSeconds * _sampleRate);
            _current.FramesRead = Math.Max(0, seekFrames);
            _audiblePath = _current.Path;
            _audibleDuration = _current.TotalDuration;
            _audibleOriginFrame = speakerPlayedFrames - _current.FramesRead;
        }
        toDispose?.Dispose();
        if (announce != null)
        {
            foreach (string p in announce)
                TrackTransitioned?.Invoke(p);
        }
    }

    public TimeSpan GetAudiblePosition(long speakerPlayedFrames)
    {
        PollAudibleTransition(speakerPlayedFrames);
        lock (_sync)
        {
            long elapsedFrames = Math.Max(0, speakerPlayedFrames - _audibleOriginFrame);
            var pos = TimeSpan.FromSeconds(elapsedFrames / (double)_sampleRate);
            if (_audibleDuration > TimeSpan.Zero && pos > _audibleDuration)
                return _audibleDuration;
            return pos;
        }
    }

    public void PollAudibleTransition(long speakerPlayedFrames)
    {
        List<string>? transitionedPaths = null;
        lock (_sync)
        {
            while (_pendingTransitions.Count > 0 && speakerPlayedFrames >= _pendingTransitions.Peek().SpeakerFrame)
            {
                var tr = _pendingTransitions.Dequeue();
                _audiblePath = tr.Path;
                _audibleDuration = tr.Duration;
                _audibleOriginFrame = tr.NewOriginFrame;
                transitionedPaths ??= new List<string>(1);
                transitionedPaths.Add(tr.Path);
            }
        }

        if (transitionedPaths != null)
        {
            foreach (string p in transitionedPaths)
                TrackTransitioned?.Invoke(p);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / _channels;
        if (framesRequested <= 0) return 0;

        int framesWritten = 0;
        var toDisposeAfterLock = new List<TrackSlot>();

        lock (_sync)
        {
            if (_disposed || _current == null) return 0;

            while (framesWritten < framesRequested && _current != null)
            {
                int remFrames = framesRequested - framesWritten;
                int dstOffset = offset + framesWritten * _channels;

                // 1) Active equal-power crossfade: the outgoing track's last audio (in _ring) under the incoming one
                if (_outgoingFade != null && _fadeTotalFrames > 0)
                {
                    int fadeChunkFrames = Math.Min(remFrames, _fadeTotalFrames - _fadeElapsedFrames);
                    int fadeSamples = fadeChunkFrames * _channels;
                    EnsureMixScratch(fadeSamples);

                    int readIn = ReadSlotSamples(_current, buffer, dstOffset, fadeSamples);
                    int readOut = _ring.Read(_mixScratch, 0, fadeChunkFrames) * _channels;
                    int actualFrames = Math.Max(readIn, readOut) / _channels;

                    if (actualFrames <= 0)
                    {
                        EndFadeLocked(toDisposeAfterLock);
                        continue;
                    }

                    // Zero-pad whichever stream ended early inside the crossfade window
                    for (int s = readIn; s < actualFrames * _channels; s++) buffer[dstOffset + s] = 0f;
                    for (int s = readOut; s < actualFrames * _channels; s++) _mixScratch[s] = 0f;

                    float invTotal = 1f / Math.Max(1, _fadeTotalFrames);
                    for (int f = 0; f < actualFrames; f++)
                    {
                        float t = Math.Clamp((_fadeElapsedFrames + f) * invTotal, 0f, 1f);
                        float gainOut = MathF.Cos(t * MathF.PI * 0.5f);
                        float gainIn = MathF.Sin(t * MathF.PI * 0.5f);
                        int baseIdx = f * _channels;
                        for (int c = 0; c < _channels; c++)
                        {
                            buffer[dstOffset + baseIdx + c] =
                                _mixScratch[baseIdx + c] * gainOut + buffer[dstOffset + baseIdx + c] * gainIn;
                        }
                    }

                    _fadeElapsedFrames += actualFrames;
                    _totalFramesEmitted += actualFrames;
                    framesWritten += actualFrames;

                    if (_fadeElapsedFrames >= _fadeTotalFrames || readOut < actualFrames * _channels)
                        EndFadeLocked(toDisposeAfterLock);
                    continue;
                }

                // 2) Crossfade wanted and the next track is open: read one fade length ahead, so the
                //    real end of the track is known when it comes.
                int wantFade = DesiredFadeFramesLocked();
                if (!_lookaheadArmed && wantFade > 0 && _preopenedNext != null)
                {
                    _ring.Clear();
                    _lookaheadFrames = wantFade;
                    _lookaheadEof = ReadSlotIntoRing(_current, wantFade) < wantFade;
                    _lookaheadArmed = true;
                }

                if (_lookaheadArmed)
                {
                    if (!_lookaheadEof)
                        _lookaheadEof = ReadSlotIntoRing(_current, remFrames) < remFrames;

                    if (!_lookaheadEof)
                    {
                        int lagged = Math.Min(remFrames, _ring.Count - _lookaheadFrames);
                        if (lagged <= 0) lagged = Math.Min(remFrames, _ring.Count);
                        int n = _ring.Read(buffer, dstOffset, lagged);
                        _totalFramesEmitted += n;
                        framesWritten += n;
                        continue;
                    }

                    // The reader is dry, so the ring holds the track's last audio.
                    var next = CrossfadeSeconds >= 0.2 ? _preopenedNext : null;
                    if (next != null && _ring.Count > 0)
                    {
                        int maxFade = next.TotalFrames > 0
                            ? (int)Math.Min(int.MaxValue, Math.Max(_sampleRate / 4, next.TotalFrames / 2))
                            : int.MaxValue;
                        // The ring can hold a little more than the lookahead (the last top-up), which plays first.
                        int fadeLen = Math.Min(Math.Min(_ring.Count, maxFade), Math.Max(1, _lookaheadFrames));
                        if (_ring.Count > fadeLen)
                        {
                            int n = _ring.Read(buffer, dstOffset, Math.Min(remFrames, _ring.Count - fadeLen));
                            _totalFramesEmitted += n;
                            framesWritten += n;
                            continue;
                        }

                        ConsumeNextSlotLocked();
                        long fadeStartEmitted = _totalFramesEmitted;
                        _outgoingFade = _current;
                        _current = next;
                        _fadeTotalFrames = fadeLen;
                        _fadeElapsedFrames = 0;
                        _lookaheadArmed = false;
                        _lookaheadEof = false;
                        _reachedEnd = false;

                        // Switch visible track metadata ~30% into the crossfade as the incoming song rises
                        long notifyAt = fadeStartEmitted + _fadeTotalFrames / 3;
                        _pendingTransitions.Enqueue(new PendingAudibleTransition(
                            notifyAt, next.Path, next.TotalDuration, fadeStartEmitted));
                        continue;
                    }

                    if (_ring.Count > 0)
                    {
                        int n = _ring.Read(buffer, dstOffset, remFrames);
                        _totalFramesEmitted += n;
                        framesWritten += n;
                        continue;
                    }

                    // Drained with no crossfade to make: the gapless path below sees the dry reader.
                    _lookaheadArmed = false;
                    _lookaheadEof = false;
                }

                // 3) Standard / Gapless read from _current
                int neededSamples = remFrames * _channels;
                int gotSamples = ReadSlotSamples(_current, buffer, dstOffset, neededSamples);
                int gotFrames = gotSamples / _channels;
                if (gotFrames > 0)
                {
                    _totalFramesEmitted += gotFrames;
                    framesWritten += gotFrames;
                }

                if (gotFrames < remFrames)
                {
                    // Current track reached EOF! Attempt sample-accurate 0 ms gapless handoff into next track:
                    var nextSlot = ConsumeNextSlotLocked();
                    if (nextSlot != null)
                    {
                        toDisposeAfterLock.Add(_current);
                        long handoffFrame = _totalFramesEmitted;
                        _current = nextSlot;
                        _reachedEnd = false;
                        _pendingTransitions.Enqueue(new PendingAudibleTransition(
                            handoffFrame, nextSlot.Path, nextSlot.TotalDuration, handoffFrame));
                        continue;
                    }

                    // No next track ready (none queued, not opened in time, or a different format):
                    // let the stream end, and the player starts the next track the ordinary way.
                    _reachedEnd = true;
                    break;
                }
            }
        }

        foreach (var old in toDisposeAfterLock)
            old.Dispose();

        return framesWritten * _channels;
    }

    private int ReadSlotSamples(TrackSlot slot, float[] dst, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int got = slot.GainStage.Read(dst, offset + totalRead, count - totalRead);
            if (got <= 0) break;
            totalRead += got;
        }
        slot.FramesRead += totalRead / _channels;
        return totalRead;
    }

    private int ReadSlotIntoRing(TrackSlot slot, int frames)
    {
        int samples = frames * _channels;
        if (_readScratch.Length < samples) _readScratch = new float[samples];
        int got = ReadSlotSamples(slot, _readScratch, 0, samples) / _channels;
        _ring.Write(_readScratch, 0, got);
        return got;
    }

    /// <summary>The crossfade length in frames, or 0 when off or the current track is under 2.5 fades long.</summary>
    private int DesiredFadeFramesLocked()
    {
        double sec = CrossfadeSeconds;
        if (sec < 0.2 || _current == null) return 0;
        int frames = (int)Math.Round(sec * _sampleRate);
        if (_current.TotalFrames > 0 && _current.TotalFrames < (long)frames * 5 / 2) return 0;
        return frames;
    }

    private void EndFadeLocked(List<TrackSlot> toDispose)
    {
        if (_outgoingFade != null) toDispose.Add(_outgoingFade);
        _outgoingFade = null;
        _fadeTotalFrames = 0;
        _fadeElapsedFrames = 0;
        _ring.Clear();
    }

    /// <summary>
    /// Takes the next track only if <see cref="TryPreopenNextIfClose"/> already opened it. Opening a file
    /// here would run on the audio render thread under the lock, where a slow disk is a dropout.
    /// </summary>
    private TrackSlot? ConsumeNextSlotLocked()
    {
        if (_preopenedNext == null)
            return null;

        var ready = _preopenedNext;
        _preopenedNext = null;
        _queuedNextPath = null;
        return ready;
    }

    private TrackSlot? TryCreateSlot(string path, float replayGain)
    {
        WaveStream? reader = null;
        try
        {
            reader = _streamFactory(path);
            var conformed = BuildConformedChain(reader, _sampleRate, _channels, _allowResample, replayGain);
            if (conformed == null)
            {
                reader.Dispose();
                return null;
            }
            return new TrackSlot(path, reader, conformed, _sampleRate);
        }
        catch
        {
            try { reader?.Dispose(); } catch { }
            return null;
        }
    }

    private void EnsureMixScratch(int samples)
    {
        if (_mixScratch.Length < samples)
            _mixScratch = new float[samples];
    }

    private static SmoothGainSampleProvider? BuildConformedChain(
        WaveStream reader,
        int targetSampleRate,
        int targetChannels,
        bool allowResample,
        float replayGain)
    {
        ISampleProvider sp = reader.ToSampleProvider();
        // A different channel count (stereo after 5.1, mono after stereo) is not joined: the speaker
        // routing is built for the first track's layout, so that track starts the ordinary way.
        if (sp.WaveFormat.Channels != targetChannels)
            return null;
        if (sp.WaveFormat.SampleRate != targetSampleRate)
        {
            if (!allowResample)
                return null;
            sp = new WdlResamplingSampleProvider(sp, targetSampleRate);
        }
        return new SmoothGainSampleProvider(sp, Math.Clamp(replayGain, 0.05f, 4.0f));
    }

    public void Dispose()
    {
        TrackSlot? cur;
        TrackSlot? fade;
        TrackSlot? next;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cur = _current; _current = null;
            fade = _outgoingFade; _outgoingFade = null;
            next = _preopenedNext; _preopenedNext = null;
            _pendingTransitions.Clear();
        }
        cur?.Dispose();
        fade?.Dispose();
        next?.Dispose();
    }

    /// <summary>A growable FIFO of interleaved float frames.</summary>
    private sealed class FrameRing
    {
        private readonly int _ch;
        private float[] _buf = Array.Empty<float>();
        private int _capFrames;
        private int _start;

        public int Count { get; private set; }

        public FrameRing(int channels) => _ch = Math.Max(1, channels);

        public void Clear()
        {
            _start = 0;
            Count = 0;
        }

        public void Write(float[] src, int offset, int frames)
        {
            if (frames <= 0) return;
            if (Count + frames > _capFrames) Grow(Count + frames);
            int end = (_start + Count) % _capFrames;
            int first = Math.Min(frames, _capFrames - end);
            Array.Copy(src, offset, _buf, end * _ch, first * _ch);
            if (first < frames)
                Array.Copy(src, offset + first * _ch, _buf, 0, (frames - first) * _ch);
            Count += frames;
        }

        public int Read(float[] dst, int offset, int frames)
        {
            int n = Math.Min(frames, Count);
            if (n <= 0) return 0;
            int first = Math.Min(n, _capFrames - _start);
            Array.Copy(_buf, _start * _ch, dst, offset, first * _ch);
            if (first < n)
                Array.Copy(_buf, 0, dst, offset + first * _ch, (n - first) * _ch);
            _start = (_start + n) % _capFrames;
            Count -= n;
            return n;
        }

        private void Grow(int minFrames)
        {
            int cap = Math.Max(minFrames, Math.Max(4096, _capFrames * 2));
            var nb = new float[cap * _ch];
            int n = Count;
            if (n > 0)
            {
                int first = Math.Min(n, _capFrames - _start);
                Array.Copy(_buf, _start * _ch, nb, 0, first * _ch);
                if (first < n)
                    Array.Copy(_buf, 0, nb, first * _ch, (n - first) * _ch);
            }
            _buf = nb;
            _capFrames = cap;
            _start = 0;
        }
    }
}
