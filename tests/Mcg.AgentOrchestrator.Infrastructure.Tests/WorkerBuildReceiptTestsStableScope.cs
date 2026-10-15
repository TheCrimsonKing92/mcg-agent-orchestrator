using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerBuildReceiptTestsStableScope
{
    [Xunit.Fact]
    public void Evaluate_NewDispatchWithSameTree_ReusesReceiptWithoutBuild()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        fixture.Write("Core/Core.cs", "public class Core { public int Changed => 2; }");
        fixture.Commit();
        var before = WorkerBuildProjectScope.Resolve(fixture.Root, []);
        Assert.NotEmpty(before.Projects);
        var metadataBefore = fixture.Metadata();
        var receiptPath = WriteReceipt(fixture, before, before.StableMetadataDigest);

        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "next-task", "landing");

        var after = WorkerBuildProjectScope.Resolve(fixture.Root, []);
        Assert.NotEqual(metadataBefore.TaskId, fixture.Metadata().TaskId);
        Assert.NotEqual(metadataBefore.DispatchIdentity, fixture.Metadata().DispatchIdentity);
        Assert.NotEqual(before.MetadataDigest, after.MetadataDigest);
        Assert.Equal(before.StableMetadataDigest, after.StableMetadataDigest);
        var verdict = WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root);
        Assert.True(verdict.Matches, verdict.Reason);
        Assert.Equal("matched", verdict.Reason);
        var builds = 0;
        var resolution = Resolve(fixture, receiptPath, () => builds++);
        Assert.Equal(0, builds);
        Assert.False(resolution.FailsRound, resolution.Diagnostic);

        fixture.Change("Core");
        Assert.Equal("digest-mismatch", WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root).Reason);
        Resolve(fixture, receiptPath, () => builds++);
        Assert.Equal(1, builds);
    }

    [Xunit.Theory]
    [Xunit.InlineData("integration-branch")]
    [Xunit.InlineData("goal-baseline")]
    [Xunit.InlineData("legacy-receipt")]
    [Xunit.InlineData("wrong-stable-digest")]
    [Xunit.InlineData("blank-stable-digest")]
    [Xunit.InlineData("none-stable-digest")]
    [Xunit.InlineData("unresolved-stable-digest")]
    public void Evaluate_IncompatibleScope_RejectsReceipt(string scenario)
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        fixture.Change("Core");
        fixture.Commit();
        var before = WorkerBuildProjectScope.Resolve(fixture.Root, []);
        var stableDigest = scenario switch
        {
            "legacy-receipt" => null,
            "wrong-stable-digest" => "0000",
            "blank-stable-digest" => " ",
            "none-stable-digest" => "none",
            "unresolved-stable-digest" => "unresolved",
            _ => before.StableMetadataDigest
        };
        var receiptPath = WriteReceipt(fixture, before, stableDigest);
        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "next-task", "landing");
        if (scenario == "integration-branch")
            fixture.SetMetadata(fixture.Metadata() with { IntegrationBranch = "other" });
        if (scenario == "goal-baseline")
        {
            var ancestor = fixture.Git("rev-parse", "HEAD").Trim();
            Assert.NotEqual(fixture.Metadata().GoalBaseline, ancestor);
            fixture.SetMetadata(fixture.Metadata() with { GoalBaseline = ancestor });
        }

        Assert.Equal("scope-mismatch", WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root).Reason);
    }

    private static OrchestratorBuildEvidenceResolution Resolve(
        WorkerBuildProjectScopeTests.Fixture fixture, string receiptPath, Action built) =>
        OrchestratorBuildEvidenceCheck.Resolve(fixture.Root, AgentRole.Tester, [], [], false,
            () => WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root),
            _ => { built(); return new(true, 0, "PASS build: 0 errors"); });

    private static string WriteReceipt(WorkerBuildProjectScopeTests.Fixture fixture,
        WorkerBuildProjectScope.Result scope, string? stableDigest)
    {
        Assert.True(WorktreeTreeDigest.TryCompute(fixture.Root, out var digest, out var failure), failure);
        var artifacts = Path.Combine(fixture.Root, ".orchestrator-context", "build-artifacts");
        Directory.CreateDirectory(artifacts);
        var executions = scope.Projects.Select((project, index) =>
        {
            var logPath = Path.Combine(artifacts, $"build-{index}.log");
            File.WriteAllText(logPath, "Build succeeded.");
            return new { project, exitCode = 0, logPath };
        }).ToArray();
        var receipt = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["treeDigest"] = digest,
            ["digestAlgorithm"] = WorktreeTreeDigest.Algorithm,
            ["buildOutcome"] = "success",
            ["generatedAtUtc"] = "2026-10-14T22:00:00Z",
            ["worktreeRoot"] = fixture.Root,
            ["configuration"] = "Debug",
            ["projects"] = scope.Projects,
            ["projectResults"] = executions,
            ["scopeMetadataDigest"] = scope.MetadataDigest
        };
        if (stableDigest is not null) receipt["scopeStableDigest"] = stableDigest;
        var path = Path.Combine(artifacts, WorkerBuildReceipt.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(receipt));
        return path;
    }
}
