using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextRendererDispatchPathTests : WorkerDispatchTestSupport
{
    public enum DispatchPath
    {
        Initial,
        RetryFeedback,
        CompactRepair
    }

    [Xunit.Theory]
    [Xunit.InlineData(DispatchPath.Initial)]
    [Xunit.InlineData(DispatchPath.RetryFeedback)]
    [Xunit.InlineData(DispatchPath.CompactRepair)]
    public void RealDispatcherRendersEveryRequiredArtifactOnceWithoutGeneratedBoundaries(DispatchPath path)
    {
        var fixture = CreateDispatchFixture(path);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var prompt = File.ReadAllText(prepared.PromptPath);
        var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
        foreach (var section in receipt.Sections.Where(section =>
                     section.DeliveryMode != ContextDeliveryMode.HistoricalFile))
        {
            Assert.Equal(1, CountOccurrences(prompt, $"identity={section.LogicalIdentity};"));
        }

        Assert.DoesNotContain(WorkerContextProjectionBoundary.StartPrefix, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerContextProjectionBoundary.EndPrefix, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerContextProjectionBoundary.LiteralPrefix, prompt, StringComparison.Ordinal);
        if (path != DispatchPath.Initial)
        {
            Assert.NotNull(receipt.RetryFeedbackPromptReceipt);
            Assert.Equal(
                WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes("changed retry feedback")),
                receipt.RetryFeedbackPromptReceipt!.AcceptedFeedbackSha256);
        }
    }

    [Xunit.Fact]
    public void ForbiddenRoleArtifactIsAbsentFromRenderedPrompt()
    {
        var fixture = CreateDispatchFixture(DispatchPath.Initial);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var contextDirectory = Path.Combine(
            fixture.WorkingDirectory,
            ".orchestrator-context",
            fixture.Goal.Id.Value);
        Assert.True(File.Exists(Path.Combine(contextDirectory, "deterministic-verification.md")));
        Assert.DoesNotContain(
            "context/deterministic-verification.md",
            File.ReadAllText(prepared.PromptPath),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MandatoryArtifactWithMismatchedStoredHashIsRejectedBeforeRendering()
    {
        var root = CreateTempDirectory();
        var relativePath = ".orchestrator-context/test/mandatory.md";
        var materializedPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(materializedPath)!);
        File.WriteAllText(materializedPath, "corrupted bytes");
        var identity = new LogicalArtifactIdentity("context/mandatory.md");
        var expectedBytes = Encoding.UTF8.GetBytes("expected bytes");
        var artifact = WorkerContextArtifact.Create(
            identity,
            ContextArtifactKind.RegisteredContext,
            authoritativeBytes: null,
            [AgentRole.Developer],
            ContextDeliveryMode.MandatoryFile,
            ContextContractVersion.V1,
            relativePath,
            expectedContentHash: WorkerContextArtifact.Hash(expectedBytes),
            authoritativeByteCount: expectedBytes.Length);
        var package = new WorkerContextPackage(
            "ctxpkg-negative-control",
            ContextContractVersion.V1,
            AgentRole.Developer,
            [artifact]);
        var source = new TaskBriefSource(
            GoalId.New(),
            TaskId.New(),
            AgentRole.Developer,
            "negative control",
            [
                TaskBriefSegment.Fixed(["## Instructions"])
                    with { RoleVisibility = [AgentRole.Developer] },
                TaskBriefSegment.Projected(identity.Value, ["mandatory evidence"])
                    with { RoleVisibility = [AgentRole.Developer] }
            ],
            [new TaskBriefBudgetDecision(identity, TaskBriefBudgetDisposition.Full)]);

        var error = Assert.Throws<WorkerContextPreparationException>(() =>
            WorkerContextRenderer.Render(
                source,
                package,
                root,
                WorkerContextDeliveryPolicy.Initial));

        Assert.Equal("typed-identity-hash-mismatch", error.Reason);
    }

    [Xunit.Fact]
    public void ChangedRetryFeedbackItemAppearsInRetryPromptAndPassesAttestation()
    {
        var fixture = CreateDispatchFixture(DispatchPath.RetryFeedback);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var prompt = File.ReadAllText(prepared.PromptPath);
        var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
        Assert.NotNull(receipt.RetryFeedbackPromptReceipt);
        Assert.Contains("identity=task/criterion-retry-feedback.json;", prompt, StringComparison.Ordinal);
        Assert.Equal(
            WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes("changed retry feedback")),
            receipt.RetryFeedbackPromptReceipt!.AcceptedFeedbackSha256);
        Assert.Equal(
            WorkerContextArtifact.Hash(File.ReadAllBytes(prepared.PromptPath)),
            receipt.RetryFeedbackPromptReceipt.GeneratedPromptSha256);
    }

    [Xunit.Fact]
    public void UserTextContainingLiteralProjectionMarkerSurvivesRenderingUnchanged()
    {
        var literal = WorkerContextProjectionBoundary.Start(
            new LogicalArtifactIdentity("user/literal.md"));
        var fixture = CreateDispatchFixture(DispatchPath.Initial, literal);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.Contains(literal, RecoverInlineText(prompt, "goal/objective.md"), StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerContextProjectionBoundary.LiteralPrefix, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerContextProjectionBoundary.EndPrefix, prompt, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void TypedAndLegacyPathsDeliverIdenticalArtifactIdentitySets(AgentRole role)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), $"Exercise {role} typed selection parity.", role);
        var goal = kernel.CreateGoal("Compare typed and legacy artifact identity sets.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var contextDirectory = WorkerContextArtifacts.Write(
            goal,
            task,
            workingDirectory,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var source = kernel.BuildTaskBriefSource(
            goal.Id,
            task.Id,
            workingDirectory: workingDirectory,
            contextDirectory: contextDirectory,
            measureWithTypedSourceBoundaries: true);
        var legacy = kernel.BuildTaskBrief(
            goal.Id,
            task.Id,
            workingDirectory: workingDirectory,
            contextDirectory: contextDirectory,
            emitTypedSourceBoundaries: true);
        var parsed = WorkerContextProjectionResidual.ParseLegacyMarkedTextV1(
            legacy.Content,
            role);
        var typedIdentities = source.Segments
            .Where(segment => segment.TypedProjectionIdentity is not null)
            .Select(segment => segment.TypedProjectionIdentity.GetValueOrDefault())
            .ToHashSet();

        Assert.Equal(typedIdentities, parsed.ProjectedIdentities.ToHashSet());
        Assert.All(source.Segments, segment => Assert.Contains(role, segment.RoleVisibility!));
        Assert.Equal(
            typedIdentities,
            source.BudgetDecisions.Select(decision => decision.Identity).ToHashSet());
        Assert.All(source.BudgetDecisions, decision =>
            Assert.True(Enum.IsDefined(decision.Disposition)));
    }

    private static DispatchFixture CreateDispatchFixture(
        DispatchPath path,
        string? objectiveSuffix = null)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Render typed worker context.", AgentRole.Developer);
        var objective = "Render the worker context exactly once." +
                        (objectiveSuffix is null ? string.Empty : Environment.NewLine + objectiveSuffix);
        var goal = kernel.CreateGoal(objective, [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (path != DispatchPath.Initial)
        {
            var candidateSha = new string('a', 40);
            var location = new ReviewFindingLocation("src/Worker.cs", "Worker.Render");
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    "review",
                    workingDirectory,
                    1,
                    "review requires retry",
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    ReviewFindingTouchedAnchors: [location],
                    ReviewedCommit: candidateSha,
                    MergedReviewFindings:
                    [
                        new ReviewFinding("stable-render", ReviewFindingState.Open, location, "render once")
                    ],
                    ReviewFindingContractViolation: path == DispatchPath.CompactRepair
                        ? new ReviewFindingContractViolation("schema-invalid", "missing fields")
                        : null,
                    FullStandardOutput: "review requires retry",
                    FullStandardError: string.Empty));
            kernel.RetryTaskWithAuthoritativeFeedback(
                goal.Id,
                task.Id,
                "changed retry feedback",
                RetryCause.ContractClarification,
                retryRoundKind: path == DispatchPath.CompactRepair
                    ? RetryRoundKind.Mechanical
                    : RetryRoundKind.Standard);
        }

        return new DispatchFixture(root, workingDirectory, kernel, goal, task);
    }

    private static WorkerProfile FakeHarnessProfile() =>
        new("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}");

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var cursor = 0;
        while ((cursor = content.IndexOf(value, cursor, StringComparison.Ordinal)) >= 0)
        {
            count++;
            cursor += value.Length;
        }

        return count;
    }

    private static string RecoverInlineText(string prompt, string identity)
    {
        var header = $"INLINE FULL: identity={identity};";
        var headerStart = prompt.IndexOf(header, StringComparison.Ordinal);
        Assert.True(headerStart >= 0, $"Inline artifact '{identity}' was not rendered.");
        var payloadStart = prompt.IndexOf('\n', headerStart);
        Assert.True(payloadStart >= 0, $"Inline artifact '{identity}' has no payload.");
        payloadStart++;
        var payloadEnd = prompt.IndexOf(Environment.NewLine + Environment.NewLine, payloadStart, StringComparison.Ordinal);
        var payload = payloadEnd < 0 ? prompt[payloadStart..] : prompt[payloadStart..payloadEnd];
        return JsonSerializer.Deserialize<string>(payload)
            ?? throw new InvalidOperationException($"Inline artifact '{identity}' decoded to null.");
    }

    private sealed record DispatchFixture(
        string Root,
        string WorkingDirectory,
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Task);
}
