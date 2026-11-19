# Third-party components

| Component | Where | License | Source |
|---|---|---|---|
| PP-OCRv5 Latin recognition model (ONNX conversion) + dictionary | `Jellyfin.Plugin.SubtitleForge/Ocr/Models/` | Apache-2.0 | PaddlePaddle/PaddleOCR, ONNX conversion from huggingface.co/monkt/paddleocr-onnx (commit 7b02d0a) |
| Silero VAD v6.2.3 (`silero_vad_16k_op15.onnx`) | `Jellyfin.Plugin.SubtitleForge/Sync/Models/` | MIT | github.com/snakers4/silero-vad |
| Whisper.net 1.8.1 + whisper.cpp runtime | NuGet (`runtimes/linux-*/lib*whisper.so`) | MIT | github.com/sandrohanea/whisper.net, github.com/ggml-org/whisper.cpp |
| whisper.cpp with Vulkan backend (own build of commit 6e7629b, see tools/build-whisper-vulkan.sh) | `Jellyfin.Plugin.SubtitleForge/Whisper/native/vulkan/linux-x64/` | MIT | github.com/ggml-org/whisper.cpp |
| Whisper ggml models (downloaded at runtime, not shipped) | plugin data folder | MIT | huggingface.co/ggerganov/whisper.cpp (commit 5359861) |
| Opus-MT de-en / en-de (ONNX export by Xenova; downloaded at runtime, not shipped) | plugin data folder | Apache-2.0 | Helsinki-NLP/opus-mt-de-en, -en-de |
| FuguMT ja-en / en-ja (ONNX export by Kadonox; downloaded at runtime, not shipped) | plugin data folder | CC-BY-SA-4.0 | staka/fugumt-ja-en, -en-ja |
| libgomp.so.1 (GCC OpenMP runtime, Debian 12.2.0-14+deb12u1) | `Jellyfin.Plugin.SubtitleForge/Whisper/native/` | GPL-3.0 with GCC Runtime Library Exception | Debian package libgomp1 |
| Microsoft.ML.OnnxRuntime | NuGet | MIT | github.com/microsoft/onnxruntime |
| Noto Sans SemiBold (tests only) | `Jellyfin.Plugin.SubtitleForge.Tests/Fixtures/` | SIL OFL 1.1 | notofonts |
| DejaVu Sans (tests only) | `Jellyfin.Plugin.SubtitleForge.Tests/Fixtures/` | Bitstream Vera / DejaVu license | dejavu-fonts |
| Liberation Sans (tests only) | `Jellyfin.Plugin.SubtitleForge.Tests/Fixtures/` | SIL OFL 1.1 | liberationfonts |

Algorithmic ideas (no code copied): ffsubsync (FFT cross-correlation of speech vs. subtitle activity), alass (piecewise
offsets), Bazarr/subliminal (provider API usage), Subtitle Edit (OCR post-processing rules).
