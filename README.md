# ZenMusic

**WASAPI Music Player, Tag Editor & Lyrics Stage for Windows.**

ZenMusic is a fast, native desktop player built with WinUI 3, Win2D, and NAudio WASAPI. It is designed to browse multi-thousand-track music folders effortlessly, route audio cleanly to stereo or 5.1 surround setups, keep your tags, ratings, cover art, and lyrics tidy, and stay lightweight without accounts or telemetry.

---

## Key Features

### Library & Browsing
- **Table & Album Tile Views** — Toggle between a dense, sortable multi-column track table (with mini row artwork, `MM/yyyy` dates, and clean numeric bitrates) and a visual album-cover grid.
- **Instant Startup & Search** — Caches your library locally for quick launch, watches your music folder automatically for file additions or edits, filters in real time (`Ctrl+F`), and supports A–Z / 0–9 type-to-jump.
- **Half-Star Ratings & Filtering** — Rate any track in `0.5`-star steps (click the left half of a star for `.5`, right half for `.0`, click again to clear). Ratings are saved straight into your audio file tags, and you can filter the library by star threshold or click any artist name.
- **Play Queue & Playlists** — Manage your upcoming Play Queue or build, reorder, and play saved playlists in the right-hand workbench alongside the Inspector card.
- **File Associations & Cloud Indicators** — Open `.mp3` and supported audio files directly from File Explorer to start playing immediately, and spot OneDrive cloud-only files at a glance.

### Audio & Visualization
- **Native WASAPI Playback** — Supports `.mp3`, `.flac`, `.m4a`, `.aac`, `.wav`, `.wma`, `.ogg`, and `.opus` with selectable Windows output devices and automatic stream recovery.
- **2.0, 2.1 & 5.1 Speaker Routing** — Switch between **2.0 Stereo**, **2.1 + LFE** (`<80 Hz` subwoofer crossover), **5.1 Smart Upmix** (with adjustable center vocal fill, LFE gain, and ambient rear stage), or **5.1 Direct** multichannel passthrough.
- **GPU Visualizers & F11 Focus Stage** — Choose **Spectrum bars**, **Silk waveform**, or **Bars + waveform** in the playbar and expand into the full-screen **Focus Stage** (`F11`) with cover-art color tinting and live synced lyrics.
- **Mini Player** — Collapse to a compact bar (`Ctrl+M`) whenever you want playback controls out of the way.

### Tags, Cover Art & Lyrics
- **Auto-Tag & Cover Art Lookup** — Right-click a track (or multiple tracks) and choose **Auto-Tag** to search **MusicBrainz**, **iTunes**, and **LRCLIB** for clean titles, album metadata, high-resolution artwork, and lyrics. You choose exactly which fields to apply.
- **Single & Batch Properties (`F2`)** — Edit tags, swap cover art, inspect codec/bitrate details, batch-edit multiple tracks at once, and undo edits with `Ctrl+Z`.
- **Synced `.lrc` & Embedded Lyrics** — Displays plain or timestamped `.lrc` lyrics in the Inspector and Focus Stage, highlighting the active line and letting you click any line to jump straight to that moment in the song.

### Privacy & Portability
- **No accounts, no ads, no tracking** — Your library, playlists, and ratings stay on your machine. Online requests happen only when you choose to run Auto-Tag.
- **Light & Dark Themes** — Matches Windows Fluent styling with clean Dark and Light modes applied across the main canvas, playbar, dialogs, and dialogs' Inspector cards.

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Space` | Play / Pause |
| `Left` / `Right` | Seek backward / forward 5 seconds |
| `Home` / `End` | Jump to start / end of list or queue |
| `Ctrl+O` | Open music folder |
| `Ctrl+F` | Focus search box |
| `Ctrl+L` | Locate currently playing track in list |
| `Ctrl+B` / `Tab` | Toggle Playlists, Queue & Inspector sidebar |
| `Ctrl+M` | Toggle compact Mini Player |
| `Ctrl+Z` | Undo last tag, rating, rename, or playlist edit |
| `F1` | Show keyboard shortcuts |
| `F2` | Edit Track Properties & Lyrics (or Batch Properties) |
| `F5` | Rescan current music folder |
| `F11` / `Esc` | Toggle / Exit Focus Visualizer Stage |
| `Delete` | Remove from playlist/queue or move file to Recycle Bin |
| `A–Z` / `0–9` | Type-to-jump in the track list |

---

## Installation (Portable)

ZenMusic runs as a self-contained portable app with no installer or Windows Registry changes required:

1. Download the latest release `.zip` from [Releases](https://github.com/ec928/ZenMusic/releases).
2. Extract it to a folder of your choice (e.g. `C:\Apps\ZenMusic`).
3. Run `ZenMusic.exe`.

> **Portable Mode & Data Location:** By default, settings, playlists, and the library cache are stored in `%LocalAppData%\ZenMusic`. To keep all data self-contained inside the app folder (e.g. on a USB drive), create a `Data` folder or an empty `portable.marker` file next to `ZenMusic.exe`.
>
> **Windows SmartScreen:** Because indie builds are unsigned, Windows Defender SmartScreen may show a prompt on first launch. Click **More info → Run anyway** to start.

---

## Building from Source

Requires Windows 10/11 (x64), the .NET 8 SDK, and the Windows App SDK build tools:

```powershell
git clone https://github.com/ec928/ZenMusic.git
cd ZenMusic
dotnet build ZenMusic.csproj -c Release -p:Platform=x64
```

Run tests:

```powershell
dotnet test Tests\ZenMusic.Tests\ZenMusic.Tests.csproj -p:Platform=x64
```

---

## License

MIT License — see [LICENSE](LICENSE).
