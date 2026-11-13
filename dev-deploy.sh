#!/usr/bin/env bash
# Build the plugin in a .NET 9 SDK container and deploy it into the local dev Jellyfin (container jf-dev).
set -euo pipefail
cd "$(dirname "$0")"
podman run --rm -v "$PWD":/src:Z -v ~/.nuget-podman:/root/.nuget:Z -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet publish Jellyfin.Plugin.SubtitleForge/Jellyfin.Plugin.SubtitleForge.csproj -c Release -o /src/out 2>&1 \
  | grep -E " error |warning CS|-> /src/out" || true
test -f out/Jellyfin.Plugin.SubtitleForge.dll
DEST=../jellyfin-dev/config/plugins/SubtitleForge
rm -rf "$DEST" && mkdir -p "$DEST" && cp -r out/*.dll out/runtimes "$DEST"/
podman restart jf-dev >/dev/null
for _ in $(seq 1 60); do curl -sf localhost:8096/health >/dev/null && break; sleep 2; done
echo "deployed: $(curl -s localhost:8096/health)"
