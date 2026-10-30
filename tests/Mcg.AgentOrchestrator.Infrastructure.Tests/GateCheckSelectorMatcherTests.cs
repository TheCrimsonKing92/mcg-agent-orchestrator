using Mcg.AgentOrchestrator.App.Orchestration;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: matcher inputs are immutable values local to each test.
public sealed class GateCheckSelectorMatcherTests
{
    [Theory]
    [InlineData("npx")]
    [InlineData("pnpm")]
    [InlineData("yarn")]
    [InlineData("bunx")]
    public void LeadingRunnerAndOuterWhitespace_MatchInEitherOperand(string runner)
    {
        var check = new AcceptanceManifestCheck
        {
            Name = "type check", Command = runner, Arguments = ["tsc", "--noEmit"]
        };

        Assert.True(GateCheckSelectorMatcher.Matches(" \ttsc --noEmit\r\n", check));
        Assert.True(GateCheckSelectorMatcher.Matches($" {runner}\ttsc --noEmit ",
            new AcceptanceManifestCheck { Name = "type check", Command = "tsc", Arguments = ["--noEmit"] }));
        Assert.True(GateCheckSelectorMatcher.Matches("tsc --noEmit",
            new AcceptanceManifestCheck { Name = $" {runner} tsc --noEmit " }));
    }

    [Theory]
    [InlineData("tsc --noEmit --strict")]
    [InlineData("eslint .")]
    [InlineData("tsc --noemit")]
    [InlineData("TSC --noEmit")]
    [InlineData("tsc  --noEmit")]
    [InlineData("npx --yes tsc --noEmit")]
    [InlineData("npx npx tsc --noEmit")]
    [InlineData("NPX tsc --noEmit")]
    [InlineData("npx-tsc --noEmit")]
    [InlineData("tsc --noEmit && eslint .")]
    [InlineData("npx")]
    [InlineData(" ")]
    public void DifferentCommandOrArguments_DoNotMatch(string selector)
    {
        Assert.False(GateCheckSelectorMatcher.Matches(selector,
            GateTestObligationCheckOutcomeTests.TypeCheck()));
    }

    [Fact]
    public void CompleteName_MatchesIndependentlyOfCommand()
    {
        var check = new AcceptanceManifestCheck { Name = "type check", Command = "npx", Arguments = ["tsc", "--noEmit"] };

        Assert.True(GateCheckSelectorMatcher.Matches(" type check ", check));
        Assert.False(GateCheckSelectorMatcher.Matches("type", check));
        Assert.False(GateCheckSelectorMatcher.Matches("Type check", check));
    }

    [Theory]
    [InlineData("dotnet-test", "dotnet", "test", true)]
    [InlineData("DOTNET-TEST", "dotnet", "test", true)]
    [InlineData("crosswalk-test", "crosswalk", "test", true)]
    [InlineData("command", "go", "test", true)]
    [InlineData("command", "go", "build", false)]
    [InlineData("command", "npx", "tsc", false)]
    public void ManifestDeclaration_DeterminesTestResultProduction(
        string type, string command, string argument, bool expected)
    {
        var check = new AcceptanceManifestCheck { Type = type, Command = command, Arguments = [argument] };

        Assert.Equal(expected, GateCheckSelectorMatcher.IsTestResultProducing(check));
    }

    [Theory]
    [InlineData(true, false, "Failed")]
    [InlineData(false, false, "Failed")]
    [InlineData(false, null, "Pending")]
    [InlineData(false, true, "Satisfied")]
    [InlineData(true, null, "Satisfied")]
    public void MultipleMatchingChecks_RequireEveryCheckToPass(
        bool inferredPass, bool? secondOutcome, string expected)
    {
        var checks = new[]
        {
            new AcceptanceManifestCheck { Name = "first", Command = "tsc", Arguments = ["--noEmit"] },
            new AcceptanceManifestCheck { Name = "second", Command = "npx", Arguments = ["tsc", "--noEmit"] }
        };
        var outcomes = new Dictionary<string, bool>(StringComparer.Ordinal) { ["first"] = true };
        if (secondOutcome.HasValue) outcomes["second"] = secondOutcome.Value;

        Assert.Equal(expected, GateCheckSelectorMatcher.Grade("tsc --noEmit",
            new(checks, outcomes, inferredPass)).ToString());
    }

    [Fact]
    public void UnrelatedFailedCheck_DoesNotOverrideExplicitPass()
    {
        var check = GateTestObligationCheckOutcomeTests.TypeCheck();
        var outcomes = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [check.Name] = true, ["eslint ."] = false
        };

        Assert.Equal(GateCheckSelectorMatcher.Outcome.Satisfied,
            GateCheckSelectorMatcher.Grade("tsc --noEmit", new([check], outcomes, false)));
    }
}
