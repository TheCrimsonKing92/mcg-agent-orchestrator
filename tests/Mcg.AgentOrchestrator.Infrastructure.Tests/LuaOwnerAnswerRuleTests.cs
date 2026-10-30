using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: validation is pure and does not mutate shared state.
public sealed class LuaOwnerAnswerRuleTests
{
    [Theory]
    [InlineData("environment/lua-test-framework", "yes", "yes")]
    [InlineData("environment/lua-test-framework", "YES", "yes")]
    [InlineData("environment/lua-test-framework", "no", "no")]
    [InlineData("environment/lua-test-framework", "No", "no")]
    [InlineData("environment/lua-luarocks-install", "yes", "yes")]
    [InlineData("environment/lua-luarocks-install", "YeS", "yes")]
    [InlineData("environment/lua-luarocks-install", "no", "no")]
    [InlineData("environment/lua-luarocks-install", "NO", "no")]
    public void Validate_LuaAnswers_NormalizesExplicitYesOrNo(string key, string value, string expected)
    {
        Assert.Equal(expected, OwnerAnswerValidator.Validate(key, value));
    }

    [Theory]
    [InlineData("environment/lua-test-framework", "busted")]
    [InlineData("environment/lua-test-framework", "")]
    [InlineData("environment/lua-test-framework", " yes")]
    [InlineData("environment/lua-test-framework", "yes ")]
    [InlineData("environment/lua-luarocks-install", "busted")]
    [InlineData("environment/lua-luarocks-install", "")]
    [InlineData("environment/lua-luarocks-install", " yes")]
    [InlineData("environment/lua-luarocks-install", "yes ")]
    [InlineData("environment/lua-luarocks-install", "true")]
    public void Validate_OtherAnswers_RejectsWithoutGrantingConsent(string key, string value)
    {
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, value));

        Assert.Contains("requires yes or no", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has no answer rule", error.Message, StringComparison.Ordinal);
    }
}
