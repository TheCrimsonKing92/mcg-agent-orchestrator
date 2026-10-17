using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static FocusedEvidenceUnionPlanningTests;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedUnionReceipts : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public void OmittedPartition_CannotCreditTheOriginalMixedTarget()
    {
        var request = "Infrastructure.Tests:" + Fqn(ClassA) + "|" + Fqn(MethodA);
        var checks = Resolve(request, out var coverage);
        var completed = new AcceptanceCheckResult(checks[0].Name, true, 0, "passed", ExecutedTestCount: 1);
        var notRun = FocusedEvidenceNotRunPlan.Compare(checks, [completed], coverage, continueAfterFailure: true);
        Assert.Equal(checks[1].Name, Assert.Single(notRun.Checks).CheckName);
        Assert.Equal(Fqn(ClassA) + "|" + Fqn(MethodA), Assert.Single(notRun.Selections).TestClass);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(1)]
    public void RecordedResult_RequiresPositiveExecutionForItsMappedSelection(int? executed)
    {
        AcceptanceManifestCheck[] checks = [new() { Name = "Core" }, new() { Name = "Infrastructure" }];
        var coverage = new FocusedEvidenceCoverage([
            new("Core.Tests:DispatchExecutionTests", [checks[0].Name]),
            new("Infrastructure.Tests:ConductorDriverTests", [checks[1].Name])]);
        AcceptanceCheckResult[] results = [
            new(checks[0].Name, true, 0, "passed", ExecutedTestCount: 1),
            new(checks[1].Name, executed is > 0, executed is > 0 ? 0 : 8, "recorded",
                ExecutedTestCount: executed)];

        var notRun = FocusedEvidenceNotRunPlan.Compare(checks, results, coverage, continueAfterFailure: true);

        if (executed is > 0)
        {
            Assert.Empty(notRun.Checks);
            Assert.Empty(notRun.Selections);
        }
        else
        {
            var missing = Assert.Single(notRun.Checks);
            Assert.Equal(checks[1].Name, missing.CheckName);
            Assert.Equal("not-run:execution-not-demonstrated", missing.Reason);
            Assert.Equal(new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests"),
                Assert.Single(notRun.Selections));
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void SingleSelectionGroup_RequiresEveryPositiveAndNeverCreditsExclusions(bool corrupt)
    {
        var check = Assert.Single(Resolve($"{Fqn(ClassA)}|{Fqn(ClassB)}", out _));
        var root = Directory.CreateTempSubdirectory("focused-union-receipt-").FullName;
        try
        {
            var path = Path.Combine(root, "partial.trx");
            WriteMtpTrx(Argv(check, path), 1, [ClassA + ".Executed"]);
            if (corrupt) File.WriteAllText(path, "<malformed");
            var coverage = GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(check.FocusedEvidenceSelections, [path]);
            if (corrupt) Assert.Equal(path, Assert.Single(coverage.UnreadableReceiptPaths));
            else Assert.Equal(Fqn(ClassB), Assert.Single(coverage.UncoveredSelections));

            check = Assert.Single(Resolve($"{Fqn(ClassB)}&FullyQualifiedName!~{ClassA}", out _));
            coverage = GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(check.FocusedEvidenceSelections, [path]);
            Assert.Contains(Fqn(ClassB), coverage.UncoveredSelections);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public async Task FailedPartition_KeepsLaterPassingReceiptAndAllTargetMappings(int failingPartition)
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        var goal = new GoalId(Guid.NewGuid().ToString("N"));
        var receipts = new List<string>();
        try
        {
            var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "MtpTestRunnerScriptTests.cs"),
                "public class MtpTestRunnerScriptTests { [Xunit.Fact] public void MtpScriptFilterTranslationAndFilenameBoundingMatchGateConventions() { } }");
            File.WriteAllText(Path.Combine(directory, ClassA + ".cs"), $"public class {ClassA} {{ }}");
            var testLaunches = 0;
            var verifier = new GoalAcceptanceVerifier((argv, _, _, _) =>
            {
                if (!IsMtpExecutableCall(argv, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                    return Task.FromResult(new CommandResult(0, "Build succeeded."));
                var index = testLaunches++;
                var classIndex = Array.IndexOf(argv, "--filter-class");
                var identity = classIndex >= 0 ? ClassA + ".Executed" : MethodA;
                WriteMtpTrx(argv, 1, [identity]);
                receipts.Add(Path.Combine(argv[Array.IndexOf(argv, "--results-directory") + 1],
                    argv[Array.IndexOf(argv, "--report-trx-filename") + 1]));
                if (index == failingPartition)
                {
                    var trx = System.Xml.Linq.XDocument.Load(receipts[^1]);
                    trx.Descendants("UnitTestResult").Single().SetAttributeValue("outcome", "Failed");
                    trx.Descendants("Counters").Single().SetAttributeValue("passed", 0);
                    trx.Descendants("Counters").Single().SetAttributeValue("failed", 1);
                    trx.Save(receipts[^1]);
                }
                return Task.FromResult(new CommandResult(index == failingPartition ? 1 : 0, "Executed one test."));
            });
            var result = await verifier.RunFocusedEvidenceAsync(root, goal, $"{Fqn(ClassA)}|{Fqn(MethodA)}");
            Assert.True(result.Accepted);
            Assert.False(result.Passed);
            Assert.Equal(2, testLaunches);
            Assert.Equal(2, result.Checks.Count);
            Assert.Contains(result.Checks, check => !check.Passed);
            Assert.Contains(result.Checks, check => check.Passed);
            Assert.Equal(2, Assert.Single(result.Coverage!.TargetToChecks).CheckNames.Count);
            Assert.Equal(2, receipts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(Assert.Single(result.Arms!).NotRunSelections!);
            Assert.All(receipts, path =>
            {
                Assert.True(File.Exists(path));
                Assert.Contains(result.Checks, check => (check.TestResultPaths ?? []).Contains(path));
                Assert.Contains(path, result.Summary, StringComparison.Ordinal);
            });
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal);
            DeleteDirectoryWithRetry(root);
        }
    }
}
