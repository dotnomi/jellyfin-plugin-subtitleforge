using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.SubtitleForge.Common;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Result of converting one image subtitle stream.
/// </summary>
/// <param name="Cues">Recognized cues.</param>
/// <param name="MeanConfidence">Mean line confidence 0..1.</param>
/// <param name="LowConfidenceLines">Number of lines below 0.8 confidence.</param>
/// <param name="AllForced">Whether every subtitle was flagged forced.</param>
public sealed record OcrResult(List<SubtitleCue> Cues, double MeanConfidence, int LowConfidenceLines, bool AllForced);

/// <summary>
/// Converts a PGS stream into text cues: decode → split lines → recognize → post-process → merge.
/// </summary>
public static class ImageSubtitleOcr
{
    /// <summary>
    /// Runs OCR on a .sup stream.
    /// </summary>
    /// <param name="sup">PGS data.</param>
    /// <param name="recognizer">Recognizer.</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Result.</returns>
    public static OcrResult Convert(Stream sup, PaddleRecognizer recognizer, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var subs = PgsParser.Parse(sup);
        var cues = new List<SubtitleCue>();
        double confSum = 0;
        int lineCount = 0, low = 0;
        var allForced = subs.Count > 0;

        for (var i = 0; i < subs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sub = subs[i];
            allForced &= sub.Forced;
            var lines = new List<string>();
            foreach (var (_, _, image) in sub.Images)
            {
                foreach (var lineImage in LineSegmenter.Split(image))
                {
                    var (text, conf) = recognizer.Recognize(lineImage);
                    text = OcrPostProcessor.Fix(text);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    lines.Add(text);
                    confSum += conf;
                    lineCount++;
                    if (conf < 0.8f)
                    {
                        low++;
                    }
                }
            }

            if (lines.Count > 0)
            {
                var text = string.Join('\n', lines);
                var prev = cues.Count > 0 ? cues[^1] : null;

                // PGS often re-sends the same text (e.g. fades); merge contiguous duplicates.
                if (prev is not null && prev.Text == text && sub.StartMs - prev.EndMs <= 100)
                {
                    cues[^1] = prev with { EndMs = sub.EndMs };
                }
                else
                {
                    cues.Add(new SubtitleCue(sub.StartMs, sub.EndMs, text));
                }
            }

            progress?.Report((i + 1.0) / subs.Count);
        }

        return new OcrResult(cues, lineCount == 0 ? 0 : confSum / lineCount, low, allForced);
    }
}
