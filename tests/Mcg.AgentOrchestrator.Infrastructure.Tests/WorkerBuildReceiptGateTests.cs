using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class WorkerBuildReceiptGateTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void TextClaimWithoutReceiptRunsConductorBuild()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature",
                "deferred - Invoke-WorkerBuildCheck passed with 0 errors"),
            string.Empty, clock, AddCompiledFeature);
        var builds = 0;
        new BackgroundDispatchRunner(clock, runOrchestratorBuildCheck: _ =>
        {
            builds++;
            return new(true, 0, "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1");
        }).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(1, builds);
        Xunit.Assert.Contains("build_evidence_producer=orchestrator", task.LastVerification!.StandardError);
        Xunit.Assert.Contains("worker_build_receipt=receipt-missing", task.LastVerification.StandardError);
    }

    [Xunit.Fact]
    public void StaleReceiptAfterEditRunsConductorBuild() =>
        VerifyReceiptGate("stale", expectedBuilds: 1, "digest-mismatch");

    [Xunit.Fact]
    public void MatchingReceiptSkipsConductorBuild() =>
        VerifyReceiptGate("matching", expectedBuilds: 0, null);

    [Xunit.Fact]
    public void FailureReceiptRunsConductorBuild() =>
        VerifyReceiptGate("failure", expectedBuilds: 1, "build-failed");

    [Xunit.Fact]
    public void ForeignWorktreeReceiptRunsConductorBuild() =>
        VerifyReceiptGate("foreign", expectedBuilds: 1, "worktree-mismatch");

    [Xunit.Theory]
    [Xunit.InlineData("bad-json", "receipt-malformed")]
    [Xunit.InlineData("unknown-schema", "schema-unknown")]
    public void MalformedReceiptRunsConductorBuild(string kind, string reason) =>
        VerifyReceiptGate(kind, expectedBuilds: 1, reason);

    private static void VerifyReceiptGate(string kind, int expectedBuilds, string? reason)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var receiptPath = Path.Combine(CreateTempDirectory(), WorkerBuildReceipt.FileName);
        var workerTests = kind == "matching"
            ? "deferred - acceptance gate owns tests"
            : "deferred - Invoke-WorkerBuildCheck passed with 0 errors";
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", workerTests),
            string.Empty, clock, worktree =>
            {
                AddCompiledFeature(worktree);
                Xunit.Assert.True(WorktreeTreeDigest.TryCompute(worktree, out var digest, out var failure), failure);
                File.WriteAllText(receiptPath, kind == "bad-json" ? "{" : JsonSerializer.Serialize(new
                {
                    schemaVersion = kind == "unknown-schema" ? 2 : 1,
                    treeDigest = digest,
                    digestAlgorithm = WorktreeTreeDigest.Algorithm,
                    buildOutcome = kind == "failure" ? "failure" : "success",
                    generatedAtUtc = "2026-08-22T12:00:00Z",
                    worktreeRoot = kind == "foreign" ? root : worktree,
                    configuration = "Debug",
                    projects = new[] { "src/Feature/Feature.csproj" }
                }));
                if (kind == "stale")
                    File.AppendAllText(Path.Combine(worktree, "src", "Feature", "Feature.cs"), "\npublic sealed class LaterEdit { }");
            });
        var builds = 0;
        new BackgroundDispatchRunner(clock,
            runOrchestratorBuildCheck: _ =>
            {
                builds++;
                return new(true, 0, "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1");
            },
            resolveWorkerBuildReceiptPath: _ => receiptPath)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(expectedBuilds, builds);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        if (reason is null)
            Xunit.Assert.DoesNotContain("build_evidence_producer=orchestrator", task.LastVerification.StandardError);
        else
            Xunit.Assert.Contains($"worker_build_receipt={reason}; build_evidence_producer=orchestrator", task.LastVerification.StandardError);
        Xunit.Assert.Equal(string.Empty, ReadGit(process.WorkingDirectory, ["status", "--short"]));
    }

    private static void AddCompiledFeature(string worktree)
    {
        var directory = Path.Combine(worktree, "src", "Feature");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Feature.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(directory, "Feature.cs"), "public sealed class Feature { }");
    }
}
