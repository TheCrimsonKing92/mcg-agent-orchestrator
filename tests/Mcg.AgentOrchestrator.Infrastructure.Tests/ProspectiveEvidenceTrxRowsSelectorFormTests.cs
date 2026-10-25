using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each fixture owns its source module and receipt files.
public sealed class ProspectiveEvidenceTrxRowsSelectorFormTests
{
    [Fact]
    public void Match_NestedClasses_AcceptsInnerAndFullSelectors()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "nested.trx",
            new("worker", Evidence.NestedClass, "Check", Evidence.NestedClass + ".Check", "Passed"),
            new("inner", "Mcg.Sample.Outer+InnerTests", "Check", "Mcg.Sample.Outer+InnerTests.Check", "Passed"));
        var rows = ProspectiveEvidenceTrxRows.Read(path).ToArray();

        foreach (var selector in new[] { Evidence.NestedSelector, Evidence.NestedSelector + ".Check", Evidence.NestedClass })
            Assert.Equal(Evidence.NestedClass, Assert.Single(ProspectiveEvidenceTrxRows.Match(selector, rows)).ClassName);
        foreach (var selector in new[] { "InnerTests", "InnerTests.Check", "Mcg.Sample.Outer+InnerTests" })
            Assert.Equal("Mcg.Sample.Outer+InnerTests", Assert.Single(ProspectiveEvidenceTrxRows.Match(selector, rows)).ClassName);
    }

    [Theory]
    [InlineData("XWorkerDispatchPlannerHandoffTests", "WorkerDispatchPlannerHandoffTests")]
    [InlineData("Outer+XWorkerDispatchPlannerHandoffTests", "WorkerDispatchPlannerHandoffTests")]
    [InlineData("Outer+InnerTests", "Outer")]
    public void Match_UnrelatedSuffixOrOuterClass_RejectsSelector(string className, string selector)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "negative.trx",
            new GroupedGateTrxSamples.Row("negative", className, "Check", className + ".Check", "Passed"));
        var rows = ProspectiveEvidenceTrxRows.Read(path).ToArray();

        Assert.Single(ProspectiveEvidenceTrxRows.Match(className, rows));
        Assert.Empty(ProspectiveEvidenceTrxRows.Match(selector, rows));
    }

    [Fact]
    public void Match_GoBareName_AcceptsExactNameAndRejectsNearMisses()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Evidence.WriteColorReceipt(fixture.Root);
        var rows = ProspectiveEvidenceTrxRows.Read(path).ToArray();

        var row = Assert.Single(ProspectiveEvidenceTrxRows.Match(Evidence.GoSelector, rows));
        Assert.Equal(Evidence.ColorModule + "/internal/color", row.ClassName);
        Assert.Equal("color." + Evidence.GoSelector, row.ShortIdentity);
        Assert.Equal("internal/color." + Evidence.GoSelector, row.AliasIdentity);
        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([Evidence.GoSelector], [path], fixture.Root));
        Assert.True(evidence.Satisfied);
        Assert.Null(evidence.AmbiguousClassNames);
        Assert.Empty(ProspectiveEvidenceTrxRows.Match("TestAutoMode", rows));
        Assert.Empty(ProspectiveEvidenceTrxRows.Match("color.TestAutoModeHonorsNoColo", rows));
    }

    internal static class Evidence
    {
        internal const string NestedSelector = "WorkerDispatchPlannerHandoffTests";
        internal const string NestedClass = "WorkerDispatchTestSupport+WorkerDispatchPlannerHandoffTests";
        internal const string GoSelector = "TestAutoModeHonorsNoColor";
        internal const string ColorModule = "github.com/TheCrimsonKing92/schema-drift";

        internal static string[] WriteSelectorReceipts(string root, string className = NestedClass)
        {
            Directory.CreateDirectory(root);
            var nested = GroupedGateTrxSamples.Write(root, "nested.trx",
                new GroupedGateTrxSamples.Row("nested", className, "Check", className + ".Check", "Passed"));
            return [nested, WriteColorReceipt(root)];
        }

        internal static string WriteColorReceipt(string root) =>
            WriteGoReceipt(root, ColorModule, ("internal/color", "color", GoSelector));

        internal static string WriteGoReceipt(
            string root, string module, params (string Directory, string Clause, string Test)[] tests)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "go.mod"), $"module {module}\n");
            foreach (var test in tests)
            {
                var directory = Path.Combine(root, test.Directory);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "sample_test.go"), $"package {test.Clause}\n");
            }
            var stream = string.Join("\n", tests.Select(test => JsonSerializer.Serialize(new
            {
                Package = module + "/" + test.Directory, test.Test, Action = "pass"
            })));
            var check = new AcceptanceManifestCheck
            {
                Name = "go test", Type = "command", Command = "go", Arguments = ["test", "-count=1", "./..."]
            };
            var receipt = GoTestJsonGateReceipt.Attach(check, ["go", "test", "-json", "-count=1", "./..."],
                new(0, stream), root, Path.Combine(root, "attempt"), value => value, new("go test", true, 0, null));
            return Assert.Single(receipt.TestResultPaths!);
        }
    }
}
