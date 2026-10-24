using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: immutable rules and no shared state or external resources.
public sealed class WorkerStandingRulesGoTests
{
    private static readonly string[] ExpectedGoRules =
    [
        "Name each new test file `<name>_test.go` and keep it next to the code it tests.",
        "Tests never decide pass or fail on wall-clock time; bounded waits are hang-only and name the event that did not happen.",
        "Run `go build ./...`, `go vet ./...` and `go test ./...` yourself and report each result in `tests`.",
        "Before changing any message, reason or diagnostic text, search the tests for exact-equality assertions on it, and if one exists put new information in a separate field instead of editing the asserted text.",
        "Do not reformat Go files you did not change, and do not run `go mod tidy` unless the task asks."
    ];

    [Fact]
    public void GoDeveloperRulesMatchRequiredTextAndOrder()
    {
        Assert.Equal(ExpectedGoRules, WorkerStandingRules.RulesForRole(AgentRole.Developer, ProjectStack.Go));
    }

    [Fact]
    public void GoTesterRulesAreFirstThreeDeveloperRules()
    {
        Assert.Equal(ExpectedGoRules.Take(3), WorkerStandingRules.RulesForRole(AgentRole.Tester, ProjectStack.Go));
    }

    [Fact]
    public void GoDeveloperSectionFitsExistingTextBudget()
    {
        var section = WorkerStandingRules.RenderBriefSection(AgentRole.Developer, stack: ProjectStack.Go);

        string[] expectedSection = [WorkerStandingRules.Heading, .. ExpectedGoRules.Select(rule => $"- {rule}"), string.Empty];
        Assert.Equal(expectedSection, section);
        Assert.InRange(string.Join('\n', section).Length, 1, 638);
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void DefaultStackRetainsDotnetRulesAndRendering(AgentRole role)
    {
        Assert.Equal(WorkerStandingRules.RulesForRole(role, ProjectStack.Dotnet), WorkerStandingRules.RulesForRole(role));
        Assert.Equal(WorkerStandingRules.RenderBriefSection(role, stack: ProjectStack.Dotnet), WorkerStandingRules.RenderBriefSection(role));
    }
}
