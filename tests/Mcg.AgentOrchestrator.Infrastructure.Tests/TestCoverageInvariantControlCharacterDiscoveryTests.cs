using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestCoverageInvariantControlCharacterDiscoveryTests
{
    private const string RawControlDiscovery = "{\"schemaVersion\":1,\"tests\":[{\"uid\":\"u1\",\"displayName\":\"Row(x: \\\" \t1. a. \u0007\\\")\"}]}";
    private const string EscapedDiscovery = "{\"schemaVersion\":1,\"tests\":[{\"uid\":\"u2\",\"displayName\":\"Row(x: \\\"a\\\\tb\\\")\"}]}";

    [Xunit.Fact]
    public void ParseDiscovery_AcceptsRawTabAndBel()
    {
        var discovery = TestCoverageInvariant.ParseDiscovery(RawControlDiscovery);

        Assert.Contains("1. a.", Assert.Single(discovery.Tests), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ParseDiscovery_PreservesAlreadyEscapedText()
    {
        var discovery = TestCoverageInvariant.ParseDiscovery(EscapedDiscovery);

        Assert.Contains("a\\tb", Assert.Single(discovery.Tests), StringComparison.Ordinal);
        Assert.Equal(EscapedDiscovery, TestCoverageInvariantDiscoveryJson.EscapeRawControlCharacters(EscapedDiscovery));
    }

    [Xunit.Fact]
    public void EscapeRawControlCharacters_PreservesDecodedDisplayNames()
    {
        using var controlDocument = JsonDocument.Parse(
            TestCoverageInvariantDiscoveryJson.EscapeRawControlCharacters(RawControlDiscovery));
        Assert.Equal("Row(x: \" \t1. a. \u0007\")", DisplayName(controlDocument));

        const string newlineDiscovery = "{\"schemaVersion\":1,\"tests\":[{\"uid\":\"u3\",\"displayName\":\"Row(x: \\\"a\nb\\\")\"}]}";
        using var newlineDocument = JsonDocument.Parse(
            TestCoverageInvariantDiscoveryJson.EscapeRawControlCharacters(newlineDiscovery));
        Assert.Equal("Row(x: \"a\nb\")", DisplayName(newlineDocument));
    }

    [Xunit.Fact]
    public void EscapeRawControlCharacters_RoundTripsEveryControlCharacter()
    {
        for (var code = 0; code < 0x20; code++)
        {
            var value = ((char)code).ToString();
            var json = "[\n\t\"" + value + "\"\r\n]";
            var escaped = TestCoverageInvariantDiscoveryJson.EscapeRawControlCharacters(json);
            using var document = JsonDocument.Parse(escaped);

            Assert.Equal(value, document.RootElement[0].GetString());
            Assert.StartsWith("[\n\t\"", escaped, StringComparison.Ordinal);
            Assert.EndsWith("\"\r\n]", escaped, StringComparison.Ordinal);
        }
    }

    private static string? DisplayName(JsonDocument document) =>
        document.RootElement.GetProperty("tests")[0].GetProperty("displayName").GetString();
}
