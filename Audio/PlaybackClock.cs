using System;

namespace ZenMusic.Audio;

/// <summary>
/// Maps the WASAPI render clock onto a position in the song.
/// The file reader's cursor runs ahead of the speakers by whatever the device has buffered.
/// </summary>
internal static class PlaybackClock
{
    public static TimeSpan Heard(TimeSpan origin, long clockBytes, long clockBytesAtOrigin, int bytesPerSecond, TimeSpan duration)
    {
        if (bytesPerSecond <= 0)
            return Clamp(origin, duration);

        long delta = clockBytes - clockBytesAtOrigin;
        if (delta < 0) delta = 0;
        var heard = origin + TimeSpan.FromSeconds(delta / (double)bytesPerSecond);
        return Clamp(heard, duration);
    }

    /// <summary>
    /// Where to reopen a file so playback continues from <paramref name="requested"/>.
    /// A missing duration still seeks: skipping the seek starts the song over while the
    /// scrubber is still showing <paramref name="requested"/>.
    /// </summary>
    public static TimeSpan SeekTarget(TimeSpan requested, TimeSpan total)
    {
        if (requested <= TimeSpan.Zero) return TimeSpan.Zero;
        if (total <= TimeSpan.Zero) return requested;
        double maxMs = Math.Max(0, total.TotalMilliseconds - 50);
        if (requested.TotalMilliseconds > maxMs)
            return TimeSpan.FromMilliseconds(maxMs);
        return requested;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (duration > TimeSpan.Zero && value > duration) return duration;
        return value;
    }
}
