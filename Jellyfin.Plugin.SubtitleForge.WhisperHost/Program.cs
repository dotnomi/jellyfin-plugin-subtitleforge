using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.SubtitleForge.Whisper;

// Protocol (stdout, one line each): "R <runtime>", "P <progress 0..1>", "S <startMs> <endMs> <url-escaped text>".
// Exit code 0 = success; anything else (including a crash) = failure, details on stderr.
var opts = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    opts[args[i]] = args[i + 1];
}

string Arg(string name) => opts.TryGetValue(name, out var v) ? v : throw new ArgumentException("missing " + name);

try
{
    var gpu = Arg("--gpu") == "1";
    if (gpu && Environment.GetEnvironmentVariable("SF_WHISPER_FAKE_GPU_CRASH") == "1")
    {
        Console.Error.WriteLine("simulated GPU crash (SF_WHISPER_FAKE_GPU_CRASH)");
        Environment.FailFast("simulated GPU crash");
    }

    WhisperCore.ConfigureNative(Arg("--lib"), gpu);
    var windows = new List<(long Start, long End)>();
    foreach (var line in File.ReadAllLines(Arg("--windows")))
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 2)
        {
            windows.Add((long.Parse(p[0], CultureInfo.InvariantCulture), long.Parse(p[1], CultureInfo.InvariantCulture)));
        }
    }

    var reported = false;
    void ReportRuntime()
    {
        if (!reported && WhisperCore.LoadedRuntime is { } r)
        {
            reported = true;
            Console.Out.WriteLine("R " + r);
        }
    }

    var progress = new SyncProgress(v =>
    {
        ReportRuntime();
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"P {v:F4}"));
    });

    await WhisperCore.TranscribeAsync(
        Arg("--model"),
        Arg("--pcm"),
        windows,
        Arg("--lang"),
        Arg("--translate") == "1",
        int.Parse(Arg("--threads"), CultureInfo.InvariantCulture),
        s =>
        {
            ReportRuntime();
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"S {s.StartMs} {s.EndMs} {Uri.EscapeDataString(s.Text)}"));
        },
        progress,
        CancellationToken.None).ConfigureAwait(false);
    ReportRuntime();
    Console.Out.Flush();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 2;
}

/// <summary>Progress reporting on the calling thread (Progress&lt;T&gt; would post to the thread pool, out of order).</summary>
internal sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
