#!/usr/bin/env bash
# Builds the plugin and packs it for a Jellyfin plugin repository: dist/subtitleforge_<version>.zip with the files at
# the top level (Jellyfin extracts it into plugins/SubtitleForge_<version>/ and writes meta.json itself).
# Usage: tools/package.sh 0.3.0.0   (needs the .NET 10 SDK and python3)
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${1:?version, e.g. 0.3.0.0}"
rm -rf dist/pkg dist/publish && mkdir -p dist/pkg
dotnet publish Jellyfin.Plugin.SubtitleForge/Jellyfin.Plugin.SubtitleForge.csproj -c Release -o dist/publish \
  -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION" >/dev/null
cp dist/publish/*.dll dist/pkg/
cp -r dist/publish/runtimes dist/publish/whisperhost dist/pkg/
python3 - "$VERSION" <<'PY'
import os, sys, zipfile
version = sys.argv[1]
root = "dist/pkg"
out = f"dist/subtitleforge_{version}.zip"
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for dirpath, _, files in os.walk(root):
        for f in sorted(files):
            path = os.path.join(dirpath, f)
            arc = os.path.relpath(path, root)
            info = zipfile.ZipInfo.from_file(path, arc)
            if arc.endswith("sf-whisper"):
                info.external_attr = (0o100755 << 16)  # keep the helper executable
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as fh:
                z.writestr(info, fh.read())
print(out, os.path.getsize(out) // (1024 * 1024), "MB")
PY
