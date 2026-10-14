#!/bin/bash
# Builds and packages a Paradis Trim release.
#
# Usage: tools/package-release.sh <output-folder> [folder-with-ffmpeg-source-tarballs]
#
# Produces in <output-folder>:
#   Paradis Trim/                          the ready-to-run release folder
#   ParadisTrim-<ver>-win-x64.zip          that folder, zipped (what users download)
#   ParadisTrim-<ver>-source.zip           this source code
#   ParadisTrim-<ver>-ffmpeg-source.zip    source for the bundled FFmpeg (only if tarball folder given)
set -euo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
OUT=$(realpath -m "$1")
FFSRC=${2:-}
VERSION=$(sed -n 's/.*Version = "\(.*\)";/\1/p' "$REPO/src/ParadisTrim.Core/AppInfo.cs")
APP="$OUT/Paradis Trim"

echo "Packaging Paradis Trim $VERSION into $OUT"
rm -rf "$APP" "$OUT/publish-tmp"
mkdir -p "$OUT"

dotnet publish "$REPO/src/ParadisTrim" -c Release -o "$OUT/publish-tmp" -p:Version="$VERSION"

mkdir -p "$APP"
mv "$OUT/publish-tmp/ParadisTrim.exe" "$APP/Paradis Trim.exe"
mv "$OUT/publish-tmp/ffmpeg" "$APP/ffmpeg"
rm -rf "$OUT/publish-tmp"

cp "$REPO/tools/release-docs/README.txt" "$APP/README.txt"
cp "$REPO/LICENSE" "$APP/LICENSE.txt"
cp "$REPO/THIRD-PARTY-NOTICES.txt" "$APP/THIRD-PARTY-NOTICES.txt"
cp "$REPO/RELEASE-NOTES.md" "$APP/RELEASE-NOTES.txt"
cp -r "$REPO/third_party/licenses" "$APP/licenses"

python3 -I - "$OUT" "$VERSION" "$REPO" "$FFSRC" <<'PY'
import os, sys, zipfile
out, version, repo, ffsrc = sys.argv[1:5]

def add_tree(z, folder, prefix, skip=lambda rel: False):
    for root, dirs, files in os.walk(folder):
        dirs[:] = sorted(d for d in dirs if not skip(os.path.relpath(os.path.join(root, d), folder)))
        for f in sorted(files):
            full = os.path.join(root, f)
            rel = os.path.relpath(full, folder)
            if not skip(rel):
                z.write(full, os.path.join(prefix, rel))

def zipped(name):
    return zipfile.ZipFile(os.path.join(out, name), "w", zipfile.ZIP_DEFLATED, compresslevel=9)

with zipped(f"ParadisTrim-{version}-win-x64.zip") as z:
    add_tree(z, os.path.join(out, "Paradis Trim"), "Paradis Trim")

junk = {"bin", "obj", ".vs", ".git", ".idea", "TestResults"}
with zipped(f"ParadisTrim-{version}-source.zip") as z:
    add_tree(z, repo, f"ParadisTrim-{version}-source",
             skip=lambda rel: any(part in junk for part in rel.split(os.sep)) or rel.endswith(".user"))

if ffsrc:
    with zipped(f"ParadisTrim-{version}-ffmpeg-source.zip") as z:
        prefix = f"ParadisTrim-{version}-ffmpeg-source"
        for f in sorted(os.listdir(ffsrc)):
            z.write(os.path.join(ffsrc, f), os.path.join(prefix, "sources", f))
        z.write(os.path.join(repo, "third_party/ffmpeg/build-ffmpeg-windows.sh"), os.path.join(prefix, "build-ffmpeg-windows.sh"))
        z.write(os.path.join(repo, "third_party/ffmpeg/README.md"), os.path.join(prefix, "README.md"))
        add_tree(z, os.path.join(repo, "third_party/licenses"), os.path.join(prefix, "licenses"))
PY

ls -la "$OUT"
