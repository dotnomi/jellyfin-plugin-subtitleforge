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

public class DeepLTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        public List<(string Url, string Body, string? Auth)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            return _respond(request, body);
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, false);
    }

    private static HttpResponseMessage Json(string json, int status = 200) =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Translate_BatchesWithContextAndEnglishVariant()
    {
        var handler = new FakeHandler((req, body) =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var texts = doc.RootElement.GetProperty("text").EnumerateArray().Select(t => "T:" + t.GetString()).ToList();
            return Json(System.Text.Json.JsonSerializer.Serialize(new { translations = texts.Select(t => new { text = t }) }));
        });
        var deepl = new Jellyfin.Plugin.SubtitleForge.Translation.DeepLTranslator(new Factory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.SubtitleForge.Translation.DeepLTranslator>.Instance, () => "abc:fx");
        var lines = Enumerable.Range(0, 95).Select(i => "Zeile " + i).ToList();

        var result = await deepl.TranslateAsync(lines, "de", "en", null, CancellationToken.None);

        Assert.Equal(lines.Select(l => "T:" + l), result);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.StartsWith("https://api-free.deepl.com/v2/translate", r.Url, StringComparison.Ordinal));
        Assert.All(handler.Requests, r => Assert.Equal("DeepL-Auth-Key abc:fx", r.Auth));
        Assert.Contains("\"target_lang\":\"EN-US\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("Zeile 39", handler.Requests[1].Body.Split("\"context\"")[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Translate_RetriesOn429_AndThrowsQuotaOn456()
    {
        var calls = 0;
        var handler = new FakeHandler((req, body) => ++calls == 1 ? Json("{}", 429) : Json("{\"translations\":[{\"text\":\"ok\"}]}"));
        var deepl = new Jellyfin.Plugin.SubtitleForge.Translation.DeepLTranslator(new Factory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.SubtitleForge.Translation.DeepLTranslator>.Instance, () => "pro-key");
        Assert.Equal(new[] { "ok" }, await deepl.TranslateAsync(new[] { "x" }, "ja", "de", null, CancellationToken.None));
        Assert.StartsWith("https://api.deepl.com", handler.Requests[0].Url, StringComparison.Ordinal);

        var quota = new Jellyfin.Plugin.SubtitleForge.Translation.DeepLTranslator(new Factory(new FakeHandler((_, _) => Json("{\"message\":\"Quota exceeded\"}", 456))), Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.SubtitleForge.Translation.DeepLTranslator>.Instance, () => "k:fx");
        await Assert.ThrowsAsync<Jellyfin.Plugin.SubtitleForge.Translation.DeepLQuotaException>(() => quota.TranslateAsync(new[] { "x" }, "ja", "de", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("ja", "de", true)]
    [InlineData("de", "en", true)]
    [InlineData("xx", "de", false)]
    [InlineData("de", "de", false)]
    public void Supports(string from, string to, bool expected) =>
        Assert.Equal(expected, Jellyfin.Plugin.SubtitleForge.Translation.DeepLTranslator.Supports(from, to));
}
