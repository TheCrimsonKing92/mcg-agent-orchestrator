using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: validation has no I/O or mutable state.
public sealed class OwnerAnswerValidatorTests
{
    public static IEnumerable<object[]> TimeoutCases() =>
        new[] { "engine/timeouts/defaultMinutes", "engine/timeouts/buildServerShutdownMinutes",
            "engine/timeouts/discoveryMinutes" }.SelectMany(key =>
            new[] { "1", "25", "1440" }.Select(value => new object[] { key, value }));

    [Theory]
    [MemberData(nameof(TimeoutCases))]
    public void Validate_AllowedTimeout_ReturnsInvariantValue(string key, string value) =>
        Assert.Equal(value, OwnerAnswerValidator.Validate(key, value));

    [Fact]
    public void Validate_LeadingZeros_NormalizesTimeout() =>
        Assert.Equal("25", OwnerAnswerValidator.Validate("engine/timeouts/defaultMinutes", "0025"));

    [Theory]
    [InlineData("10.0.100")]
    [InlineData("9.0.300-preview.1")]
    [InlineData("8.0")]
    [InlineData("8.0.1.2")]
    public void Validate_AllowedSdk_ReturnsVersion(string value) =>
        Assert.Equal(value, OwnerAnswerValidator.Validate("environment/dotnet-sdk", value));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1441")]
    [InlineData("2.5")]
    [InlineData("10 ")]
    [InlineData("1e2")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+5")]
    [InlineData("1 0")]
    [InlineData("999999999999")]
    public void Validate_InvalidTimeout_NamesKeyAndRule(string value)
    {
        foreach (var key in new[] { "engine/timeouts/defaultMinutes",
            "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes" })
        {
            var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, value));
            Assert.Contains(key, error.Message);
            Assert.Contains("positive integer from 1 to 1440", error.Message);
        }
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("10.*")]
    [InlineData("^9.0")]
    [InlineData("v10.0.100")]
    [InlineData("10")]
    [InlineData("")]
    [InlineData("10.0.0.0.0")]
    [InlineData("10.0 ")]
    public void Validate_InvalidSdk_NamesKeyAndRule(string value)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            OwnerAnswerValidator.Validate("environment/dotnet-sdk", value));
        Assert.Contains("environment/dotnet-sdk", error.Message);
        Assert.Contains("two to four dot-separated numeric version parts", error.Message);
    }

    [Theory]
    [InlineData("integration-branch")]
    [InlineData("units/x/isTest")]
    public void Validate_UnknownKey_ListsSupportedKeys(string key)
    {
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, "1"));
        Assert.Contains(key + " has no answer rule in this slice", error.Message);
        foreach (var known in new[] { "engine/timeouts/defaultMinutes",
            "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes", "environment/dotnet-sdk" })
            Assert.Contains(known, error.Message);
    }
}
