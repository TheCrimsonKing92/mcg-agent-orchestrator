using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: private workspace/database, injected owner and boot probes, local writers.
public sealed class ConductorStatusLandingBlockerTests
{
    [Fact]
    public async Task Status_VerifiedBlocker_PrintsOneExactHumanLineAndLeavesJsonUnchanged()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal(new GoalId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), "held verified goal");
        kernel.CreateGoal(new GoalId("cccccccccccccccccccccccccccccccc"), "goal without blocker");
        first.ObserveLandingBlocker(new GoalLandingBlocker("landing-completion/criterion-evidence-hold",
            "operator evidence outstanding", "evidence:operator",
            new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(-5)),
            "evidence-owner", "criterion-evidence-record"));
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == first.Id.Value
                ? goal with { Status = GoalStatus.Verified } : goal).ToArray()
        });
        await InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath)
            .SaveAsync(kernel);

        string Status(params string[] arguments)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, CliConductorCommand.Run(arguments, fixture.Workspace,
                lockProbe: new ConductorVerbStartTests.FixedProbe(null),
                bootTime: () => DateTimeOffset.UnixEpoch, mainCommit: () => "def456",
                output: output, error: error));
            Assert.Equal("", error.ToString());
            return output.ToString();
        }

        Assert.Equal(string.Join(Environment.NewLine,
            "Conductor: stopped",
            "Since: unavailable",
            "Supervisor build: unavailable",
            "Generation: unavailable (no supervisor build recorded)",
            "Latest tick: unavailable",
            "Non-terminal goals: 2",
            "  aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa | Verified | role=Researcher",
            "    landing-blocker: landing-completion/criterion-evidence-hold since=2026-10-24T09:00:00.0000000-05:00 next=evidence-owner verb=criterion-evidence-record detail=operator evidence outstanding",
            "  cccccccccccccccccccccccccccccccc | Draft | role=Researcher",
            "Pending clarifications: 0; human waits: 0",
            "Most recent landing: unavailable",
            "Worker adoption: unavailable",
            "Pending operator intents: unavailable (intent database missing)",
            "Owner directives: 0 active", ""), Status("conductor", "status"));
        Assert.DoesNotContain("landingBlocker", Status("conductor", "status", "--json"),
            StringComparison.OrdinalIgnoreCase);
    }
}
