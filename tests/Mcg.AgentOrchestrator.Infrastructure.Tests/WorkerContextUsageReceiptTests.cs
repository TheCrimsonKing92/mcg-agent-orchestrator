using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextUsageReceiptTests
{
    [Xunit.Fact]
    public void Parse_CodexCli01470CapturedJsonl_PreservesWorkerTextAndProviderUsage()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "CodexJsonl",
            "codex-cli-0.147.0-turn.jsonl");

        var result = CodexJsonlUsageParser.Parse(File.ReadAllText(path));

        Assert.True(result.Recognized);
        Assert.Equal("WORKER_RESULT_OK", result.WorkerOutput);
        Assert.Equal(21476, result.Usage!.InputTokens);
        Assert.Equal(11008, result.Usage.CachedInputTokens);
        Assert.Equal(8, result.Usage.OutputTokens);
        Assert.Equal(["WORKER_RESULT_OK"], result.AgentMessages);
        Assert.Equal(0, result.MalformedLineCount);
    }

    [Xunit.Fact]
    public void Parse_TurnUsage_PreservesProviderValuesAndWorkerText()
    {
        var jsonl = """
            {"type":"item.completed","item":{"type":"agent_message","text":"WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT"}}
            {"type":"turn.completed","usage":{"input_tokens":123456,"cached_input_tokens":120000,"output_tokens":789}}
            """;

        var result = CodexJsonlUsageParser.Parse(jsonl);

        Assert.True(result.Recognized);
        Assert.Contains("WORKER_RESULT:", result.WorkerOutput, StringComparison.Ordinal);
        Assert.Equal(123456, result.Usage!.InputTokens);
        Assert.Equal(120000, result.Usage.CachedInputTokens);
        Assert.Equal(789, result.Usage.OutputTokens);
    }

    [Xunit.Fact]
    public void Parse_TurnUsage_PreservesValuesBeyondInt32()
    {
        var jsonl = """
            {"type":"turn.completed","usage":{"input_tokens":2147483648,"cached_input_tokens":2147483649,"output_tokens":2147483650}}
            """;

        var result = CodexJsonlUsageParser.Parse(jsonl);

        Assert.Equal(2147483648L, result.Usage!.InputTokens);
        Assert.Equal(2147483649L, result.Usage.CachedInputTokens);
        Assert.Equal(2147483650L, result.Usage.OutputTokens);
    }

    [Xunit.Fact]
    public void Parse_UnrecognizedUsageShape_PreservesWorkerResultAndReportsUnsupported()
    {
        var jsonl = """
            {"type":"item.completed","item":{"type":"agent_message","text":"WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT"}}
            {"type":"turn.finished","token_usage":{"input":123,"cached":100,"output":9}}
            """;

        var result = CodexJsonlUsageParser.Parse(jsonl);

        Assert.True(result.Recognized);
        Assert.Contains("WORKER_RESULT:", result.WorkerOutput, StringComparison.Ordinal);
        Assert.Null(result.Usage);
        Assert.Equal("unsupported", result.UsageUnavailableReason);
    }

    [Xunit.Fact]
    public void Parse_MalformedUsageShape_PreservesWorkerResultAndReportsMalformed()
    {
        var jsonl = """
            {"type":"item.completed","item":{"type":"agent_message","text":"WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT"}}
            {"type":"turn.completed","usage":"not-an-object"}
            """;

        var result = CodexJsonlUsageParser.Parse(jsonl);

        Assert.True(result.Recognized);
        Assert.Contains("WORKER_RESULT:", result.WorkerOutput, StringComparison.Ordinal);
        Assert.Null(result.Usage);
        Assert.Equal("malformed", result.UsageUnavailableReason);
    }

    [Xunit.Fact]
    public void Parse_MalformedLine_IsCountedWithoutChangingWorkerOutputContract()
    {
        var result = CodexJsonlUsageParser.Parse("""
            not-json
            {"type":"item.completed","item":{"type":"agent_message","text":"first"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"second"}}
            """);

        Assert.Equal(1, result.MalformedLineCount);
        Assert.Equal(["first", "second"], result.AgentMessages);
        Assert.Equal($"first{Environment.NewLine}second", result.WorkerOutput);
    }

    [Xunit.Fact]
    public void WithProviderUsage_AbsentFields_RemainTypedUnknown()
    {
        var bytes = Encoding.UTF8.GetBytes("section");
        var artifact = WorkerContextArtifact.Create(
            new LogicalArtifactIdentity("brief/current.md"),
            ContextArtifactKind.OperatorInstructions,
            bytes,
            [AgentRole.Developer],
            ContextDeliveryMode.InlineFull,
            ContextContractVersion.V1);
        var package = new WorkerContextPackageBuilder().Prepare(AgentRole.Developer, Path.GetTempPath(), [artifact]);

        var receipt = WorkerContextPackageBuilder.CreateReceipt(package)
            .WithProviderUsage(new ModelUsage(42, null, 11), "absent");

        Assert.Equal(42, receipt.InputTokens.Value);
        Assert.Equal(11, receipt.CachedInputTokens.Value);
        Assert.Equal(ProviderUsageState.Unknown, receipt.OutputTokens.State);
        Assert.Equal("absent", receipt.OutputTokens.UnknownReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(ProviderUsageState.Reported, null, null)]
    [Xunit.InlineData(ProviderUsageState.Unknown, 1L, "absent")]
    public void Constructor_ContradictoryState_Rejects(
        ProviderUsageState state,
        long? value,
        string? reason)
    {
        Assert.Throws<ArgumentException>(() => new ProviderUsageValue(state, value, reason));
    }

    [Xunit.Fact]
    public void TaggedUsageStateCannotBeMutatedThroughObjectInitializerOrWithExpression()
    {
        var properties = typeof(ProviderUsageValue).GetProperties();

        Assert.All(properties, property => Assert.Null(property.SetMethod));
    }

    [Xunit.Fact]
    public void ApplyRefreshOutcomeAttributesUsageToOriginatingAttempt()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Attribute delayed usage", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var firstAt = DateTimeOffset.Parse("2026-08-11T20:00:00Z");
        var secondAt = firstAt.AddMinutes(2);
        WorkerContextPackageReceipt Receipt(char id) => new(
            "ctxpkg-v1-sha256:" + new string(id, 64),
            [],
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"));
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "first", "C:\\repo", firstAt, ContextPackageReceipt: Receipt('a')));
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "second", "C:\\repo", secondAt, ContextPackageReceipt: Receipt('b')),
            allowPendingRecordedDispatchRefresh: true);
        var completedAt = secondAt.AddMinutes(1);
        var process = new TaskProcessRecord(
            1, "first", "C:\\repo", "out.log", "err.log", "exit.txt",
            firstAt.AddSeconds(1), completedAt, 1);
        var verification = new TaskVerificationRecord(
            "first", "C:\\repo", 1, "failed", string.Empty, completedAt);
        var outcome = new DispatchRefreshOutcome(
            process,
            verification,
            ProviderUsage: new ProviderReportedUsage(4_000_000_000L, 17, 3_000_000_000L),
            DispatchAttemptAt: firstAt);

        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, developer.Id, outcome);

        var task = kernel.GetTask(goal.Id, developer.Id);
        Assert.Equal(4_000_000_000L, task.DispatchHistory[0].ContextPackageReceipt!.InputTokens.Value);
        Assert.Equal(ProviderUsageState.Unknown, task.DispatchHistory[1].ContextPackageReceipt!.InputTokens.State);
        Assert.Null(task.LastProcess);
        Assert.Null(task.LastVerification);
    }

    [Xunit.Fact]
    public void DispatchAttemptTimestampIsMonotonicForUsageAttribution()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Allocate unambiguous attempt identity", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var dispatchedAt = DateTimeOffset.Parse("2026-08-12T20:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "first", "C:\\repo", dispatchedAt, ContextPackageReceipt: Receipt('a')));
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "second", "C:\\repo", dispatchedAt, ContextPackageReceipt: Receipt('b')),
            allowPendingRecordedDispatchRefresh: true);

        var history = kernel.GetTask(goal.Id, developer.Id).DispatchHistory;
        Assert.Equal(2, history.Count);
        Assert.Equal(dispatchedAt, history[0].DispatchedAt);
        Assert.Equal(dispatchedAt.AddTicks(1), history[1].DispatchedAt);

        ApplyUsage(history[0], 101);
        ApplyUsage(history[1], 202);

        history = kernel.GetTask(goal.Id, developer.Id).DispatchHistory;
        Assert.Equal(101, history[0].ContextPackageReceipt!.InputTokens.Value);
        Assert.Equal(202, history[1].ContextPackageReceipt!.InputTokens.Value);

        void ApplyUsage(TaskDispatchRecord dispatch, long inputTokens)
        {
            var completedAt = dispatch.DispatchedAt.AddSeconds(1);
            BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, developer.Id, new DispatchRefreshOutcome(
                new TaskProcessRecord(
                    checked((int)inputTokens),
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    $"{inputTokens}.out.log",
                    $"{inputTokens}.err.log",
                    $"{inputTokens}.exit.txt",
                    dispatch.DispatchedAt,
                    completedAt,
                    0),
                new TaskVerificationRecord(
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    0,
                    "passed",
                    string.Empty,
                    completedAt),
                ProviderUsage: new ProviderReportedUsage(inputTokens, 1, 1),
                DispatchAttemptAt: dispatch.DispatchedAt));
        }
    }

    [Xunit.Fact]
    public void NormalizeStructuredCodexOutput_Replay_UsesImmutableJsonlAudit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ctx-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputPath = Path.Combine(root, "worker.out.log");
            var originalJsonl = """
                {"type":"item.completed","item":{"type":"agent_message","text":"WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT"}}
                {"type":"turn.completed","usage":{"input_tokens":4000000000,"cached_input_tokens":3000000000,"output_tokens":2000000000}}
                """;
            File.WriteAllText(outputPath, originalJsonl);
            var dispatch = new TaskDispatchRecord(
                "developer",
                "codex exec --json",
                root,
                DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                ContextPackageReceipt: Receipt('a'));

            var first = BackgroundDispatchRunner.NormalizeStructuredCodexOutput(dispatch, outputPath);
            File.WriteAllText(outputPath, """
                {"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":2,"output_tokens":3}}
                """);
            var replay = BackgroundDispatchRunner.NormalizeStructuredCodexOutput(dispatch, outputPath);

            Assert.Equal(originalJsonl, File.ReadAllText(outputPath + ".jsonl"));
            Assert.Equal(first!.WorkerOutput, File.ReadAllText(outputPath));
            Assert.Equal(4_000_000_000L, replay!.Usage!.InputTokens);
            Assert.Equal(3_000_000_000L, replay.Usage.CachedInputTokens);
            Assert.Equal(2_000_000_000L, replay.Usage.OutputTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(ProviderKind.OpenAICodexCli)]
    [Xunit.InlineData(ProviderKind.OpenAICodexSpark)]
    public void NormalizeStructuredCodexOutput_JsonCommandWithoutContextPackageReceipt_PreservesWorkerDecision(
        ProviderKind providerKind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ctx-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputPath = Path.Combine(root, "worker.out.log");
            File.WriteAllText(outputPath, """
                {"type":"item.completed","item":{"type":"agent_message","text":"WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT"}}
                {"type":"turn.completed","usage":{"input_tokens":41,"cached_input_tokens":37,"output_tokens":5}}
                """);
            var dispatch = new TaskDispatchRecord(
                "ideation",
                "codex exec --json",
                root,
                DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
                WorkerProviderKind: providerKind);

            var result = BackgroundDispatchRunner.NormalizeStructuredCodexOutput(dispatch, outputPath);

            Assert.NotNull(result);
            Assert.Contains("WORKER_RESULT:", result.WorkerOutput, StringComparison.Ordinal);
            Assert.Equal(result.WorkerOutput, File.ReadAllText(outputPath));
            Assert.True(File.Exists(outputPath + ".jsonl"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void NormalizeStructuredCodexOutput_UnavailableAuditPath_PreservesUsageAndDoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ctx-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputPath = Path.Combine(root, "worker.out.log");
            var originalJsonl = """
                {"type":"item.completed","item":{"type":"agent_message","text":"WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT"}}
                {"type":"turn.completed","usage":{"input_tokens":41,"cached_input_tokens":37,"output_tokens":5}}
                """;
            File.WriteAllText(outputPath, originalJsonl);
            Directory.CreateDirectory(outputPath + ".jsonl");
            var dispatch = new TaskDispatchRecord(
                "developer",
                "codex exec --json",
                root,
                DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                ContextPackageReceipt: Receipt('a'));

            var result = BackgroundDispatchRunner.NormalizeStructuredCodexOutput(dispatch, outputPath);

            Assert.Equal(originalJsonl, File.ReadAllText(outputPath));
            Assert.Equal(41, result!.Usage!.InputTokens);
            Assert.Equal(37, result.Usage.CachedInputTokens);
            Assert.Equal(5, result.Usage.OutputTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void NormalizeStructuredCodexOutput_UnreadableSource_IsTypedUnreadable(bool unauthorized)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ctx-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputPath = Path.Combine(root, "worker.out.log");
            File.WriteAllText(outputPath, "present");
            var dispatch = new TaskDispatchRecord(
                "developer",
                "codex exec --json",
                root,
                DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                ContextPackageReceipt: Receipt('a'));

            var result = BackgroundDispatchRunner.NormalizeStructuredCodexOutput(
                dispatch,
                outputPath,
                _ => throw (unauthorized
                    ? new UnauthorizedAccessException("denied")
                    : new IOException("locked")));

            Assert.NotNull(result);
            Assert.False(result.Recognized);
            Assert.Null(result.Usage);
            Assert.Equal("unreadable", result.UsageUnavailableReason);
            Assert.Equal("present", File.ReadAllText(outputPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ApplyRefreshOutcome_ConflictingReplay_PreservesReportedUsage()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve authoritative usage", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var dispatchedAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "codex exec --json", "C:\\repo", dispatchedAt,
            ContextPackageReceipt: Receipt('a')));
        var completedAt = dispatchedAt.AddMinutes(1);
        var process = new TaskProcessRecord(
            1, "codex exec --json", "C:\\repo", "out.log", "err.log", "exit.txt",
            dispatchedAt.AddSeconds(1), completedAt, 0);
        var verification = new TaskVerificationRecord(
            "codex exec --json", "C:\\repo", 0, "passed", string.Empty, completedAt);

        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, developer.Id, new DispatchRefreshOutcome(
            process,
            verification,
            ProviderUsage: new ProviderReportedUsage(4_000_000_000L, 2_000_000_000L, 3_000_000_000L),
            DispatchAttemptAt: dispatchedAt));
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, developer.Id, new DispatchRefreshOutcome(
            process,
            verification,
            ProviderUsage: new ProviderReportedUsage(1, 3, 2),
            ProviderUsageUnavailableReason: "unsupported",
            DispatchAttemptAt: dispatchedAt));

        var receipt = kernel.GetTask(goal.Id, developer.Id).LastDispatch.ContextPackageReceipt!;
        Assert.Equal(4_000_000_000L, receipt.InputTokens.Value);
        Assert.Equal(3_000_000_000L, receipt.CachedInputTokens.Value);
        Assert.Equal(2_000_000_000L, receipt.OutputTokens.Value);
    }

    private static WorkerContextPackageReceipt Receipt(char id) => new(
        "ctxpkg-v1-sha256:" + new string(id, 64),
        [],
        ProviderUsageValue.Unknown("not-yet-reported"),
        ProviderUsageValue.Unknown("not-yet-reported"),
        ProviderUsageValue.Unknown("not-yet-reported"));
}
