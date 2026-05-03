using CodePulse.Models;
using CodePulse.Services;
using Xunit;

namespace CodePulse.Tests;

public sealed class CodeExtractorServiceTests
{
    [Fact]
    public void ExtractCandidates_FindsEmbeddedPrefixCodeInsideJoinedOcrToken()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "FZK",
            Prefixes = ["KOLFZK"]
        };

        var candidates = service.ExtractCandidates(channel, "6XWCPEU6N3KOLFZK4NYYKE");

        Assert.Contains(candidates, candidate => candidate.Value == "KOLFZK4NYYKE");
    }

    [Fact]
    public void ExtractCandidates_SplitsJoinedGenericAndPrefixCodes()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "FZK",
            Prefixes = ["KOLFZK"]
        };

        var candidates = service.ExtractCandidates(channel, "6XWCPEU6N3KOLFZK4NYYKE");

        Assert.Collection(
            candidates.Take(2),
            candidate => Assert.Equal("6XWCPEU6N3", candidate.Value),
            candidate => Assert.Equal("KOLFZK4NYYKE", candidate.Value));
    }

    [Fact]
    public void ExtractCandidates_DoesNotCutLongPrefixCodeWithShorterDefaultPrefix()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "FZK",
            Prefixes = ["KOLFZK"]
        };

        var candidates = service.ExtractCandidates(channel, "6XWCPEU6N3KOLFZK4NYYKE");

        Assert.DoesNotContain(candidates, candidate => candidate.Value == "KOLFZK4NY");
    }

    [Fact]
    public void ExtractCandidates_FindsKitNgayLongPrefixCodeWithTrailingPunctuation()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "กิต งายย",
            Prefixes = ["KOLKRITNGI", "BRANDKRIT", "KOL"]
        };

        var candidates = service.ExtractCandidates(channel, "KOLKRITNGI4AF4XX.");

        Assert.Contains(candidates, candidate => candidate.Value == "KOLKRITNGI4AF4XX");
    }
}
