using Jellyfin.Plugin.SubtitleForge.Common;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// [SF] mark, origin detection, rename migration.
/// </summary>
public class FeatureTests3
{
    private const string Video = "/media/Show/Show.S01E01.mkv";

    [Theory]
    [InlineData("Show.S01E01.de.[SF] SubDL.srt", "download", "SubDL", true)]
    [InlineData("Show.S01E01.de.SubDL.srt", "download", "SubDL", false)]
    [InlineData("Show.S01E01.ger.forced.[SF] OCR.srt", "ocr", "forced.OCR", true)]
    [InlineData("Show.S01E01.eng.[SF] [AI] Translated de-en.srt", "translation", "[AI] Translated de-en", true)]
    [InlineData("Show.S01E01.eng.[AI] Übersetzt de-en.srt", "translation", "[AI] Übersetzt de-en", false)]
    [InlineData("Show.S01E01.ger.[AI] Whisper.srt", "whisper", "[AI] Whisper", false)]
    [InlineData("Show.S01E01.ger.Whisper-ja.srt", "whisper", "Whisper-ja", false)]
    [InlineData("Show.S01E01.de.[SF] Sync.srt", "plugin", "Sync", true)]
    [InlineData("Show.S01E01.de.[SF] Something New.srt", "plugin", "Something New", true)]
    [InlineData("Show.S01E01.eng.srt", "foreign", "", false)]
    [InlineData("Show.S01E01.de.MyOwn.srt", "foreign", "MyOwn", false)]
    [InlineData("Other.Movie.de.SubDL.srt", "foreign", "", false)]
    [InlineData("Show.S01E01.en.whisper.srt", "foreign", "whisper", false)]
    [InlineData("Show.S01E01.en.whisper-large.srt", "foreign", "whisper-large", false)]
    [InlineData("Show.S01E01.en.opensubtitles.srt", "foreign", "opensubtitles", false)]
    [InlineData("Show.S01E01.eng.Forced.srt", "foreign", "Forced", false)]
    [InlineData("Show.S01E01.de.OCRfixed.srt", "foreign", "OCRfixed", false)]
    [InlineData("Show.S01E01.ger.OCR2.srt", "ocr", "OCR2", false)]
    [InlineData("Show.S01E01.eng.[SF] [AI] Whisper de-en.srt", "whisper", "[AI] Whisper de-en", true)]
    public void Origin_IsDetectedFromTheFileName(string file, string origin, string tag, bool branded)
    {
        var o = SubtitleOrigin.Classify(Video, "/media/Show/" + file);
        Assert.Equal(origin, o.Origin);
        if (origin != "foreign" || tag.Length > 0)
        {
            Assert.Equal(tag, o.Tag);
        }

        Assert.Equal(branded, o.Branded);
    }

    [Theory]
    [InlineData("Show.S01E01.de.SubDL.srt", "Show.S01E01.de.[SF] SubDL.srt")]
    [InlineData("Show.S01E01.ger.forced.OCR.srt", "Show.S01E01.ger.forced.[SF] OCR.srt")]
    [InlineData("Show.S01E01.eng.[AI] Übersetzt de-en.srt", "Show.S01E01.eng.[SF] [AI] Übersetzt de-en.srt")]
    [InlineData("Show.S01E01.de.[SF] SubDL.srt", null)]
    [InlineData("Show.S01E01.eng.srt", null)]
    [InlineData("Show.S01E01.de.MyOwn.srt", null)]
    [InlineData("Show.S01E01.eng.Forced.srt", null)]
    [InlineData("Show.S01E01.en.whisper.srt", null)]
    public void Migration_OnlyRenamesOwnUnmarkedFiles(string file, string? expected)
    {
        var target = BrandMigration.BrandedPath(Video, "/media/Show/" + file);
        Assert.Equal(expected is null ? null : "/media/Show/" + expected, target);
    }

    [Fact]
    public void Stems_CarryTheMarkAndKnowTheirLegacyName()
    {
        Assert.Equal("SubDL", SubtitleFileWriter.StripBrand("[SF] SubDL"));
        Assert.Equal("SubDL", SubtitleFileWriter.StripBrand("SubDL"));
        Assert.Equal("OCR", SubtitleFileWriter.TagOf("Show.S01E01.ger.forced.[SF] OCR"));
        Assert.Equal("Show.S01E01.ger.forced.OCR", SubtitleFileWriter.LegacyStem("Show.S01E01.ger.forced.[SF] OCR"));
        Assert.Equal("Show.S01E01.ger.OCR", SubtitleFileWriter.LegacyStem("Show.S01E01.ger.OCR"));
    }

    [Fact]
    public void ExtradataParser_HandlesMissingData()
    {
        Assert.Null(FfmpegRunner.ParseExtradata("[STREAM]\nindex=0\ncodec_name=dvd_subtitle\n[/STREAM]\n"));
        Assert.Equal("size: 720x576\n", FfmpegRunner.ParseExtradata("extradata=\n00000000: 7369 7a65 3a20 3732 3078 3537 360a       size: 720x576.\nextradata_size=14\n"));
    }
}
