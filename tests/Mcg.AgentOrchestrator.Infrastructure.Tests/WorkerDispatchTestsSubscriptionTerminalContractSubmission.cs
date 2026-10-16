using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerDispatchTestsSubscriptionTerminalContractSubmission : WorkerDispatchTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedSubmissionRepairsCensusShapeBeforeEveryResultReader(bool structuredCodex)
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
        var validationRoot = CreateValidationRoot();
        var canonical = WorkerResultSubmission.Validate(AgentRole.Tester, WorkerResultSubmissionTests.Fixture, validationRoot).CanonicalBlock;
        var censusOutput = string.Join('\n', canonical.Split('\n')[1..^1]);
        var rawOutput = structuredCodex ? JsonSerializer.Serialize(new
        {
            type = "item.completed", item = new { type = "agent_message", text = censusOutput }
        }) : censusOutput;
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            CreateSeededDispatchRepository(), AgentRole.Tester, rawOutput, string.Empty, clock,
            command: structuredCodex ? "codex exec --json prompt" : "codex exec prompt",
            workerProviderKind: structuredCodex ? ProviderKind.OpenAICodexCli : ProviderKind.Unknown);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Tester, censusOutput).Status);
        Assert.True(DispatchFailureClassifier.IsSubscriptionProviderCliDispatch(task));
        var path = Submit(validationRoot, goal, task, process, task.Id, task.LastDispatch!.DispatchedAt.AddMinutes(1));

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        task = kernel.GetTask(goal.Id, task.Id);
        var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, verification).Status);
        Assert.DoesNotContain("rule=verification-terminal-contract-incomplete",
            DispatchFailureClassifier.Classify(task, verification).ClassifierReceipt);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var suffix = $"[orchestrator ingested worker result submission {path} sha256={hash}]\n{canonical}\n";
        Assert.EndsWith(suffix, File.ReadAllText(process.StandardOutputPath));
        if (structuredCodex) Assert.Equal(rawOutput, File.ReadAllText(process.StandardOutputPath + ".jsonl"));
        Assert.EndsWith(suffix.TrimEnd(), verification.AuthoritativeStandardOutput!.ReplaceLineEndings("\n").TrimEnd());
        Assert.Contains("worker_result_submission=ingested", verification.StandardError);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("stale")]
    [InlineData("task-mismatch")]
    public void MissingOrUnusableSubmissionKeepsLegacyMissingContract(string scenario)
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
        var validationRoot = CreateValidationRoot();
        var canonical = WorkerResultSubmission.Validate(AgentRole.Tester, WorkerResultSubmissionTests.Fixture, validationRoot).CanonicalBlock;
        var censusOutput = string.Join('\n', canonical.Split('\n')[1..^1]);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            CreateSeededDispatchRepository(), AgentRole.Tester, censusOutput, string.Empty, clock);
        if (scenario != "absent")
            Submit(validationRoot, goal, task, process, scenario == "task-mismatch" ? TaskId.New() : task.Id,
                task.LastDispatch!.DispatchedAt.AddMinutes(scenario == "stale" ? -1 : 1));
        var original = File.ReadAllBytes(process.StandardOutputPath);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        task = kernel.GetTask(goal.Id, task.Id);
        // Contract repair clears the latest record for retry, retaining the failed round in history.
        Assert.Null(task.LastVerification);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        var verification = Assert.Single(task.VerificationHistory);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessSubscriptionVerification(task, verification).Status);
        Assert.Contains("rule=verification-terminal-contract-incomplete",
            DispatchFailureClassifier.Classify(task, verification).ClassifierReceipt);
        Assert.Equal(original, File.ReadAllBytes(process.StandardOutputPath));
        if (scenario == "absent")
            Assert.DoesNotContain("worker_result_submission=", verification.StandardError);
        else
            Assert.Contains($"worker_result_submission=ignored; reason={scenario}", verification.StandardError);
    }

    [Fact]
    public void CompleteLegacyBlockWithoutSubmissionRemainsByteIdentical()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
        var canonical = WorkerResultSubmission.Validate(AgentRole.Tester, WorkerResultSubmissionTests.Fixture, CreateValidationRoot()).CanonicalBlock;
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            CreateSeededDispatchRepository(), AgentRole.Tester, canonical, string.Empty, clock);
        var original = File.ReadAllBytes(process.StandardOutputPath);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        task = kernel.GetTask(goal.Id, task.Id);
        var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, verification).Status);
        Assert.Equal(original, File.ReadAllBytes(process.StandardOutputPath));
        Assert.DoesNotContain("worker_result_submission=", verification.StandardError);
    }

    [Fact]
    public void SubmissionWinsOverEarlierCompleteLegacyBlock()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
        var validationRoot = CreateValidationRoot();
        var canonical = WorkerResultSubmission.Validate(AgentRole.Tester, WorkerResultSubmissionTests.Fixture, validationRoot).CanonicalBlock;
        var legacy = canonical.Replace("confidence: high", "confidence: low", StringComparison.Ordinal);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            CreateSeededDispatchRepository(), AgentRole.Tester, legacy, string.Empty, clock);
        Submit(validationRoot, goal, task, process, task.Id, task.LastDispatch!.DispatchedAt.AddMinutes(1));

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.EndsWith(canonical + "\n", File.ReadAllText(process.StandardOutputPath));
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, task.LastVerification).Status);
    }

    private static string CreateValidationRoot()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, WorkerResultSubmissionTests.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, string.Empty);
        return root;
    }

    private static string Submit(string validationRoot, Goal goal, TaskSpec task, TaskProcessRecord process,
        TaskId submittedTask, DateTimeOffset submittedAt)
    {
        var path = WorkerResultSubmission.PathFor(process.WorkingDirectory, goal.Id);
        var contextRoot = Path.Combine(process.WorkingDirectory, ".orchestrator-context");
        Directory.CreateDirectory(contextRoot);
        File.WriteAllText(Path.Combine(contextRoot, ".gitignore"), "*\n");
        var input = Path.Combine(validationRoot, "input.json");
        File.WriteAllText(input, WorkerResultSubmissionTests.Fixture);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, WorkerResultSubmission.RunSubmitCommand(
            ["--worktree", validationRoot, "--task", submittedTask.Value, "--path", path, "--input", input],
            output, error, () => submittedAt));
        Assert.Equal(string.Empty, error.ToString());
        using var record = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(submittedTask.Value, record.RootElement.GetProperty("task_id").GetString());
        Assert.Equal(submittedAt, record.RootElement.GetProperty("submitted_at_utc").GetDateTimeOffset());
        return path;
    }
}
