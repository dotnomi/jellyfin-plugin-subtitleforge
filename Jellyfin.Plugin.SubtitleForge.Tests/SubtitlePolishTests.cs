using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubtitleForge.Common;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Encoding repair, line wrapping and reading time of written subtitles.
/// </summary>
public class SubtitlePolishTests
{
    [Theory]
    [InlineData("SchÃ¶ne GrÃ¼ÃŸe aus MÃ¼nchen", "Schöne Grüße aus München")]
    [InlineData("Er sagte: â€žHalloâ€œ", "Er sagte: „Hallo“")]
    [InlineData("Donâ€™t go", "Don’t go")]
    [InlineData("KÃ¤se\nÃ„rger", "Käse\nÄrger")]
    [InlineData("TschÃƒÂ¼ss", "Tschüss")]
    public void FixMojibake_RepairsDoubleEncodedUtf8(string input, string expected)
    {
        Assert.Equal(expected, SubtitlePolish.FixMojibake(input));
    }

    [Theory]
    [InlineData("Schöne Grüße aus München")]
    [InlineData("Ärger über Öl – „gut“ … ’s")]
    [InlineData("<i>Café à la carte</i>\nNaïve Æsop")]
    [InlineData("SÃO PAULO")]
    public void FixMojibake_LeavesCorrectTextUnchanged(string input)
    {
        Assert.Equal(input, SubtitlePolish.FixMojibake(input));
    }

    [Fact]
    public void FixMojibake_StripsReplacementAndControlCharacters()
    {
        Assert.Equal("Hallo Welt\nZeile", SubtitlePolish.FixMojibake("Hal\u0000lo�� Welt\u0007\nZeile\u001b"));
    }

    [Fact]
    public void WrapLines_ShortTextIsUnchanged()
    {
        Assert.Equal("Hallo.\nWie geht's?", SubtitlePolish.WrapLines("Hallo.\nWie geht's?"));
    }

    [Fact]
    public void WrapLines_BreaksBalancedIntoTwoLines()
    {
        var text = "Ich habe dir doch schon gesagt, dass ich morgen nicht kommen kann.";
        var lines = SubtitlePolish.WrapLines(text, 42).Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= 42, l));
        Assert.Equal(text, string.Join(' ', lines));
        Assert.True(System.Math.Abs(lines[0].Length - lines[1].Length) < 12);
    }

    [Fact]
    public void WrapLines_RejoinsThreeShortLinesIntoTwo()
    {
        var lines = SubtitlePolish.WrapLines("Das ist\nein Satz der\nzu oft umbrochen wurde.", 42).Split('\n');
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void WrapLines_UsesThreeLinesWhenUnavoidable()
    {
        var text = "Das ist ein ziemlich langer Untertitel, der wirklich nicht in zwei Zeilen passt, egal wie man ihn bricht.";
        var lines = SubtitlePolish.WrapLines(text, 42).Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= 42, l));
    }

    [Fact]
    public void WrapLines_KeepsItalicTagsValidOnEveryLine()
    {
        var text = "<i>Ich habe dir doch schon gesagt, dass ich morgen nicht kommen kann.</i>";
        var lines = SubtitlePolish.WrapLines(text, 42).Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l =>
        {
            Assert.StartsWith("<i>", l);
            Assert.EndsWith("</i>", l);
            Assert.True(l.Length - 7 <= 42, l);
        });
    }

    [Fact]
    public void WrapLines_PartialItalicsAreClosedAndReopened()
    {
        var text = "Er sagte nur: <i>Komm sofort nach Hause, wir warten alle</i> und ging.";
        var lines = SubtitlePolish.WrapLines(text, 42).Split('\n');
        Assert.Equal(2, lines.Length);
        foreach (var l in lines)
        {
            Assert.Equal(CountOf(l, "<i>"), CountOf(l, "</i>"));
            Assert.DoesNotContain("<i></i>", l, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WrapLines_NeverBreaksInsideATag()
    {
        var lines = SubtitlePolish.WrapLines("<i>Eins zwei drei vier fünf sechs sieben acht neun zehn elf zwölf</i>", 20).Split('\n');
        Assert.All(lines, l => Assert.Equal(CountOf(l, "<"), CountOf(l, ">")));
    }

    [Fact]
    public void WrapLines_KeepsDialogueLinesSeparate()
    {
        var text = "- Wo warst du?\n- Ich war die ganze Nacht im Büro und habe an dem Bericht gearbeitet.\n- Ach so.";
        var lines = SubtitlePolish.WrapLines(text, 42).Split('\n');
        Assert.Equal("- Wo warst du?", lines[0]);
        Assert.StartsWith("- Ich war", lines[1], System.StringComparison.Ordinal);
        Assert.False(lines[2].StartsWith('-'));
        Assert.Equal("- Ach so.", lines[^1]);
        Assert.All(lines, l => Assert.True(l.Length <= 42, l));
    }

    [Fact]
    public void WrapLines_DialogueThatFitsIsUnchanged()
    {
        Assert.Equal("- Ja.\n- Nein.", SubtitlePolish.WrapLines("- Ja.\n- Nein."));
    }

    [Fact]
    public void ExtendShortDurations_ExtendsIntoFreeTimeWithoutOverlap()
    {
        var cues = new List<SubtitleCue>
        {
            new(0, 200, "Hallo, wie geht es dir heute so?"), // 32 chars → 1600 ms, plenty of room
            new(5000, 5300, "Gut."), // → 1000 ms, but next starts at 5600
            new(5600, 5700, "Ein sehr langer Satz, der eigentlich viel Zeit zum Lesen braucht, aber das hier ist noch länger als sieben Sekunden wert, weil er einfach immer weiter und weiter geht."),
        };

        var result = SubtitlePolish.ExtendShortDurations(cues);

        Assert.Equal(new long[] { 0, 5000, 5600 }, result.Select(c => c.StartMs).ToArray());
        Assert.Equal(1600, result[0].EndMs);
        Assert.Equal(5600 - 80, result[1].EndMs);
        Assert.Equal(5600 + 7000, result[2].EndMs);
        for (var i = 0; i + 1 < result.Count; i++)
        {
            Assert.True(result[i].EndMs <= result[i + 1].StartMs);
        }
    }

    [Fact]
    public void ExtendShortDurations_NeverShortensOrTouchesOverlappingCues()
    {
        var cues = new List<SubtitleCue>
        {
            new(1000, 3000, "Lang genug."),
            new(2500, 2600, "Überlappt schon."),
            new(2550, 2700, "Auch."),
        };

        var result = SubtitlePolish.ExtendShortDurations(cues);

        Assert.Equal(3000, result[0].EndMs);
        Assert.Equal(2600, result[1].EndMs);
        Assert.Equal(3550, result[2].EndMs);
    }

    [Fact]
    public void Apply_RunsOnlyEnabledSteps()
    {
        var cues = new List<SubtitleCue> { new(0, 100, "GrÃ¼ÃŸe aus einer sehr langen Zeile, die man umbrechen sollte, bitte.") };

        var none = SubtitlePolish.Apply(cues, PolishOptions.None);
        Assert.Equal(cues, none);

        var all = SubtitlePolish.Apply(cues, new PolishOptions(true, true, 42, true));
        Assert.StartsWith("Grüße", all[0].Text, System.StringComparison.Ordinal);
        Assert.Contains('\n', all[0].Text);
        Assert.True(all[0].EndMs > 100);
    }

    private static int CountOf(string s, string part) => (s.Length - s.Replace(part, string.Empty, System.StringComparison.Ordinal).Length) / part.Length;
}
