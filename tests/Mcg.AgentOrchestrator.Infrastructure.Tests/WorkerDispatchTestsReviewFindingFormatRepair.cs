using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerDispatchTestsReviewFindingFormatRepair : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void PreparationDeliversRejectedFindingWithoutWorkerStart()
    {
        var root = CreateSeededDispatchRepository();
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Repair finding format", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Keep the malformed finding available for repair", [reviewer]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(new("reviewer"), "Reviewer", AgentRole.Reviewer,
            new("OpenAI", "gpt-6.1-sol", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new("codex-cli", "gpt-6.1-sol", "low"))]);
        var profile = new WorkerProfile("codex-cli", "codex exec --sandbox read-only --cd {workingDirectory} {promptPath}");
        var prompts = Path.Combine(root, "prompts");
        WorkerProfileDispatcher.PrepareTask(kernel, goal, reviewer, profile, prompts, root, DateTimeOffset.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias);
        var candidate = ReadGit(root, ["rev-parse", "HEAD"]);
        Assert.False(string.IsNullOrWhiteSpace(candidate));
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, candidate);
        var rejected = JsonSerializer.Serialize(new[]
        {
            new ReviewFinding("rejected-mutation", ReviewFindingState.Open,
                new("src/Target.cs", "Run"), "The guard is broken", FindingSeverity.Blocking, FindingCategory.Correctness,
                new FindingEvidenceRequest([], Mutation: new("src/Target.cs", "true", "false")))
        });
        var output = string.Join("\n", "WORKER_RESULT:", "files: none", "commands: none", "tests: pass",
            "commit: none", "blockers: none", "verdict: pass", "findings: " + rejected,
            "touched_anchors: []", "criteria_verdicts: []",
            "model_fit: fixture/model - adequate - finding format repair",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(reviewer.LastDispatch.Command, root, 0, output, "",
            DateTimeOffset.UtcNow, WorkerResultPresent: true,
            DispatchStartedAt: reviewer.LastDispatch.DispatchedAt,
            DispatchAttemptAt: reviewer.LastDispatch.DispatchedAt,
            EvidenceSource: VerificationEvidenceSource.Dispatch, ReviewedCommit: candidate);
        Assert.False(WorkerResultBlockers.TryFindReviewFindingRound(verification, out _, out _, out _, out var violation));
        Assert.NotNull(violation);
        Assert.Equal(VerificationTerminalContractStatus.Malformed,
            WorkerResultBlockers.AssessSubscriptionVerification(reviewer, verification).Status);
        // Prompt delivery consumes a retained typed rejection. Actual subscription completion
        // now routes this malformed contract to same-role repair before finding convergence.
        var rejectedRound = new TaskVerificationSnapshot(verification.Command, root, 0, output, "",
            verification.CompletedAt, ReviewedCommit: candidate, MergedReviewFindings: [],
            WorkerResultPresent: true, ReviewFindingContractViolation: violation,
            DispatchStartedAt: verification.DispatchStartedAt, AuthoritativeStandardOutput: output,
            AuthoritativeStandardError: "", EvidenceSource: VerificationEvidenceSource.Dispatch,
            DispatchAttemptAt: verification.DispatchAttemptAt);
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with
            {
                Tasks = [goalSnapshot.Tasks.Single() with
                {
                    Status = WorkTaskStatus.Failed,
                    LastVerification = rejectedRound,
                    VerificationHistory = [rejectedRound]
                }]
            }]
        });
        goal = kernel.GetGoal(goal.Id);
        reviewer = kernel.GetTask(goal.Id, reviewer.Id);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Empty(reviewer.LastVerification!.MergedReviewFindings!);
        kernel.RetryTask(goal.Id, reviewer.Id, "review-finding contract-repair: attempt 1/1",
            retryRoundKind: RetryRoundKind.Mechanical);

        var prepared = WorkerProfileDispatcher.PrepareTask(kernel, goal, reviewer, profile, prompts, root, DateTimeOffset.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias);
        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.Contains("task/review-contract-repair-envelope.json", prompt);
        Assert.Contains("ERR_REVIEW_FINDING_FORMAT", prompt);
        var package = Path.Combine(root, ".orchestrator-context", goal.Id.Value, "authoritative", reviewer.Id.Value, "typed");
        Assert.Equal(ReviewFindingHistoryProjectionMode.ContractRepair,
            reviewer.LastDispatch!.ContextPackageReceipt!.ReviewFindingProjectionMode);
        // Decode the delivered inline envelope; its referenced round body is delivered as a file.
        const string envelopeIdentity = "task/review-contract-repair-envelope.json";
        Assert.Contains(reviewer.LastDispatch.ContextPackageReceipt.Sections, section =>
            section.LogicalIdentity == envelopeIdentity && section.DeliveryMode == ContextDeliveryMode.InlineFull);
        var promptLines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var envelopeHeaderIndex = Array.FindIndex(promptLines, line =>
            line.StartsWith($"INLINE FULL: identity={envelopeIdentity};", StringComparison.Ordinal));
        Assert.True(envelopeHeaderIndex >= 0, "The repair envelope was not delivered inline.");
        Assert.EndsWith("encoding=utf8-json", promptLines[envelopeHeaderIndex]);
        Assert.True(envelopeHeaderIndex + 1 < promptLines.Length, "The inline repair envelope has no payload.");
        var envelopeJson = JsonSerializer.Deserialize<string>(promptLines[envelopeHeaderIndex + 1]);
        Assert.NotNull(envelopeJson);
        using var envelope = JsonDocument.Parse(envelopeJson);
        Assert.Equal("pass", envelope.RootElement.GetProperty("prior_substantive_verdict").GetString());
        Assert.Empty(envelope.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Contains("mutation requires negative_control 'revert-src'",
            envelope.RootElement.GetProperty("contract_violation").GetProperty("Message").GetString());
        var reference = envelope.RootElement.GetProperty("immutable_references").EnumerateArray().Single();
        var logicalIdentity = reference.GetProperty("logical_identity").GetString()!;
        var bodyPath = Path.Combine(package, logicalIdentity.Replace('/', Path.DirectorySeparatorChar));
        using var body = JsonDocument.Parse(File.ReadAllText(bodyPath));
        Assert.Contains(reviewer.LastDispatch.ContextPackageReceipt.Sections, section =>
            section.LogicalIdentity == logicalIdentity && section.DeliveryMode == ContextDeliveryMode.OnDemandFile);
        Assert.Equal(rejected, body.RootElement.GetProperty("RejectedFindingsJson").GetString());
        Assert.Equal("[]", body.RootElement.GetProperty("RejectedTouchedAnchorsJson").GetString());
        Assert.Equal("pass", body.RootElement.GetProperty("SubstantiveVerdict").GetString());
        Assert.Empty(body.RootElement.GetProperty("Findings").EnumerateArray());
        Assert.Null(reviewer.LastProcess);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskProcessStarted);
    }
}
