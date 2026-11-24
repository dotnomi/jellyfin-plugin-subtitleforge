using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Jellyfin.Plugin.SubtitleForge.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

public class FeatureTests2
{
    private const string Ass = """
[Script Info]
Title: test

[V4+ Styles]
Format: Name, Fontname, Fontsize
Style: Default,Arial,20
Style: Sign,Arial,20
Style: OP,Arial,20

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Hallo, wie geht's?
Dialogue: 0,0:00:04.00,0:00:06.50,Default,,0,0,0,,{\i1}Gut,{\i0} danke.\NUnd dir?
Dialogue: 0,0:00:05.00,0:00:07.00,Default,,0,0,0,,{\pos(320,50)}Bahnhof Shinjuku
Dialogue: 1,0:00:05.00,0:00:07.00,Default,,0,0,0,,{\pos(321,51)}Bahnhof Shinjuku
Dialogue: 0,0:00:08.00,0:00:10.00,Sign,,0,0,0,,Kapitel 1
Dialogue: 0,0:00:11.00,0:00:14.00,OP,,0,0,0,,{\k20}Lied{\k30}zeile
Dialogue: 0,0:00:15.00,0:00:15.10,Sign,,0,0,0,,Blitz
Dialogue: 0,0:00:16.00,0:00:18.00,Default,,0,0,0,,{\p1}m 0 0 l 100 0 100 100{\p0}
Comment: 0,0:00:19.00,0:00:20.00,Default,,0,0,0,,Kommentar
""";

    [Fact]
    public void Ass_SeparatesDialogueSignsAndSongs()
    {
        var events = AssConverter.Parse(Ass);
        var dialogue = AssConverter.Build(events, AssEventKind.Dialogue, AssEventKind.Song);
        Assert.Equal(new[] { "Hallo, wie geht's?", "<i>Gut,</i> danke.\nUnd dir?", "Liedzeile" }, dialogue.Select(c => c.Text));
        Assert.Equal(4000, dialogue[1].StartMs);
        Assert.Equal(6500, dialogue[1].EndMs);

        var forced = AssConverter.Build(events, AssEventKind.Sign, AssEventKind.Song);
        // Two layers of the same sign become one cue; the 100 ms flash is dropped; drawings are ignored.
        Assert.Equal(new[] { "Bahnhof Shinjuku", "Kapitel 1", "Liedzeile" }, forced.Select(c => c.Text));
    }

    [Fact]
    public void SmallTracks_FindsUnflaggedForcedTrack()
    {
        var small = ForcedDetector.SmallTracks(new[] { ("a", "de", 38), ("b", "de", 323), ("c", "en", 30) }).ToList();
        Assert.Equal(new[] { "a" }, small); // a single English track is never forced by count
        Assert.Empty(ForcedDetector.SmallTracks(new[] { ("a", "de", 300), ("b", "de", 320) }));
    }

    [Theory]
    [InlineData(false, 1, 7, 12, true)]
    [InlineData(true, 1, 7, 3, true)]
    [InlineData(true, 1, 7, 7, false)]
    [InlineData(true, 22, 6, 23, true)]
    [InlineData(true, 22, 6, 2, true)]
    [InlineData(true, 22, 6, 12, false)]
    public void TimeWindow(bool enabled, int start, int end, int hour, bool expected)
    {
        var config = new PluginConfiguration { AutoWorkWindowEnabled = enabled, AutoWorkStartHour = start, AutoWorkEndHour = end };
        Assert.Equal(expected, Throttle.InWindow(new DateTime(2026, 1, 1, hour, 30, 0), config));
    }

    [Fact]
    public void LastResort_PicksOldestGapsOnly()
    {
        var now = new DateTime(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);
        var a = new Movie { Id = Guid.NewGuid(), Name = "a" };
        var b = new Movie { Id = Guid.NewGuid(), Name = "b" };
        var c = new Movie { Id = Guid.NewGuid(), Name = "c" };
        var d = new Movie { Id = Guid.NewGuid(), Name = "d" };
        var state = new Dictionary<(Guid, string), DownloadStateStore.Entry>
        {
            [(a.Id, "de")] = new() { Outcome = "none-found", FirstMissingUtc = now.AddDays(-30) },
            [(b.Id, "de")] = new() { Outcome = "none-found", FirstMissingUtc = now.AddDays(-20) },
            [(c.Id, "de")] = new() { Outcome = "none-found", FirstMissingUtc = now.AddDays(-2) },  // too recent
            [(d.Id, "de")] = new() { Outcome = "none-found", FirstMissingUtc = now.AddDays(-40) },
            [(d.Id, "whisper:de")] = new() { Outcome = "whisper" },                                 // already tried
        };
        var config = new PluginConfiguration { WhisperLastResortAfterDays = 14, WhisperLastResortPerRun = 5 };
        var picks = Providers.DownloadTask.PickLastResort(
            new Video[] { a, b, c, d },
            config,
            (_, _) => false,
            (v, l) => state.TryGetValue((v.Id, l), out var e) ? e : null,
            _ => new[] { "de" },
            now);
        Assert.Equal(new[] { "a", "b" }, picks.Select(p => p.Video.Name));

        config.WhisperLastResortPerRun = 1;
        Assert.Single(Providers.DownloadTask.PickLastResort(new Video[] { a, b }, config, (_, _) => false, (v, l) => state.TryGetValue((v.Id, l), out var e) ? e : null, _ => new[] { "de" }, now));
    }

    [Theory]
    [InlineData("[SubsPlease] Frieren - 05 (1080p).ja.srt", "ja")]
    [InlineData("Frieren.S01E05.WEB.ass", "ja")]
    [InlineData("Frieren - 05 [English].srt", "en")]
    [InlineData("frieren_05.eng.srt", "en")]
    public void Jimaku_GuessLanguage(string name, string expected) => Assert.Equal(expected, JimakuSource.GuessLanguage(name));

    [Fact]
    public void DownloadState_TracksFirstMissing()
    {
        // FirstMissingUtc survives further failures and is cleared once a subtitle was saved.
        var store = new DownloadStateStore();
        var id = Guid.NewGuid();
        store.Set(id, "de", "none-found", null);
        var first = store.Get(id, "de")!.FirstMissingUtc;
        Assert.NotNull(first);
        store.Set(id, "de", "no-safe-match", null);
        Assert.Equal(first, store.Get(id, "de")!.FirstMissingUtc);
        store.Set(id, "de", "saved", null);
        Assert.Null(store.Get(id, "de")!.FirstMissingUtc);
    }

    [Fact]
    public void NvidiaNeedsProprietaryVulkanDriver()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("sf-icd").FullName;
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "nouveau_icd.json"), "{}");
            Assert.False(Whisper.GpuDetector.HasNvidiaVulkanDriver(dir));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "nvidia_icd.json"), "{}");
            Assert.True(Whisper.GpuDetector.HasNvidiaVulkanDriver(dir));
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }
}
