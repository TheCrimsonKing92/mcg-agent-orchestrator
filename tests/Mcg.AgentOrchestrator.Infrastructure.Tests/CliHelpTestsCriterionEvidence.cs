using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliHelpTestsCriterionEvidence
{
    [Xunit.Theory]
    [Xunit.InlineData("criterion-evidence-map")]
    [Xunit.InlineData("criterion-evidence-record")]
    public void PublicEvidenceCommandsAcceptGoalAndIntentAttribution(string command)
    {
        CliCommandHelp.ThrowIfInvalidFlags([command, "--goal", "goal-prefix",
            "--idempotency-key", "operator-replay-key", "--operator-actor", "operator"]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("criterion-evidence-map")]
    [Xunit.InlineData("criterion-evidence-record")]
    public void PublicEvidenceCommandsStillRejectUnknownFlags(string command)
    {
        var error = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags([command, "--waive-evidence"]));
        Xunit.Assert.Contains("Unknown option '--waive-evidence'", error.Message, StringComparison.Ordinal);
    }
}
