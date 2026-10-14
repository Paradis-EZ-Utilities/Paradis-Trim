# Paradis Trim release notes

## 1.0.2 (October 2026)

A behind-the-scenes update: trimmed videos are no longer bigger than they should be. Nothing
looks or works differently.

- **Sensible file sizes**: keep half a video, get about half the file. 1.0.1 saved every video
  cut at a fixed very high quality setting, which could make a copy 2-5 times bigger per second
  than the original (two halves of a 140 MB recording came to about 370 MB). Now a re-encoded
  video never uses much more data per second than the original did for that part.
- **Trims from the beginning are copied unchanged**: if the part you keep starts at the very
  start of the video, the original picture and sound are copied as they are. No quality change,
  size exactly in proportion, and much faster. Cutting anywhere else stays exact to the moment
  you choose.
- Very low-bitrate sound is no longer raised as much when it has to be re-encoded (minimum now
  64 kbps stereo / 48 kbps mono instead of 96 / 64), so small videos don't grow from their sound.
- The Settings, SAVE TO, and everything else are unchanged.
- The diagnostic log (About → Diagnostic log) now records how each copy was made and how big it
  came out, for troubleshooting.

## 1.0.1 (October 2026)

A polish update. Opening, choosing and saving work exactly as before.

- **SAVE TO**: choose where trimmed copies are saved. The default is still the same folder as
  the original. A chosen folder is remembered between launches, and **Use source folder** goes
  back to the default. If the folder can't be found or used, Paradis Trim explains and lets you
  pick another one.
- **Settings**: a small window with two options: the ending added to file names (`_trimmed` by
  default, e.g. `_cut` or `_clip`) and **Open output folder after saving** (off by default).
- **Smaller files, same sound**: when sound has to be re-encoded, Paradis Trim now uses about the
  original's own bitrate instead of always 192 kbps or more. A video with 96 kbps sound keeps
  96 kbps sound, so the copy isn't bigger for no reason. Picture quality and sync are unchanged.
- **Paradis EZ look**: a dark charcoal design that matches Paradis Capture, the "PARADIS EZ TRIM"
  header, a new app icon, dark message boxes and a dark title bar.
- **About**: a **More from Paradis EZ Utilities** link (opens the collection on GitHub), plus
  Third-party notices and Diagnostic log. The View Source link stays hidden until Paradis Trim's
  own source page exists.


## 1.0.0 (October 2026)

First release.

- Drop an audio or video file on the window (or click **Choose File**).
- Drag the two blue handles to choose the part to keep. Everything between them is kept.
- Preview: play/pause (Space), click or drag on the timeline to jump, **Preview beginning**
  and **Preview ending** to hear exactly where the clip starts and stops.
- **Set start here** / **Set end here** use the current playback position.
- A waveform of the sound is drawn on the timeline.
- **Save Trimmed Copy** saves a new file next to the original (`Song.mp3` → `Song_trimmed.mp3`,
  then `Song_trimmed_2.mp3`, …). The original is never changed.
- Progress with a Cancel button; closing the window while saving asks first.
- Works with MP4, MOV, M4V, MP3, WAV and M4A.
- Cuts are exact: video is re-encoded at visually lossless quality, same resolution and frame
  rate; WAV and Apple Lossless stay lossless; MP3 and AAC are re-encoded at a high bitrate.
- Everything happens on your computer. No accounts, no uploads, no telemetry, no ads.

Keyboard: Space = play/pause, ← / → = move 1 second (hold Shift for 0.1 s),
[ = set start here, ] = set end here.
