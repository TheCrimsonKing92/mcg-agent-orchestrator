using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Class-owned template; each invocation owns an isolated copy and never changes process environment.
public sealed class WorkerBuildScopeDispatchBindingTests(WorkerBuildScopeDispatchFixtureTemplate template)
    : IClassFixture<WorkerBuildScopeDispatchFixtureTemplate>
{
    [Theory]
    [InlineData("goal")]
    [InlineData("task")]
    [InlineData("attempt")]
    [InlineData("worktree")]
    [InlineData("dispatch-base")]
    [InlineData("pinned-baseline")]
    [InlineData("missing-contents")]
    [InlineData("missing-binding")]
    [InlineData("corrupt-base64")]
    [InlineData("corrupt-json")]
    [InlineData("integration")]
    [InlineData("nonce")]
    [InlineData("contents-goal")]
    [InlineData("contents-task")]
    [InlineData("contents-worktree")]
    [InlineData("contents-common")]
    [InlineData("contents-branch")]
    [InlineData("contents-dispatch-base")]
    [InlineData("contents-baseline")]
    public void ForeignOrCorruptDispatchAuthorityFailsClosed(string scenario)
    {
        using var fixture = template.Copy();
        fixture.Prepare(fixture.Successor);
        var context = fixture.Context();
        var dispatch = context.Dispatch;
        var identity = dispatch.WorkerBuildScope!;
        context = scenario switch
        {
            "goal" => context with { GoalId = GoalId.New() },
            "task" => context with { TaskId = TaskId.New() },
            _ => context
        };
        dispatch = scenario switch
        {
            "attempt" => dispatch with { DispatchedAt = dispatch.DispatchedAt.AddSeconds(1) },
            "worktree" => dispatch with { WorkingDirectory = fixture.Main },
            "dispatch-base" => dispatch with { BaseCommit = WorkerBuildScopeDispatchFixture.Git(fixture.Root, "rev-parse", "HEAD").Trim() },
            _ => dispatch
        };
        identity = scenario switch
        {
            "pinned-baseline" => identity with { GoalBaseline = new string('a', 40) },
            "missing-contents" => identity with { RetainedMetadataBase64 = null },
            "missing-binding" => identity with { CapturedDispatchAt = null },
            "corrupt-base64" => identity with { RetainedMetadataBase64 = "invalid base64!" },
            "corrupt-json" => identity with { RetainedMetadataBase64 = Convert.ToBase64String("{}"u8) },
            "integration" => Mutate(identity, metadata => metadata with { IntegrationBranch = "foreign" }),
            "nonce" => Mutate(identity, metadata => metadata with { DispatchIdentity = "foreign" }),
            // Even self-consistent bytes plus a full digest cannot replace authoritative owner checks.
            "contents-goal" => Mutate(identity, metadata => metadata with { GoalId = GoalId.New().Value }, true),
            "contents-task" => Mutate(identity, metadata => metadata with { TaskId = TaskId.New().Value }, true),
            "contents-worktree" => Mutate(identity, metadata => metadata with { WorktreeRoot = fixture.Main }, true),
            "contents-common" => Mutate(identity, metadata => metadata with { GitCommonDirectory = fixture.Root }, true),
            "contents-branch" => Mutate(identity, metadata => metadata with { Branch = "goal/foreign" }, true),
            "contents-dispatch-base" => Mutate(identity, metadata => metadata with
                { DispatchBase = WorkerBuildScopeDispatchFixture.Git(fixture.Root, "rev-parse", "HEAD").Trim() }, true),
            "contents-baseline" => Mutate(identity, metadata => metadata with
                { GoalBaseline = WorkerBuildScopeDispatchFixture.Git(fixture.Root, "rev-parse", "HEAD").Trim() }, true),
            _ => identity
        };
        context = context with { Dispatch = dispatch with { WorkerBuildScope = identity } };

        if (scenario == "corrupt-base64") Assert.Throws<FormatException>(() => context.Resolve(fixture.Root, []));
        else Assert.Throws<IOException>(() => context.Resolve(fixture.Root, []));
        var receipt = WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Root, null, context);
        Assert.False(receipt.Matches);
        Assert.Equal("scope-invalid", receipt.Reason);
        var builds = 0;
        var resolution = OrchestratorBuildEvidenceCheck.Resolve(fixture.Root, AgentRole.Developer, [], [], false,
            () => receipt, _ => { builds++; return new(true, 0, "Unexpected fallback build."); }, completedDispatch: context);
        Assert.True(resolution.MissingEvidence);
        Assert.StartsWith("worker_build_scope=invalid;", resolution.Diagnostic);
        Assert.Equal(0, builds);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("staged")]
    [InlineData("committed")]
    [InlineData("untracked")]
    public void ChangedRequiredInputsCannotReusePredecessorReceipt(string change)
    {
        using var fixture = template.Copy();
        fixture.Prepare(fixture.Successor);
        WorkerBuildScopeDispatchFixture.Write(fixture.Root, change == "untracked" ? "Core/New.cs" : "Core/Core.cs",
            "public class ChangedInput { }");
        if (change == "staged") WorkerBuildScopeDispatchFixture.Git(fixture.Root, "add", "Core/Core.cs");
        if (change == "committed") fixture.Commit();

        Assert.Equal("digest-mismatch", fixture.Receipt().Reason);
        Assert.True(fixture.Resolve().MissingEvidence);
        Assert.Equal(1, fixture.Builds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingCandidateWithIncompleteProjectCoverageFailsClosed(bool staged)
    {
        using var fixture = template.Copy();
        fixture.Prepare(fixture.Successor);
        WorkerBuildScopeDispatchFixture.Write(fixture.Root, "Other/Other.csproj", "<Project />");
        WorkerBuildScopeDispatchFixture.Write(fixture.Root, "Other/Other.cs", "public class Other { }");
        if (staged) WorkerBuildScopeDispatchFixture.Git(fixture.Root, "add", "Other");
        Assert.Contains("Other/Other.csproj", fixture.Context().Resolve(fixture.Root, []).RequiredProjects);
        fixture.WriteReceipt(fixture.Predecessor, ["Core/Core.csproj"]);
        Assert.True(fixture.Receipt().Matches);

        var resolution = fixture.Resolve();

        Assert.True(resolution.MissingEvidence);
        Assert.Contains("projects-incomplete", resolution.Diagnostic);
        Assert.Contains("Other/Other.csproj", resolution.Diagnostic);
        Assert.Equal(1, fixture.Builds);
    }

    [Fact]
    public void CurrentWorkerCannotNarrowPinnedScopeToCertifyIncompleteBuild()
    {
        using var fixture = template.Copy();
        fixture.Prepare(fixture.Successor);
        var path = WorkerBuildProjectScope.MetadataPath(fixture.Root);
        var metadata = JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(File.ReadAllBytes(path))!;
        var tampered = metadata with { GoalBaseline = metadata.DispatchBase };
        File.WriteAllText(path, JsonSerializer.Serialize(tampered));
        var narrowed = WorkerBuildProjectScope.Resolve(fixture.Root, []);
        Assert.Empty(narrowed.RequiredProjects);
        Assert.NotEmpty(fixture.Context(fixture.Successor).Resolve(fixture.Root, []).RequiredProjects);
        Assert.Throws<IOException>(() => WorkerBuildProjectScope.Resolve(fixture.Root, [],
            expectedMetadataDigest: fixture.Successor.LastDispatch!.WorkerBuildScope!.MetadataDigest));
        fixture.WriteReceipt(fixture.Successor, []);
        var receipt = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.ReceiptPath))!;
        receipt["scopeMetadataDigest"] = narrowed.MetadataDigest;
        receipt["scopeStableDigest"] = narrowed.StableMetadataDigest;
        File.WriteAllText(fixture.ReceiptPath, receipt.ToJsonString());

        Assert.Equal("scope-mismatch", fixture.Receipt(fixture.Successor).Reason);
        Assert.True(fixture.Resolve(fixture.Successor).MissingEvidence);
    }

    [Fact]
    public void LegacyRecordOnlyAcceptsItsStillProvableCurrentScope()
    {
        using var fixture = template.Copy();
        var dispatch = fixture.Predecessor.LastDispatch!;
        var identity = dispatch.WorkerBuildScope!;
        var legacy = fixture.Context() with { Dispatch = dispatch with
            { WorkerBuildScope = new(identity.GoalBaseline, identity.MetadataDigest) } };
        Assert.NotEmpty(legacy.Resolve(fixture.Root, []).RequiredProjects);
        Assert.True(WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Root, null, legacy).Matches);
        fixture.Prepare(fixture.Successor);
        Assert.Throws<IOException>(() => legacy.Resolve(fixture.Root, []));
        Assert.Equal("scope-invalid", WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Root, null, legacy).Reason);
    }

    [Fact]
    public void MissingRetainedAndCurrentContentsCannotCertifyLegacyDispatch()
    {
        using var fixture = template.Copy();
        var dispatch = fixture.Predecessor.LastDispatch!;
        var identity = dispatch.WorkerBuildScope!;
        var legacy = fixture.Context() with { Dispatch = dispatch with
            { WorkerBuildScope = new(identity.GoalBaseline, identity.MetadataDigest) } };
        File.Delete(WorkerBuildProjectScope.MetadataPath(fixture.Root));
        Assert.Throws<IOException>(() => legacy.Resolve(fixture.Root, []));
        Assert.Equal("scope-invalid", WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Root, null, legacy).Reason);
        Assert.True(fixture.Receipt().Matches); // Retained authority does not require the mutable file.
        Assert.False(fixture.Resolve().FailsRound);
    }

    [Fact]
    public void NonDotnetWithoutScopeKeepsExistingCompletionBehavior()
    {
        var root = CreateTempDirectory();
        try
        {
            var resolution = OrchestratorBuildEvidenceCheck.Resolve(root, AgentRole.Developer, ["README.md"], [], false,
                () => throw new InvalidOperationException("No receipt required."),
                _ => throw new InvalidOperationException("No build required."));
            Assert.False(resolution.FailsRound);
        }
        finally { ScriptSandboxCleanup.DeleteOrThrow([root]); }
    }

    [Fact]
    public void ScopeWithoutCapturedAuthorityCannotCertifyTrackedCompletion()
    {
        using var fixture = template.Copy();
        var unbound = fixture.Context() with { Dispatch = fixture.Predecessor.LastDispatch! with { WorkerBuildScope = null } };
        Assert.Throws<IOException>(() => unbound.Resolve(fixture.Root, []));
        Assert.Equal("scope-invalid", WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Root, null, unbound).Reason);
    }

    private static WorkerBuildScopeIdentity Mutate(WorkerBuildScopeIdentity identity,
        Func<WorkerBuildProjectScope.Metadata, WorkerBuildProjectScope.Metadata> change, bool captureDigest = false)
    {
        var metadata = JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(Convert.FromBase64String(identity.RetainedMetadataBase64!))!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(change(metadata));
        return identity with { RetainedMetadataBase64 = Convert.ToBase64String(bytes),
            MetadataDigest = captureDigest ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) : identity.MetadataDigest };
    }
}
