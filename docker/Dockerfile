# Official Jellyfin image plus the Vulkan loader and Mesa drivers, so SubtitleForge's Whisper can use the GPU
# (AMD, Intel; NVIDIA needs the NVIDIA Container Toolkit instead, see compose.yaml).
# Build: docker build -t jellyfin-vulkan --build-arg JELLYFIN_VERSION=12.4 .
ARG JELLYFIN_VERSION=12.4
FROM jellyfin/jellyfin:${JELLYFIN_VERSION}

RUN apt-get update \
 && apt-get install -y --no-install-recommends libvulkan1 mesa-vulkan-drivers \
 && rm -rf /var/lib/apt/lists/*
