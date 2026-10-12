using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: validation has no mutable inputs or external resources.
public sealed class OwnerAnswerCheckTimeoutRuleTests
{
    private const string Key = "commands/tests/A/A.csproj/timeoutMinutes";

    [Theory]
    [InlineData("025", "25")]
    [InlineData("1", "1")]
    [InlineData("1440", "1440")]
    public void Validate_UnitTimeout_NormalizesMinutes(string value, string expected) =>
        Assert.Equal(expected, OwnerAnswerValidator.Validate(Key, value));

    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("1.5")]
    [InlineData("-3")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("")]
    public void Validate_InvalidMinutes_NamesKey(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(Key, value));
        Assert.Contains(Key, error.Message);
        Assert.DoesNotContain("has no answer rule", error.Message);
    }

    [Fact]
    public void Validate_EmptyUnit_PreservesUnknownKeyRefusal()
    {
        const string key = "commands//timeoutMinutes";
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, "25"));
        Assert.Contains(key + " has no answer rule in this slice", error.Message);
        Assert.Contains(OwnerAnswerValidator.DefaultTimeoutKey, error.Message);
        Assert.Contains(OwnerAnswerValidator.BuildServerShutdownKey, error.Message);
        Assert.Contains(OwnerAnswerValidator.DiscoveryKey, error.Message);
        Assert.Contains(OwnerAnswerValidator.DotnetSdkKey, error.Message);
        Assert.Contains("commands/<unit>/test", error.Message);
        Assert.Contains("commands/<unit>/timeoutMinutes", error.Message);
    }
}
