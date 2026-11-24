using Jellyfin.Plugin.SubtitleForge.Ocr;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

public class OcrDictionaryTests
{
    private static string One(string text, string? language) => OcrDictionary.Correct(new[] { text }, language)[0];

    [Theory]
    [InlineData("Good rnorning, everyone.", "en", "Good morning, everyone.")]
    [InlineData("You need to cIean up.", "en", "You need to clean up.")]
    [InlineData("Iater, maybe.", "en", "later, maybe.")]
    [InlineData("See you Iater.", "en", "See you later.")]
    [InlineData("Don't tum around!", "en", "Don't turn around!")]
    [InlineData("We have to h0ld on.", "en", "We have to hold on.")]
    [InlineData("Wir müssen das Iassen.", "de", "Wir müssen das lassen.")]
    [InlineData("Das ist rnein Haus.", "de", "Das ist mein Haus.")]
    [InlineData("Das ist fiir dich.", "de", "Das ist für dich.")]
    [InlineData("Er sagte: lch komme.", "de", "Er sagte: Ich komme.")]
    public void FixesConfusionsTheLinePassMisses(string input, string language, string expected)
    {
        Assert.Equal(expected, One(input, language));
    }

    [Theory]
    [InlineData("We went to IKEA yesterday.", "en")]
    [InlineData("It was 1984, and l0l was not a word.", "en")]
    [InlineData("Arnold, Clive and Ilse are here.", "en")]
    [InlineData("Arnold und Clara sind da.", "de")]
    [InlineData("Er kam um 19:45 Uhr, nicht um 20 Uhr.", "de")]
    [InlineData("Visit www.rnorning.com or mail clean@rnail.org", "en")]
    [InlineData("OK, TV is on. I'm here.", "en")]
    [InlineData("We'll see, it's 10th and 1st place.", "en")]
    [InlineData("Ich habe Hunger und keine Zeit.", "de")]
    [InlineData("Das ist ein Test-Untertitel mit Umlauten.", "de")]
    public void LeavesRealTextUnchanged(string input, string language)
    {
        Assert.Equal(input, One(input, language));
    }

    [Fact]
    public void UsesWordsFromTheSubtitleItselfWithoutWordList()
    {
        // No word list for French: "maison" appears twice correctly, so the one "rnaison" is fixed.
        var texts = new[] { "La maison est grande.", "Une maison bleue.", "Dans la rnaison." };
        var fixedTexts = OcrDictionary.Correct(texts, "fr");
        Assert.Equal("Dans la maison.", fixedTexts[2]);
        Assert.Equal(texts[0], fixedTexts[0]);
    }

    [Fact]
    public void FrequentUnknownWordsAndRepeatedNamesStay()
    {
        // "Clint" (capitalised, twice) looks like a name; "Brrrn" (three times) is intentional.
        var texts = new[] { "Clint, wait!", "Clint is here.", "Brrrn.", "Brrrn!", "Brrrn?" };
        Assert.Equal(texts, OcrDictionary.Correct(texts, "en"));
    }

    [Fact]
    public void AmbiguousVariantsStayUnchanged()
    {
        // "clarn" could be "clam" (rn→m) or "darn" (cl→d), both seen twice in the file: nothing is guessed.
        var texts = new[] { "clam darn", "clam darn", "It was a clarn." };
        Assert.Equal(texts, OcrDictionary.Correct(texts, "fr"));
    }

    [Fact]
    public void KeepsCountAndHandlesLineBreaks()
    {
        var texts = new[] { "Good rnorning.\n- Hi.", string.Empty, "Iater." };
        var result = OcrDictionary.Correct(texts, "eng");
        Assert.Equal(new[] { "Good morning.\n- Hi.", string.Empty, "later." }, result);
    }
}
