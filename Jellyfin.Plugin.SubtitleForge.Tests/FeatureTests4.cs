using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubtitleForge.Api;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Recycle bin, learned source ranking, weekly report, manual shift.
/// </summary>
[Collection("static-stores")]
public sealed class FeatureTests4 : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sf-test-" + Guid.NewGuid().ToString("N"));

    public FeatureTests4()
    {
        Directory.CreateDirectory(_dir);
        SubtitleTrash.FolderOverride = Path.Combine(_dir, "trash");
        SourceStats.FolderOverride = _dir;
        SourceStats.Reset();
    }

    public void Dispose()
    {
        SubtitleTrash.FolderOverride = null;
        SubtitleTrash.UtcNow = () => DateTime.UtcNow;
        SourceStats.FolderOverride = null;
        SourceStats.Reset();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Trash_MovesFileAndKeepsState()
    {
        var file = Path.Combine(_dir, "Show.S01E01.de.[SF] SubDL.srt");
        File.WriteAllText(file, "1\n00:00:01,000 --> 00:00:02,000\nHallo\n");
        var item = Guid.NewGuid();
        var entry = SubtitleTrash.Remove(item, file, "deleted", new[] { new SubtitleTrash.StateSnapshot { Key = "de", Outcome = "saved", Reference = "SubDL|1" } });

        Assert.NotNull(entry);
        Assert.False(File.Exists(file));
        var listed = Assert.Single(SubtitleTrash.List(item));
        Assert.Equal(file, listed.OriginalPath);
        Assert.Equal("SubDL|1", listed.States.Single().Reference);
        Assert.Empty(SubtitleTrash.List(Guid.NewGuid()));

        var read = SubtitleTrash.Read(entry!.Id);
        Assert.NotNull(read);
        Assert.Contains("Hallo", System.Text.Encoding.UTF8.GetString(read!.Value.Content), StringComparison.Ordinal);

        SubtitleTrash.Forget(entry.Id);
        Assert.Empty(SubtitleTrash.List(item));
        Assert.Null(SubtitleTrash.Read(entry.Id));
    }

    [Fact]
    public void Trash_NeverKeepsLinks()
    {
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "secret");
        var link = Path.Combine(_dir, "Show.S01E01.de.[SF] SubDL.srt");
        File.CreateSymbolicLink(link, secret);
        var item = Guid.NewGuid();

        Assert.Null(SubtitleTrash.Remove(item, link, "deleted"));
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(secret));
        Assert.Empty(SubtitleTrash.List(item));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("")]
    public void Trash_RejectsInvalidIds(string id) => Assert.Null(SubtitleTrash.Read(id));

    [Fact]
    public void Trash_PurgesOldEntries()
    {
        var file = Path.Combine(_dir, "a.srt");
        File.WriteAllText(file, "x");
        var item = Guid.NewGuid();
        SubtitleTrash.UtcNow = () => DateTime.UtcNow.AddDays(-31);
        SubtitleTrash.Remove(item, file, "deleted");
        SubtitleTrash.UtcNow = () => DateTime.UtcNow;

        Assert.Empty(SubtitleTrash.List(item));
        Assert.Empty(Directory.GetFiles(SubtitleTrash.FolderOverride!, "*.srt"));
    }

    [Fact]
    public void SourceStats_BonusFollowsHistory()
    {
        Assert.Equal(0, SourceStats.Bonus(new SourceStats.Counts()));
        Assert.Equal(0, SourceStats.Bonus(new SourceStats.Counts { Saved = 5, Failed = 5 }));
        Assert.True(SourceStats.Bonus(new SourceStats.Counts { Saved = 40, Failed = 2 }) > 15);
        Assert.True(SourceStats.Bonus(new SourceStats.Counts { Saved = 1, Failed = 30 }) < -15);
        Assert.InRange(SourceStats.Bonus(new SourceStats.Counts { Saved = 100000 }), 0, SourceStats.MaxBonus);
        Assert.True(SourceStats.Bonus(new SourceStats.Counts { Saved = 1 }) < 5); // little data → little effect

        for (var i = 0; i < 20; i++)
        {
            SourceStats.Saved("SubDL");
            SourceStats.Failed("Jimaku");
        }

        SourceStats.Reset();
        var bonus = SourceStats.Bonuses();
        Assert.True(bonus["SubDL"] > 0);
        Assert.True(bonus["Jimaku"] < 0);
    }

    [Fact]
    public void Ranker_SourceBonusNeverBeatsHash()
    {
        var query = new SubtitleQuery { Languages = new[] { "de" } };
        var hash = new SubtitleCandidate { Source = "OpenSubtitles", HashMatch = true, Language = "de" };
        var liked = new SubtitleCandidate { Source = "SubDL", Language = "de" };
        var plain = new SubtitleCandidate { Source = "Jimaku", Language = "de" };
        var bonus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["SubDL"] = 25, ["OpenSubtitles"] = -25, ["Jimaku"] = -25 };

        var ranked = CandidateRanker.Rank(new[] { plain, liked, hash }, query, false, bonus);

        Assert.Same(hash, ranked[0]);
        Assert.Same(liked, ranked[1]);
        Assert.Contains("source-history", liked.ScoreReasons);
    }

    [Fact]
    public void Shift_MovesCuesAndDropsThoseBeforeZero()
    {
        var cues = new List<SubtitleCue> { new(500, 1500, "a"), new(3000, 4000, "b") };

        var later = SubtitleForgeController.ShiftCues(cues, 1250);
        Assert.Equal(new long[] { 1750, 4250 }, later.Select(c => c.StartMs));

        var earlier = SubtitleForgeController.ShiftCues(cues, -2000);
        var only = Assert.Single(earlier);
        Assert.Equal(1000, only.StartMs);
        Assert.Equal("b", only.Text);
    }

    [Fact]
    public void WeeklyReport_DueOncePerWeek()
    {
        var monday9 = new DateTime(2026, 11, 23, 9, 30, 0, DateTimeKind.Local);
        Assert.True(WeeklyReport.IsDue(monday9, null, 1, 9));
        Assert.False(WeeklyReport.IsDue(monday9.AddHours(-2), null, 1, 9));
        Assert.False(WeeklyReport.IsDue(monday9.AddDays(1), null, 1, 9));
        Assert.False(WeeklyReport.IsDue(monday9.AddHours(1), monday9.ToUniversalTime(), 1, 9));
        Assert.True(WeeklyReport.IsDue(monday9.AddDays(7), monday9.ToUniversalTime(), 1, 9));
    }

    [Fact]
    public void WeeklyReport_ComposesCountsAndFailures()
    {
        var (title, shortLine, body) = WeeklyReport.Compose(
            new Dictionary<string, int> { ["download"] = 3, ["whisper"] = 1 },
            Enumerable.Range(1, 20).Select(i => "Show S01E" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " (de)").ToList());

        Assert.Contains("4", title, StringComparison.Ordinal);
        Assert.Contains("20", shortLine, StringComparison.Ordinal);
        Assert.Contains("3 downloaded", body, StringComparison.Ordinal);
        Assert.Contains("Show S01E15", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Show S01E16", body, StringComparison.Ordinal);
        Assert.Contains("5 more", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rum", "ro")]
    [InlineData("ron", "ro")]
    [InlineData("gre", "el")]
    [InlineData("slo", "sk")]
    [InlineData("ice", "is")]
    [InlineData("per", "fa")]
    [InlineData("ger", "de")]
    public void Lang_KnowsBothIso6392Codes(string code, string two)
    {
        Assert.Equal(two, Lang.ToTwoLetter(code));
        Assert.True(Lang.Same(code, two));
    }

    [Fact]
    public void Lang_FileNamesUseBibliographicCode() => Assert.Equal("rum", Lang.ToThreeLetter("ro"));

    [Fact]
    public void Decode_UsesLegacyCodePageOfTheLanguage()
    {
        var cyrillic = System.Text.Encoding.GetEncoding(1251).GetBytes("1\n00:00:01,000 --> 00:00:02,000\nПривет\n");
        Assert.Contains("Привет", SubtitleContent.Decode(cyrillic, "ru"), StringComparison.Ordinal);
        var german = System.Text.Encoding.GetEncoding(1252).GetBytes("Grüße");
        Assert.Equal("Grüße", SubtitleContent.Decode(german, "de"));
        Assert.Equal("Grüße", SubtitleContent.Decode(german));
    }

    [Fact]
    public void Aligner_IgnoresCuesFarBeyondTheAudio()
    {
        // 10 minutes of audio, one junk cue at 99 h: must not size buffers by that time.
        var frames = new byte[60_000];
        var cues = new List<SubtitleCue>
        {
            new(10_000, 12_000, "a"),
            new(20_000, 22_000, "b"),
            new(99L * 3_600_000, (99L * 3_600_000) + 1000, "junk"),
        };
        var before = GC.GetTotalAllocatedBytes(true);
        var result = Jellyfin.Plugin.SubtitleForge.Sync.SubtitleAligner.Align(cues, new Jellyfin.Plugin.SubtitleForge.Sync.SpeechSignal(frames));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 400_000_000);
        Assert.DoesNotContain(result.Cues, c => c.Text == "junk");
    }
}
