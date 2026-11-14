using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleForge.Ocr;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.SubtitleForge.Translation;

/// <summary>
/// Offline machine translation with an Opus-MT (Marian) model exported to ONNX (encoder + decoder), CPU only.
/// </summary>
public sealed class MarianTranslator : IDisposable
{
    private const int MaxInputTokens = 256;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePiece _sourceSpm;
    private readonly Dictionary<string, long> _vocab;
    private readonly string[] _inverse;
    private readonly long _eos;
    private readonly long _pad;
    private readonly long _unk;
    private readonly long _decoderStart;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarianTranslator"/> class.
    /// </summary>
    /// <param name="directory">Folder with encoder_model_quantized.onnx, decoder_model_quantized.onnx, source.spm, vocab.json, config.json.</param>
    /// <param name="threads">CPU threads.</param>
    public MarianTranslator(string directory, int threads = 2)
    {
        OnnxNativeLoader.EnsureRegistered();
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, threads),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _encoder = new InferenceSession(Path.Combine(directory, "encoder_model_quantized.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(directory, "decoder_model_quantized.onnx"), options);
        _sourceSpm = SentencePiece.Load(Path.Combine(directory, "source.spm"));
        _vocab = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Path.Combine(directory, "vocab.json")))
            ?? throw new InvalidDataException("vocab.json");
        _inverse = new string[_vocab.Values.Max() + 1];
        foreach (var (piece, id) in _vocab)
        {
            _inverse[id] = piece;
        }

        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "config.json")));
        _eos = config.RootElement.GetProperty("eos_token_id").GetInt64();
        _pad = config.RootElement.GetProperty("pad_token_id").GetInt64();
        _decoderStart = config.RootElement.TryGetProperty("decoder_start_token_id", out var ds) ? ds.GetInt64() : _pad;
        _unk = _vocab.TryGetValue("<unk>", out var u) ? u : 1;
    }

    /// <summary>
    /// Translates one sentence or short paragraph.
    /// </summary>
    /// <param name="text">Source text.</param>
    /// <returns>Translation.</returns>
    public string Translate(string text)
    {
        var pieces = _sourceSpm.Encode(text);
        if (pieces.Count == 0)
        {
            return string.Empty;
        }

        var ids = pieces.Take(MaxInputTokens - 1).Select(p => _vocab.TryGetValue(p, out var id) ? id : _unk).Append(_eos).ToArray();
        var mask = Enumerable.Repeat(1L, ids.Length).ToArray();
        var inputIds = new DenseTensor<long>(ids, new[] { 1, ids.Length });
        var attention = new DenseTensor<long>(mask, new[] { 1, ids.Length });

        using var encoded = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
        });
        var hidden = encoded.First().AsTensor<float>().ToDenseTensor();

        var output = new List<long> { _decoderStart };
        var maxLength = Math.Min(MaxInputTokens, (ids.Length * 3) + 10);
        while (output.Count < maxLength)
        {
            var decoderIds = new DenseTensor<long>(output.ToArray(), new[] { 1, output.Count });
            using var result = _decoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", decoderIds),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attention),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
            });
            var logits = result.First(r => r.Name == "logits").AsTensor<float>();
            var vocabSize = logits.Dimensions[2];
            var last = output.Count - 1;

            var best = -1L;
            var bestScore = float.NegativeInfinity;
            for (var v = 0; v < vocabSize; v++)
            {
                if (v == _pad)
                {
                    continue; // bad_words_ids
                }

                var score = logits[0, last, v];
                if (score > bestScore && !WouldRepeatTrigram(output, v))
                {
                    bestScore = score;
                    best = v;
                }
            }

            if (best < 0 || best == _eos)
            {
                break;
            }

            output.Add(best);
        }

        var outPieces = output.Skip(1)
            .Where(id => id != _eos && id != _pad && id != _unk && id < _inverse.Length)
            .Select(id => _inverse[id]);
        return SentencePiece.Decode(outPieces);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }

    // no_repeat_ngram_size = 3: prevents "the word that is a word that is a word ..." loops.
    private static bool WouldRepeatTrigram(List<long> output, long next)
    {
        var n = output.Count;
        if (n < 3)
        {
            return false;
        }

        var a = output[n - 2];
        var b = output[n - 1];
        for (var i = 0; i + 2 < n; i++)
        {
            if (output[i] == a && output[i + 1] == b && output[i + 2] == next)
            {
                return true;
            }
        }

        return false;
    }
}
