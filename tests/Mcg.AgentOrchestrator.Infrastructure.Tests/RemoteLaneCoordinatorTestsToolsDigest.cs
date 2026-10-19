using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel safe: each fake owns occupancy files, ledger, transport and clock.
public sealed class RemoteLaneCoordinatorTestsToolsDigest
{
    [Xunit.Theory]
    [Xunit.InlineData("executor-tools-mismatch", "ExecutorToolsMismatch", null)]
    [Xunit.InlineData("executor-tools-unverified", "ExecutorToolsUnverified", "executor")]
    public async Task ToolsRefusal_RecordsExecutorFaultAndReleasesSlotsUntilLease(string reason, string code, string? expectedOwner)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var entry = new RemoteLaneExecutorEntry("one", 60, Slots: 2);
        var step = new RemoteLaneStep("tools-digest", 0, false, fake.Clock.GetUtcNow(), fake.Clock.GetUtcNow(),
            "expected=local observed=remote");
        var submissions = 0;
        var executor = new FakeRemoteLaneExecutor
        {
            Submit = (_, _) =>
            {
                submissions++;
                return Task.FromResult(new RemoteLaneSubmission(null, reason, [step]));
            }
        };
        using var coordinator = new RemoteLaneCoordinator(
            new([entry], [FakeSshRemoteLaneTransport.Request.Lane], null),
            new RemoteLaneCandidateIdentity("attempt-one", "goal", "123456789abcdef", "tree", "main", "manifest"),
            fake.Root, Path.Combine(fake.Root, "attempt", "result"), executor, fake.Clock, TimeSpan.FromMilliseconds(1), null);
        Assert.Equal(entry, coordinator.TryClaimIdleExecutor());
        var check = new Check
        {
            Name = FakeSshRemoteLaneTransport.Request.Lane, Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", FakeSshRemoteLaneTransport.Request.Filter]
        };
        var outcome = await coordinator.RunRemoteAsync(check, entry, TimeSpan.FromMinutes(10), default);
        Assert.Equal(1, submissions);
        Assert.Null(outcome.Handle);
        Assert.Null(outcome.Accepted);
        var ledger = RemoteExecutorHealthLedger.ResolveStorePath(fake.Root);
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(ledger));
        Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(code), row.Outcome);
        Assert.Equal(reason, row.Reason);
        Assert.Equal(expectedOwner, row.FaultOwner);
        Assert.NotNull(row.Attempt);
        Assert.Equal(step, Assert.Single(row.Attempt.Steps));
        using (var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(ledger))))
        {
            Assert.Equal(reason, json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(expectedOwner, json.RootElement.GetProperty("fault_owner").GetString());
        }
        using (var slot0 = RemoteExecutorOccupancy.TryClaimExclusive(fake.Root, entry.Id, 0))
        using (var slot1 = RemoteExecutorOccupancy.TryClaimExclusive(fake.Root, entry.Id, 1))
        {
            Assert.NotNull(slot0);
            Assert.NotNull(slot1);
        }
        Assert.Null(coordinator.TryClaimIdleExecutor());
        fake.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(coordinator.TryClaimIdleExecutor());
        fake.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(entry, coordinator.TryClaimIdleExecutor());
        Assert.Empty(new RemoteLaneFailureHistory().Read(ledger));
    }
}
