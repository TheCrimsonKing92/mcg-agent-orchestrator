using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each case owns its retention root, Go sources and artifacts; the sweep clock is fixed.
public sealed class NormalizedResultRetentionScenarioTests
{
    [Fact]
    public void ReadIdentities_NormalizedOnlyReceipt_RetainsDeclaredAttemptOwnership()
    {
        using var fixture = new RetentionReclaimFixture();
        var goalDirectory = Path.Combine(fixture.AttemptRoot(), RetentionReclaimFixture.FailedId);
        Directory.CreateDirectory(goalDirectory);
        var normalized = Path.Combine(goalDirectory, "crosswalk.ctrf.json");
        var nested = Path.Combine(goalDirectory, "nested.ctrf.json");
        var outside = Path.Combine(fixture.ExecutionDirectory, "outside.ctrf.json");
        File.WriteAllText(Path.Combine(goalDirectory, "a1.attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = "a1", goalId = RetentionReclaimFixture.FailedId, ordinal = 1,
            startedAt = RetentionReclaimFixture.Now, outcome = 1, reconciledAt = RetentionReclaimFixture.Now,
            normalizedTestResultPaths = new[] { normalized, outside },
            checks = new[] { new { normalizedTestResultPaths = new[] { nested } } }
        }));
        Assert.True(StorageRetentionMaintenance.TryReadAttemptIdentities(goalDirectory,
            RetentionReclaimFixture.FailedId, out var attempts, out var reason), reason);
        Assert.Equal(new[] { normalized, nested }.Order(StringComparer.OrdinalIgnoreCase), Assert.Single(attempts).DeclaredPaths);
        Assert.DoesNotContain(outside, Assert.Single(attempts).DeclaredPaths!);
    }

    [Fact]
    public void WithNormalizedPeer_TrxPaths_AddsPeerAndKeepsOtherInputs()
    {
        using var fixture = new RetentionReclaimFixture();
        var path = Path.Combine(fixture.ExecutionDirectory, "a.go-test.trx");
        Assert.Equal(new[] { path, NormalizedTestResultWriter.PathFor(path) },
            NormalizedTestResultRetentionPaths.WithNormalizedPeer(path));
        var upper = Path.Combine(fixture.ExecutionDirectory, "A.TRX");
        Assert.Equal(new[] { upper, NormalizedTestResultWriter.PathFor(upper) },
            NormalizedTestResultRetentionPaths.WithNormalizedPeer(upper));
        foreach (var other in new[] { Path.Combine(fixture.ExecutionDirectory, "a.log"), null, "" })
            Assert.Equal(new[] { other }, NormalizedTestResultRetentionPaths.WithNormalizedPeer(other));
    }

    [Fact]
    public void ReadIdentities_DeclaredTrx_OwnsOnlyContainedPeerAndOriginalPaths()
    {
        using var fixture = new RetentionReclaimFixture();
        var goalDirectory = Path.Combine(fixture.AttemptRoot(), RetentionReclaimFixture.FailedId);
        Directory.CreateDirectory(goalDirectory);
        var stdout = Path.Combine(goalDirectory, "a1.out.log");
        var trx = Path.Combine(goalDirectory, "a1.go-test.trx");
        var extra = Path.Combine(goalDirectory, "a1.extra.log");
        var outside = Path.Combine(fixture.ExecutionDirectory, "outside.trx");
        File.WriteAllText(Path.Combine(goalDirectory, "a1.attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = "a1",
            goalId = RetentionReclaimFixture.FailedId,
            ordinal = 1,
            startedAt = RetentionReclaimFixture.Now,
            outcome = 1,
            reconciledAt = RetentionReclaimFixture.Now,
            stdoutPath = stdout,
            testResultPaths = new[] { trx, extra, outside }
        }));

        Assert.True(StorageRetentionMaintenance.TryReadAttemptIdentities(
            goalDirectory, RetentionReclaimFixture.FailedId, out var attempts, out var reason), reason);
        var attempt = Assert.Single(attempts);
        var expected = new[] { stdout, trx, NormalizedTestResultWriter.PathFor(trx), extra }
            .Select(Path.GetFullPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected, attempt.DeclaredPaths);
        Assert.DoesNotContain(Path.GetFullPath(outside), attempt.DeclaredPaths!);
        Assert.DoesNotContain(Path.GetFullPath(NormalizedTestResultWriter.PathFor(outside)), attempt.DeclaredPaths!);
    }

    [Theory]
    [InlineData("acceptance-gate-attempts")]
    [InlineData("pre-review-evidence-attempts")]
    public void Sweep_DeclaredPeers_FollowsAttemptRetentionAcrossTwoRuns(string family)
    {
        using var fixture = new RetentionReclaimFixture();
        var goal = fixture.Goal(RetentionReclaimFixture.FailedId, GoalStatus.Failed);
        var goalDirectory = Path.Combine(fixture.AttemptRoot(family), goal.GoalId);
        Directory.CreateDirectory(goalDirectory);
        var sources = Path.Combine(fixture.ExecutionDirectory, "go-sources");
        Directory.CreateDirectory(sources);
        GoTestJsonGateReceiptTests.CreateGoSources(sources);
        var old = WriteGoAttempt(goalDirectory, sources, "old", ordinal: 1, days: 30);
        var recent = WriteGoAttempt(goalDirectory, sources, "recent", ordinal: 2, days: 2);
        var final = WriteGoAttempt(goalDirectory, sources, "final", ordinal: 3, days: 30);
        // The recent shadow owner survives both runs; an expired old owner would be reclaimed.
        var control = fixture.WriteAgedFile(Path.Combine(goalDirectory, "recent.extra.ctrf.json"), days: 30);
        var oldTrx = Assert.Single(old.TestResultPaths!);
        var oldPeer = Assert.Single(old.NormalizedTestResultPaths!);
        var recentPeer = Assert.Single(recent.NormalizedTestResultPaths!);
        var finalTrx = Assert.Single(final.TestResultPaths!);
        var finalPeer = Assert.Single(final.NormalizedTestResultPaths!);
        foreach (var path in new[] { oldTrx, oldPeer, recentPeer, finalTrx, finalPeer, control })
            Assert.True(File.Exists(path), path);

        var first = fixture.Run(null, goal);

        Assert.False(File.Exists(oldTrx));
        Assert.True(File.Exists(oldTrx + ".test-identities.json"));
        Assert.False(File.Exists(oldPeer));
        var deleted = Assert.Single(first.Decisions, decision => decision.Path == oldPeer);
        Assert.Equal(EvidenceRetentionAction.Deleted, deleted.Action);
        Assert.Equal("old", deleted.AttemptId);
        Assert.Equal(EvidenceOwnershipSource.Declared, deleted.OwnershipSource);
        Assert.True(File.Exists(recentPeer));
        var archived = Assert.Single(first.Decisions, decision => decision.Path == recentPeer);
        Assert.Equal(EvidenceRetentionAction.Preserved, archived.Action);
        Assert.Equal("recent", archived.AttemptId);
        Assert.Equal(EvidenceOwnershipSource.Declared, archived.OwnershipSource);
        Assert.Equal(EvidenceEligibility.Archive, archived.Eligibility);
        var protectedTrx = AssertPreserved(first, finalTrx, "final");
        var protectedPeer = AssertPreserved(first, finalPeer, "final");
        Assert.Equal(protectedTrx.Reason, protectedPeer.Reason);
        AssertUnresolved(first, control);

        var second = fixture.Run(null, goal);

        foreach (var path in new[] { recentPeer, finalTrx, finalPeer, control })
        {
            Assert.True(File.Exists(path), path);
            Assert.DoesNotContain(second.Decisions,
                decision => decision.Path == path && decision.Action == EvidenceRetentionAction.Deleted);
        }
        AssertUnresolved(second, control);
    }

    private static EvidenceRetentionDecision AssertPreserved(StorageRetentionResult result, string path, string attemptId)
    {
        Assert.True(File.Exists(path), path);
        // A protected TRX is observed by both the TRX pass and the goal-folder pass.
        var decisions = result.Decisions.Where(decision => decision.Path == path).ToArray();
        Assert.NotEmpty(decisions);
        Assert.All(decisions, decision =>
        {
            Assert.Equal(EvidenceRetentionAction.Preserved, decision.Action);
            Assert.Equal(attemptId, decision.AttemptId);
            Assert.Equal(EvidenceOwnershipSource.Declared, decision.OwnershipSource);
            Assert.Equal(decisions[0].Reason, decision.Reason);
        });
        return decisions[0];
    }

    private static void AssertUnresolved(StorageRetentionResult result, string path)
    {
        Assert.True(File.Exists(path), path);
        var decision = Assert.Single(result.Decisions, decision => decision.Path == path);
        Assert.Equal(EvidenceRetentionAction.RetainedUndecidable, decision.Action);
        Assert.Equal("artifact-owner-unresolved", decision.Reason);
    }

    private static AcceptanceCheckResult WriteGoAttempt(
        string goalDirectory, string sources, string attemptId, int ordinal, int days)
    {
        var receipt = GoTestJsonGateReceipt.Attach(new AcceptanceManifestCheck
            {
                Name = "go test", Type = "command", Command = "go", Arguments = ["test", "-count=1", "./..."]
            }, ["go", "test", "-json", "-count=1", "./..."],
            new(0, GoTestJsonGateReceiptTests.StreamA), sources, Path.Combine(goalDirectory, attemptId),
            value => value, new("go test", true, 0, null));
        var metadata = Path.Combine(goalDirectory, attemptId + ".attempt.json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(new
        {
            attemptId,
            goalId = RetentionReclaimFixture.FailedId,
            ordinal,
            startedAt = RetentionReclaimFixture.Now.AddDays(-days),
            outcome = 1,
            reconciledAt = RetentionReclaimFixture.Now.AddDays(-days),
            metadataPath = metadata,
            testResultPaths = receipt.TestResultPaths
        }));
        foreach (var path in receipt.TestResultPaths!.Concat(receipt.NormalizedTestResultPaths!).Append(metadata))
            File.SetLastWriteTimeUtc(path, RetentionReclaimFixture.Now.AddDays(-days).UtcDateTime);
        return receipt;
    }
}
