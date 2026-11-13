using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.SubtitleForge.Ocr;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.SubtitleForge.Sync;

/// <summary>
/// Streaming voice activity detection with Silero VAD (16 kHz, 512-sample windows, embedded model, CPU).
/// Very quiet windows are skipped without running the model.
/// </summary>
public sealed class SileroVad : IDisposable
{
    /// <summary>Samples per window.</summary>
    public const int WindowSize = 512;

    private const int ContextSize = 64;
    private const float SilenceRms = 0.0015f;

    private readonly InferenceSession _session;
    private readonly bool _hasSampleRateInput;
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _context = new float[ContextSize];
    private readonly float[] _input = new float[ContextSize + WindowSize];

    /// <summary>
    /// Initializes a new instance of the <see cref="SileroVad"/> class.
    /// </summary>
    public SileroVad()
    {
        OnnxNativeLoader.EnsureRegistered();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.SubtitleForge.Sync.Models.silero_vad.onnx")
            ?? throw new InvalidOperationException("VAD model resource missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        _session = new InferenceSession(ms.ToArray(), options);
        _hasSampleRateInput = _session.InputMetadata.ContainsKey("sr");
    }

    /// <summary>Gets the number of windows that actually ran through the model.</summary>
    public long ModelCalls { get; private set; }

    /// <summary>
    /// Processes the next 512-sample window (continuous stream).
    /// </summary>
    /// <param name="window">Exactly 512 samples in -1..1.</param>
    /// <returns>Speech probability 0..1.</returns>
    public float Process(ReadOnlySpan<float> window)
    {
        double sum = 0;
        foreach (var v in window)
        {
            sum += v * v;
        }

        var rms = (float)Math.Sqrt(sum / window.Length);
        if (rms < SilenceRms)
        {
            // Keep context consistent; recurrent state decays naturally on silence, reset is a fine approximation.
            Array.Clear(_state);
            window[^ContextSize..].CopyTo(_context);
            return 0f;
        }

        _context.CopyTo(_input, 0);
        window.CopyTo(_input.AsSpan(ContextSize));

        var inputs = new System.Collections.Generic.List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, new[] { 1, ContextSize + WindowSize })),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
        };
        if (_hasSampleRateInput)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>())));
        }

        using var results = _session.Run(inputs);
        var prob = results.First(r => r.Name == "output").AsTensor<float>().GetValue(0);
        var newState = results.First(r => r.Name == "stateN").AsTensor<float>();
        var i = 0;
        foreach (var v in newState)
        {
            _state[i++] = v;
        }

        window[^ContextSize..].CopyTo(_context);
        ModelCalls++;
        return prob;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
