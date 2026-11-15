# ZenMusic — UI/UX Specification, Features & Design Options

Designed to combine **ModernImageViewer (MIV)**'s high-craft Fluent aesthetic with the full library, playlist, visualizer, and audio-configuration power of a flagship music manager.

---

## 1. Visual Language & "Dark-in-Light" Premium Chrome

- **Dual Window Theme (Light & Dark)** with **Pinned Dark Floating Surfaces (`RequestedTheme="Dark"`)**:
  - Even when the main library table/grid is in Light theme, the **floating transport bar**, **visualizer HUD**, **settings/EQ popovers**, and **context flyouts** remain in rich dark frosted acrylic (`#181818`–`#202020` @ 88% opacity, `1px` subtle border stroke), matching MIV Image 3.
- **Theme-Aware Star Legibility (`FavouriteStarBrush`)**:
  - Vibrant Gold (`#FFC83B`) on dark surfaces; Warm Amber (`#D97706`) on light table rows so half-star ratings never wash out.
- **Typography & Casing**:
  - `Segoe UI Variable` with CJK fallback (`Microsoft YaHei UI`), sentence case across all menus/labels/headers, and `Segoe Fluent Icons`.

---

## 2. Optional Audio Visualizer (Win2D 60 fps)

The visualizer is **completely optional**—toggled on/off with a single click on the playbar (or in `⚙` Settings), consuming 0% GPU/CPU when turned off. When active, it feels responsive, musical, and elevated:

### Visual Modes (User-Selectable)
1. **Precision Spectrum Bars (Default)**:
   - 32 or 64 logarithmic frequency bands (20 Hz – 16 kHz) with rounded caps, smooth gravity decay, floating peak-hold dots, and subtle gradient tinting (Fluent Blue `#0078D4` -> Warm Gold `#FFC83B` or sampled from current album art).
2. **Silk Waveform / Oscilloscope**:
   - Dual-channel (or L/R + Sub) anti-aliased glowing ribbon curve rendered via Win2D geometry.
3. **Ambient Beat Pulse**:
   - Soft radial glow behind the album artwork driven by low-end/kick energy and `BeatGrid` downbeats.
4. **Off / Minimal Indicator**:
   - Disables GPU rendering completely (0% GPU/CPU) for background work sessions.

### Visualizer Placement (See Section 5 for Competing Layout Options)
- **Compact In-Bar / Dock View**: Always visible while browsing your library without hiding your tracks.
- **Expanded Theater / Focus Mode (`F11` or click visualizer)**: Full-canvas dark listening stage pairing the large Win2D visualizer with high-res album art and time-synced scrolling lyrics.

---

## 3. First-Class Playlists & Commercial Music Manager Features

Playlists are a primary pillar of ZenMusic, matching and improving on MediaMonkey's workflow:

### Playlist Capabilities
- **Static Playlists & Folders**:
  - Create, rename, duplicate, and organize playlists into folders.
  - **Drag-and-Drop**: Drag tracks from the library table or album grid straight onto any playlist, or drag within a playlist to reorder tracks effortlessly.
  - **Right-Click Context Menu**: `Add to playlist >` pins your top/recent playlists at the top of the sub-menu plus `+ New playlist from selection...`.
  - **Duplicate Guard**: Alerts (or skips) if you accidentally drop songs into a playlist that already contains them.
- **Smart / Auto-Playlists (Dynamic Rules)**:
  - Built-in defaults: `★ Top Rated (4.0+)`, `Recently Added`, `Most Played`, `Unrated Tracks`, `High Energy Mix`.
  - Custom rule builder (`Rating`, `Genre`, `Artist`, `Year`, `Bitrate`, `Play Count`, `Date Added`, `BPM / Energy`) with live track count preview.
- **Queue <-> Playlist Bridge**:
  - One-click **`Save queue as playlist...`** in the Now Playing Queue header.
  - **`Import / Export M3U & M3U8`** + direct import of all existing playlists from **MediaMonkey (`MM5.DB`)**.

### Core Commercial Manager Parity Checklist
- **Strict Separation of `Selected` vs. `Now Playing`**: Single-clicking inspects/edits/rates a track in the `Selected` panel; playback continues untouched until you explicitly Play or Queue.
- **Inline & Batch Tag Editing** with `Ctrl+Z` Undo stack.
- **One-Click `Lookup image` & `Lookup lyrics`** in the `Selected` track inspector.
- **`Files to edit` Hygiene Nodes**: *Missing cover art*, *Missing tags*, *Dirty downloader filenames*, *Duplicates*, *Low bitrate (<128 kbps)*, *OneDrive cloud-only files*.
- **Auto-Organize / Rename Files from Tags** with mandatory Before -> After preview.
- **Column Customization**: Show/hide, resize, and reorder table columns (`#`, `Title`, `Artist`, `Album`, `Date`, `Rating`, `Length`, `Timestamp`, `Bitrate`, `Plays`, `Path`).

---

## 4. Settings Menu (`⚙` Wheel Icon) & 2.0 / 5.1 Audio Setup

Clicking the top-right **`⚙` Settings** wheel opens a clean, dark-frosted configuration dialog/flyout organized into 4 clear tabs:

### 1. Audio & Speaker Setup
- **Output Device**: `Default Windows Audio Device` or specific WASAPI endpoint (Speakers, Headphones, HDMI AVR, Optical/SPDIF, USB DAC).
- **Speaker / Channel Configuration**:
  - **`2.0 Stereo (Default)`** — Standard left/right output (cleanly downmixes multi-channel files if played on stereo headphones/speakers).
  - **`2.1 Stereo + Subwoofer`** — Stereo L/R plus dedicated LFE subwoofer bass routing (<80 Hz crossover).
  - **`5.1 Surround (Smart Stereo Upmix)`** — Expands stereo music across all 6 channels (`Front L/R`, `Center` vocal fill, `LFE Subwoofer` bass, and `Rear Surround L/R` ambient stage) with sliders for **Center Level**, **Subwoofer (LFE) Boost**, and **Rear Surround Level**.
  - **`5.1 Surround (Direct / Multichannel)`** — Bit-accurate 6-channel output without upmixing stereo tracks.
- **Playback Transitions**: `Gapless (Default)` or `Crossfade (1–10 seconds)`.
- **Loudness / EQ**: ReplayGain mode (`Off`, `Track`, `Album`) + 10-band EQ preset selector.

### 2. Visualizer & Now Playing
- **Visualizer Style**: `Spectrum Bars`, `Silk Waveform`, `Bars + Waveform`, `Off`.
- **Color Palette**: `Theme Accent`, `Gold / Amber`, or `Dynamic Album Art Colors`.
- **Sensitivity & Smoothing**: `Calm / Smooth` vs. `Crisp / Fast`.

### 3. Library & MediaMonkey Migration
- **Watched Folders**: Add/remove music folders + `Rescan now`.
- **Import from MediaMonkey 2024**: Browse or auto-detect `MM5.DB` to import ratings, play counts, timestamps, and playlists.
- **Tag Write-Back**: Toggle to write ratings/tags directly to audio files and toggle to **Preserve file modification timestamp (`LastWriteTime`)** when rating tracks.

### 4. Appearance & Layout
- **App Theme**: `Dark (Default)`, `Light`, or `Use system setting`.
- **Floating Chrome**: Keep transport bar & popovers pinned to dark frosted style in Light theme (`On` by default, matching MIV).

---

## 5. Locked-In UI/UX Layout Decisions

1. **Optional Visualizer Placement**: **Embedded in the Bottom Dark Playbar + Click / `F11` for Full-Screen Stage** (with a 1-click toggle to hide/disable it completely).
2. **Playlists Access**: **Both** — Left navigation sidebar tree (supporting direct drag-and-drop from the track table) **plus** quick `Add to playlist >` right-click context menu and top-bar access.
3. **Default Window Layout**: **Collapsible 3-Pane Hybrid** — Opens with Left Sidebar (Library facets + Playlists), Center Canvas (Track Table / Album Grid), and Right Pane (Now Playing Queue + Selected Track Inspector), with 1-click collapse buttons on both side panes for a full-bleed MIV view.

---

## 6. UI as built (v0.4.0)

What the window actually does today, where it differs from or adds to the sections above.

- **Layout**: two panes (track table or tile view, plus the right pane with the queue or a playlist above the selected-track card). There is no left sidebar. The right pane is 316 px wide and slides in when opened.
- **Cover tint**: the playing cover's dominant colour washes the playbar, the right pane and, more faintly, the track list. It fades between tracks and is re-applied at lower strength in the light theme.
- **Tracks without a cover**: get a generated cover, a diagonal gradient in a hue derived from the artist name (album, then title, when there is no artist) with the artist's initial. The same hue drives the tint, so one artist always has one colour. Used in table rows, tiles, queue rows, playbar, inspector and focus stage.
- **Table**: no zebra striping; a 22 px cover starts each row; numeric columns (year, length, date added, bitrate) are one contrast level below artist and album; row stars are muted gold and return to full gold on the playing row and under the pointer.
- **Playbar**: the seek rail runs the full width along the playbar's top edge in place of a divider line; elapsed and total time sit either side of the transport buttons.
- **Tile view**: cards have no frame; the card lifts and its play button fades in under the pointer. Tiles are sized so a whole number fills the row, the spare width going into the covers. Ratings use the same star cell as the table. The table's header buttons close up into a "Sort by" strip at the left, since there are no columns to sit over.
- **Playing row and tile**: the highlight takes the cover colour (amber when there is no tint); the amber bar at the row's left edge stays.
- **Track card**: shows the selection, or the playing track when nothing is selected, and its stars and edit button act on whichever it shows. With nothing to show it follows the app theme. Text laid over the cover sits on a fade that builds up above the first line.
- **Table columns**: Title, Artist and Album share the spare width 2.4 : 1.1 : 1.2; the other columns are fixed.
- **Theme**: Dark, Light or "Use system setting", which follows the Windows app mode and changes with it while the app is open.
- **Cover colour strength**: Settings > Cover colour, 0 to 200 % in steps of 10 (100 = the standard wash). It scales the playbar, right pane and list washes; the playing row follows it between a floor and a ceiling so the row text stays readable. At 0 the playing row is amber and the About header is amber.
- **High contrast**: the cover washes are off and the playing row is the gold it has in the high-contrast theme.
- **About**: the six highlights sit in two columns when the window is at least 860 px wide, one column otherwise.
- **Ratings in MP3 files**: a rating is written to every rating frame the file carries, not only the Windows Media Player one, so a file first rated in another tagger reads back with the new value.
- **Focus stage (F11)**: the cover, decoded very small and stretched across the stage, forms a soft blurred backdrop under a dark veil; the cover itself is 300 px.
- **Settings**: a panel that slides in over the right of the window. Every change applies as it is made; there is no Apply or Cancel. It closes with its X, Esc, or a click outside. Slider changes are written and applied 250 ms after the last movement.
- **Menus**: the track and playlist context menus carry icons on their top-level actions, assigned by how each label starts (`MenuIconMap` in `MainWindow.xaml.cs`).
- **Motion**: cover images cross-fade on track and selection change; the playing-row highlight fades; the right pane, settings panel and focus stage fade or slide in. Closing is immediate.
