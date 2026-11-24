using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Translation;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Japanese → German: pivot via English (FuguMT ja-en + Opus-MT en-de) vs. direct Opus-MT ja-de, scored with chrF
/// against an official German subtitle. Runs only when SF_JADE_DIR contains ja.out (helper output), ref.srt and
/// models/{ja-en,en-de,ja-de}.
/// </summary>
public class JaDeEvalTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("SF_JADE_DIR");
    private readonly ITestOutputHelper _out;

    public JaDeEvalTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Compare()
    {
        if (Dir is null || !File.Exists(Path.Combine(Dir, "ja.out")))
        {
            return;
        }

        var segments = File.ReadAllLines(Path.Combine(Dir, "ja.out"))
            .Where(l => l.StartsWith("S ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', 4))
            .Select(p => (Start: long.Parse(p[1]), End: long.Parse(p[2]), Text: Uri.UnescapeDataString(p[3])))
            .ToList();
        var reference = Srt.Parse(File.ReadAllText(Path.Combine(Dir, "ref.srt")));
        var pairs = new List<(string Ja, string Ref)>();
        foreach (var s in segments)
        {
            var refs = reference.Where(r => Math.Min(r.EndMs, s.End) - Math.Max(r.StartMs, s.Start) > 0.5 * Math.Min(r.EndMs - r.StartMs, s.End - s.Start)).ToList();
            if (refs.Count > 0)
            {
                pairs.Add((s.Text, string.Join(' ', refs.Select(r => r.Text.Replace('\n', ' ')))));
            }
        }

        var m = Path.Combine(Dir, "models");
        using var jaEn = new MarianTranslator(Path.Combine(m, "ja-en"), 4);
        using var enDe = new MarianTranslator(Path.Combine(m, "en-de"), 4);
        using var jaDe = new MarianTranslator(Path.Combine(m, "ja-de"), 4);
        var pivot = new List<string>();
        var direct = new List<string>();
        var swPivot = new Stopwatch();
        var swDirect = new Stopwatch();
        foreach (var (ja, _) in pairs)
        {
            swPivot.Start();
            pivot.Add(enDe.Translate(jaEn.Translate(ja)));
            swPivot.Stop();
            swDirect.Start();
            direct.Add(jaDe.Translate(ja));
            swDirect.Stop();
        }

        var refs2 = pairs.Select(p => p.Ref).ToList();
        _out.WriteLine($"{pairs.Count} segments with reference (of {segments.Count})");
        _out.WriteLine($"chrF pivot  ja→en→de: {ChrF(pivot, refs2):F1}  ({swPivot.Elapsed.TotalSeconds:F1} s)");
        _out.WriteLine($"chrF direct ja→de:    {ChrF(direct, refs2):F1}  ({swDirect.Elapsed.TotalSeconds:F1} s)");
        for (var i = 0; i < pairs.Count; i += Math.Max(1, pairs.Count / 15))
        {
            _out.WriteLine($"\nJA:  {pairs[i].Ja}\nREF: {pairs[i].Ref}\nPIV: {pivot[i]}\nDIR: {direct[i]}");
        }
    }

    /// <summary>Corpus chrF (character n-grams 1..6, β = 2, whitespace ignored), 0..100.</summary>
    private static double ChrF(IReadOnlyList<string> hyp, IReadOnlyList<string> refs)
    {
        const int N = 6;
        var match = new double[N + 1];
        var hypTotal = new double[N + 1];
        var refTotal = new double[N + 1];
        for (var i = 0; i < hyp.Count; i++)
        {
            var h = new string(hyp[i].Where(c => !char.IsWhiteSpace(c)).ToArray());
            var r = new string(refs[i].Where(c => !char.IsWhiteSpace(c)).ToArray());
            for (var n = 1; n <= N; n++)
            {
                var hc = Grams(h, n);
                var rc = Grams(r, n);
                hypTotal[n] += hc.Values.Sum();
                refTotal[n] += rc.Values.Sum();
                match[n] += hc.Sum(kv => Math.Min(kv.Value, rc.TryGetValue(kv.Key, out var v) ? v : 0));
            }
        }

        double p = 0, rr = 0;
        for (var n = 1; n <= N; n++)
        {
            p += hypTotal[n] > 0 ? match[n] / hypTotal[n] : 0;
            rr += refTotal[n] > 0 ? match[n] / refTotal[n] : 0;
        }

        p /= N;
        rr /= N;
        const double B2 = 4;
        return p + rr == 0 ? 0 : 100 * (1 + B2) * p * rr / ((B2 * p) + rr);
    }

    private static Dictionary<string, int> Grams(string s, int n)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + n <= s.Length; i++)
        {
            var g = s.Substring(i, n);
            d[g] = d.TryGetValue(g, out var c) ? c + 1 : 1;
        }

        return d;
    }
}
