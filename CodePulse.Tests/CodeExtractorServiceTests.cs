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
    public void ExtractCandidates_PrefixOnlySkipsGenericCodeInJoinedText()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "FZK",
            Prefixes = ["KOLFZK"],
            PrefixOnly = true
        };

        var candidates = service.ExtractCandidates(channel, "6XWCPEU6N3KOLFZK4NYYKE");

        Assert.DoesNotContain(candidates, candidate => candidate.Value == "6XWCPEU6N3");
        Assert.Contains(candidates, candidate => candidate.Value == "KOLFZK4NYYKE");
    }

    [Fact]
    public void ExtractCandidates_PrefixOnlyDoesNotUseDefaultPrefixes()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "Strict",
            Prefixes = ["KOLFZK"],
            PrefixOnly = true
        };

        var candidates = service.ExtractCandidates(channel, "KOL123456");

        Assert.DoesNotContain(candidates, candidate => candidate.Value == "KOL123456");
    }

    [Fact]
    public void ExtractCandidates_PrefixOnlyUsesExplicitShortPrefix()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "Strict",
            Prefixes = ["KOL"],
            PrefixOnly = true
        };

        var candidates = service.ExtractCandidates(channel, "KOL123456");

        Assert.Contains(candidates, candidate => candidate.Value == "KOL123456");
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

    [Fact]
    public void ExtractCandidates_ConvertsThaiLetterNameAWhenItTouchesPrefixCode()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "KOL ขึ้นจอ",
            Prefixes = ["KOLCHICK"]
        };

        var candidates = service.ExtractCandidates(
            channel,
            "KOLCHICKTTW6Rเอ     ภารกิจต่อไป  5.6 พันไลค์ ลุยยย");

        Assert.Contains(candidates, candidate => candidate.Value == "KOLCHICKTTW6RA");
    }

    [Theory]
    [InlineData("เอ", "A")]
    [InlineData("บี", "B")]
    [InlineData("ซี", "C")]
    [InlineData("ดี", "D")]
    [InlineData("อี", "E")]
    [InlineData("เอฟ", "F")]
    [InlineData("จี", "G")]
    [InlineData("เอช", "H")]
    [InlineData("ไอ", "I")]
    [InlineData("เจ", "J")]
    [InlineData("เค", "K")]
    [InlineData("แอล", "L")]
    [InlineData("เอ็ม", "M")]
    [InlineData("เอ็น", "N")]
    [InlineData("โอ", "O")]
    [InlineData("พี", "P")]
    [InlineData("คิว", "Q")]
    [InlineData("อาร์", "R")]
    [InlineData("เอส", "S")]
    [InlineData("ที", "T")]
    [InlineData("ยู", "U")]
    [InlineData("วี", "V")]
    [InlineData("ดับเบิลยู", "W")]
    [InlineData("เอ็กซ์", "X")]
    [InlineData("วาย", "Y")]
    [InlineData("แซด", "Z")]
    [InlineData("ศูนย์", "0")]
    [InlineData("หนึ่ง", "1")]
    [InlineData("สอง", "2")]
    [InlineData("สาม", "3")]
    [InlineData("สี่", "4")]
    [InlineData("ห้า", "5")]
    [InlineData("หก", "6")]
    [InlineData("เจ็ด", "7")]
    [InlineData("แปด", "8")]
    [InlineData("เก้า", "9")]
    public void ExtractCandidates_ConvertsThaiCodeAliasesWhenTheyTouchGenericCode(
        string thaiAlias,
        string expectedCharacter)
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "Generic"
        };

        var candidates = service.ExtractCandidates(channel, $"12ABCD789{thaiAlias}");

        Assert.Contains(candidates, candidate => candidate.Value == $"12ABCD789{expectedCharacter}");
    }

    [Fact]
    public void ExtractCandidates_DoesNotConvertThaiCodeAliasesWhenDisabled()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "KOL ขึ้นจอ",
            Prefixes = ["KOLCHICK"]
        };

        var candidates = service.ExtractCandidates(
            channel,
            "KOLCHICKTTW6Rเอ",
            normalizeThaiCodeAliases: false);

        Assert.DoesNotContain(candidates, candidate => candidate.Value == "KOLCHICKTTW6RA");
    }

    [Theory]
    [InlineData("KOLCHICKTTW6R เอ")]
    [InlineData("KOLCHICKTTW6R-เอ")]
    [InlineData("KOLCHICKTTW6R/เอ")]
    public void ExtractCandidates_BridgesSeparatedThaiAliasWhenItCompletesPrefixCode(string message)
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "KOL ขึ้นจอ",
            Prefixes = ["KOLCHICK"]
        };

        var candidates = service.ExtractCandidates(channel, message);

        Assert.Contains(candidates, candidate => candidate.Value == "KOLCHICKTTW6RA");
    }

    [Fact]
    public void ExtractCandidates_BridgesThaiAliasInTheMiddleWhenItCompletesPrefixCode()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "KOL ขึ้นจอ",
            Prefixes = ["KOLCHICK"]
        };

        var candidates = service.ExtractCandidates(channel, "KOLCHICKTT ดับเบิลยู 6RA");

        Assert.Contains(candidates, candidate => candidate.Value == "KOLCHICKTTW6RA");
    }

    [Fact]
    public void ExtractCandidates_BridgesSeparatedThaiAliasWhenItCompletesGenericCode()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "Generic"
        };

        var candidates = service.ExtractCandidates(channel, "12ABCD789 เอ");

        Assert.Contains(candidates, candidate => candidate.Value == "12ABCD789A");
    }

    [Fact]
    public void ExtractCandidates_DoesNotBridgeSeparatedThaiAliasWhenItWouldNotFormCode()
    {
        var service = new CodeExtractorService();
        var channel = new ChannelProfile
        {
            Name = "KOL ขึ้นจอ",
            Prefixes = ["KOLCHICK"]
        };

        var candidates = service.ExtractCandidates(channel, "KOLCHICKTT เอ ภารกิจต่อไป");

        Assert.DoesNotContain(candidates, candidate => candidate.Value == "KOLCHICKTTA");
    }
}
