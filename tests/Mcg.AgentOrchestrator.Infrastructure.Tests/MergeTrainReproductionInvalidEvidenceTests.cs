using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all evidence paths belong to this fixture's unique directory.
public sealed class MergeTrainReproductionInvalidEvidenceTests
{
    private const string TestName = "Ns.UnchangedTests.Fails";

    [Theory]
    [InlineData("valid", null)]
    [InlineData("tree", "tree-mismatch")]
    [InlineData("main", "tree-mismatch")]
    [InlineData("member", "tree-mismatch")]
    [InlineData("root", "root-mismatch")]
    [InlineData("assembly", "assembly-outside-root")]
    [InlineData("missing", "missing-provenance")]
    [InlineData("missing-tree", "missing-provenance")]
    [InlineData("missing-root", "missing-provenance")]
    [InlineData("missing-assembly", "missing-provenance")]
    public void PassingResultIsNotNonReproductionUnlessItsProvenanceIsValid(string mutation, string? reason)
    {
        WithReceipt((receipt, root) =>
        {
            var outcomes = new List<MergeTrainReproductionOutcome>();
            var calls = new List<GoalId>();
            var members = receipt.Identity.Members;
            var attributed = MergeTrainReproductionAttribution.TryAttribute(receipt, members,
                (member, main, selected, _) =>
                {
                    calls.Add(member.GoalId);
                    var provenance = Evidence(member, main, root);
                    if (member == members[0]) provenance = mutation switch
                    {
                        "tree" => provenance with { TreeRevision = new string('f', 40) },
                        "main" => provenance with { MainRevision = new string('f', 40) },
                        "member" => provenance with { MemberRevision = new string('f', 40) },
                        "root" => provenance with { BuildOutputRoots = [Path.Combine(root, "another-partition")] },
                        // A sibling with the same textual prefix is outside the root.
                        "assembly" => provenance with { AssemblyPaths = [provenance.PartitionBuildOutputRoot + "-other/tests.dll"] },
                        "missing" => null,
                        "missing-tree" => provenance with { TreeRevision = "" },
                        "missing-root" => provenance with { BuildOutputRoots = [] },
                        "missing-assembly" => provenance with { AssemblyPaths = [] },
                        _ => provenance
                    };
                    return new(selected, [], provenance);
                }, CancellationToken.None, outcomes.Add);

            Assert.Null(attributed);
            Assert.Equal(members.Select(member => member.GoalId), calls);
            Assert.Equal(3, outcomes.Count);
            var first = Assert.Single(outcomes, item => item.MemberId == members[0].GoalId.Value);
            Assert.Equal(reason is null ? "not-reproduced" : "invalid-evidence", first.Result);
            Assert.Equal(reason, first.Reason);
            Assert.All(outcomes.Skip(1), item => Assert.Equal("not-reproduced", item.Result));
        });
    }

    [Fact]
    public void InvalidFirstMemberDoesNotDiscardAValidThirdMemberFailure()
    {
        WithReceipt((receipt, root) =>
        {
            var outcomes = new List<MergeTrainReproductionOutcome>();
            var members = receipt.Identity.Members;
            var attributed = MergeTrainReproductionAttribution.TryAttribute(receipt, members,
                (member, main, selected, _) => new(selected,
                    member == members[2] ? [new(TestName, "Failed", "member defect", null)] : [],
                    member == members[0] ? null : Evidence(member, main, root)),
                CancellationToken.None, outcomes.Add);

            Assert.Equal(members[2].GoalId, Assert.Single(attributed!).Member.GoalId);
            Assert.Equal(new[] { "invalid-evidence", "not-reproduced", "reproduced" }, outcomes.Select(item => item.Result));
        });
    }

    private static MergeTrainReproductionProvenance Evidence(MergeTrainMemberBinding member, string main, string root)
    {
        var output = Path.Combine(root, member.GoalId.Value, "artifacts");
        return new(main, member.CandidateRevision, member.CandidateRevision, member.CandidateRevision,
            output, [output], [Path.Combine(output, "tests.dll")]);
    }

    private static void WithReceipt(Action<MergeTrainReceipt, string> assertion)
    {
        var root = Path.Combine(Path.GetTempPath(), "invalid-reproduction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var trx = Path.Combine(root, "failed.trx");
            new XDocument(new XElement("TestRun", new XElement("Results", new XElement("UnitTestResult",
                new XAttribute("testName", TestName), new XAttribute("outcome", "Failed"),
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
            assertion(receipt, root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
