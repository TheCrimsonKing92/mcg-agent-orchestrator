using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliHandledVerbRegistryTests
{
    [Theory(DisplayName = "Handled verbs include aliases and match case insensitively")]
    [InlineData("goal")]
    [InlineData("recover")]
    [InlineData("revise")]
    [InlineData("goal-depends")]
    [InlineData("cancel-goal")]
    [InlineData("adjudicate")]
    [InlineData("conduct")]
    [InlineData("backlog-list")]
    [InlineData("help")]
    [InlineData("operator-commands")]
    [InlineData("GOALS")]
    [InlineData("GOAL")]
    [InlineData("goal-refinement-run")]
    [InlineData("goal-delivery-retry")]
    [InlineData("approve-policy-change")]
    [InlineData("cancel-dispatch")]
    [InlineData("criterion-evidence-map")]
    [InlineData("criterion-evidence-record")]
    [InlineData("criterion-evidence-repair")]
    [InlineData("operator-intent-status")]
    [InlineData("model-outcomes")]
    [InlineData("experiment-apply-flag")]
    [InlineData("experiment-extend")]
    [InlineData("stop")]
    public void HandledVerbsAreRecognized(string verb)
    {
        Assert.True(CliHandledVerbRegistry.IsHandled(verb), verb);
    }

    [Theory(DisplayName = "Unknown operator verbs are rejected")]
    [InlineData("stauts")]
    [InlineData("backlog")]
    [InlineData("goal-show")]
    [InlineData("goal-card")]
    [InlineData("goal-objective")]
    public void UnknownVerbsAreNotRecognized(string verb)
    {
        Assert.False(CliHandledVerbRegistry.IsHandled(verb), verb);
    }

    [Fact(DisplayName = "Every lowercase handler case label is covered by the registry")]
    public void EveryHandlerCaseLabelIsRecognized()
    {
        var cliDirectory = Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Cli");
        var verbs = Directory.EnumerateFiles(cliDirectory, "CliCommandHandlers*.cs")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\bcase\s+""([a-z-]+)""\s*:")
                .Cast<Match>().Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(verb => verb, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(verbs);

        var missing = verbs.Where(verb => !CliHandledVerbRegistry.IsHandled(verb)).ToArray();
        Assert.True(missing.Length == 0, $"Handler verbs missing from registry: {string.Join(", ", missing)}");
    }
}
