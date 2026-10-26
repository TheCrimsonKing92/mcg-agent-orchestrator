using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: attempt metadata and TRX files use a unique test-owned directory.
public sealed class OwnerConsoleFailureReasonTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedGateUsesItsAttemptTrxOrTheExplicitMissingEvidenceFallback(bool recorded)
    {
        var directory = Path.Combine(Path.GetTempPath(), "owner-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (recorded)
            {
                var attempt = Path.Combine(directory, "acceptance-gate-attempts", "11111111-full");
                Directory.CreateDirectory(attempt);
                File.WriteAllText(Path.Combine(attempt, "a1.attempt.json"), JsonSerializer.Serialize(new
                    { attemptId = "a1", testResultPaths = new[] { "failure.trx" } }));
                File.WriteAllText(Path.Combine(attempt, "a1.result.json"), """{"kind":"rejected","acceptance":{"passed":false}}""");
                File.WriteAllText(Path.Combine(attempt, "a1.exit.txt"), "1");
                File.WriteAllText(Path.Combine(attempt, "failure.trx"), """
                    <TestRun><Results><UnitTestResult testName="Tests.AcceptanceLaneMembershipTests.ExistingRunnableClassesKeepTheirSubstringLaneMembership" outcome="Failed">
                    <Output><ErrorInfo><Message>found 1 offending test class(es)
                    second line must stay out of the console</Message></ErrorInfo></Output>
                    </UnitTestResult></Results></TestRun>
                    """);
            }
            var reader = new OwnerGateFailureEvidence(directory);
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene(evidence: reader.Read);
            scene.AddGoals();
            await scene.Render([new(scene.Harness.Clock.GetUtcNow(), "acceptance", "11111111",
                "ACCEPTANCE goal=11111111 result=failed attempt=a1")]);
            var line = Assert.Single(scene.View.ActivityLines);
            if (recorded)
            {
                Assert.Contains("AcceptanceLaneMembershipTests.ExistingRunnableClassesKeepTheirSubstringLaneMembership: found 1 offending test class(es)", line);
                Assert.DoesNotContain("second line", line);
                Assert.DoesNotContain("has not been recorded", line);
                await scene.View.HandleKeyAsync(Key.Tab);
                await scene.View.HandleKeyAsync(Key.Tab);
                await scene.View.HandleKeyAsync(Key.Enter);
                Assert.Contains("Why: AcceptanceLaneMembershipTests.ExistingRunnableClassesKeepTheirSubstringLaneMembership: found 1 offending test class(es)", scene.Dialogs.Messages[^1].Text);
            }
            else Assert.Contains("the failure reason has not been recorded", line);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PlannerContractRejectionSurvivesLifecycleParsingRoleEnrichmentAndRendering()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        var line = JsonSerializer.Serialize(new { timestamp = time, eventType = "TaskFailed", taskId = "p1",
            message = "Dispatch failed: Planner output contract failed. Stdout plan reason: target citation '.config/dotnet-tools.json' does not exist and is not marked as a new file; retry required. Retry Planner for contract repair. Command: secret command" });
        Assert.True(OwnerGoalLifecycleEvent.TryParse(line, "11111111", out var parsed));
        var item = OwnerGoalLifecycleEvent.WithRole(parsed!, new Dictionary<string, Mcg.AgentOrchestrator.Core.AgentRole>
            { ["p1"] = Mcg.AgentOrchestrator.Core.AgentRole.Planner });
        await scene.Render([item]);
        var activity = Assert.Single(scene.View.ActivityLines);
        Assert.Contains("Planner sent Search back: plan rejected: cited a file that does not exist: .config/dotnet-tools.json", activity);
        Assert.DoesNotContain("secret command", activity);
        Assert.DoesNotContain("the worker could not finish", activity);
    }
}
