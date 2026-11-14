# ZenMusic — Iterative Thin-Slice Implementation Plan

Structured around **working end-to-end thin slices** so you can launch, listen, see the visualizer, test 2.0 vs. 5.1 output, and iterate on UI/UX polish from the very first milestone.

---

## Slice 1: Working Audio Engine (2.0 / 5.1), Live Win2D Visualizer & Prototype UI
**Goal**: A runnable, self-contained `ZenMusic.exe` in `Bin\` that scans a music folder, displays a clean track list, plays audio through the WASAPI engine with **2.0 Stereo / 5.1 Surround** switching in the **`⚙` Settings** menu, and renders a live **60 fps Win2D Visualizer**.

- [ ] **Project & Build Foundation**:
  - `ZenMusic.csproj` (`net8.0-windows10.0.19041.0`, WinUI 3, Win2D, `NAudio`, `Microsoft.Data.Sqlite`, `TagLibSharp`) outputting directly to `Bin\`.
  - `Tests\ZenMusic.Tests\ZenMusic.Tests.csproj` (`net8.0` xUnit) testing the 2.0/5.1 channel matrix, FFT binning, and half-star tag parser.
  - Core MIV utilities: `AppPaths.cs`, `AtomicFile.cs`, `ListMerge.cs`.
- [ ] **WASAPI Audio Engine (`Audio\WasapiAudioEngine.cs`)**:
  - Play / Pause / Seek / Next / Previous / Volume / `MusicDeck` shuffle.
  - **`SpeakerMatrixProvider`**: Live switching between `2.0 Stereo (Default)`, `2.1 Stereo + Sub`, `5.1 Surround (Stereo Upmix with Center/LFE/Rear trim)`, and `5.1 Direct`.
  - **Lock-Free FFT & Waveform Tap (`Audio\FftAnalyzer.cs`)**: 2048-point Hann FFT mapped to logarithmic frequency bars + stereo waveform samples.
  - Windows SMTC (`SystemMediaTransportControls`) integration for media keys.
- [ ] **Live Win2D Visualizer (`UI\VisualizerControl.cs`)**:
  - 60 fps hardware-accelerated Win2D control supporting **Spectrum Bars** (with peak hold) and **Silk Waveform** modes.
- [ ] **Prototype Shell & `⚙` Config Menu**:
  - Integrated titlebar (Open Folder, Search filter, View toggle, **`⚙` Settings wheel**).
  - **`⚙` Settings Flyout/Dialog**: Output device selector, **2.0 Stereo / 5.1 Surround** channel configuration, Visualizer mode selector, and Light/Dark theme switch.
  - Virtualized Track Table (Title, Artist, Album, Rating, Duration, Bitrate, Path) with OneDrive `RecallOnDataAccess` safety and half-star display.
  - Pinned-Dark Floating Transport Playbar with scrubber, transport buttons, and visualizer integration.

---

## Slice 2: SQLite Library, Playlists & MediaMonkey (`MM5.DB`) Migration
**Goal**: Persistent instant-load library, full static & smart playlist management, and one-click import from MediaMonkey 2024.

- [ ] **SQLite + FTS5 Database (`Library\MusicDatabase.cs`)**:
  - Instant search across Latin + CJK tracks, facet filtering (`All files`, `Albums`, `Artists`, `Genres`, `Years`, `Ratings`), and `ArtCache` thumbnail deduplication.
- [ ] **First-Class Playlists (`Library\PlaylistService.cs` & UI)**:
  - Create, rename, delete (with Undo), drag-and-drop track ordering, right-click `Add to playlist`, `Save Queue as Playlist`, and `.m3u` / `.m3u8` import/export.
  - Rule-based **Smart / Auto-Playlists** (`Top Rated 4★+`, `Recently Added`, `Most Played`, custom rules).
- [ ] **MediaMonkey 2024 (`MM5.DB`) Importer**:
  - One-click import in `⚙` Settings bringing over all ratings, `PlayCounter`, `DateAdded` (`Timestamp`), `LastTimePlayed`, and existing MediaMonkey playlists.
- [ ] **Two-Way Half-Star Rating Sync**:
  - Inline half-star clicking in the table, inspector, and playbar with immediate SQLite update + background ID3 `POPM` / Vorbis `RATING` tag write-back.

---

## Slice 3: Multi-View Canvas, `Selected` Inspector & Tag Hygiene
**Goal**: Complete MediaMonkey parity and superiority in curation, cover art, lyrics, and library cleanup.

- [ ] **View Switcher**:
  - **Album Art Grid** (MIV Image 1 style) with inline expandable tracklist + **Multi-Column Compact View** (MIV Image 2 style).
- [ ] **Right Split Drawer (`Now Playing Queue` + `Selected Track Inspector`)**:
  - Strictly decoupled `Selected` track editing from `Now Playing`.
  - One-click **`Lookup image`** (Cover Art Archive / iTunes / Deezer 4-image picker) and **`Lookup lyrics`** (LRCLIB synced `.lrc` & plain lyrics).
- [ ] **`Files to edit` Smart Hygiene & Batch Tagging**:
  - Views for *Missing cover art*, *Missing tags*, *Dirty downloader filenames*, *Duplicates*, and *Low bitrate (<128 kbps)*.
  - Batch tag editor + Auto-Organize file renamer with Before -> After diff preview and `Ctrl+Z` Undo stack.

---

## Slice 4: Audiophile Polish, Focus Mode (`F11`) & BeatGrid Smart Mixes
**Goal**: Elevated listening stage and intelligent DSP.

- [ ] **10-Band Parametric/Graphic EQ & ReplayGain**:
  - Pinned-dark EQ popover on the playbar with presets + automatic track/album loudness normalization.
- [ ] **Focus / Zen Listening Stage (`F11`)**:
  - Full-window dark stage with large album art, ambient color wash, time-synced scrolling `.lrc` lyrics, and full-canvas Win2D visualizer.
- [ ] **BeatGrid Tempo & Energy Mixes**:
  - Background BPM/energy analysis (ported from MIV's `BeatReader` / `BeatGrid`) powering one-click mood/tempo mixes.
