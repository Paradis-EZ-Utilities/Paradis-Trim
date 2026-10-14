#!/bin/bash
# Rebuilds the FFmpeg programs bundled with Paradis Trim (ffmpeg.exe, ffprobe.exe and DLLs)
# by cross-compiling for 64-bit Windows on Ubuntu 24.04.
#
# Usage:  ./build-ffmpeg-windows.sh <folder-with-source-tarballs> <output-folder>
#
# The source tarballs are the exact, unmodified upstream sources published in the
# "Paradis Trim FFmpeg source" ZIP (see README.md next to this script):
#   ffmpeg_8.1.2.orig.tar.xz, x264_0.165.3223+git20250910.0480cb0.orig.tar.xz,
#   x265_4.1.orig.tar.xz, lame_3.100.orig.tar.gz
# zlib1.dll comes from Ubuntu's libz-mingw-w64 1.3.1 package (source also in that ZIP).
#
# Needs: sudo apt-get install gcc-mingw-w64-x86-64 g++-mingw-w64-x86-64 mingw-w64-tools \
#                             libz-mingw-w64-dev cmake nasm pkg-config make
set -euxo pipefail
SRC=$(realpath "$1")
OUT=$(realpath -m "$2")
ROOT=$(mktemp -d)
P=$ROOT/prefix
H=x86_64-w64-mingw32
export CC=$H-gcc-win32 CXX=$H-g++-win32 AR=$H-ar RANLIB=$H-ranlib
export PKG_CONFIG_PATH=$P/lib/pkgconfig PKG_CONFIG_LIBDIR=$P/lib/pkgconfig
mkdir -p $ROOT/work $P && cd $ROOT/work
J=$(nproc)

# ---- LAME 3.100 (LGPL) : MP3 encoder ----
tar xf $SRC/lame_3.100.orig.tar.gz
cd lame-3.100
./configure --host=$H --prefix=$P --enable-static --disable-shared --disable-frontend --disable-decoder --disable-gtktest
make -j$J && make install
cd ..

# ---- x264 (GPL) : H.264 encoder ----
mkdir x264 && tar xf $SRC/x264_*.orig.tar.xz -C x264 --strip-components=1
cd x264
./configure --host=$H --cross-prefix=$H- --prefix=$P --enable-static --disable-cli --enable-pic --enable-win32thread \
  --bit-depth=all --disable-lavf --disable-swscale --disable-ffms --disable-gpac --disable-lsmash
make -j$J && make install
cd ..

# ---- x265 4.1 (GPL) : HEVC encoder, 8-bit + 10-bit in one library ----
mkdir x265 && tar xf $SRC/x265_4.1.orig.tar.xz -C x265 --strip-components=1
TC="-DCMAKE_SYSTEM_NAME=Windows -DCMAKE_C_COMPILER=$CC -DCMAKE_CXX_COMPILER=$CXX -DCMAKE_RC_COMPILER=$H-windres -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DCMAKE_BUILD_TYPE=Release"
mkdir -p x265/b10 x265/b8
cd x265/b10
cmake ../source $TC -DHIGH_BIT_DEPTH=ON -DEXPORT_C_API=OFF -DENABLE_SHARED=OFF -DENABLE_CLI=OFF
make -j$J
cp libx265.a ../b8/libx265_main10.a
cd ../b8
cmake ../source $TC -DEXTRA_LIB="x265_main10.a" -DEXTRA_LINK_FLAGS=-L. -DLINKED_10BIT=ON -DENABLE_SHARED=OFF -DENABLE_CLI=OFF -DCMAKE_INSTALL_PREFIX=$P
make -j$J
mv libx265.a libx265_main.a
$AR -M <<MRI
CREATE libx265.a
ADDLIB libx265_main.a
ADDLIB libx265_main10.a
SAVE
END
MRI
make install
sed -i 's/^Libs.private:.*/Libs.private: -lstdc++/' $P/lib/pkgconfig/x265.pc
cd ../..

# ---- FFmpeg 8.1.2 (GPL v2+ because of x264/x265) ----
mkdir ffmpeg && tar xf $SRC/ffmpeg_8.1.2.orig.tar.xz -C ffmpeg --strip-components=1
cd ffmpeg
./configure --prefix=$P --arch=x86_64 --target-os=mingw32 --cross-prefix=$H- \
  --cc=$CC --cxx=$CXX --pkg-config=pkg-config --pkg-config-flags=--static \
  --enable-gpl --disable-autodetect --enable-w32threads --enable-zlib \
  --enable-libx264 --enable-libx265 --enable-libmp3lame \
  --enable-shared --disable-static --disable-ffplay --disable-doc --disable-debug \
  --disable-network --disable-devices --disable-avdevice --disable-hwaccels \
  --extra-cflags="-I$P/include" --extra-ldflags="-L$P/lib -static-libgcc -static-libstdc++" \
  --extra-libs="-lstdc++" --extra-version=ParadisTrim
make -j$J && make install

mkdir -p "$OUT"
cp $P/bin/*.exe $P/bin/*.dll "$OUT/"
cp /usr/x86_64-w64-mingw32/lib/zlib1.dll "$OUT/"
echo "Done: $OUT"
