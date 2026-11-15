# ZenMusic

**A calm home for the music you own, on Windows.**

ZenMusic plays the music files in a folder on your PC. Point it at the folder and it gives you a fast, sortable library, clean sound on stereo or 5.1 speakers, and a window that takes on the colour of whatever is playing. There is no account, no subscription and no tracking.

---

## It takes on the colour of your music

- **The window follows the cover.** The colour of the playing track's cover washes the playbar, the side pane and, more faintly, the track list. When the track changes, the colour and the cover fade across to the next one.
- **Every track has a cover.** A track with no artwork gets a generated one: a gradient in a colour worked out from the artist's name, with the artist's initial on it. One artist always has one colour, and that colour tints the window too.
- **Focus Stage (`F11`).** The window fills with the cover over a soft, blurred copy of itself, a visualizer, and the lyrics. Synced lyrics light up line by line, and a click on a line jumps to that moment.
- **A quiet surface.** No zebra stripes and no boxes around things. A small cover starts each row, the seek bar runs along the top edge of the playbar, and album tiles lift and show a play button under the pointer.
- **Light, dark, or whatever Windows is set to.** Every theme carries the tint, at a lower strength in light.
- **As much colour as you like.** A slider in Settings takes the tint from off, through the standard wash, to vivid.

## Sound for the speakers you have

- **WASAPI playback** of `.mp3`, `.flac`, `.m4a`, `.aac`, `.wav`, `.wma`, `.ogg` and `.opus`, on the output device you choose, with automatic recovery when the device changes.
- **2.0, 2.1 or 5.1.** Pick **2.0 Stereo**, **2.1 + LFE** (a subwoofer crossover below 80 Hz), **5.1 Smart Upmix** (stereo spread across a surround system, with adjustable centre vocal fill, LFE gain and rear ambience) or **5.1 Direct** (multichannel files played as they are).
- **Visualizers** drawn on the GPU: spectrum bars, a silk waveform, or both, in the playbar and on the Focus Stage.
- **Mini Player (`Ctrl+M`)** when you only want the controls.

## A library that keeps up

- **Opens at once.** The library is cached, so a folder of thousands of tracks is there when the window appears, and the folder is watched for files you add or change.
- **Table or album tiles.** Sort the table with a click on a column, or browse covers in the tile view.
- **Find anything.** Search as you type (`Ctrl+F`), jump by typing a letter or digit, filter by star rating, or click an artist's name to see their tracks.
- **Half-star ratings** saved into the audio files themselves, so they travel with your music.
- **Queue and playlists** in the side pane, next to a card for the selected track with its cover, details and lyrics.
- **Opens from File Explorer.** Open a supported audio file and it starts playing. Files that are in OneDrive but not downloaded are marked.

## Tidy tags, covers and lyrics

- **Auto-Tag.** Right-click one track or several and ZenMusic looks up titles, album details, high-resolution artwork and lyrics on **MusicBrainz**, **iTunes** and **LRCLIB**. You tick the fields you want to keep.
- **Properties (`F2`).** Edit the tags of one track or many at once, change the cover, and check the codec and bitrate. `Ctrl+Z` undoes the last edit.
- **Lyrics.** Plain or timestamped, embedded in the file or in an `.lrc` file beside it.

## Yours

- **No account, no ads, no tracking.** Your library, playlists and ratings stay on your PC. ZenMusic goes online only when you run Auto-Tag.
- **Portable.** No installer and no registry changes.
- **Free and open source** under the MIT licence.

---

## Keyboard shortcuts

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
| `F11` / `Esc` | Toggle / Exit Focus Stage |
| `Delete` | Remove from playlist/queue or move file to Recycle Bin |
| `A–Z` / `0–9` | Type-to-jump in the track list |

---

## Install

ZenMusic is a self-contained portable app for Windows 10 and 11 (x64):

1. Download the latest `.zip` from [Releases](https://github.com/ec928/ZenMusic/releases).
2. Extract it to a folder of your choice (for example `C:\Apps\ZenMusic`).
3. Run `ZenMusic.exe`.

> **Where your data lives:** settings, playlists and the library cache are stored in `%LocalAppData%\ZenMusic`. To keep them inside the app folder instead (on a USB drive, say), create a `Data` folder or an empty `portable.marker` file next to `ZenMusic.exe`.
>
> **Windows SmartScreen:** the build is unsigned, so SmartScreen may show a prompt on first launch. Click **More info → Run anyway**.

---

## Building from source

Requires Windows 10/11 (x64), the .NET 8 SDK and the Windows App SDK build tools:

```powershell
git clone https://github.com/ec928/ZenMusic.git
cd ZenMusic
dotnet build ZenMusic.csproj -c Release -p:Platform=x64
```

Run the tests:

```powershell
dotnet test Tests\ZenMusic.Tests\ZenMusic.Tests.csproj -p:Platform=x64
```

---

## License

MIT License. See [LICENSE](LICENSE).
