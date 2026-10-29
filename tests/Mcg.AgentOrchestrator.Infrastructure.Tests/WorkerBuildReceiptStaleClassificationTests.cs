using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: stateless formatting, classification, and guidance; no shared state mutation.
public sealed class WorkerBuildReceiptStaleClassificationTests : WorkerDispatchTestSupport
{
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
    [InlineData(true, "the build receipt does not match the final tree; files changed after build: src/Feature.cs")]
    [InlineData(false, "receipt does not cover project src/Feature.csproj")]
    public void Classify_StaleReceipt_PreservesSpecificDiagnostic(bool treeChanged, string diagnostic)
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
        Assert.Contains(diagnostic, outcome.ClassifierReceipt, StringComparison.Ordinal);
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
