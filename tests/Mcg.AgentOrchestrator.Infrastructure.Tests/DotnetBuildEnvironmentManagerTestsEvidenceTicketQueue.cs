using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsEvidenceTicketQueue : DotnetBuildEnvironmentManagerRootedTestBase
{
    private static BuildPermitClaimant Claim(string key, BuildPermitClaimantKind kind,
        int pid = 123, bool ownerBound = false) => new(key, kind, pid, ownerBound);

    [Fact]
    public void EarlierEvidenceStaysAheadOfPromotedGate()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var evidence = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        var gate = Claim("gate", BuildPermitClaimantKind.Gate);
        queue.Register(evidence);
        clock.Advance(TimeSpan.FromMinutes(1));
        queue.Register(gate);
        clock.Advance(TimeSpan.FromMinutes(1));
        for (var minute = 2; minute <= 6; minute += 2)
        {
            queue.Register(evidence);
            queue.Register(gate);
            clock.Advance(TimeSpan.FromMinutes(minute == 6 ? 1 : 2));
        }

        Assert.Equal(new[] { evidence.Key, gate.Key }, queue.Snapshot().Select(t => t.Key));
        Assert.False(queue.Admit(gate, 1));
        Assert.True(queue.Admit(evidence, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EvidenceAndGateSharePromotionWhileWorkerStaysInLowerTier(bool includeEvidence)
    {
        var clock = new RecordingTimeProvider();
        var start = clock.GetUtcNow();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var worker = Claim("worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true);
        var evidence = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        var gate = Claim("gate", BuildPermitClaimantKind.Gate);
        var registered = new List<BuildPermitClaimant> { worker };
        queue.Register(worker);
        for (var minute = 1; minute <= 7; minute++)
        {
            clock.Advance(start.AddMinutes(minute) - clock.GetUtcNow());
            if (minute == 1 && includeEvidence) registered.Add(evidence);
            if (minute == 2) registered.Add(gate);
            foreach (var claimant in registered) queue.Register(claimant);
            if (minute == 5)
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                Assert.Equal(includeEvidence ? new[] { worker.Key, evidence.Key, gate.Key } : new[] { worker.Key, gate.Key },
                    queue.Snapshot().Select(t => t.Key));
            }
            if (minute == 6)
            {
                Assert.Equal(includeEvidence ? new[] { evidence.Key, worker.Key, gate.Key } : new[] { worker.Key, gate.Key },
                    queue.Snapshot().Select(t => t.Key));
            }
        }
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(includeEvidence ? new[] { evidence.Key, gate.Key, worker.Key } : new[] { gate.Key, worker.Key },
            queue.Snapshot().Select(t => t.Key));
    }

    [Fact]
    public void AdoptionTransfersOwnerAndRegisterPreservesItUntilTenMinuteBoundary()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, pid => pid == 456);
        var attempt = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        var original = queue.Register(attempt);
        clock.Advance(TimeSpan.FromMinutes(1));
        // Even a different incoming kind cannot change a live ticket's identity or queue position.
        var conductor = attempt with { Kind = BuildPermitClaimantKind.Gate, OwnerPid = 456, OwnerBound = true };
        var adopted = queue.Adopt(conductor);
        Assert.Equal(original with
        {
            OwnerPid = 456, OwnerBound = true, LastRefreshedAt = clock.GetUtcNow()
        }, adopted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = new BuildPermitWaitQueue(StorageRoot, clock, pid => pid == 456).Register(attempt);
        Assert.Equal(adopted with { LastRefreshedAt = clock.GetUtcNow() }, refreshed);

        var lastRefresh = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(6.5));
        Assert.Equal(original.FirstWaitedAt, Assert.Single(queue.Snapshot()).FirstWaitedAt);
        clock.Advance(lastRefresh + BuildPermitWaitQueue.EvidenceTicketStaleAfter - TimeSpan.FromTicks(1) - clock.GetUtcNow());
        Assert.Equal(refreshed, Assert.Single(queue.Snapshot()));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Empty(queue.Snapshot());
        Assert.False(File.Exists(queue.TicketPath(attempt.Key)));
    }

    [Fact]
    public void DeadConductorRemovesEvidenceAndAdmitsGate()
    {
        var clock = new RecordingTimeProvider();
        var alive = true;
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, pid => pid == 456 && alive);
        var evidence = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        queue.Register(evidence);
        var adopted = queue.Adopt(evidence with { OwnerPid = 456, OwnerBound = true });
        clock.Advance(TimeSpan.FromTicks(1));
        var gate = Claim("gate", BuildPermitClaimantKind.Gate);
        queue.Register(gate);
        Assert.False(queue.Admit(gate, 1));
        alive = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(gate.Key, Assert.Single(queue.Snapshot()).Key);
        Assert.False(File.Exists(queue.TicketPath(adopted.Key)));
        Assert.True(queue.Admit(gate, 1));
    }

    [Theory]
    [InlineData(BuildPermitClaimantKind.FocusedEvidence, false)]
    [InlineData(BuildPermitClaimantKind.Gate, false)]
    [InlineData(BuildPermitClaimantKind.Gate, true)]
    [InlineData(BuildPermitClaimantKind.WorkerBuild, true)]
    public void OtherTicketsKeepFourMinuteExpiry(BuildPermitClaimantKind kind, bool ownerBound)
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var claimant = Claim("control", kind, ownerBound: ownerBound);
        var original = queue.Register(claimant);
        clock.Advance(BuildPermitWaitQueue.TicketStaleAfter - TimeSpan.FromTicks(1));
        Assert.Equal(original, Assert.Single(queue.Snapshot()));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Empty(queue.Snapshot());
        Assert.False(File.Exists(queue.TicketPath(claimant.Key)));
    }

    [Fact]
    public void AdoptionCreatesFreshIdentityWhenPriorTicketExpired()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var attempt = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        queue.Register(attempt);
        clock.Advance(BuildPermitWaitQueue.TicketStaleAfter);
        var conductor = attempt with { OwnerPid = 456, OwnerBound = true };
        var adopted = queue.Adopt(conductor);
        Assert.Equal(new BuildPermitWaitQueue.Ticket(conductor.Key, conductor.Kind, 456, true,
            clock.GetUtcNow(), clock.GetUtcNow()), adopted);
        Assert.Equal(adopted, Assert.Single(queue.Snapshot()));
    }
}
