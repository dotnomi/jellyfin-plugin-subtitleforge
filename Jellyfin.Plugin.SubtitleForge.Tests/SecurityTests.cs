using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Regression tests for the security review findings.
/// </summary>
public class SecurityTests
{
    [Fact]
    public void RemoteSubtitleIds_AreSignedAndTamperProof()
    {
        var c = new SubtitleCandidate { Source = "SubDL", Reference = "/subtitle/123.zip", Language = "de" };
        var id = ForgeSubtitleProvider.EncodeId(c, new SubtitleQuery { Season = 1, Episode = 2 }, null);
        var decoded = ForgeSubtitleProvider.DecodeId(id);
        Assert.Equal("/subtitle/123.zip", decoded.Reference);
        Assert.Equal(2, decoded.Episode);

        // An id built by a client (old unsigned format or a forged payload with the old signature) is rejected.
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("SubDL\u001fhttps://evil.example/x\u001fde\u001f\u001f\u001f0\u001f")).TrimEnd('=');
        Assert.Throws<InvalidDataException>(() => ForgeSubtitleProvider.DecodeId(forged));
        Assert.Throws<InvalidDataException>(() => ForgeSubtitleProvider.DecodeId(forged + "." + id[(id.IndexOf('.') + 1)..]));
        Assert.Throws<InvalidDataException>(() => ForgeSubtitleProvider.DecodeId("not-an-id"));

        // An unknown language (would become a file name) is rejected even when signed.
        var bad = ForgeSubtitleProvider.EncodeId(new SubtitleCandidate { Source = "SubDL", Reference = "/x", Language = "../../x" }, new SubtitleQuery(), null);
        Assert.Throws<InvalidDataException>(() => ForgeSubtitleProvider.DecodeId(bad));
    }

    [Theory]
    [InlineData("https://dl.subdl.com/subtitle/1.zip", true)]
    [InlineData("https://subdl.com/x", true)]
    [InlineData("http://dl.subdl.com/subtitle/1.zip", false)]
    [InlineData("https://dl.subdl.com.evil.tld/x", false)]
    [InlineData("https://evilsubdl.com/x", false)]
    [InlineData("https://user@evil.tld/x", false)]
    [InlineData("https://dl.subdl.com@evil.tld/x", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative", false)]
    public void DownloadUrls_OnlyHttpsToAllowedHosts(string url, bool allowed)
    {
        if (allowed)
        {
            Assert.NotNull(SafeHttp.RequireHost(url, "subdl.com"));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => SafeHttp.RequireHost(url, "subdl.com"));
        }
    }

    [Fact]
    public void RelativeSubDlReferences_CannotEscapeTheHost()
    {
        foreach (var reference in new[] { "@evil.tld/x", ".evil.tld/x", "//evil.tld/x" })
        {
            // Either resolved as a path on dl.subdl.com, or (protocol-relative) rejected – never another host.
            var target = new Uri(new Uri("https://dl.subdl.com"), reference).ToString();
            try
            {
                Assert.Equal("dl.subdl.com", SafeHttp.RequireHost(target, "subdl.com").Host);
            }
            catch (InvalidDataException)
            {
                Assert.StartsWith("//", reference, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("ger", "ger")]
    [InlineData("de", "de")]
    [InlineData("../../../srv/x.eng", "und")]
    [InlineData("eng/../../x", "und")]
    [InlineData(null, "und")]
    [InlineData("xyz", "und")]
    public void LanguageTokens_InFileNamesAreSafe(string? code, string expected)
    {
        Assert.Equal(expected, Lang.FileToken(code));
    }

    [Fact]
    public void ZipBombs_AreNotInflated()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            for (var i = 0; i < 3; i++)
            {
                var entry = zip.CreateEntry($"bomb{i}.srt", CompressionLevel.SmallestSize);
                using var s = entry.Open();
                var zeros = new byte[1024 * 1024];
                for (var k = 0; k < 9; k++)
                {
                    s.Write(zeros);
                }
            }
        }

        Assert.True(ms.Length < 200_000);
        Assert.Null(SubtitleContent.Parse(ms.ToArray(), "pack.zip", null, null));
    }

    [Fact]
    public void LongTimingLines_AreSkippedQuickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var srt = "1\n" + new string('9', 200_000) + ":00:00,000 --> 00:00:02,000\nA\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n";
        Assert.Single(Srt.Parse(srt));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void BrokenSubtitleTimes_DoNotThrow()
    {
        var srt = "1\n99999999999999999999:00:00,000 --> 00:00:02,000\nA\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n\n3\n00:00:05,000 --> 00:00:06,000\n" + new string('<', 10_000) + "\n";
        var cues = Srt.Parse(srt);
        Assert.Single(cues);
        Assert.Equal("B", cues[0].Text);
    }

    [Fact]
    public void HugeImageSubtitleObjects_AreNotRendered()
    {
        // A PGS display set whose object claims 65535×65535 pixels.
        static byte[] Seg(byte type, byte[] data)
        {
            var h = new byte[13];
            h[0] = (byte)'P';
            h[1] = (byte)'G';
            h[10] = type;
            h[11] = (byte)(data.Length >> 8);
            h[12] = (byte)data.Length;
            var r = new byte[13 + data.Length];
            h.CopyTo(r, 0);
            data.CopyTo(r, 13);
            return r;
        }

        using var ms = new MemoryStream();
        ms.Write(Seg(0x16, new byte[] { 7, 128, 4, 56, 0x10, 0, 1, 0x80, 0, 0, 1, 0, 0, 0, 0, 0, 10, 0, 10 }));
        ms.Write(Seg(0x14, new byte[] { 0, 0, 1, 235, 128, 128, 255 }));
        ms.Write(Seg(0x15, new byte[] { 0, 0, 0, 0xC0, 0, 0, 10, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0 }));
        ms.Write(Seg(0x80, Array.Empty<byte>()));
        ms.Position = 0;
        Assert.Empty(Ocr.PgsParser.Parse(ms));
    }
}
