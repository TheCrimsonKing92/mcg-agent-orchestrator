using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

// Parallel-safe: CaptureConsole routes output through AsyncLocal; fixtures have no shared state.
public sealed class AutonomyPoliciesTextViewCharacterizationTests
{
    // Derived from ConsoleViews.AutonomyPolicies.cs:7-15 and AutonomyPolicy.cs:41-83 before the extraction.
    [Fact]
    public void Print_AllPolicies_PreservesExactText()
    {
        var actual = CaptureConsole(AutonomyPoliciesTextView.PrintAutonomyPolicies);
        var expected = string.Join(Environment.NewLine,
            "Default autonomy policy: supervised-auto",
            "Policy observe: Read and report state only; do not start workers, invoke models, verify, merge, clean up, or edit operator records.",
            "  starts=False; model=False; refresh=True; retry=False; failover=False; build-test=False; acceptance=False; cleanup=False; backlog-log=False",
            "Policy safe-auto: Allow reversible goal progress such as starts, refreshes, retries, failover, and verification; block merge, cleanup, and backlog/log edits.",
            "  starts=True; model=True; refresh=True; retry=True; failover=True; build-test=True; acceptance=False; cleanup=False; backlog-log=False",
            "Policy supervised-auto: Allow the full supervised lifecycle after explicit command/API confirmations, including verification, acceptance, cleanup, and operator record edits.",
            "  starts=True; model=True; refresh=True; retry=True; failover=True; build-test=True; acceptance=True; cleanup=True; backlog-log=True",
            "");
        Assert.Equal(expected, actual);
    }
}
