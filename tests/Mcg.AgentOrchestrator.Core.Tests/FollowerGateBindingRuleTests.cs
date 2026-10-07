using Xunit;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FollowerGateBindingRuleTests
{
    [Fact]
    public void ExactLandingIsValid()
    {
        var verdict = FollowerGateBindingRule.Evaluate(Receipt(), ExactLanding());
        Assert.True(verdict.IsValid);
        Assert.Null(verdict.Reason);
    }

    [Theory]
    [InlineData(FollowerGateInvalidReason.LeaderFailed, "leader-failed")]
    [InlineData(FollowerGateInvalidReason.BaseMoved, "base-moved")]
    [InlineData(FollowerGateInvalidReason.LeaderTreeDiffers, "leader-tree-differs")]
    [InlineData(FollowerGateInvalidReason.FollowerBranchMoved, "follower-branch-moved")]
    [InlineData(FollowerGateInvalidReason.RebaseTreeDiffers, "rebase-tree-differs")]
    [InlineData(FollowerGateInvalidReason.PlanChanged, "plan-changed")]
    public void SingleDifferenceReturnsItsTypedReason(FollowerGateInvalidReason reason, string wireName)
    {
        AssertInvalid(reason, Difference(ExactLanding(), reason));
        Assert.Equal(wireName, FollowerGateBindingRule.WireName(reason));
    }

    [Theory]
    [InlineData(FollowerLeaderOutcome.Failed)]
    [InlineData(FollowerLeaderOutcome.Cancelled)]
    [InlineData(FollowerLeaderOutcome.Stale)]
    [InlineData((FollowerLeaderOutcome)999)]
    public void NonLandedLeaderFails(FollowerLeaderOutcome outcome) =>
        AssertInvalid(FollowerGateInvalidReason.LeaderFailed, ExactLanding() with { LeaderOutcome = outcome });

    [Fact]
    public void RebaseConflictDiscardsTheBinding() =>
        AssertInvalid(FollowerGateInvalidReason.RebaseTreeDiffers, ExactLanding() with { RebasedFollowerTree = null });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservedIdsIgnoreCaseAndSurroundingWhitespace(bool padded)
    {
        string Format(string value) => padded ? $" \t{value.ToUpperInvariant()}\r\n" : value.ToUpperInvariant();
        var exact = ExactLanding();
        var observation = exact with
        {
            LandedFirstParent = Format(exact.LandedFirstParent!),
            LandedTree = Format(exact.LandedTree!),
            CurrentFollowerBranchHead = Format(exact.CurrentFollowerBranchHead!),
            RebasedFollowerTree = Format(exact.RebasedFollowerTree!)
        };
        Assert.True(FollowerGateBindingRule.Evaluate(Receipt(), observation).IsValid);
    }

    public static IEnumerable<object?[]> MalformedObservedIds()
    {
        foreach (var reason in new[] { FollowerGateInvalidReason.BaseMoved, FollowerGateInvalidReason.LeaderTreeDiffers,
                     FollowerGateInvalidReason.FollowerBranchMoved, FollowerGateInvalidReason.RebaseTreeDiffers })
            foreach (var value in new[] { null, "", " \t", new string('a', 39), new string('a', 41), new string('g', 40) })
                yield return [reason, value];
    }

    [Theory]
    [MemberData(nameof(MalformedObservedIds))]
    public void MalformedObservedIdNeverMatches(FollowerGateInvalidReason reason, string? value)
    {
        var exact = ExactLanding();
        var observation = reason switch
        {
            FollowerGateInvalidReason.BaseMoved => exact with { LandedFirstParent = value },
            FollowerGateInvalidReason.LeaderTreeDiffers => exact with { LandedTree = value },
            FollowerGateInvalidReason.FollowerBranchMoved => exact with { CurrentFollowerBranchHead = value },
            FollowerGateInvalidReason.RebaseTreeDiffers => exact with { RebasedFollowerTree = value },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        AssertInvalid(reason, observation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PLAN-B")]
    [InlineData(" plan-b ")]
    public void PlanIdentityComparisonIsOrdinal(string? plan) =>
        AssertInvalid(FollowerGateInvalidReason.PlanChanged, ExactLanding() with { CurrentFollowerPlanIdentity = plan });

    public static IEnumerable<object[]> Reasons() =>
        Enum.GetValues<FollowerGateInvalidReason>().Select(reason => new object[] { reason });

    [Theory]
    [MemberData(nameof(Reasons))]
    public void MultipleDifferencesReturnFirstReason(FollowerGateInvalidReason first)
    {
        // Explicit precedence, independent of the enum's numeric values.
        FollowerGateInvalidReason[] order = [FollowerGateInvalidReason.LeaderFailed, FollowerGateInvalidReason.BaseMoved,
            FollowerGateInvalidReason.LeaderTreeDiffers, FollowerGateInvalidReason.FollowerBranchMoved,
            FollowerGateInvalidReason.RebaseTreeDiffers, FollowerGateInvalidReason.PlanChanged];
        var observation = ExactLanding();
        foreach (var reason in order.Skip(Array.IndexOf(order, first)))
            observation = Difference(observation, reason);
        AssertInvalid(first, observation);
    }

    public static IEnumerable<object?[]> MalformedReceiptIds()
    {
        for (var field = 0; field < 5; field++)
            foreach (var value in new[] { null, "", " \t", new string('a', 39), new string('a', 41), new string('g', 40) })
                yield return [field, value];
    }

    [Theory]
    [MemberData(nameof(MalformedReceiptIds))]
    public void MalformedReceiptRevisionThrows(int field, string? value)
    {
        var revisions = Revisions();
        revisions[field] = value!;
        Assert.ThrowsAny<ArgumentException>(() => Receipt(revisions));
    }

    [Fact]
    public void ReceiptNormalizesAllRevisionsAndPreservesOpaquePlan()
    {
        var receipt = Receipt(Revisions().Select(value => $" \t{value.ToUpperInvariant()}\n").ToArray(), " plan-b ");
        Assert.Equal(Revisions(), new[] { receipt.LeaderCandidateRevision, receipt.LeaderCandidateTree,
            receipt.BaseMainRevision, receipt.FollowerBranchHead, receipt.FollowerTestedTree });
        Assert.Equal(" plan-b ", receipt.FollowerPlanIdentity);
        Assert.True(FollowerGateBindingRule.Evaluate(receipt,
            ExactLanding() with { CurrentFollowerPlanIdentity = " plan-b " }).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void BlankReceiptPlanThrows(string? plan) =>
        Assert.ThrowsAny<ArgumentException>(() => Receipt(plan: plan!));

    [Theory]
    [InlineData(true, FollowerGateDisposition.LandFollower)]
    [InlineData(false, FollowerGateDisposition.ChargeFollower)]
    public void ValidBindingUsesFollowerResult(bool passed, FollowerGateDisposition expected) =>
        Assert.Equal(expected, FollowerGateBindingRule.Disposition(
            FollowerGateBindingRule.Evaluate(Receipt(), ExactLanding()), passed));

    [Theory]
    [MemberData(nameof(Reasons))]
    public void EveryInvalidBindingDiscardsPassedAndRedFollowers(FollowerGateInvalidReason reason)
    {
        var verdict = FollowerGateBindingRule.Evaluate(Receipt(), Difference(ExactLanding(), reason));
        Assert.Equal(FollowerGateDisposition.Discard, FollowerGateBindingRule.Disposition(verdict, true));
        Assert.Equal(FollowerGateDisposition.Discard, FollowerGateBindingRule.Disposition(verdict, false));
    }

    [Fact]
    public void UndefinedInvalidReasonThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FollowerGateBindingVerdict.Invalid((FollowerGateInvalidReason)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => FollowerGateBindingRule.WireName((FollowerGateInvalidReason)999));
    }

    private static string[] Revisions() => "abcde".Select(character => new string(character, 40)).ToArray();

    private static FollowerGateReceipt Receipt(string[]? revisions = null, string plan = "plan-b")
    {
        var ids = revisions ?? Revisions();
        return new(new GoalId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ids[0], ids[1], ids[2],
            new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), ids[3], ids[4], plan);
    }

    private static FollowerGateLandingObservation ExactLanding()
    {
        var receipt = Receipt();
        return new(FollowerLeaderOutcome.Landed, receipt.BaseMainRevision, receipt.LeaderCandidateTree,
            receipt.FollowerBranchHead, receipt.FollowerTestedTree, receipt.FollowerPlanIdentity);
    }

    private static FollowerGateLandingObservation Difference(FollowerGateLandingObservation observation,
        FollowerGateInvalidReason reason) => reason switch
    {
        FollowerGateInvalidReason.LeaderFailed => observation with { LeaderOutcome = FollowerLeaderOutcome.Failed },
        FollowerGateInvalidReason.BaseMoved => observation with { LandedFirstParent = new string('f', 40) },
        FollowerGateInvalidReason.LeaderTreeDiffers => observation with { LandedTree = new string('f', 40) },
        FollowerGateInvalidReason.FollowerBranchMoved => observation with { CurrentFollowerBranchHead = new string('f', 40) },
        FollowerGateInvalidReason.RebaseTreeDiffers => observation with { RebasedFollowerTree = new string('f', 40) },
        FollowerGateInvalidReason.PlanChanged => observation with { CurrentFollowerPlanIdentity = "changed-plan" },
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    private static void AssertInvalid(FollowerGateInvalidReason expected, FollowerGateLandingObservation observation)
    {
        var verdict = FollowerGateBindingRule.Evaluate(Receipt(), observation);
        Assert.False(verdict.IsValid);
        Assert.Equal(expected, verdict.Reason);
    }
}
