# ZenMusic — Architecture & Engineering Ledger

## 1. Product & Engineering Philosophy
**ZenMusic** is an unpackaged, portable-first **WinUI 3 + Win2D** desktop music manager and player (`.NET 8`, `net8.0-windows10.0.19041.0`) designed to match MediaMonkey 2024's library and playlist management while delivering superior audio playback (2.0 Stereo & 5.1 Surround), a 60 fps hardware-accelerated visualizer, and the clean engineering discipline of **ModernImageViewer (MIV)**.

Core architectural tenets carried over from MIV:
1. **Single-Project Simplicity**: One main project (`ZenMusic.csproj`) at the repository root outputting directly to `Bin\`, with unit tests in `Tests\ZenMusic.Tests` linking pure logic files by source (avoiding multi-project WinUI packaging overhead while keeping domain, tag, channel-matrix, and FFT math unit-testable on standard `net8.0`).
2. **Unpackaged & Portable State**: `<WindowsPackageType>None</WindowsPackageType>`. Never call `ApplicationData.Current`. State lives in `%LocalAppData%\ZenMusic\`, or in `Data\` beside `ZenMusic.exe` when that folder or `portable.marker` is present. `settings.json` and `library-cache.json` are written with `AtomicFile` (a temp file, then `File.Move`, retried on `IOException` or `UnauthorizedAccessException`). A `settings.json` that will not parse is copied to `settings.json.unreadable` and replaced with defaults. A library cache that will not parse is ignored, and the next scan rebuilds it.
3. **Off the UI thread**: Disk enumeration, the startup library-cache read, tag parsing, thumbnail decoding, and loudness analysis run off the UI thread. Folder scans use a monotonic `_scanId` so a stale scan cannot overwrite a newer one. A startup rescan keeps the existing `TrackItem` when the path matches, so selection and the now-playing row survive the refresh.

---

## 2. Repository & Build Layout

```text
ZenMusic/
├── Bin/                         # Self-contained build & publish output (no TFM/RID subfolders)
├── Docs/                        # Architecture, UI/UX design ledger, implementation plan
├── Tests/
│   └── ZenMusic.Tests/          # xUnit (net8.0) linking pure logic files by source
├── Library/                     # JSON library cache and incremental scanner
├── Audio/                       # WasapiAudioEngine (2.0 / 5.1 routing), FftAnalyzer, EQ, SMTC
├── Tagging/                     # TagLibSharp + POPM/Vorbis half-star bridge, Auto-Organize, Online Lookup
├── UI/                          # Win2D Visualizer, floating playbar, playlists, inspector, grid/table views
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs
├── AppPaths.cs
├── AtomicFile.cs
├── ListMerge.cs
└── ZenMusic.csproj
```

### Build Invariants (`ZenMusic.csproj`)
- `<SelfContained>true</SelfContained>`, `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`, `<PublishSingleFile>False</PublishSingleFile>`.
- Includes MIV's `TrimLocaleResourceFolders` (retains `en*` `.mui` only) and `RemoveUnusedWindowsML` (strips ~43 MB of unused ONNX/DirectML/AI projection DLLs).
- `<Compile Remove="Tests\**" />` so the in-tree xUnit project does not collide with the SDK default glob.
- NuGet packages: `Microsoft.WindowsAppSDK` (2.0.1), `Microsoft.Graphics.Win2D` (1.4.0), `NAudio.Core` and `NAudio.Wasapi` (2.2.1, WASAPI playback, FFT, and file readers), `TagLibSharp` (2.3.0). There is no database package.

---

## 3. Audio Engine, 2.0 / 5.1 Channel Routing & Live Visualizer Pipeline

Because a first-class **real-time visualizer** and **2.0 Stereo / 5.1 Surround speaker configuration** are core requirements, ZenMusic uses **NAudio + WASAPI (`WasapiOut`)** as its primary audio engine from Thin Slice 1 (rather than deferring it):

```text
[Audio File (MP3 / FLAC / M4A / WAV / OGG / WMA)]
        │
        ▼
[MediaFoundationReader / WaveFileReader / AiffFileReader, then ToSampleProvider]
        │
        ▼
[Optional resample, ReplayGain gain, 10-band biquad EQ]
        │
        ├──► [Lock-Free Ring Buffer -> 2048-pt Hann FFT Tap] ──► [Win2D 60fps Visualizer]
        │
        ▼
[SpeakerMatrixProvider (Channel Layout Engine)]
   ├── 2.0 Stereo (Default): Direct L/R output (or downmix if source is 6ch)
   ├── 2.1 Stereo + Sub: L/R + low-pass LFE crossover (80 Hz)
   ├── 5.1 Surround (Stereo Upmix):
   │     • Front L / Front R: Direct stereo
   │     • Center (C): (L + R) * 0.5 vocal/centerfill gain
   │     • LFE / Subwoofer: Low-pass (<80 Hz) biquad filter from (L + R)
   │     • Rear Surround L / R: (L - R) ambient/Haas-delayed matrix with user gain trim
   └── 5.1 Surround (Direct / Multichannel): Native 6-channel passthrough for 5.1 FLAC/WAV/AAC
        │
        ▼
[WASAPI Output (Shared or Exclusive Mode) + Windows SMTC Media Keys]
```

### Real-Time Visualizer Architecture (Win2D)
- **Zero-Allocation Audio Tap**: The `FftTapSampleProvider` copies stereo PCM samples into a pre-allocated ring buffer on the audio thread without locks or GC allocations.
- **Frequency & Waveform Processing**:
  - Computes a 2048-point Fast Fourier Transform (Hann window) mapped onto **logarithmic musical frequency bands** (sub-bass 20 Hz up to brilliance 16 kHz) with configurable attack (`~15 ms`) and exponential decay (`~180 ms`) plus peak-hold indicators.
  - Also exposes a decimated stereo time-domain oscilloscope/waveform buffer and instantaneous bass/beat energy.
- **GPU Rendering (`CanvasControl`)**:
  - Three Win2D controls (left playbar, right playbar, focus stage) share one `FftAnalyzer`. A frame computed in the last 20 ms is reused, so the two playbar controls do not each run the FFT. The spectrum gradient brush is kept for the life of the control and recreated when the theme changes or Win2D recreates the device. Drawing stops when playback stops, the mode is Off, or that stage is hidden.

---

## 4. Data Architecture (JSON) & Playlists

There is no database. Tracks live in `library-cache.json` (`LibraryCache`). Playlists, custom filters, the saved queue, and the other settings live in `settings.json` (`AppSettings`). Search, album and artist grouping, rating filters, and hygiene views run on the in-memory track list.

A cached track stores path, file size, last-write time, title, artist, album, genre, year, track number, disc number, duration, bitrate, sample rate, channels, codec, date added, rating, whether embedded art is present, play count, last played, and ReplayGain (track and album gain and peak, plus whether the gain came from the tag).

A playlist stores an id, a name, and the track paths. A custom filter stores an id, a name, an optional minimum rating, and an optional text match.

M3U and M3U8 import reads the file as UTF-8. Export writes `#EXTM3U` as UTF-8 with a BOM.

---

## 5. Critical File, Cloud & Unicode Invariants

1. **OneDrive Cloud-Only Guard (`RecallOnDataAccess = 0x400000`)**:
   - Checks `((int)File.GetAttributes(path) & 0x400000) != 0` before opening any file stream during scans so cloud-only OneDrive files are never downloaded accidentally.
2. **Full Unicode / CJK Path Support**:
   - All file streams and `TagLibSharp` abstractions use UTF-16 native Windows file handles so CJK (`屠洪刚`, `海来阿木`, `宋冬野`) and accented paths (`Dernière`) never fail.
3. **Two-Way Half-Star Rating Sync**:
   - Clicking a half-star updates the in-memory track, schedules `library-cache.json`, and writes ID3v2 `POPM` (MIV's `PopularimeterSteps` byte table) or FLAC/Vorbis `RATING` (`0–100`) on a background queue, so `ModernImageViewer` slideshows and `ZenMusic` read the same stars.
4. **WinUI `SoftwareBitmapSource` Lifetime**:
   - Leave `SoftwareBitmap` and `SoftwareBitmapSource` to GC after `SetBitmapAsync` to avoid WinUI `RO_E_CLOSED` background crashes.
