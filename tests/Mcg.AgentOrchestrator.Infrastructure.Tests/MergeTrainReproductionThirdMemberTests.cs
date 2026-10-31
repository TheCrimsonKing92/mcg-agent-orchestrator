using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all evidence paths belong to this test's unique directory.
public sealed class MergeTrainReproductionThirdMemberTests
{
    [Fact]
    public void InvalidFirstMemberDoesNotDiscardAValidThirdMemberFailure()
    {
        const string testName = "Ns.UnchangedTests.Fails";
        var root = Path.Combine(Path.GetTempPath(), "third-member-reproduction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var trx = Path.Combine(root, "failed.trx");
            new XDocument(new XElement("TestRun", new XElement("Results", new XElement("UnitTestResult",
                new XAttribute("testName", testName), new XAttribute("outcome", "Failed"),
                new XElement("Output", new XElement("ErrorInfo", new XElement("Message", "assertion failed"))))))).Save(trx);
            var members = Enumerable.Range(1, 3).Select(index =>
            {
                var revision = new string((char)('a' + index), 40);
                return new MergeTrainMemberBinding(new GoalId(index.ToString("x32")), revision, revision,
                    [], [], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected",
                    RebasedTrainHeadRevision: revision);
            }).ToArray();
            var identity = MergeTrainIdentity.Create(members, new string('a', 40), new string('e', 40), "manifest");
            var receipt = new MergeTrainReceipt("red", identity, MergeTrainGateOutcome.Failed,
                DateTimeOffset.UtcNow, 0, ["tests"], 1, [trx]);
            Assert.True(MergeTrainRedAttribution.IsGenuineTrainRed(receipt));

            var outcomes = new List<MergeTrainReproductionOutcome>();
            var calls = new List<GoalId>();
            var attributed = MergeTrainReproductionAttribution.TryAttribute(receipt, members,
                (member, main, selected, _) =>
                {
                    calls.Add(member.GoalId);
                    var output = Path.Combine(root, member.GoalId.Value, "artifacts");
                    MergeTrainReproductionProvenance? provenance = member == members[0] ? null :
                        new(main, member.CandidateRevision, member.CandidateRevision, member.CandidateRevision,
                            output, [output], [Path.Combine(output, "tests.dll")]);
                    return new(selected,
                        member == members[2] ? [new(testName, "Failed", "member defect", null)] : [],
                        provenance);
                }, CancellationToken.None, outcomes.Add);

            Assert.Equal(members.Select(member => member.GoalId), calls);
            Assert.Equal(members[2].GoalId, Assert.Single(attributed!).Member.GoalId);
            Assert.Equal(new[] { "invalid-evidence", "not-reproduced", "reproduced" }, outcomes.Select(item => item.Result));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
