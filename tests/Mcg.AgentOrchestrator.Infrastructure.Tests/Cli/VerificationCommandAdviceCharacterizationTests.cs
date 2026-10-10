using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: pure command builders and read-only source inspection have no shared mutable state.
public sealed class VerificationCommandAdviceCharacterizationTests
{
    // Derived from ConsoleViews.VerificationCommands.cs at d19f9e623, before extraction.
    [Theory]
    [InlineData(VerificationGateStatus.NotReady, "task 3")]
    [InlineData(VerificationGateStatus.MissingVerification, "verify 3 <command> | verify-manual 3 passed <note>")]
    [InlineData(VerificationGateStatus.FailedVerification, "verifications 3 | retry 3 <note>")]
    [InlineData(VerificationGateStatus.Passed, "gates")]
    [InlineData((VerificationGateStatus)99, "monitor")]
    public void Verification_AllStatuses_PreserveExactText(VerificationGateStatus status, string expected)
    {
        Assert.Equal(expected, VerificationCommandAdvice.BuildVerificationSuggestedCommand(3, status));
    }

    // Derived from ConsoleViews.VerificationCommands.cs at d19f9e623, before extraction.
    [Fact]
    public void HumanInput_LongRequestId_PreservesExactText()
    {
        Assert.Equal("answer abcdef01 <answer>",
            VerificationCommandAdvice.BuildHumanInputSuggestedCommand(new HumanInputRequestId("abcdef0123456789")));
    }

    // Derived from ConsoleViews.VerificationCommands.cs at d19f9e623, before extraction.
    [Theory]
    [InlineData(StageReadinessStatus.NeedsDelegation, TaskEvidenceKind.None, "delegate")]
    [InlineData(StageReadinessStatus.ReadyToRun, TaskEvidenceKind.None, "run 3 | profile-dispatch 3 <profile-name>")]
    [InlineData(StageReadinessStatus.InProgress, TaskEvidenceKind.RunningProcess, "refresh-dispatch 3")]
    [InlineData(StageReadinessStatus.InProgress, TaskEvidenceKind.Dispatch, "execute-dispatch 3 --confirm-dispatch-start")]
    [InlineData(StageReadinessStatus.InProgress, TaskEvidenceKind.None, "task 3")]
    [InlineData(StageReadinessStatus.WaitingForHuman, TaskEvidenceKind.None, "input-needed")]
    [InlineData(StageReadinessStatus.NeedsVerification, TaskEvidenceKind.None, "verify 3 <command> | verify-manual 3 passed <note>")]
    [InlineData(StageReadinessStatus.VerificationFailed, TaskEvidenceKind.None, "verifications 3 | retry 3 <note>")]
    [InlineData(StageReadinessStatus.Verified, TaskEvidenceKind.None, "stages")]
    [InlineData(StageReadinessStatus.FailedOrCancelled, TaskEvidenceKind.None, "retry 3 <note>")]
    [InlineData((StageReadinessStatus)99, TaskEvidenceKind.None, "monitor")]
    public void Stage_AllStatusesAndInProgressEvidence_PreserveExactText(
        StageReadinessStatus status, TaskEvidenceKind evidence, string expected)
    {
        var stage = new TaskStageReadiness(new TaskId("task-id"), AgentRole.Developer, "description",
            WorkTaskStatus.Assigned, true, status, evidence, VerificationGateStatus.NotReady, 0, "message", "suggested action");

        Assert.Equal(expected, VerificationCommandAdvice.BuildStageSuggestedCommand(3, stage));
    }

    // Derived from ConsoleViews.VerificationCommands.cs at d19f9e623, before extraction.
    [Theory]
    [InlineData(GoalAcceptanceBlockerKind.PendingHumanInput, true, "answer abcdef01 <answer>", "answer abcdef01 <answer>")]
    [InlineData(GoalAcceptanceBlockerKind.PendingHumanInput, false, "monitor", "monitor")]
    [InlineData(GoalAcceptanceBlockerKind.VerificationNotReady, false, "task 3", "monitor")]
    [InlineData(GoalAcceptanceBlockerKind.VerificationMissing, false, "verify 3 <command> | verify-manual 3 passed <note>", "monitor")]
    [InlineData(GoalAcceptanceBlockerKind.VerificationFailed, false, "verifications 3 | retry 3 <note>", "monitor")]
    [InlineData(GoalAcceptanceBlockerKind.AcceptanceFailed, false, "inspect acceptance evidence", "inspect acceptance evidence")]
    [InlineData(GoalAcceptanceBlockerKind.AcceptanceAborted, false, "inspect acceptance evidence", "inspect acceptance evidence")]
    [InlineData((GoalAcceptanceBlockerKind)99, false, "monitor", "monitor")]
    public void Acceptance_AllKindsAndGuards_PreserveExactText(
        GoalAcceptanceBlockerKind kind, bool hasRequestId, string expectedWithTask, string expectedWithoutTask)
    {
        var blocker = new GoalAcceptanceBlocker(kind, new TaskId("task-id"),
            hasRequestId ? new HumanInputRequestId("abcdef0123456789") : null, "message", "inspect acceptance evidence");

        Assert.Equal(expectedWithTask, VerificationCommandAdvice.BuildAcceptanceSuggestedCommand(blocker, 3));
        Assert.Equal(expectedWithoutTask, VerificationCommandAdvice.BuildAcceptanceSuggestedCommand(blocker, null));
    }

    [Fact]
    public void ConsoleViewsRatchet_UsesReducedAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 3047);
        Assert.True(ceiling.MaximumPartialFileCount <= 38);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    [Fact]
    public void ExtractedAdvice_LeavesOnlyGoalStageOverloadOnConsoleViews()
    {
        var root = VerifiedRepositoryRoot.Find();
        var cliRoot = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Cli");
        var advicePath = Path.Combine(cliRoot, "VerificationCommandAdvice.cs");
        Assert.True(File.Exists(advicePath), "VerificationCommandAdvice.cs must own the extracted builders.");
        var advice = File.ReadAllText(advicePath);
        Assert.Contains("internal static class VerificationCommandAdvice", advice);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", advice);

        // A forwarding member with a moved name/signature fails the same absence checks.
        var oldMethods = typeof(ConsoleViews).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (var name in new[] { "BuildVerificationSuggestedCommand", "BuildHumanInputSuggestedCommand",
                     "BuildAcceptanceSuggestedCommand" })
            Assert.DoesNotContain(oldMethods, method => method.Name == name);
        Assert.DoesNotContain(oldMethods, method => method.Name == "BuildStageSuggestedCommand" &&
            method.GetParameters()[0].ParameterType == typeof(int));
        Assert.Single(oldMethods, method => method.Name == "BuildStageSuggestedCommand" &&
            method.GetParameters()[0].ParameterType == typeof(Goal));

        foreach (var path in Directory.EnumerateFiles(cliRoot, "*.cs"))
        {
            if (path == advicePath)
                continue;
            Assert.DoesNotMatch(
                @"\bstatic\s+string\s+(?:Build(?:Verification|HumanInput|Acceptance)SuggestedCommand\s*\(|BuildStageSuggestedCommand\s*\(\s*int\b)",
                File.ReadAllText(path));
        }

        // Match the ratchet's declaration scan, including ConsoleViews.cs and excluding build outputs.
        var partials = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\bpartial\s+class\s+ConsoleViews\b",
                RegexOptions.CultureInvariant));
        Assert.Equal(38, partials.Count());
    }
}
