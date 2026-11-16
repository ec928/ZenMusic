using NAudio.Wave;
using System;
using System.IO;

namespace ZenMusic.Audio;

/// <summary>
/// Opens audio files for playback and loudness analysis, handling WAV/AIFF directly and
/// skipping any legacy ID3v2 header prepended before a FLAC stream so Windows Media Foundation
/// never fails with MF_E_UNSUPPORTED_BYTESTREAM_TYPE (0xC00D36C4).
/// </summary>
public static class AudioFileOpener
{
    public static WaveStream OpenWaveStream(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".wav" or ".aiff")
            return new AudioFileReader(path);

        if (ext == ".flac")
        {
            long flacOffset = FindFlacMagicAfterId3(path);
            if (flacOffset > 0)
            {
                return OpenStreamAtOffset(path, flacOffset);
            }
        }

        try
        {
            return new MediaFoundationReader(path);
        }
        catch
        {
            if (ext == ".flac")
            {
                long flacOffset = ScanFlacMagic(path);
                if (flacOffset > 0)
                    return OpenStreamAtOffset(path, flacOffset);
            }
            return new AudioFileReader(path);
        }
    }

    public static long FindFlacMagicAfterId3(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 14) return 0;

            Span<byte> hdr = stackalloc byte[10];
            if (fs.Read(hdr) < 10) return 0;

            // Check ID3v2 header: "ID3" + major/minor + flags + 4-byte synchsafe size
            if (hdr[0] != (byte)'I' || hdr[1] != (byte)'D' || hdr[2] != (byte)'3')
                return 0;

            long size = ((long)(hdr[6] & 0x7FU) << 21)
                      | ((long)(hdr[7] & 0x7FU) << 14)
                      | ((long)(hdr[8] & 0x7FU) << 7)
                      | (long)(hdr[9] & 0x7FU);
            long offset = 10 + size;
            if ((hdr[5] & 0x10) != 0) offset += 10; // optional ID3v2.4 footer
            if (offset + 4 > fs.Length) return 0;

            fs.Position = offset;
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) == 4 &&
                magic[0] == (byte)'f' && magic[1] == (byte)'L' && magic[2] == (byte)'a' && magic[3] == (byte)'C')
            {
                return offset;
            }
        }
        catch { }
        return 0;
    }

    private static long ScanFlacMagic(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int cap = (int)Math.Min(fs.Length, 2 * 1024 * 1024);
            var buf = new byte[cap];
            int n = fs.Read(buf, 0, cap);
            for (int i = 1; i + 3 < n; i++)
            {
                if (buf[i] == (byte)'f' && buf[i + 1] == (byte)'L' && buf[i + 2] == (byte)'a' && buf[i + 3] == (byte)'C')
                    return i;
            }
        }
        catch { }
        return 0;
    }

    private static WaveStream OpenStreamAtOffset(string path, long offset)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var sub = new OffsetReadStream(fs, offset);
        try
        {
            var reader = new StreamMediaFoundationReader(sub);
            return new OwnedWaveStream(reader, sub, fs);
        }
        catch
        {
            sub.Dispose();
            fs.Dispose();
            throw;
        }
    }

    private sealed class OwnedWaveStream : WaveStream
    {
        private readonly WaveStream _inner;
        private readonly Stream _sub;
        private readonly Stream _backing;

        public OwnedWaveStream(WaveStream inner, Stream sub, Stream backing)
        {
            _inner = inner;
            _sub = sub;
            _backing = backing;
        }

        public override WaveFormat WaveFormat => _inner.WaveFormat;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _sub.Dispose();
                _backing.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class OffsetReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _baseOffset;

        public OffsetReadStream(Stream inner, long baseOffset)
        {
            _inner = inner;
            _baseOffset = baseOffset;
            _inner.Position = _baseOffset;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => Math.Max(0, _inner.Length - _baseOffset);

        public override long Position
        {
            get => Math.Max(0, _inner.Position - _baseOffset);
            set => _inner.Position = _baseOffset + Math.Clamp(value, 0, Length);
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_inner.Position < _baseOffset) _inner.Position = _baseOffset;
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => Position
            };
            Position = target;
            return Position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
