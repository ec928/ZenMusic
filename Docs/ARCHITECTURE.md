# ZenMusic — Architecture & Engineering Ledger

## 1. Product & Engineering Philosophy
**ZenMusic** is an unpackaged, portable-first **WinUI 3 + Win2D** desktop music manager and player (`.NET 8`, `net8.0-windows10.0.19041.0`) designed to match MediaMonkey 2024's library and playlist management while delivering superior audio playback (2.0 Stereo & 5.1 Surround), a 60 fps hardware-accelerated visualizer, and the clean engineering discipline of **ModernImageViewer (MIV)**.

Core architectural tenets carried over from MIV:
1. **Single-Project Simplicity**: One main project (`ZenMusic.csproj`) at the repository root outputting directly to `Bin\`, with unit tests in `Tests\ZenMusic.Tests` linking pure logic files by source (avoiding multi-project WinUI packaging overhead while keeping domain, SQLite, tag, channel-matrix, and FFT math 100% unit-testable on standard `net8.0`).
2. **Unpackaged & Portable State**: `<WindowsPackageType>None</WindowsPackageType>`. Never call `ApplicationData.Current`. State lives in `%LocalAppData%\ZenMusic\` (or `Data\` beside `ZenMusic.exe` when running in Portable Mode). All JSON state (`settings.json`, saved queues/sessions) is written via `AtomicFile` with AV-lock retry and `.unreadable` backup recovery.
3. **Zero UI Thread Blocking**: All disk enumeration, SQLite writes, tag parsing, thumbnail decoding, and beat analysis run off the UI thread with monotonic cancellation/scan tickets (`_currentScanId`, `_openTicket`). UI collection updates use in-place diffing (`ListMerge`) rather than `Clear()` + reset so selection, scroll position, and live row controls survive refreshes.

---

## 2. Repository & Build Layout

```text
ZenMusic/
├── Bin/                         # Self-contained build & publish output (no TFM/RID subfolders)
├── Docs/                        # Architecture, UI/UX design ledger, implementation plan
├── Tests/
│   └── ZenMusic.Tests/          # xUnit (net8.0) linking pure logic files by source
├── Assets/                      # App icons, default vinyl/cover placeholder
├── Library/                     # SQLite + FTS5 store, incremental scanner, MM5.DB importer, Playlists
├── Audio/                       # WasapiAudioEngine (2.0 / 5.1 routing), FftAnalyzer, EQ, SMTC, BeatGrid
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
- NuGet packages: `Microsoft.WindowsAppSDK` (2.0.1), `Microsoft.Graphics.Win2D` (1.4.0), `NAudio` (MIT, for WASAPI 2.0/5.1 & live FFT), `Microsoft.Data.Sqlite`, `TagLibSharp`.

---

## 3. Audio Engine, 2.0 / 5.1 Channel Routing & Live Visualizer Pipeline

Because a first-class **real-time visualizer** and **2.0 Stereo / 5.1 Surround speaker configuration** are core requirements, ZenMusic uses **NAudio + WASAPI (`WasapiOut`)** as its primary audio engine from Thin Slice 1 (rather than deferring it):

```text
[Audio File (MP3 / FLAC / M4A / WAV / OGG / WMA)]
        │
        ▼
[AudioFileReader / MediaFoundationReader (Float32 PCM)]
        │
        ▼
[Gapless / Crossfade Transition Mixer]
        │
        ▼
[ReplayGain & 10-Band Biquad EQ SampleProvider]
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
- **GPU Rendering (`CanvasControl` / `CanvasAnimatedControl`)**:
  - Rendered via **Win2D** (Direct2D hardware acceleration) so a 60 fps visualizer uses <1% CPU and automatically pauses rendering when minimized or hidden.

---

## 4. Data Architecture (SQLite + FTS5) & Playlists Engine

SQLite (`Microsoft.Data.Sqlite`) in WAL mode (`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`) powers instant search, facet browsing, and playlist management.

### Core Tables
- **`tracks`**:
  - Identity & File State: `id` (PK), `path` (UNIQUE), `folder_path`, `file_name`, `file_size`, `mtime_utc`, `ctime_utc`, `is_cloud_only`, `is_missing`.
  - Metadata: `title`, `artist`, `album_artist`, `album`, `genre`, `composer`, `publisher`, `year`, `track_no`, `disc_no`, `comment`, `grouping`.
  - Audio Specs: `duration_ms`, `bitrate`, `sample_rate`, `bits_per_sample`, `channels`, `codec`.
  - Curation & History: `rating` (`0.0–5.0` in `0.5` steps), `play_count`, `skip_count`, `date_added_utc` (MediaMonkey `Timestamp`), `last_played_utc`.
  - DSP & Artwork: `replaygain_track_db`, `replaygain_album_db`, `bpm`, `beat_confidence`, `energy_avg`, `art_hash`, `has_lyrics`.
- **`tracks_fts`** (FTS5 virtual table):
  - `tokenize='unicode61 remove_diacritics 2'` for sub-millisecond Latin + CJK search.
- **`playlists`** (First-Class Commercial Playlist Support):
  - `id`, `name`, `description`, `kind` (`0 = Static`, `1 = Smart/Auto`, `2 = Pinned/System`), `parent_folder_id` (supports nested playlist folders like MediaMonkey), `rule_json` (for Smart Playlists), `sort_order`, `track_count`, `total_duration_ms`, `cover_art_hash`, `created_utc`, `updated_utc`.
- **`playlist_items`**:
  - `id`, `playlist_id`, `track_id`, `position` (integer with gap/renumber support for instant drag-and-drop reordering), `added_utc`.
- **`artwork_cache`**:
  - Deduplicated by `art_hash` (`SHA-256` -> `320x320` and `96x96` cached thumbnails in `ArtCache\`).

### Playlist Interoperability
- **M3U / M3U8 Import & Export**: Full support for relative and absolute paths with UTF-8 BOM handling for CJK filenames.
- **MediaMonkey `MM5.DB` Playlist Migration**: Imports both static playlists (`Playlists` + `PlaylistSongs`) and hierarchical playlist folders from `MM5.DB` (registering the custom `IUNICODE` collation).

---

## 5. Critical File, Cloud & Unicode Invariants

1. **OneDrive Cloud-Only Guard (`RecallOnDataAccess = 0x400000`)**:
   - Checks `((int)File.GetAttributes(path) & 0x400000) != 0` before opening any file stream during scans so cloud-only OneDrive files are never downloaded accidentally.
2. **Full Unicode / CJK Path Support**:
   - All file streams and `TagLibSharp` abstractions use UTF-16 native Windows file handles so CJK (`屠洪刚`, `海来阿木`, `宋冬野`) and accented paths (`Dernière`) never fail.
3. **Two-Way Half-Star Rating Sync**:
   - Clicking a half-star updates SQLite immediately (`0 ms` latency) and writes back to ID3v2 `POPM` (using MIV's exact `PopularimeterSteps` byte table) or FLAC/Vorbis `RATING` (`0–100`) on a background queue, keeping `ModernImageViewer` slideshows and `ZenMusic` 100% in sync.
4. **WinUI `SoftwareBitmapSource` Lifetime**:
   - Leave `SoftwareBitmap` and `SoftwareBitmapSource` to GC after `SetBitmapAsync` to avoid WinUI `RO_E_CLOSED` background crashes.
