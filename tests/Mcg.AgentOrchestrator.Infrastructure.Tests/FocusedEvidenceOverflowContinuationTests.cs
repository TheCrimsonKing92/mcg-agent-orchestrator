using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class FocusedEvidenceOverflowContinuationTests : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task CandidateOverflowExecutesSecondFilterAfterNamedFailure()
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        var storage = InfrastructureTestSupport.CreateTempDirectory();
        var classes = new[] { new string('A', 600) + "Tests", new string('B', 600) + "Tests" };
        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var name in classes)
            File.WriteAllText(Path.Combine(source, name[..8] + ".cs"), $"public class {name} {{ }}");
        var calls = new List<string[]>();
        var failure = classes[0] + ".Fails";
        try
        {
            TestOverrides.BuildStorageRootForTests = new DotnetBuildStorageRoot(storage);
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                if (!IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                calls.Add(args);
                var failed = calls.Count == 1;
                WriteMtpTrx(args, 1, [failed ? failure : classes[1] + ".Passes"]);
                if (failed)
                {
                    var path = Path.Combine(args[Array.IndexOf(args, "--results-directory") + 1],
                        args[Array.IndexOf(args, "--report-trx-filename") + 1]);
                    var trx = XDocument.Load(path);
                    trx.Descendants("UnitTestResult").Single().SetAttributeValue("outcome", "Failed");
                    var counters = trx.Descendants("Counters").Single();
                    counters.SetAttributeValue("passed", 0);
                    counters.SetAttributeValue("failed", 1);
                    trx.Save(path);
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(failed ? 2 : 0, failed ? "Failed: 1" : "Passed: 1"));
            });
            var result = await verifier.RunFocusedEvidenceAsync(root, new GoalId(Guid.NewGuid().ToString("N")),
                string.Join("; ", classes.Select(name => "Infrastructure.Tests:FullyQualifiedName~" + name)));

            Assert.True(result.Accepted, result.Summary);
            Assert.Equal(2, calls.Count);
            Assert.Equal(2, result.Checks.Count);
            Assert.False(result.Checks[0].Passed);
            Assert.True(result.Checks[1].Passed);
            Assert.Contains("bounded-filter-overflow", result.Coverage!.ExecutionReason, StringComparison.Ordinal);
            var arm = Assert.Single(result.Arms!, candidate => candidate.Arm == FindingEvidenceArm.Candidate);
            Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
            Assert.False(arm.Passed);
            Assert.Equal([failure], arm.Checks.SelectMany(check => check.FailingTestIdentities ?? []).ToArray());
            Assert.Contains(classes[0], arm.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("check(s) passed", arm.Summary, StringComparison.Ordinal);
            Assert.Empty(arm.NotRunSelections!);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(storage);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, true, FindingEvidenceArmDisposition.Inconclusive, "not-run:no-result-recorded")]
    [Xunit.InlineData(true, false, FindingEvidenceArmDisposition.Red, "not-run:apparatus-stopped-after-failure")]
    [Xunit.InlineData(true, true, FindingEvidenceArmDisposition.Red, "not-run:no-result-recorded")]
    public async Task MissingResultIsNamedPersistedAndNeverGreen(
        bool firstFails, bool continueAfterFailure, FindingEvidenceArmDisposition disposition, string reason)
    {
        AcceptanceManifestCheck[] planned = [new() { Name = "Alpha filter" }, new() { Name = "Beta filter" }];
        var coverage = new FocusedEvidenceCoverage([
            new("Infrastructure.Tests:AlphaTests", [planned[0].Name]),
            new("Infrastructure.Tests:BetaTests", [planned[1].Name])]);
        AcceptanceCheckResult[] results = [new(planned[0].Name, !firstFails, firstFails ? 2 : 0, "",
            FailingTestIdentities: firstFails ? ["AlphaTests.Fails"] : [], ExecutedTestCount: 1)];
        var arm = await FocusedEvidenceExecution.RunFocusedEvidenceArmAsync(
            _ => throw new InvalidOperationException("No scoped runner is needed."),
            (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>(results),
            _ => "", FindingEvidenceArm.Candidate, "candidate", "unused", null, planned, coverage,
            null, null, null, CancellationToken.None, continueAfterFailure, armContextApplied: true);

        Assert.Equal(disposition, arm.Disposition);
        Assert.False(arm.Passed);
        Assert.Contains("Beta filter " + reason, arm.Summary, StringComparison.Ordinal);
        Assert.Equal(new FindingEvidenceSelection("Infrastructure.Tests", "BetaTests"), Assert.Single(arm.NotRunSelections!));
        Assert.Equal(arm.NotRunSelections, ConductorDriver.CreateFindingEvidenceArmReceipt(arm).NotRunSelections);
        if (firstFails)
            Assert.Equal(["AlphaTests.Fails"], arm.Checks.SelectMany(check => check.FailingTestIdentities ?? []).ToArray());
    }

    [Xunit.Fact]
    public async Task GeneralAcceptanceStillStopsAtFirstFailure()
    {
        var root = CreateManifestWorkspace("""
            {"version":1,"checks":[
                {"name":"first","type":"command","command":"fixture-first"},
                {"name":"second","type":"command","command":"fixture-second"}
            ],"forbiddenChangedPathGlobs":[]}
            """);
        var storage = InfrastructureTestSupport.CreateTempDirectory();
        var commands = new List<string>();
        try
        {
            TestOverrides.BuildStorageRootForTests = new DotnetBuildStorageRoot(storage);
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                commands.Add(args[0]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "fixture failure"));
            });
            var result = await verifier.RunAsync(root);
            Assert.False(result.Passed);
            Assert.Equal(["fixture-first"], commands);
            Assert.Equal("first", Assert.Single(result.Checks!).Name);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(storage);
        }
    }
}
