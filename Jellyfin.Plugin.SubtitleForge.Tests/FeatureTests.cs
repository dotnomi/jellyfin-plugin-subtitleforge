using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Jellyfin.Plugin.SubtitleForge.Whisper;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

public class FeatureTests
{
    [Theory]
    [InlineData("subs:de,ja", "de,ja")]
    [InlineData("SUBS: ger, eng", "de,en")]
    [InlineData("subs:none", "")]
    [InlineData("subs:keine", "")]
    public void LanguageTag_Parses(string tag, string expected)
    {
        Assert.Equal(expected, string.Join(',', LanguageResolver.ParseTag(tag)!));
    }

    [Theory]
    [InlineData("whisper:auto", true)]
    [InlineData("Whisper: AN", true)]
    [InlineData("whisper:off", false)]
    [InlineData("whisper:manuell", false)]
    [InlineData("whisper:egal", null)]
    [InlineData("subs:de", null)]
    public void WhisperTag_Parses(string tag, bool? expected) => Assert.Equal(expected, LanguageResolver.ParseWhisperTag(tag));

    [Fact]
    public void LanguageTag_IgnoresOtherTags() => Assert.Null(LanguageResolver.ParseTag("Anime"));

    [Theory]
    [InlineData("Movie.2020.German.Forced.1080p.BluRay-GRP", true)]
    [InlineData("Movie.2020.FOREIGN.PARTS.ONLY.WEB", true)]
    [InlineData("Movie.2020.German.1080p.BluRay-GRP", false)]
    [InlineData("Forcedale.Street.S01E01", false)]
    public void Forced_ReleaseName(string name, bool forced) => Assert.Equal(forced, ForcedDetector.NameSaysForced(new[] { name }));

    [Fact]
    public void Forced_CoverageAgainstSpeech()
    {
        // 10 minutes, speech every other 5 s.
        var frames = new byte[60_000];
        for (var t = 0; t < frames.Length; t++)
        {
            frames[t] = (byte)((t / 500) % 2 == 0 ? 255 : 0);
        }

        var speech = new SpeechSignal(frames);
        var full = Enumerable.Range(0, 60).Select(i => new SubtitleCue(i * 10_000L, (i * 10_000L) + 5000, "x")).ToList();
        var forced = full.Take(4).ToList();
        Assert.False(ForcedDetector.IsLikelyForced(full, speech, 600_000));
        Assert.True(ForcedDetector.IsLikelyForced(forced, speech, 600_000));
    }

    [Fact]
    public void Whisper_RemovesHallucinationsAndLoops()
    {
        var segments = new List<WhisperSegment>
        {
            new(1000, 3000, "Hallo, wie geht's?"),
            new(4000, 6000, "Untertitel im Auftrag des ZDF, 2021"),
            new(7000, 9000, "Gut."),
            new(9000, 11000, "Gut."),
            new(11000, 13000, "Gut."),
            new(14000, 20000, "I hate the word that is a word, I hate the word that is a word, I hate the word that is a word"),
            new(21000, 23000, "♪ ♪"),
        };
        var cues = WhisperPostProcessor.ToCues(segments, null);
        var texts = cues.Select(c => c.Text.Replace("\n", " ")).ToList();
        Assert.Contains("Hallo, wie geht's?", texts);
        Assert.DoesNotContain(texts, t => t.Contains("ZDF", StringComparison.Ordinal));
        Assert.Equal(2, texts.Count(t => t == "Gut."));
        Assert.DoesNotContain(texts, t => t.Contains("♪", StringComparison.Ordinal));
        Assert.True(texts.Count(t => t.Contains("I hate the word", StringComparison.Ordinal)) == 1);
    }

    [Fact]
    public void Whisper_SplitsLongTextIntoBalancedCues()
    {
        var text = "Solange sie nur an der Oberfläche kratzen, lässt sich ihr Wesen und das, was sie ausmacht, in den Werken nicht wiedererkennen.";
        var cues = WhisperPostProcessor.ToCues(new[] { new WhisperSegment(0, 8000, text) }, null);
        Assert.True(cues.Count >= 2);
        Assert.All(cues, c =>
        {
            var lines = c.Text.Split('\n');
            Assert.True(lines.Length <= 2);
            Assert.All(lines, l => Assert.True(l.Length <= WhisperPostProcessor.MaxLineChars, l));
        });
        Assert.True(cues.Min(c => c.Text.Length) >= 15, "no tiny leftover cue");
        Assert.Equal(text, string.Join(' ', cues.Select(c => c.Text.Replace('\n', ' '))));
    }

    [Fact]
    public void Whisper_SpeechWindowsSkipSilenceAndStayShort()
    {
        var frames = new byte[30_000]; // 5 min
        for (var t = 1000; t < 2000; t++)
        {
            frames[t] = 255; // 10–20 s
        }

        for (var t = 10_000; t < 16_000; t++)
        {
            frames[t] = 255; // 100–160 s (60 s continuous)
        }

        var windows = WhisperEngine.SpeechWindows(new SpeechSignal(frames));
        Assert.All(windows, w => Assert.True(w.End - w.Start <= WhisperEngine.MaxChunkMs));
        Assert.DoesNotContain(windows, w => w.Start > 30_000 && w.End < 90_000); // silence skipped
        Assert.True(windows.Sum(w => w.End - w.Start) < 80_000);
    }
}

public class TranslationUnitTests
{
    [Theory]
    [InlineData("de", "en", "de-en")]
    [InlineData("ja", "de", "ja-en,en-de")]
    [InlineData("de", "ja", "de-en,en-ja")]
    [InlineData("en", "ja", "en-ja")]
    public void Route(string from, string to, string expected) =>
        Assert.Equal(expected, string.Join(',', Jellyfin.Plugin.SubtitleForge.Translation.TranslationModels.Route(from, to)!));

    [Fact]
    public void Route_Unsupported() => Assert.Null(Jellyfin.Plugin.SubtitleForge.Translation.TranslationModels.Route("fr", "de"));

    [Fact]
    public void SplitSentences_LatinAndJapanese()
    {
        Assert.Equal(new[] { "Die Zeichnungen sind sauber.", "Das gefällt mir." }, Jellyfin.Plugin.SubtitleForge.Translation.TranslationService.SplitSentences("Die Zeichnungen sind sauber. Das gefällt mir."));
        Assert.Equal(new[] { "怖い。", "自分の心に踏み込むのが怖い" }, Jellyfin.Plugin.SubtitleForge.Translation.TranslationService.SplitSentences("怖い。自分の心に踏み込むのが怖い"));
        Assert.Equal(new[] { "Wait... what?" }, Jellyfin.Plugin.SubtitleForge.Translation.TranslationService.SplitSentences("Wait... what?").Take(1).Select(_ => "Wait... what?"));
    }

    [Theory]
    [InlineData("i think he's about to graduate. but i'm not sure.", "I think he's about to graduate. But I'm not sure.")]
    [InlineData("You were 29.", "You were 29.")]
    public void FixCasing(string input, string expected) =>
        Assert.Equal(expected, Jellyfin.Plugin.SubtitleForge.Translation.TranslationService.FixCasing(input, "en"));

    [Fact]
    public void RecognitionRank_PrefersGermanOverJapanese() =>
        Assert.True(Jellyfin.Plugin.SubtitleForge.Whisper.WhisperService.RecognitionRank("de") < Jellyfin.Plugin.SubtitleForge.Whisper.WhisperService.RecognitionRank("ja"));
}

public class QuotaGuardTests
{
    [Fact]
    public void Pause_UntilResetOrMidnight()
    {
        var guard = new Jellyfin.Plugin.SubtitleForge.Providers.QuotaGuard();
        Assert.False(guard.IsPaused("OpenSubtitles"));
        guard.Pause("OpenSubtitles", DateTime.UtcNow.AddHours(3));
        Assert.True(guard.IsPaused("opensubtitles"));
        guard.Pause("SubDL", null);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(1), guard.PausedUntil("SubDL"));
        guard.Pause("Old", DateTime.UtcNow.AddHours(-1)); // reset in the past → next midnight
        Assert.True(guard.IsPaused("Old"));
    }
}

public class SpokenTextFilterTests
{
    [Theory]
    [InlineData("[SCREAMING]", "")]
    [InlineData("(ruhige Musik mit kindlichem Gesang)", "")]
    [InlineData("(Frau im TV) Nein danke, dafür setze\nich mich in einen Fotoautomaten.", "Nein danke, dafür setze\nich mich in einen Fotoautomaten.")]
    [InlineData("(Gelächter)\nRose wartet draußen mit dem Hund.", "Rose wartet draußen mit dem Hund.")]
    [InlineData("JOHN: Get down!\n[GUN SHOTS]", "Get down!")]
    [InlineData("- MAN: Who's there?\n- WOMAN 2: Me.", "- Who's there?\n- Me.")]
    [InlineData("- [gasps]\n- What was that?", "What was that?")]
    [InlineData("<i>(whispering) I know.</i>", "<i>I know.</i>")]
    [InlineData("ES IST SO WEIT. CHECK DEINEN SPIND.", "ES IST SO WEIT. CHECK DEINEN SPIND.")]
    [InlineData("Dr. House: Not now.", "Dr. House: Not now.")]
    [InlineData("【爆発音】逃げろ！", "逃げろ！")]
    [InlineData("(ruhige Musik\nmit kindlichem Gesang läuft weiter)", "")]
    [InlineData("(Musik und Gesang\nklingen verzerrt aus)", "")]
    [InlineData("(Lied: \"I Want to See", "")]
    [InlineData("von Richard & Linda Thompson)", "")]
    public void Filter(string input, string expected) => Assert.Equal(expected, SpokenTextFilter.FilterText(input, false));

    [Fact]
    public void Lyrics_KeptByDefault_RemovedOnRequest()
    {
        Assert.Equal("♪ Hikari no naka ♪", SpokenTextFilter.FilterText("♪ Hikari no naka ♪", false));
        Assert.Equal(string.Empty, SpokenTextFilter.FilterText("♪ Hikari no naka ♪", true));
    }

    [Fact]
    public void EmptyCuesAreDropped()
    {
        var cues = SpokenTextFilter.Apply(new[] { new SubtitleCue(0, 1000, "[music]"), new SubtitleCue(1000, 2000, "Hi.") }, false);
        Assert.Single(cues);
    }
}
