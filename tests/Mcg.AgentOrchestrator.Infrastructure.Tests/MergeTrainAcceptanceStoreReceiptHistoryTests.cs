using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its SQLite file and evidence directory.
public sealed class MergeTrainAcceptanceStoreReceiptHistoryTests
{
    [Fact]
    public void ReadReceiptsSince_InclusiveBoundary_PreservesPayloadAndDatabaseBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "train-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "merge-train-acceptance.db");
            var store = new MergeTrainAcceptanceStore(path);
            var input = new List<MergeTrainReceipt>();
            var members = new[]
            {
                MergeTrainDropBacktestTests.Fixture.Member(1, 3),
                MergeTrainDropBacktestTests.Fixture.Member(2) with { ChangeRiskTier = (ChangeRiskTier)2 }
            };
            foreach (var outcome in new[] { MergeTrainGateOutcome.Failed, MergeTrainGateOutcome.Passed, MergeTrainGateOutcome.InfrastructureFailure })
                MergeTrainDropBacktestTests.Fixture.Add(input, outcome, 10 + input.Count, members);
            // Save out of order to distinguish completion ordering from insertion ordering.
            var saved = input.AsEnumerable().Reverse().Select(receipt => MergeTrainDropBacktestTests.Fixture.Save(store, root, receipt))
                .OrderBy(receipt => receipt.CompletedAt).ToArray();
            var before = File.ReadAllBytes(path);
            var read = MergeTrainAcceptanceStore.OpenReadOnly(path).ReadReceiptsSince(input[1].CompletedAt);
            Assert.Equal(new[] { saved[1].ReceiptId, saved[2].ReceiptId }, read.Select(receipt => receipt.ReceiptId));
            foreach (var (expected, actual) in saved.Skip(1).Zip(read))
            {
                Assert.Equal(expected.CompletedAt, actual.CompletedAt);
                Assert.Equal(expected.Outcome, actual.Outcome);
                Assert.Equal(expected.GateElapsedMilliseconds, actual.GateElapsedMilliseconds);
                Assert.Equal(expected.FailedChecks, actual.FailedChecks);
                Assert.Equal(expected.GateExitCode, actual.GateExitCode);
                Assert.Equal(expected.GateTestResultPaths, actual.GateTestResultPaths);
                Assert.Equal(expected.Identity.Value, actual.Identity.Value);
                Assert.Equal(expected.Identity.Members.Count, actual.Identity.Members.Count);
                foreach (var (expectedMember, actualMember) in expected.Identity.Members.Zip(actual.Identity.Members))
                {
                    Assert.Equal(expectedMember.GoalId, actualMember.GoalId);
                    Assert.Equal(expectedMember.CandidateRevision, actualMember.CandidateRevision);
                    Assert.Equal(expectedMember.BranchRevision, actualMember.BranchRevision);
                    Assert.Equal(expectedMember.LandingPaths, actualMember.LandingPaths);
                    Assert.Equal(expectedMember.ResourceKeys, actualMember.ResourceKeys);
                    Assert.Equal(expectedMember.ChangeRiskTier, actualMember.ChangeRiskTier);
                    Assert.Equal(expectedMember.AutoPromotionDisposition, actualMember.AutoPromotionDisposition);
                    Assert.Equal(expectedMember.MergeStatus, actualMember.MergeStatus);
                    Assert.Equal(expectedMember.MergeReason, actualMember.MergeReason);
                    Assert.Equal(expectedMember.RebasedTrainHeadRevision, actualMember.RebasedTrainHeadRevision);
                }
            }
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
