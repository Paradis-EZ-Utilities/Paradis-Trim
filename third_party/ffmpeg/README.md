# Bundled FFmpeg

`win-x64/` holds the FFmpeg programs that ship in the release's `ffmpeg` folder.
They were cross-compiled for 64-bit Windows with `build-ffmpeg-windows.sh` on Ubuntu 24.04.

| Component | Version | Source archive (Ubuntu archive, unmodified upstream) | License |
|---|---|---|---|
| FFmpeg | 8.1.2 | `ffmpeg_8.1.2.orig.tar.xz` | GPL v2+ (as configured) |
| x264 | 0.165.3223 (git 0480cb0) | `x264_0.165.3223+git20250910.0480cb0.orig.tar.xz` | GPL v2+ |
| x265 | 4.1 (8-bit + 10-bit) | `x265_4.1.orig.tar.xz` | GPL v2+ |
| LAME | 3.100 | `lame_3.100.orig.tar.gz` | LGPL |
| zlib | 1.3.1 (`zlib1.dll`) | `libz-mingw-w64_1.3.1+dfsg.orig.tar.xz` | zlib |

SHA-256 of the source archives:

```
464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c  ffmpeg_8.1.2.orig.tar.xz
de68ca43d0c6e09baeb86a38ed5cde86b0a84a21d6babcc569804201dc9df133  x264_0.165.3223+git20250910.0480cb0.orig.tar.xz
85abb8a850000bf693947459c42a666a3ecd6de10a2ac6b3c6647554b14d3270  x265_4.1.orig.tar.xz
ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e  lame_3.100.orig.tar.gz
eecc9342dbeeb7353a9c3c397cc13222cd94360eec7a517e99278b6e6b931c1f  libz-mingw-w64_1.3.1+dfsg.orig.tar.xz
```

Because x264 and x265 are GPL, this FFmpeg build is GPL v2 or later. Whenever you distribute
Paradis Trim with these files, also make `ParadisTrim-<version>-ffmpeg-source.zip` (the archives
above + the build script) available in the same place. Paradis Trim's own code stays MIT: it
only runs ffmpeg.exe/ffprobe.exe as separate programs.

FFmpeg configure line:

```
--arch=x86_64 --target-os=mingw32 --enable-gpl --disable-autodetect --enable-w32threads
--enable-zlib --enable-libx264 --enable-libx265 --enable-libmp3lame --enable-shared
--disable-static --disable-ffplay --disable-doc --disable-debug --disable-network
--disable-devices --disable-avdevice --disable-hwaccels
```

Network support, capture devices and hardware acceleration are switched off: Paradis Trim
never needs them, and leaving them out keeps the build small and strictly offline.
