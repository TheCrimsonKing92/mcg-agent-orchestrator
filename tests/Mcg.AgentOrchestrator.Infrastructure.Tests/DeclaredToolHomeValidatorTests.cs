using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its filesystem fixture; no process environment is changed.
public sealed class DeclaredToolHomeValidatorTests
{
    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("USERPROFILE")]
    [InlineData("APPDATA")]
    [InlineData("LOCALAPPDATA")]
    [InlineData("TEMP")]
    [InlineData("TMP")]
    [InlineData("GIT_CONFIG_GLOBAL")]
    [InlineData("MCG_TOOL_HOME")]
    [InlineData("rustup_home")]
    public void Validate_ReservedOrLowercaseName_RefusesDeclaration(string variable)
    {
        using var directory = new ProjectOnboardingFixture("no-solution");

        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(
            "environment/tool-home/" + variable, directory.Root));

        Assert.Contains(variable, error.Message);
    }

    [Fact]
    public void Validate_ProviderHome_RefusesEverySharedReservedName()
    {
        using var directory = new ProjectOnboardingFixture("no-solution");
        Assert.Contains("CLAUDE_CONFIG_DIR", WorkerProviderHomeEnvironment.Names);
        Assert.NotEmpty(WorkerProviderHomeEnvironment.Names);
        foreach (var variable in WorkerProviderHomeEnvironment.Names)
        {
            var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(
                "environment/tool-home/" + variable, directory.Root));
            Assert.Contains(variable, error.Message);
            Assert.Contains("reserved", error.Message);
        }
    }

    [Fact]
    public void Validate_RelativeOrMissingDirectory_RefusesDeclaration()
    {
        using var directory = new ProjectOnboardingFixture("no-solution");
        var missing = Path.Combine(directory.Root, "missing");
        Assert.False(Directory.Exists(missing));
        foreach (var value in new[] { "relative-home", missing })
        {
            var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(
                "environment/tool-home/RUSTUP_HOME", value));
            Assert.Contains("RUSTUP_HOME", error.Message);
            Assert.Contains(value, error.Message);
            Assert.Contains("existing absolute directory", error.Message);
        }
    }

    [Fact]
    public void Validate_ExistingAbsoluteDirectory_PreservesAnswerExactly()
    {
        using var directory = new ProjectOnboardingFixture("no-solution");
        var value = Path.Combine(directory.Root, ".");
        Assert.True(Path.IsPathFullyQualified(value));
        Assert.True(Directory.Exists(value));

        Assert.Equal(value, OwnerAnswerValidator.Validate("environment/tool-home/RUSTUP_HOME", value));
    }
}
