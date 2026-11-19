#!/usr/bin/env bash
# Builds the Vulkan variant of whisper.cpp exactly like Whisper.net 1.8.1 does (same whisper.cpp commit, same library
# names), for linux-x64. Vulkan covers AMD (RADV), Intel (ANV, e.g. Arc A310 / N100 iGPU) and NVIDIA GPUs.
# Output: Jellyfin.Plugin.SubtitleForge/Whisper/native/vulkan/linux-x64/*.so
# Runs inside Debian 12 (glibc 2.36) so the libraries also load on older servers.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="$PWD/Jellyfin.Plugin.SubtitleForge/Whisper/native/vulkan/linux-x64"
mkdir -p "$OUT"
podman run --rm -v "$OUT":/out:Z docker.io/library/debian:bookworm-slim bash -euxc '
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends git ca-certificates cmake g++ make libvulkan-dev glslc >/dev/null
  git clone -q --depth 1 --branch 1.8.1 https://github.com/sandrohanea/whisper.net.git /src
  cd /src && git submodule update --init --depth 1 whisper.cpp
  test "$(git -C whisper.cpp rev-parse HEAD)" = 6e7629b14680e6c958fef96cdea602306c0f7433
  cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DGGML_VULKAN=ON -DGGML_NATIVE=OFF \
        -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON >/dev/null
  cmake --build build --config Release -j"$(nproc)" >/dev/null
  cp build/whisper.cpp/src/libwhisper.so /out/
  for l in ggml-whisper ggml-base-whisper ggml-cpu-whisper; do cp build/whisper.cpp/ggml/src/lib$l.so /out/; done
  cp build/whisper.cpp/ggml/src/ggml-vulkan/libggml-vulkan-whisper.so /out/
  strip --strip-unneeded /out/*.so
'
ls -la "$OUT"
