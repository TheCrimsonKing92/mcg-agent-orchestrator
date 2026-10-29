using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: unique fixture repositories/SQLite files and fake build children; no ambient edits.
public sealed class StaleBuildReceiptClassificationTests : WorkerDispatchTestSupport
{
    [Fact]
    public void CompletedRound_EditAfterBuild_ReportsStaleReceiptWithChangedPath()
    {
        var task = RefreshWithChangeBeforeCommit(fixture =>
        {
            File.AppendAllText(Path.Combine(fixture.Task.LastProcess!.WorkingDirectory, "src/Feature/Feature.cs"),
                "\npublic sealed class LaterEdit { }");
            Assert.False(WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Task.LastProcess.WorkingDirectory).Matches);
        });

        var outcome = AssertStaleCompletedRound(task);

        Assert.Contains("the build receipt does not match the final tree; files changed after build: ",
            task.LastVerification!.StandardError, StringComparison.Ordinal);
        Assert.Contains("src/Feature/Feature.cs", outcome.EvidenceSummary, StringComparison.Ordinal);
        Assert.Contains("files changed after build: ", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.Contains("src/Feature/Feature.cs", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedRound_UncoveredProject_ReportsStaleReceiptWithProject()
    {
        var task = RefreshWithChangeBeforeCommit(fixture =>
        {
            fixture.WriteReceipt(["another/project.csproj"]);
            var receipt = WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Task.LastProcess!.WorkingDirectory);
            Assert.True(receipt.Matches, receipt.Reason);
            Assert.Equal(["src/Feature/Feature.csproj"],
                WorkerBuildReceiptCoverage.FindMissingProjects(fixture.Task.CompletionBuildEvidence!.Projects, receipt.Projects));
        });

        var outcome = AssertStaleCompletedRound(task);

        const string diagnostic = "receipt does not cover project src/Feature/Feature.csproj";
        Assert.Contains(diagnostic, task.LastVerification!.StandardError, StringComparison.Ordinal);
        Assert.Equal("Worker build receipt does not match the final tree. " + diagnostic, outcome.EvidenceSummary);
        Assert.Contains(diagnostic, outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.DoesNotContain("files changed after build:", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedRound_NoBuildEvidence_KeepsMissingEvidenceRuleAndOriginalText()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        Assert.False(File.Exists(fixture.ReceiptPath));
        var buildChecks = 0;
        var runner = new BackgroundDispatchRunner(fixture.Clock,
            isStillRunning: _ => false,
            resolveWorkerBuildReceiptPath: _ => fixture.ReceiptPath,
            runOrchestratorBuildCheck: _ =>
            {
                buildChecks++;
                return new(false, -1, "Build apparatus unavailable; no build was executed.");
            });

        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(1, buildChecks);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        var verification = Assert.IsType<TaskVerificationRecord>(fixture.Task.LastVerification);
        Assert.False(verification.Succeeded);
        Assert.Contains(DispatchFailureDiagnosticMarker.Format("worker-build-evidence-missing"),
            verification.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-build-receipt-stale", verification.StandardError, StringComparison.Ordinal);
        var outcome = DispatchFailureClassifier.Classify(fixture.Task, verification);
        Assert.Equal("worker-build-evidence-missing", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.StartsWith("Required worker build evidence was not reported.", outcome.EvidenceSummary, StringComparison.Ordinal);
        Assert.Contains("evidence=Required worker build evidence was not reported.", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    private static TaskSpec RefreshWithChangeBeforeCommit(Action<CompletionBuildEvidenceFixture> change)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var successReceiptReads = 0;
        var changed = false;
        var runner = new BackgroundDispatchRunner(fixture.Clock,
            isStillRunning: _ => false,
            completionBuildEvidence: fixture.Coordinator(),
            resolveWorkerBuildReceiptPath: _ =>
            {
                if (fixture.Task.CompletionBuildEvidence is { Phase: CompletionBuildEvidencePhase.Success } built)
                {
                    // The first read certifies the build result. Change the candidate only on
                    // the next read, at the runner's final commit check, without a timing race.
                    successReceiptReads++;
                    if (successReceiptReads == 2)
                    {
                        var receipt = WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Task.LastProcess!.WorkingDirectory);
                        Assert.True(receipt.Matches, receipt.Reason);
                        Assert.Empty(WorkerBuildReceiptCoverage.FindMissingProjects(built.Projects, receipt.Projects));
                        change(fixture);
                        changed = true;
                    }
                }
                return fixture.ReceiptPath;
            });

        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Null(fixture.Task.LastVerification);
        fixture.Release();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.True(changed, "The candidate must change after build certification and before commit authorization.");
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(CompletionBuildEvidencePhase.StaleCandidate, fixture.Task.CompletionBuildEvidence!.Phase);
        return fixture.Task;
    }

    private static DispatchOutcome AssertStaleCompletedRound(TaskSpec task)
    {
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Null(task.LastDispatch!.ResultCommit);
        var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.False(verification.Succeeded);
        Assert.Single(task.VerificationHistory);
        Assert.Contains(DispatchFailureDiagnosticMarker.Format("worker-build-receipt-stale"),
            verification.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-build-evidence-missing", verification.StandardError, StringComparison.Ordinal);
        var outcome = DispatchFailureClassifier.Classify(task, verification);
        Assert.Equal("worker-build-receipt-stale", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.DoesNotContain("Required worker build evidence was not reported.", outcome.EvidenceSummary, StringComparison.Ordinal);
        return outcome;
    }

    [Theory]
    [InlineData("worker-build-evidence-missing", true)]
    [InlineData("worker-build-receipt-stale", true)]
    [InlineData("worker-build-check-failed", false)]
    [InlineData("incomplete-scope-declaration", false)]
    [InlineData(null, false)]
    public void StaleReceiptRule_SharesMissingEvidenceRecoveryRouting(string? rule, bool expected)
    {
        Assert.Equal(expected, DispatchFailureDiagnosticMarker.SharesMissingEvidenceRecovery(rule));
    }

    [Theory]
    [InlineData(true, "the build receipt does not match the final tree; files changed after build: src/Feature.cs",
        "the build receipt does not match the final tree, files changed after build: src/Feature.cs")]
    [InlineData(false, "receipt does not cover project src/Feature.csproj",
        "receipt does not cover project src/Feature.csproj")]
    public void Classify_StaleReceipt_PreservesSpecificDiagnostic(
        bool treeChanged, string diagnostic, string receiptDiagnostic)
    {
        var marker = DispatchFailureDiagnosticMarker.FormatWorkerBuildReceiptStale(
            treeChanged, ["src/Feature.cs"], treeChanged ? [] : ["src/Feature.csproj"]);
        Assert.Equal(diagnostic + Environment.NewLine +
            "@@MCG_ORCHESTRATOR_DIAGNOSTIC_CODE@@ worker-build-receipt-stale", marker);
        var verification = new TaskVerificationRecord("cmd", "C:\\repo", 1, "Worker output",
            new string('x', 300) + "\n" + marker + "\nother diagnostic\nmore diagnostic\nlast diagnostic",
            DateTimeOffset.Parse("2026-10-28T12:00:00Z"));
        var task = new TaskSpec(TaskId.New(), "Implement feature.", AgentRole.Developer);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Equal("worker-build-receipt-stale", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal("Worker build receipt does not match the final tree. " + diagnostic, outcome.EvidenceSummary);
        // Receipt values escape semicolon field delimiters; the evidence summary above stays verbatim.
        Assert.EndsWith("; evidence=Worker build receipt does not match the final tree. " + receiptDiagnostic,
            outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStaleReceipt_BothCauses_RetainsSortedDistinctPathsAndProjects()
    {
        var marker = DispatchFailureDiagnosticMarker.FormatWorkerBuildReceiptStale(true,
            ["z.cs", "a.cs", "z.cs"], ["Z.csproj", "A.csproj", "Z.csproj"]);

        Assert.Equal("the build receipt does not match the final tree; files changed after build: a.cs, z.cs; " +
            "receipt does not cover project A.csproj; receipt does not cover project Z.csproj" + Environment.NewLine +
            "@@MCG_ORCHESTRATOR_DIAGNOSTIC_CODE@@ worker-build-receipt-stale", marker);
    }

    [Fact]
    public void FormatStaleReceipt_NoDirtyPaths_ReportsExplicitAbsence()
    {
        var marker = DispatchFailureDiagnosticMarker.FormatWorkerBuildReceiptStale(true, [], []);

        Assert.Equal("the build receipt does not match the final tree; files changed after build: (no dirty paths recorded)" +
            Environment.NewLine + "@@MCG_ORCHESTRATOR_DIAGNOSTIC_CODE@@ worker-build-receipt-stale", marker);
    }

    [Fact]
    public void DotnetGuidance_InstructsBuildCheckLastAfterFinalEdit()
    {
        const string instruction = "Run the build check last, after your final edit, and make no edits after it (notes, docs, tests); any later edit needs a fresh build check.";
        var goalId = new GoalId("stale-guidance");

        Assert.Contains(instruction,
            string.Join("\n", WorkerStackVerificationGuidance.CurrentTaskSection(ProjectStack.Dotnet)), StringComparison.Ordinal);
        Assert.Contains(instruction,
            string.Join("\n", WorkerStackVerificationGuidance.DeterministicVerificationSection(Toolchain.Dotnet, goalId)), StringComparison.Ordinal);
        Assert.DoesNotContain(instruction,
            string.Join("\n", WorkerStackVerificationGuidance.CurrentTaskSection(ProjectStack.Go)), StringComparison.Ordinal);
        Assert.DoesNotContain(instruction,
            string.Join("\n", WorkerStackVerificationGuidance.DeterministicVerificationSection(Toolchain.Go, goalId)), StringComparison.Ordinal);
    }
}
