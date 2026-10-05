using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceAttemptPriorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GateAndFocusedEvidenceLaunchCarryPriorityPolicyToChild(bool focusedEvidence)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-attempt-priority", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var enabled = Launch(root, focusedEvidence, enabled: true);
            var disabled = Launch(root, focusedEvidence, enabled: false);

            Assert.Equal(focusedEvidence
                    ? ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind
                    : ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
                enabled.Kind);
            Assert.True(ConductorParallelAcceptanceAttemptCoordinator.ResolveAttemptPolicy(enabled)
                .AcceptanceAttemptBelowNormalPriority);
            Assert.False(ConductorParallelAcceptanceAttemptCoordinator.ResolveAttemptPolicy(disabled)
                .AcceptanceAttemptBelowNormalPriority);

            var onStart = ConductorParallelAcceptanceAttemptCoordinator.BuildOwnedProcessStartInfo(
                enabled, null, "dotnet", ["Mcg.AgentOrchestrator.App.dll"]);
            var offStart = ConductorParallelAcceptanceAttemptCoordinator.BuildOwnedProcessStartInfo(
                disabled, null, "dotnet", ["Mcg.AgentOrchestrator.App.dll"]);
            Assert.Equal(onStart.FileName, offStart.FileName);
            Assert.Equal(onStart.WorkingDirectory, offStart.WorkingDirectory);
            Assert.Equal(onStart.CreateNoWindow, offStart.CreateNoWindow);
            Assert.Equal(onStart.RedirectStandardInput, offStart.RedirectStandardInput);
            Assert.Equal(onStart.RedirectStandardOutput, offStart.RedirectStandardOutput);
            Assert.Equal(onStart.RedirectStandardError, offStart.RedirectStandardError);
            Assert.Equal(onStart.Environment.OrderBy(pair => pair.Key), offStart.Environment.OrderBy(pair => pair.Key));
            Assert.Equal(onStart.ArgumentList.Take(2), offStart.ArgumentList.Take(2));
            Assert.Equal(enabled.MetadataPath, onStart.ArgumentList[2]);
            Assert.Equal(disabled.MetadataPath, offStart.ArgumentList[2]);

            var lowerCalls = 0;
            ConductorParallelAcceptanceAttemptCoordinator.ApplyOwnedProcessPriority(enabled, () =>
            {
                lowerCalls++;
                return false;
            });
            ConductorParallelAcceptanceAttemptCoordinator.ApplyOwnedProcessPriority(disabled, () =>
            {
                lowerCalls++;
                return true;
            });
            Assert.Equal(1, lowerCalls);

            // The injected request throws before the child takes its writer lease or redirects console.
            var originalOutput = Console.Out;
            var result = ConductorParallelAcceptanceAttemptCoordinator.RunOwnedProcess(
                enabled.MetadataPath,
                lowerCurrentProcessPriority: () =>
                {
                    lowerCalls++;
                    throw new PriorityRequestReachedException();
                });
            Assert.Equal(1, result);
            Assert.Equal(2, lowerCalls);
            Assert.Same(originalOutput, Console.Out);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PriorityPolicyDefaultsOnAndRejectsMalformedValue()
    {
        var json = ConductorAutonomyPolicy.Conservative.ToJson();
        Assert.True(ConductorAutonomyPolicy.ParseJson(
            json.Replace("  \"acceptanceAttemptBelowNormalPriority\": true,", "", StringComparison.Ordinal))
            .AcceptanceAttemptBelowNormalPriority);
        Assert.False(ConductorAutonomyPolicy.ParseJson((ConductorAutonomyPolicy.Conservative with
        {
            AcceptanceAttemptBelowNormalPriority = false
        }).ToJson()).AcceptanceAttemptBelowNormalPriority);
        var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(
            json.Replace("\"acceptanceAttemptBelowNormalPriority\": true",
                "\"acceptanceAttemptBelowNormalPriority\": 1", StringComparison.Ordinal), "test-policy.json"));
        Assert.Contains("test-policy.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("acceptanceAttemptBelowNormalPriority must be a boolean", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerLaunchSourcesDoNotRequestBelowNormalPriority()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        string[] workerLaunchPaths =
        [
            "src/Mcg.AgentOrchestrator.Execution/Processes/DispatchProcessHost.cs",
            "src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessRunner.cs",
            "src/Mcg.AgentOrchestrator.Execution/Processes/BackgroundDispatchRunner.cs",
            "src/Mcg.AgentOrchestrator.Execution/Workers/WorkerProfileDispatcher.cs",
            "src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs",
            "src/Mcg.AgentOrchestrator.Execution/Processes/OwnedProcessGroup.cs"
        ];
        foreach (var path in workerLaunchPaths)
        {
            var source = File.ReadAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("TryLowerCurrentProcessToBelowNormal", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BelowNormal", source, StringComparison.Ordinal);
            Assert.DoesNotContain("0x00004000", source, StringComparison.Ordinal);
        }
    }

    private static ConductorParallelAcceptanceAttempt Launch(string root, bool focusedEvidence, bool enabled)
    {
        var goal = new Goal(GoalId.New(), "Priority attempt test",
            [new TaskSpec(TaskId.New(), "Verify launch", AgentRole.Developer)]);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Priority.cs"],
            enabled ? "branch-on" : "branch-off", "main");
        ConductorParallelAcceptanceAttempt? captured = null;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            root,
            executionDirectory: root,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                captured = launch.Attempt;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(8700);
            });
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            AcceptanceAttemptBelowNormalPriority = enabled
        };
        if (focusedEvidence)
        {
            _ = coordinator.EvaluateFocusedEvidence(candidate, policy, "priority evidence",
                (_, _, _, _) => throw new InvalidOperationException("The injected launcher must not run checks."));
        }
        else
        {
            _ = coordinator.Evaluate(candidate, policy,
                (_, _, _, _, _) => throw new InvalidOperationException("The injected launcher must not run checks."));
        }
        return Assert.IsType<ConductorParallelAcceptanceAttempt>(captured);
    }

    private sealed class PriorityRequestReachedException : Exception { }
}
