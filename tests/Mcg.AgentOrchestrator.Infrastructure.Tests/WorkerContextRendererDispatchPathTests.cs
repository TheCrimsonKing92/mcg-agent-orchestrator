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
        var expectedIdentities = ExpectedRequiredArtifactIdentities(fixture, path);
        Assert.Equal(
            expectedIdentities,
            receipt.Sections.Select(section => section.LogicalIdentity).ToHashSet(StringComparer.Ordinal));
        foreach (var expectedIdentity in expectedIdentities)
        {
            var section = Assert.Single(receipt.Sections, candidate =>
                candidate.LogicalIdentity.Equals(expectedIdentity, StringComparison.Ordinal));
            Assert.Equal(
                section.DeliveryMode == ContextDeliveryMode.HistoricalFile ? 0 : 1,
                CountOccurrences(prompt, $"identity={expectedIdentity};"));
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

        Assert.Equal(
            path == DispatchPath.CompactRepair
                ? ReviewFindingHistoryProjectionMode.ContractRepair
                : path == DispatchPath.RetryFeedback
                    ? ReviewFindingHistoryProjectionMode.FullInspection
                    : null,
            receipt.ReviewFindingProjectionMode);
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
    public void TypedAndLegacyPathsPreserveIdentityRoleAndBudgetSelections(AgentRole role)
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
        Assert.All(source.Segments, segment => Assert.Equal([role], segment.RoleVisibility));
        Assert.Equal(
            typedIdentities,
            source.BudgetDecisions.Select(decision => decision.Identity).ToHashSet());
        foreach (var decision in source.BudgetDecisions)
        {
            var selectedSegment = Assert.Single(source.Segments, segment =>
                segment.TypedProjectionIdentity == decision.Identity);
            var expectedLegacyBlock = string.Join(
                Environment.NewLine,
                new[] { WorkerContextProjectionBoundary.Start(decision.Identity) }
                    .Concat(selectedSegment.Lines.Select(WorkerContextProjectionBoundary.EscapeReservedLiteral))
                    .Append(WorkerContextProjectionBoundary.End(decision.Identity)));

            Assert.Contains(expectedLegacyBlock, legacy.Content, StringComparison.Ordinal);
            Assert.Equal(
                decision.Disposition == TaskBriefBudgetDisposition.Collapsed,
                selectedSegment.Lines.Any(line =>
                    line.Contains("collapsed to stay under", StringComparison.Ordinal)));
        }
    }

    private static DispatchFixture CreateDispatchFixture(
        DispatchPath path,
        string? objectiveSuffix = null)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var headCommit = InitializeRepository(workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Render typed worker context.", AgentRole.Developer);
        var objective = "Render the worker context exactly once." +
                        (objectiveSuffix is null ? string.Empty : Environment.NewLine + objectiveSuffix);
        var goal = kernel.CreateGoal(objective, [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (path != DispatchPath.Initial)
        {
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
                    ReviewedCommit: headCommit,
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

        return new DispatchFixture(root, workingDirectory, kernel, goal, task, headCommit);
    }

    private static WorkerProfile FakeHarnessProfile() =>
        new("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}");

    private static HashSet<string> ExpectedRequiredArtifactIdentities(
        DispatchFixture fixture,
        DispatchPath path)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "goal/objective.md",
            "task/description.md",
            "task/metadata.json",
            "task/criterion-retry-feedback.json",
            "goal/timeline.json",
            "brief/header-residual.md",
            "brief/current.md",
            "context/manifest.v1.json"
        };
        var contextDirectory = Path.Combine(
            fixture.WorkingDirectory,
            ".orchestrator-context",
            fixture.Goal.Id.Value);
        using var registry = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")));
        foreach (var artifact in registry.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            var pathValue = artifact.GetProperty("path").GetString()!;
            var visible = artifact.GetProperty("roleVisibility").EnumerateArray()
                .Select(item => item.GetString())
                .Contains(fixture.Task.RequiredRole.ToString(), StringComparer.Ordinal);
            if (visible && WorkerProfileDispatcher.DeliverableRegistryArtifactPaths.Contains(
                    pathValue,
                    StringComparer.Ordinal))
            {
                expected.Add($"context/{pathValue}");
            }
        }

        if (path == DispatchPath.Initial)
        {
            return expected;
        }

        var projection = ReviewFindingContextProjector.Project(
            fixture.Goal,
            fixture.Task,
            fixture.HeadCommit,
            fixture.HeadCommit);
        expected.Add("goal/review-finding-history.json");
        expected.UnionWith(projection.RoundBodies.Concat(projection.ReceiptBodies)
            .Select(body => body.LogicalIdentity));
        if (fixture.Task.LastVerification is { } lastVerification)
        {
            expected.Add("task/last-verification/stdout");
            if (lastVerification.AuthoritativeStandardError is not null)
            {
                expected.Add("task/last-verification/stderr");
            }
        }

        if (path != DispatchPath.CompactRepair)
        {
            return expected;
        }

        Assert.Equal(ReviewFindingHistoryProjectionMode.ContractRepair, projection.Metrics.Mode);
        expected.Add("task/review-contract-repair-envelope.json");
        return expected;
    }

    private static string InitializeRepository(string workingDirectory)
    {
        AssertGitSucceeded(GitCli.Run(workingDirectory, "init", "--initial-branch=main"));
        File.WriteAllText(Path.Combine(workingDirectory, "fixture.txt"), "worker context fixture");
        AssertGitSucceeded(GitCli.Run(workingDirectory, "add", "fixture.txt"));
        AssertGitSucceeded(GitCli.Run(
            workingDirectory,
            "-c", "user.name=Worker Context Tests",
            "-c", "user.email=worker-context-tests@example.invalid",
            "commit", "--quiet", "-m", "fixture"));
        var head = GitCli.Run(workingDirectory, "rev-parse", "HEAD");
        AssertGitSucceeded(head);
        return head.Output.Trim();
    }

    private static void AssertGitSucceeded(GitCli.GitResult result) =>
        Assert.True(
            result.Succeeded && !result.DrainTimedOut,
            $"git fixture command failed: exit={result.ExitCode}; stderr={result.Error}");

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
        TaskSpec Task,
        string HeadCommit);
}
