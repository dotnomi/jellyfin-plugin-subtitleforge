using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

public class ProviderTests
{
    [Theory]
    [InlineData("Dune.Part.Two.2024.1080p.BluRay.x264-SPARKS.mkv", "sparks", "bluray", "1080p", null)]
    [InlineData("The.Bear.S03E01.Tomorrow.2160p.DSNP.WEB-DL.DDP5.1.H.265-FLUX.mkv", "flux", "web", "2160p", "dsnp")]
    [InlineData("[SubsPlease] Frieren - 05 (1080p) [A1B2C3D4].mkv", "subsplease", null, "1080p", null)]
    [InlineData("Blade.Runner.1982.Final.Cut.720p.BluRay.x264-HDEX", "hdex", "bluray", "720p", null)]
    [InlineData("Frieren - S01E05 - Phantoms of the Dead [WEBDL-1080p][8bit][x264][AAC 2.0][JA][SubsPlease].mkv", "subsplease", "web", "1080p", null)]
    [InlineData("Der.Tatortreiniger.S01E02.German.720p.HDTV.x264-TVP", "tvp", "hdtv", "720p", null)]
    public void ReleaseInfo_Parses(string name, string? group, string? source, string? res, string? service)
    {
        var r = ReleaseInfo.Parse(name);
        Assert.Equal(group, r.Group);
        Assert.Equal(source, r.SourceType);
        Assert.Equal(res, r.Resolution);
        Assert.Equal(service, r.Service);
    }

    [Fact]
    public void Ranker_PrefersHashThenGroup_AndMarksSafeMatches()
    {
        var query = new SubtitleQuery
        {
            IsEpisode = true,
            Season = 1,
            Episode = 5,
            Languages = new[] { "de" },
            Release = ReleaseInfo.Parse("Show.S01E05.1080p.NF.WEB-DL.DDP5.1.x264-NTb.mkv"),
        };
        var candidates = new[]
        {
            new SubtitleCandidate { Source = "SubDL", Reference = "a", Language = "de", Season = 1, Episode = 5, Releases = { "Show.S01E05.720p.HDTV.x264-KILLERS" }, Downloads = 5000 },
            new SubtitleCandidate { Source = "SubDL", Reference = "b", Language = "de", Season = 1, Episode = 5, Releases = { "Show.S01E05.1080p.NF.WEB-DL.DDP5.1.x264-NTb" } },
            new SubtitleCandidate { Source = "OpenSubtitles", Reference = "c", Language = "de", Season = 1, Episode = 5, HashMatch = true },
            new SubtitleCandidate { Source = "SubDL", Reference = "d", Language = "de", Season = 1, Episode = 6, Releases = { "Show.S01E06.1080p.NF.WEB-DL.DDP5.1.x264-NTb" } },
        };

        var ranked = CandidateRanker.Rank(candidates, query);

        Assert.Equal(new[] { "c", "b", "a" }, ranked.Select(c => c.Reference));
        Assert.True(ranked[0].IsSafeMatch);
        Assert.True(ranked[1].IsSafeMatch);
        Assert.False(ranked[2].IsSafeMatch);
    }

    [Fact]
    public void Ranker_DropsWrongEpisodeFromReleaseName()
    {
        var query = new SubtitleQuery { IsEpisode = true, Season = 1, Episode = 5, Languages = new[] { "de" } };
        var ranked = CandidateRanker.Rank(
            new[]
            {
                new SubtitleCandidate { Reference = "wrong", Language = "de", Releases = { "Frieren.S01E33.CR.WEB-DL.German" } },
                new SubtitleCandidate { Reference = "right", Language = "de", Releases = { "Frieren.S01E05.CR.WEB-DL.German" } },
                new SubtitleCandidate { Reference = "unknown", Language = "de", Releases = { "Frieren German Subs" } },
            },
            query);
        Assert.Equal(new[] { "right", "unknown" }, ranked.Select(c => c.Reference).OrderByDescending(r => r == "right"));
    }

    [Fact]
    public void Ranker_EditionMismatchIsNotSafe()
    {
        var query = new SubtitleQuery { Languages = new[] { "en" }, Release = ReleaseInfo.Parse("Movie.2010.Extended.1080p.BluRay.x264-GRP") };
        var ranked = CandidateRanker.Rank(new[] { new SubtitleCandidate { Language = "en", Releases = { "Movie.2010.1080p.BluRay.x264-GRP" } } }, query);
        Assert.False(ranked[0].IsSafeMatch);
    }

    [Fact]
    public void SubDl_ParsesSearchAndPicksUnpackedEpisode()
    {
        const string json = """
        {"status":true,"subtitles":[
          {"release_name":"Show.S01.1080p.WEB-DL-GRP","name":"SUBDL::pack.zip","lang":"german","language":"DE","author":"x",
           "url":"/subtitle/1-2.zip","season":1,"episode":null,"episode_from":null,"episode_end":0,"full_season":true,"hi":false,
           "releases":["Show.S01.1080p.WEB-DL-GRP"],
           "unpack_files":[{"url":"/subtitle/1-2/e4.srt","name":"Show.S01E04.srt","season":1,"episode":4,"file_n_id":44},
                           {"url":"/subtitle/1-2/e5.srt","name":"Show.S01E05.srt","season":1,"episode":5,"file_n_id":45}]},
          {"release_name":"Show.S01E05.720p.HDTV-OTHER","name":"x.zip","lang":"english","language":"EN","url":"/subtitle/3-4.zip?api_key=SECRET",
           "season":1,"episode":5,"episode_from":5,"episode_end":5,"hi":1,"releases":["Show.S01E05.720p.HDTV-OTHER"]},
          {"release_name":"Show.S01E05","name":"y.zip","lang":"french","language":"FR","url":"/subtitle/5.zip","season":1,"episode":5}
        ]}
        """;
        var q = new SubtitleQuery { IsEpisode = true, Season = 1, Episode = 5, Languages = new[] { "de", "en" } };
        var result = SubDlSource.ParseSearch(json, q);

        Assert.Equal(2, result.Count);
        Assert.Equal("/subtitle/1-2/e5.srt", result[0].Reference);
        Assert.False(result[0].IsPack);
        Assert.Equal("de", result[0].Language);
        Assert.Equal("en", result[1].Language);
        Assert.True(result[1].HearingImpaired);
        Assert.Equal("/subtitle/3-4.zip", result[1].Reference); // API key never kept
    }

    [Fact]
    public void SubDl_DropsPackWhoseContentLacksTheEpisode()
    {
        const string json = """
        {"status":true,"subtitles":[{"release_name":"Frieren.S01E33.CR.WEB-DL.German","language":"DE","url":"/subtitle/9.zip",
          "season":1,"episode":5,"episode_from":5,"episode_end":33,"full_season":false,
          "releases":["Frieren.S01E33.CR.WEB-DL.German","[Crunchyroll] Sousou no Frieren - 05"],
          "unpack_files":[{"url":"/subtitle/9/e33.ass","name":"Frieren.S01E33.ass","season":1,"episode":33}]}]}
        """;
        var q = new SubtitleQuery { IsEpisode = true, Season = 1, Episode = 5, Languages = new[] { "de" } };
        Assert.Empty(SubDlSource.ParseSearch(json, q));
    }

    [Fact]
    public void OpenSubtitles_ParsesSearch()
    {
        const string json = """
        {"total_count":2,"data":[
          {"id":"1","type":"subtitle","attributes":{"language":"de","download_count":1200,"hearing_impaired":false,"foreign_parts_only":false,
            "ai_translated":false,"machine_translated":false,"release":"Movie.2020.1080p.BluRay.x264-GRP","moviehash_match":true,
            "uploader":{"name":"bob"},"feature_details":{"year":2020},"files":[{"file_id":4242,"file_name":"a.srt"}]}},
          {"id":"2","type":"subtitle","attributes":{"language":"en","hearing_impaired":false,"foreign_parts_only":true,
            "ai_translated":true,"release":"Movie.2020.WEB","files":[{"file_id":7,"file_name":"b.srt"}]}}
        ]}
        """;
        var result = OpenSubtitlesSource.ParseSearch(json);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].HashMatch);
        Assert.Equal("4242", result[0].Reference);
        Assert.Equal(1200, result[0].Downloads);
        Assert.True(result[1].Forced);
        Assert.True(result[1].MachineTranslated);
    }

    [Fact]
    public void OpenSubtitlesHash_MatchesReferenceAlgorithm()
    {
        var data = new byte[300_000];
        new Random(42).NextBytes(data);
        using var ms = new MemoryStream(data);

        ulong expected = (ulong)data.Length;
        for (var i = 0; i < 65536; i += 8)
        {
            expected = unchecked(expected + BitConverter.ToUInt64(data, i));
            expected = unchecked(expected + BitConverter.ToUInt64(data, data.Length - 65536 + i));
        }

        Assert.Equal(expected.ToString("x16"), OpenSubtitlesHash.Compute(ms));
    }

    [Fact]
    public void Content_ZipWithCp1252AndEpisodePick()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1252 = Encoding.GetEncoding(1252);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                using var s = zip.CreateEntry(name).Open();
                var b = cp1252.GetBytes(text);
                s.Write(b);
            }

            Add("Show.S01E04.srt", "1\r\n00:00:01,000 --> 00:00:02,000\r\nFalsche Folge\r\n");
            Add("Show.S01E05.srt", "1\r\n00:00:01,000 --> 00:00:02,000\r\nGrüße aus Köln\r\n");
        }

        var cues = SubtitleContent.Parse(ms.ToArray(), "pack.zip", 1, 5);
        Assert.NotNull(cues);
        Assert.Equal("Grüße aus Köln", cues![0].Text.Trim());
    }

    [Fact]
    public void Content_ParsesAssAndDropsDrawings()
    {
        const string ass = "[Script Info]\nTitle: x\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:01.50,0:00:03.00,Default,,0,0,0,,Hallo, {\\i1}Welt{\\i0}\\Nzweite Zeile\n" +
            "Dialogue: 0,0:00:02.00,0:00:04.00,Sign,,0,0,0,,{\\p1}m 0 0 l 100 0 100 100\n";
        var cues = SubtitleContent.Parse(Encoding.UTF8.GetBytes(ass), "x.ass", null, null);
        Assert.NotNull(cues);
        Assert.Single(cues!);
        Assert.Equal(1500, cues![0].StartMs);
        Assert.Equal("Hallo, Welt\nzweite Zeile", Jellyfin.Plugin.SubtitleForge.Common.Srt.NormalizeText(cues[0].Text));
    }
}

public class StreamMapTests
{
    [Fact]
    public void Specifier_IgnoresExternalSubtitlesInJellyfinIndex()
    {
        // Jellyfin lists external subtitles first (index 0,1); embedded streams follow (2..).
        var streams = new List<MediaBrowser.Model.Entities.MediaStream>
        {
            new() { Index = 0, Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true },
            new() { Index = 1, Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true },
            new() { Index = 2, Type = MediaBrowser.Model.Entities.MediaStreamType.Video },
            new() { Index = 3, Type = MediaBrowser.Model.Entities.MediaStreamType.Audio },
            new() { Index = 4, Type = MediaBrowser.Model.Entities.MediaStreamType.Audio },
            new() { Index = 5, Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle },
            new() { Index = 6, Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle },
        };
        Assert.Equal("0:a:1", Jellyfin.Plugin.SubtitleForge.Common.StreamMap.Specifier(streams, streams[4]));
        Assert.Equal("0:s:1", Jellyfin.Plugin.SubtitleForge.Common.StreamMap.Specifier(streams, streams[6]));
    }
}

public class CleanerTests
{
    [Fact]
    public void Clean_RemovesTypesettingDebris()
    {
        var cues = new List<Jellyfin.Plugin.SubtitleForge.Common.SubtitleCue>
        {
            new(1000, 3000, "Hello there."),
            new(5000, 5040, "M"),
            new(6000, 7000, "x"),
        };
        for (var i = 0; i < 6; i++)
        {
            cues.Add(new(9000, 11000, "Sign " + i));
        }

        var clean = Jellyfin.Plugin.SubtitleForge.Common.SubtitleCleaner.Clean(cues);
        Assert.Single(clean);
        Assert.Equal("Hello there.", clean[0].Text);
    }
}
