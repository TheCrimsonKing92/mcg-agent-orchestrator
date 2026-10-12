using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each test owns its audit directory and in-memory task state.
public sealed class ProviderInterruptionRawAuditTests
{
    private const string CapacityAudit = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"turn.started"}
        {"type":"error","message":"Selected model is at capacity. Please try a different model."}
        {"type":"turn.failed","error":{"message":"Selected model is at capacity. Please try a different model."}}
        """;

    [Fact(DisplayName = "Normalized Developer stdout uses raw failed-turn evidence for automatic retry")]
    public void DeveloperCapacityFailureUsesRawAudit()
    {
        using var fixture = new AuditFixture(CapacityAudit);
        Assert.True(File.Exists(fixture.OutputPath + ".jsonl"));
        Assert.DoesNotContain("{", File.ReadAllText(fixture.OutputPath));

        var outcome = fixture.Classify(AgentRole.Developer);

        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Assert.Contains("rule=provider-interruption;", outcome.ClassifierReceipt);
        Assert.Contains("Selected model is at capacity", EvidenceField(outcome));
    }

    [Theory(DisplayName = "Planner and Tester raw failed turns select provider interruption")]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Tester)]
    public void OtherRolesUseRawAudit(AgentRole role)
    {
        using var fixture = new AuditFixture(CapacityAudit);
        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, fixture.Classify(role).Kind);
    }

    [Fact(DisplayName = "A raw failed Developer turn resolves to the existing interruption retry cause")]
    public void RawAuditOutcomeResolvesProviderRetryCause()
    {
        using var fixture = new AuditFixture(CapacityAudit);
        var task = new TaskSpec(TaskId.New(), "Implement the requested change", AgentRole.Developer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Raw audit retry cause", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var verification = fixture.Verification();
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, outcome.Kind);
        Assert.Equal(RetryCause.ProviderInterruption, AutomaticWorkerRetryCause.Resolve(task, outcome));
    }

    [Theory(DisplayName = "A completed turn in either source suppresses raw failed-turn interruption")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CompletedTurnInEitherSourcePreventsInterruption(bool completionInStdout, bool failureInStdout)
    {
        const string completed = "{\"type\":\"turn.completed\"}";
        using var fixture = new AuditFixture(
            failureInStdout ? completed : completionInStdout ? CapacityAudit : CapacityAudit + "\n" + completed,
            failureInStdout ? CapacityAudit : completionInStdout ? completed : null);

        var outcome = fixture.Classify(AgentRole.Developer);

        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Contains("rule=unknown-failure;", outcome.ClassifierReceipt);
        Assert.DoesNotContain("rule=provider-interruption", outcome.ClassifierReceipt);
    }

    [Fact(DisplayName = "Unrelated failed-turn wording selects interruption and appears only as evidence")]
    public void UnrelatedFailedTurnIsStructuralEvidence()
    {
        using var fixture = new AuditFixture(
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"unrelated parser crash\"}}");

        var outcome = fixture.Classify(AgentRole.Developer);

        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, outcome.Kind);
        Assert.Contains("unrelated parser crash", EvidenceField(outcome));
        Assert.DoesNotContain("capacity", EvidenceField(outcome));
    }

    [Theory(DisplayName = "Absent or nonterminal raw records preserve unknown failure")]
    [InlineData(null)]
    [InlineData("{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n{\"type\":\"turn.started\"}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Working\"}}")]
    public void NoFailedRecordPreservesUnknownFailure(string? audit)
    {
        using var fixture = new AuditFixture(audit);
        Assert.Equal(audit is not null, File.Exists(fixture.OutputPath + ".jsonl"));

        var outcome = fixture.Classify(AgentRole.Developer);

        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Contains("rule=unknown-failure;", outcome.ClassifierReceipt);

        // A positive control proves this same sibling path is actually consulted.
        File.WriteAllText(fixture.OutputPath + ".jsonl", CapacityAudit);
        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, fixture.Classify(AgentRole.Developer).Kind);
    }

    private static string EvidenceField(DispatchOutcome outcome)
    {
        Assert.Contains("; evidence=", outcome.ClassifierReceipt);
        return outcome.ClassifierReceipt.Split("; evidence=", 2, StringSplitOptions.None)[1];
    }

    private sealed class AuditFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "raw-audit-" + Guid.NewGuid().ToString("N"));

        public AuditFixture(string? audit, string? stdout = null)
        {
            Directory.CreateDirectory(directory);
            OutputPath = Path.Combine(directory, "worker.out.log");
            File.WriteAllText(OutputPath, stdout ?? "I inspected the implementation and started working.");
            if (audit is not null)
            {
                File.WriteAllText(OutputPath + ".jsonl", audit);
            }
        }

        public string OutputPath { get; }

        public TaskVerificationRecord Verification() => new(
            "codex exec", directory, 1, File.ReadAllText(OutputPath), string.Empty,
            DateTimeOffset.UnixEpoch, StandardOutputPath: OutputPath);

        public DispatchOutcome Classify(AgentRole role) => DispatchFailureClassifier.Classify(
            new TaskSpec(TaskId.New(), "Implement the requested change", role), Verification());

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
