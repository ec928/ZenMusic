using NAudio.Wave;
using System;
using System.IO;

namespace ZenMusic.Audio;

/// <summary>
/// FLAC playback with exact seeks. Media Foundation's FLAC source ignores a seek made before its
/// first read, lands seconds away from the target in many files otherwise, and reports the requested
/// position as if it had landed there, so the scrubber and lyrics drift from the audio.
/// This reader finds the frame that holds the target itself (every FLAC frame header carries its
/// frame or sample number and a CRC), hands Media Foundation the file's header followed by the file
/// from that frame on, and drops the samples before the target. Media Foundation only ever decodes
/// from a frame start, so the position is exact. If the frame cannot be found, it decodes forward
/// from the start, which is exact but slower.
/// </summary>
public sealed class ExactSeekFlacReader : WaveStream
{
    private readonly string _path;
    private readonly FlacLayout _layout;
    private readonly WaveFormat _format;
    private readonly long _length;
    private WaveStream _inner;
    private long _position;
    private long _pendingSeek = -1;
    private long _dropBytes;

    /// <param name="path">The FLAC file.</param>
    /// <param name="flacOffset">Where "fLaC" starts (after an ID3v2 header, or 0).</param>
    public ExactSeekFlacReader(string path, long flacOffset)
    {
        _path = path;
        _layout = FlacLayout.Read(path, flacOffset);
        _inner = OpenFrom(_layout.FirstFrame);
        _format = _inner.WaveFormat;
        _length = _inner.Length;
    }

    public override WaveFormat WaveFormat => _format;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            long v = Math.Max(0, value);
            v -= v % Math.Max(1, _format.BlockAlign);
            _pendingSeek = v;
            _position = v;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pendingSeek >= 0) SeekNow();

        while (_dropBytes > 0)
        {
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _dropBytes));
            if (n <= 0) { _dropBytes = 0; return 0; }
            _dropBytes -= n;
        }

        int read = _inner.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    private void SeekNow()
    {
        long targetBytes = _pendingSeek;
        _pendingSeek = -1;
        int align = Math.Max(1, _format.BlockAlign);
        long targetSample = targetBytes / align;

        long frameOffset = _layout.FirstFrame;
        long frameSample = 0;
        if (targetSample > 0 && !_layout.TryFindFrame(targetSample, out frameOffset, out frameSample))
        {
            frameOffset = _layout.FirstFrame;
            frameSample = 0;
        }

        var next = OpenFrom(frameOffset);
        _inner.Dispose();
        _inner = next;
        _dropBytes = (targetSample - frameSample) * align;
    }

    // Media Foundation reads the file's header (fLaC + metadata) followed by the frames from frameOffset.
    private WaveStream OpenFrom(long frameOffset)
    {
        var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var spliced = new SplicedStream(fs, _layout.FlacOffset, _layout.FirstFrame, frameOffset);
        try
        {
            return new OwningReader(new StreamMediaFoundationReader(spliced), spliced);
        }
        catch
        {
            spliced.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed class OwningReader : WaveStream
    {
        private readonly WaveStream _reader;
        private readonly Stream _stream;
        public OwningReader(WaveStream reader, Stream stream) { _reader = reader; _stream = stream; }
        public override WaveFormat WaveFormat => _reader.WaveFormat;
        public override long Length => _reader.Length;
        public override long Position { get => _reader.Position; set => _reader.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _reader.Dispose(); _stream.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

/// <summary>Where the parts of a FLAC file are, and how to find the frame that holds a sample.</summary>
internal sealed class FlacLayout
{
    public long FlacOffset { get; private init; }
    public long FirstFrame { get; private init; }
    public long FileLength { get; private init; }
    public int MinBlock { get; private init; }
    public int MaxBlock { get; private init; }
    public long TotalSamples { get; private init; }
    private string Path { get; init; } = "";

    public static FlacLayout Read(string path, long flacOffset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = flacOffset;
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) != 4 || magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C')
            throw new InvalidDataException("Not a FLAC stream");

        int minBlock = 0, maxBlock = 0; long total = 0;
        Span<byte> hdr = stackalloc byte[4];
        while (true)
        {
            if (fs.Read(hdr) != 4) throw new InvalidDataException("Truncated FLAC metadata");
            bool last = (hdr[0] & 0x80) != 0;
            int type = hdr[0] & 0x7F;
            int len = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            if (type == 0 && len >= 18)
            {
                var si = new byte[18];
                fs.ReadExactly(si);
                minBlock = (si[0] << 8) | si[1];
                maxBlock = (si[2] << 8) | si[3];
                total = ((long)(si[13] & 0x0F) << 32) | ((long)si[14] << 24) | ((long)si[15] << 16) | ((long)si[16] << 8) | si[17];
                fs.Position += len - 18;
            }
            else
            {
                fs.Position += len;
            }
            if (last) break;
        }
        return new FlacLayout
        {
            Path = path,
            FlacOffset = flacOffset,
            FirstFrame = fs.Position,
            FileLength = fs.Length,
            MinBlock = minBlock,
            MaxBlock = maxBlock,
            TotalSamples = total
        };
    }

    /// <summary>Finds the frame whose samples include <paramref name="target"/> (binary search on byte offset).</summary>
    public bool TryFindFrame(long target, out long frameOffset, out long frameSample)
    {
        frameOffset = FirstFrame; frameSample = 0;
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long lo = FirstFrame, hi = FileLength;
            long bestOff = FirstFrame, bestSample = 0;
            for (int i = 0; i < 64 && hi - lo > 4096; i++)
            {
                long mid = lo + (hi - lo) / 2;
                if (!NextFrame(fs, mid, hi, out long off, out long s)) { hi = mid; continue; }
                if (s <= target) { bestOff = off; bestSample = s; lo = off + 1; }
                else hi = mid;
            }
            // Walk forward frame by frame to the last frame that starts at or before the target.
            long at = bestOff + 1;
            for (int i = 0; i < 4096; i++)
            {
                if (!NextFrame(fs, at, FileLength, out long off, out long s) || s > target) break;
                bestOff = off; bestSample = s; at = off + 1;
            }
            frameOffset = bestOff; frameSample = bestSample;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The first valid frame header at or after 'from' (before 'limit'), and the sample it starts at.
    private bool NextFrame(FileStream fs, long from, long limit, out long offset, out long sample)
    {
        offset = 0; sample = 0;
        var buf = new byte[65536 + 16];
        long pos = Math.Max(from, FirstFrame);
        while (pos < limit)
        {
            fs.Position = pos;
            int n = fs.Read(buf, 0, buf.Length);
            if (n < 16) return false;
            for (int i = 0; i + 16 <= n; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xFE) != 0xF8) continue;
                if (pos + i >= limit) return false;
                if (TryParseHeader(buf, i, n, out sample)) { offset = pos + i; return true; }
            }
            // A short read is the end of the file: stop (advancing by n - 16 could be 0 there).
            if (n < buf.Length) return false;
            pos += n - 16;
        }
        return false;
    }

    private bool TryParseHeader(byte[] b, int at, int end, out long sample)
    {
        sample = 0;
        bool variable = (b[at + 1] & 0x01) != 0;
        int bsCode = b[at + 2] >> 4, srCode = b[at + 2] & 0x0F;
        int chCode = b[at + 3] >> 4, ssCode = (b[at + 3] >> 1) & 0x07;
        if (bsCode == 0 || srCode == 15 || chCode > 10 || ssCode == 3 || (b[at + 3] & 0x01) != 0) return false;

        int p = at + 4;
        // UTF-8 style coded frame number (fixed block size) or sample number (variable).
        int first = b[p];
        int extra;
        long value;
        if ((first & 0x80) == 0) { value = first; extra = 0; }
        else if ((first & 0xE0) == 0xC0) { value = first & 0x1F; extra = 1; }
        else if ((first & 0xF0) == 0xE0) { value = first & 0x0F; extra = 2; }
        else if ((first & 0xF8) == 0xF0) { value = first & 0x07; extra = 3; }
        else if ((first & 0xFC) == 0xF8) { value = first & 0x03; extra = 4; }
        else if ((first & 0xFE) == 0xFC) { value = first & 0x01; extra = 5; }
        else if (first == 0xFE) { value = 0; extra = 6; }
        else return false;
        p++;
        for (int k = 0; k < extra; k++, p++)
        {
            if (p >= end || (b[p] & 0xC0) != 0x80) return false;
            value = (value << 6) | (long)(b[p] & 0x3F);
        }
        if (bsCode == 6) p += 1; else if (bsCode == 7) p += 2;
        if (srCode == 12) p += 1; else if (srCode == 13 || srCode == 14) p += 2;
        if (p >= end) return false;
        if (Crc8(b, at, p - at) != b[p]) return false;

        if (variable) sample = value;
        else
        {
            // Fixed block size: the header holds the frame number; every frame but the last is MaxBlock long.
            if (MaxBlock <= 0) return false;
            sample = value * MaxBlock;
        }
        if (TotalSamples > 0 && sample > TotalSamples) return false;
        return true;
    }

    private static byte Crc8(byte[] data, int start, int length)
    {
        int crc = 0;
        for (int i = start; i < start + length; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return (byte)crc;
    }
}

/// <summary>Reads [headerStart, headerEnd) of a file followed by [bodyStart, end): a FLAC header and the frames from one frame on.</summary>
internal sealed class SplicedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _headerStart, _headerLength, _bodyStart, _bodyLength;
    private long _position;

    public SplicedStream(Stream inner, long headerStart, long headerEnd, long bodyStart)
    {
        _inner = inner;
        _headerStart = headerStart;
        _headerLength = Math.Max(0, headerEnd - headerStart);
        _bodyStart = bodyStart;
        _bodyLength = Math.Max(0, inner.Length - bodyStart);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _headerLength + _bodyLength;
    public override long Position { get => _position; set => _position = Math.Clamp(value, 0, Length); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0 && _position < Length)
        {
            long src; long avail;
            if (_position < _headerLength) { src = _headerStart + _position; avail = _headerLength - _position; }
            else { src = _bodyStart + (_position - _headerLength); avail = Length - _position; }
            _inner.Position = src;
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, avail));
            if (n <= 0) break;
            _position += n; offset += n; count -= n; total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => _position
        };
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
