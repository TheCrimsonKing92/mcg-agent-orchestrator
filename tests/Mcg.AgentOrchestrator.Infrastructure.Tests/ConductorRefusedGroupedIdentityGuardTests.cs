using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: pure records, with no process, filesystem or ambient state observations.
public sealed class ConductorRefusedGroupedIdentityGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalRefusal_WithEitherMemberOrder_IsRefused(bool reverse)
    {
        var record = Refusal();
        var members = record.Members.Select(member => member.ToProjection(record.MainRevision)).ToArray();
        if (reverse) Array.Reverse(members);

        Assert.True(ConductorRefusedGroupedIdentityGuard.IsRefused([record], "train", members,
            record.MainRevision, record.CombinedTreeRevision, record.IdentityValue));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("member")]
    [InlineData("candidate")]
    [InlineData("main")]
    [InlineData("tree")]
    [InlineData("identity")]
    public void DifferentIdentityField_AllowsLaunch(string field)
    {
        var original = Refusal();
        var changed = field switch
        {
            "kind" => original with { Kind = "cohort" },
            "member" => original with { Members = [original.Members[0] with { GoalId = new string('f', 32) }, original.Members[1]] },
            "candidate" => original with { Members = [original.Members[0] with { CandidateRevision = new string('f', 40) }, original.Members[1]] },
            "main" => original with { MainRevision = new string('f', 40) },
            "tree" => original with { CombinedTreeRevision = new string('f', 40) },
            "identity" => original with { IdentityValue = "different-identity" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.False(ConductorRefusedGroupedIdentityGuard.IsRefused([changed], "train",
            original.Members.Select(member => member.ToProjection(original.MainRevision)).ToArray(),
            original.MainRevision, original.CombinedTreeRevision, original.IdentityValue));
    }

    [Theory]
    [InlineData("owner-dead")]
    [InlineData("child-result-published")]
    [InlineData("launch-failed:IOException:x")]
    [InlineData(null)]
    public void OtherReconciliationReason_AllowsLaunch(string? detail)
    {
        var record = Refusal() with { Detail = detail };

        Assert.False(ConductorRefusedGroupedIdentityGuard.IsRefused([record], "train",
            record.Members.Select(member => member.ToProjection(record.MainRevision)).ToArray(),
            record.MainRevision, record.CombinedTreeRevision, record.IdentityValue));
    }

    private static ConductorGroupedGateAttempt Refusal() => new(
        "attempt", "train",
        [new(new string('1', 32), new string('a', 40), new string('a', 40), ["first.cs"], [], ChangeRiskTier.Behavior),
         new(new string('2', 32), new string('b', 40), new string('b', 40), ["second.cs"], [], ChangeRiskTier.Behavior)],
        new string('c', 40), new string('d', 40), "manifest", "identity", DateTimeOffset.UnixEpoch,
        0, 1, "attempt.json", "result.json", "exit", "out", "err", "repo", "{}",
        ReconciledAt: DateTimeOffset.UnixEpoch, Detail: "refused:identity-changed field=tree", Outcome: "Reconciled");
}
