# Paradis Trim

**Part of Paradis EZ Utilities — simple tools for simple jobs.**

Paradis Trim is a simple way to cut audio and video down to the part you actually want.
Open a file, drag two handles around the part you want to keep, and save a trimmed copy.
That's the whole program.

> **Your media stays on your computer. Paradis Trim processes files locally and does not upload them.**
>
> No account, no internet connection, no telemetry, no ads, no watermark, no limits.

## Using it

1. Download `ParadisTrim-1.0.2-win-x64.zip`.
2. Right-click it → **Extract All…**
3. Open the extracted folder and double-click **Paradis Trim.exe**.
4. Drop an audio or video file on the window (or click **Choose File**).
5. Drag the blue handles so the highlighted part is what you want to keep.
6. Click **Save Trimmed Copy**.

The copy is saved next to the original: `Holiday.mp4` becomes `Holiday_trimmed.mp4`
(then `Holiday_trimmed_2.mp4`, and so on). **The original file is never changed, moved or deleted.**

### Choosing where copies go

Under the timeline, **SAVE TO** shows where the trimmed copy will be saved. It starts as
**Same folder as source**. Click **Change** to pick another folder; Paradis Trim remembers it
the next time you open the app. Click **Use source folder** to go back to saving next to the original.
If the chosen folder disappears (for example a USB drive that's unplugged), Paradis Trim says so
and lets you pick another folder or save next to the original instead.

### Settings

**Settings** (top right) has two options:

- **File name ending**: what's added to the name, `_trimmed` by default. You could use `_cut`
  or `_clip` instead. Characters Windows doesn't allow in file names are refused, and a blank
  ending goes back to `_trimmed`.
- **Open output folder after saving**: off by default. When on, File Explorer opens at the new
  file after each save.

Settings are kept in a small file at `%LOCALAPPDATA%\Paradis Trim\settings.json`. If that file is
missing or damaged, Paradis Trim simply starts with the defaults.

Nothing needs to be installed: no .NET, no FFmpeg, nothing else. Windows 10 or 11 (64-bit).

The first time you open it, Windows may show "Windows protected your PC" because the app isn't
code-signed yet. Click **More info → Run anyway**.

**Handy keys:** Space = play/pause · ← / → = move 1 second (Shift: 0.1 s) ·
`[` = start here · `]` = end here

### Supported files

MP4, MOV, M4V (video) and MP3, WAV, M4A (audio).

The preview uses Windows' own media playback. If Windows can't preview a particular file
(for example an HEVC/H.265 video on a PC without the HEVC extension), Paradis Trim says so and
you can still trim it using the timeline.

### Quality

Cuts are exact to the moment you choose, and a trimmed copy is about as big as the part you kept
(keep half a video, get about half the file). Paradis Trim picks how to do that by itself:

- If the part you keep starts at the very beginning of a video, the original picture and sound are
  copied unchanged: no quality change at all, and it's fast. The copy may run up to two frames
  (under a tenth of a second) past the end you chose.
- Any other video cut is re-encoded, keeping the resolution and frame rate (H.264, or HEVC if the
  original was HEVC, including 10-bit/HDR). It aims for visually lossless quality but never uses
  much more data per second than the original did for that part (about 10% more, up to 25% more
  for small, already very heavily compressed videos of 640×480 or less).
- WAV and Apple Lossless audio stay lossless, sample for sample.
- MP3 and AAC audio are re-encoded at about the original's own bitrate (a 96 kbps original stays
  96 kbps), so the copy doesn't grow for no reason. Very low bitrates are lifted to a small minimum.
- Album art and tags are kept.

## Known limitations

- Preview depends on what Windows itself can play (see above). Trimming does not.
- HDR video keeps its HDR information, but how it looks depends on your player.
- Very unusual "professional" codecs (e.g. ProRes) are saved as H.264, unless the cut starts at
  the very beginning (then they're copied unchanged).
- A re-encoded cut is a new generation of the picture. It's limited to about the original's size,
  so very grainy or detailed footage can look very slightly softer when compared frame by frame.
- Chapters are not kept in the trimmed copy (their times would be wrong).
- Opening a very long MP3/M4A and trimming near its end takes a few extra seconds, because
  the sound is read from the beginning to get an exact cut.

## For developers

### Project layout

```
ParadisTrim.sln
src/ParadisTrim.Core/      everything that isn't UI (testable on any OS)
   AppInfo.cs              name, version, About links  <- edit links here (View Source is empty until
                           the Paradis Trim repository exists; an empty link is hidden)
   Media/                  MediaProbe (ffprobe), MediaInfo, WaveformGenerator, SupportedFormats
   Trimming/               TrimSelection (trim rules), OutputNaming, TrimStrategy (copy or re-encode,
                           and the size limit), TrimPlanner (ffmpeg arguments),
                           TrimService (temp file, progress, cancel, safe rename), TimeFormat
   FFmpeg/                 FFmpegRunner (the only place processes are started), FFmpegLocator
   Infrastructure/         AppSettings (settings.json), Log, AppPaths, TempFileJanitor
src/ParadisTrim/           the WPF app
   App.xaml                the Paradis EZ dark theme: colours and button styles in one place
   MainWindow.xaml(.cs)    the one window: drop area → editor → saving → done, plus SAVE TO
   Controls/TrimTimeline   the timeline with waveform, handles and playhead
   Controls/Wordmark       the "PARADIS EZ TRIM" brand mark (replace this file to change the logo)
   SettingsWindow          the small Settings window
   MessageDialog           Paradis Trim's own dark message box
   Playback/               PlaybackController (wraps MediaElement)
   Native/ChildProcessGuard ffmpeg is stopped if the app is closed or crashes
   AboutWindow.xaml(.cs)
tests/ParadisTrim.Tests/   automated tests; they generate their own test media
third_party/ffmpeg/        bundled FFmpeg binaries + how they were built
third_party/licenses/      license texts shipped with the release
tools/                     packaging and icon scripts
```

### How a trim works

1. `MediaProbe` asks ffprobe for the length and tracks.
2. `TrimSelection` holds start/end and refuses impossible selections (start ≥ end, zero length,
   outside the file).
3. `TrimService` creates `Name_trimmed.paradis-trim-unfinished.ext` in the output folder, has
   ffmpeg write into it (arguments from `TrimPlanner`), checks the result can be read back, and only
   then renames it to the final name. Failures and cancellations delete the unfinished file;
   if the app is killed, the next start deletes it (`TempFileJanitor`).

### Build and run

- Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or Visual Studio 2026 with
  ".NET desktop development").
- Open `ParadisTrim.sln`, set **ParadisTrim** as the startup project, press F5.
  The bundled FFmpeg is copied next to the app automatically.

Release build (one self-contained `Paradis Trim.exe` + `ffmpeg` folder):

```
dotnet publish src/ParadisTrim -c Release -o publish
```

`tools/package-release.sh` does that and assembles the release ZIP, source ZIP and FFmpeg source ZIP.

### Tests

```
dotnet test
```

The tests need `ffmpeg`/`ffprobe` with libx264, libx265, libmp3lame and lavfi on the PATH
(on Windows: `set PATH=%CD%\third_party\ffmpeg\win-x64;%PATH%`). They create short test files
whose picture brightness and sound volume change every second, trim them, and check where the
result starts and ends, that it plays cleanly, and that the original is byte-for-byte unchanged.

## License

Paradis Trim's source code: MIT (see `LICENSE`).
The bundled FFmpeg build is GPL v2 or later because it includes x264 and x265; it is a separate
program that Paradis Trim runs. When you distribute the release, also distribute the matching
`ParadisTrim-<version>-ffmpeg-source.zip`. Details in `THIRD-PARTY-NOTICES.txt` and
`third_party/ffmpeg/README.md`.
