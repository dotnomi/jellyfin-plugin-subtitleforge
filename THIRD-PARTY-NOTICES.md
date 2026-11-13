# Third-party components

| Component | Where | License | Source |
|---|---|---|---|
| PP-OCRv5 Latin recognition model (ONNX conversion) + dictionary | `Jellyfin.Plugin.SubtitleForge/Ocr/Models/` | Apache-2.0 | PaddlePaddle/PaddleOCR, ONNX conversion from huggingface.co/monkt/paddleocr-onnx (commit 7b02d0a) |
| Silero VAD v6.2.3 (`silero_vad_16k_op15.onnx`) | `Jellyfin.Plugin.SubtitleForge/Sync/Models/` | MIT | github.com/snakers4/silero-vad |
| Microsoft.ML.OnnxRuntime | NuGet | MIT | github.com/microsoft/onnxruntime |
| Noto Sans SemiBold (tests only) | `Jellyfin.Plugin.SubtitleForge.Tests/Fixtures/` | SIL OFL 1.1 | notofonts |
| DejaVu Sans (tests only) | `Jellyfin.Plugin.SubtitleForge.Tests/Fixtures/` | Bitstream Vera / DejaVu license | dejavu-fonts |
| Liberation Sans (tests only) | `Jellyfin.Plugin.SubtitleForge.Tests/Fixtures/` | SIL OFL 1.1 | liberationfonts |

Algorithmic ideas (no code copied): ffsubsync (FFT cross-correlation of speech vs. subtitle activity), alass (piecewise
offsets), Bazarr/subliminal (provider API usage), Subtitle Edit (OCR post-processing rules).
