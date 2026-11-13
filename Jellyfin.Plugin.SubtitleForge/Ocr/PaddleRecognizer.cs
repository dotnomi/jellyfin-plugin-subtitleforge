using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Text line recognizer using the PP-OCRv5 Latin model (embedded, CPU via ONNX Runtime, CTC decoding).
/// </summary>
public sealed class PaddleRecognizer : IDisposable
{
    private const int DefaultHeight = 48;
    private const int MaxWidth = 2400;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _height;
    private readonly string[] _charset;

    /// <summary>
    /// Initializes a new instance of the <see cref="PaddleRecognizer"/> class.
    /// </summary>
    /// <param name="threads">Intra-op threads (keep low on small NAS CPUs).</param>
    public PaddleRecognizer(int threads = 2)
    {
        OnnxNativeLoader.EnsureRegistered();
        var asm = Assembly.GetExecutingAssembly();
        byte[] model;
        using (var s = asm.GetManifestResourceStream("Jellyfin.Plugin.SubtitleForge.Ocr.Models.rec.onnx")
            ?? throw new InvalidOperationException("OCR model resource missing"))
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            model = ms.ToArray();
        }

        using (var s = asm.GetManifestResourceStream("Jellyfin.Plugin.SubtitleForge.Ocr.Models.dict.txt")
            ?? throw new InvalidOperationException("OCR dictionary resource missing"))
        using (var reader = new StreamReader(s, Encoding.UTF8))
        {
            var chars = new List<string> { string.Empty }; // CTC blank at index 0
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                chars.Add(line.TrimEnd('\r'));
            }

            chars.Add(" "); // use_space_char
            _charset = chars.ToArray();
        }

        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, threads),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(model, options);
        var input = _session.InputMetadata.First();
        _inputName = input.Key;
        var dims = input.Value.Dimensions;
        _height = dims.Length == 4 && dims[2] > 0 ? dims[2] : DefaultHeight;
    }

    /// <summary>
    /// Recognizes one text line.
    /// </summary>
    /// <param name="line">Ink-on-white line image.</param>
    /// <returns>Text and mean confidence (0..1).</returns>
    public (string Text, float Confidence) Recognize(GrayImage line)
    {
        var width = (int)Math.Ceiling((double)line.Width * _height / line.Height);
        width = Math.Clamp(width, 16, MaxWidth);
        var tensor = new DenseTensor<float>(new[] { 1, 3, _height, width });
        var scaleX = (double)line.Width / width;
        var scaleY = (double)line.Height / _height;

        for (var y = 0; y < _height; y++)
        {
            var sy = Math.Min(line.Height - 1.0, (y + 0.5) * scaleY - 0.5);
            sy = Math.Max(0, sy);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, line.Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Max(0, Math.Min(line.Width - 1.0, (x + 0.5) * scaleX - 0.5));
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, line.Width - 1);
                var fx = sx - x0;
                var v = ((line[x0, y0] * (1 - fx)) + (line[x1, y0] * fx)) * (1 - fy)
                      + ((line[x0, y1] * (1 - fx)) + (line[x1, y1] * fx)) * fy;
                var n = (float)((v / 255.0 - 0.5) / 0.5);
                tensor[0, 0, y, x] = n;
                tensor[0, 1, y, x] = n;
                tensor[0, 2, y, x] = n;
            }
        }

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = results.First().AsTensor<float>();
        var steps = output.Dimensions[1];
        var classes = output.Dimensions[2];

        var sb = new StringBuilder();
        var last = -1;
        double confSum = 0;
        var confCount = 0;
        for (var t = 0; t < steps; t++)
        {
            var best = 0;
            var bestP = float.MinValue;
            for (var c = 0; c < classes; c++)
            {
                var p = output[0, t, c];
                if (p > bestP)
                {
                    bestP = p;
                    best = c;
                }
            }

            if (best != 0 && best != last && best < _charset.Length)
            {
                sb.Append(_charset[best]);
                confSum += bestP;
                confCount++;
            }

            last = best;
        }

        return (sb.ToString().Trim(), confCount == 0 ? 0 : (float)(confSum / confCount));
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
