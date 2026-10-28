using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;

internal sealed class CohortNormalizedFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-normalized-cohort", Guid.NewGuid().ToString("N"));
    internal string SourceRoot => Path.Combine(Root, "attempt");
    internal string OrchestratorRoot => Path.Combine(Root, ".orchestrator");
    internal string StorePath => Path.Combine(OrchestratorRoot, "cohort-acceptance.db");
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
    internal AcceptanceCohortIdentity Identity { get; } = MakeIdentity();

    internal CohortNormalizedFixture() => Directory.CreateDirectory(SourceRoot);

    internal AcceptanceCheckResult Normalized(string name = "crosswalk", string id = "same opaque ID",
        string status = "passed", string[]? selected = null, int runOrdinal = 1)
    {
        var records = status == "empty" ? Array.Empty<TestResult>() : new[] { Record(id, status) };
        var path = Path.Combine(SourceRoot, name + ".ctrf.json");
        NormalizedTestResultWriter.Write(path, "crosswalk", records);
        var evidence = new AcceptanceCohortCheckEvidence(name, AcceptanceCohortCheckEvidence.NormalizedFormat,
            "project", "attempt", runOrdinal, 0, SourceRoot, selected ?? [id], records.Count(r => r.Status != TestResultStatus.Skipped),
            [CohortTestResultEvidence.Capture(path, AcceptanceCohortCheckEvidence.NormalizedFormat)]);
        return new(name, true, 0, null, TestProjectPath: "project", TestResultAttemptId: "attempt", TestResultRunOrdinal: runOrdinal,
            ExecutedTestCount: evidence.ExecutedTestCount)
        { CohortEvidence = evidence, NormalizedTestResultPaths = [path], CrosswalkDiagnostics = [] };
    }

    internal AcceptanceCheckResult Trx(int runOrdinal = 1)
    {
        var path = Path.Combine(SourceRoot, "dotnet.trx");
        File.WriteAllText(path, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testId="local-id" testName="test" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        return new("dotnet", true, 0, null, TestResultPaths: [path], TestResultAttemptId: "attempt", TestResultRunOrdinal: runOrdinal,
            ExecutedTestCount: 1);
    }

    internal AcceptanceVerificationResult Verification(params AcceptanceCheckResult[] checks) => new(true, false, 0, null,
        Checks: checks, TestResultPaths: checks.SelectMany(check => check.NormalizedTestResultPaths ?? check.TestResultPaths ?? []).ToArray())
    {
        AttemptId = "attempt",
        RequiredCrosswalkChecks = checks.Where(check => check.CohortEvidence is not null).Select(check => check.Name).ToArray(),
        RequiredTestCheckNames = checks.Select(check => check.Name).ToArray()
    };

    internal AcceptanceCohortReceipt Save(AcceptanceVerificationResult result, bool red = false)
    {
        var classified = AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResultWithPaths(result);
        if (!red) Assert.Equal(AcceptanceCohortGateOutcome.Passed, classified.Classification.Outcome);
        return new CohortAcceptanceStore(StorePath).SaveGateReceipt(new("receipt", Identity,
            red ? AcceptanceCohortGateOutcome.Failed : classified.Classification.Outcome, Now.AddDays(-15), 10, [], red ? 1 : 0,
            classified.NormalizedTestResultPaths, ValidForLanding: !red)
            { GateChecks = classified.Checks.Select(check => check.BindToCohort(Identity)).ToArray() });
    }

    internal static AcceptanceCohortIdentity MakeIdentity(char main = 'a', char tree = 'd', char candidate = 'b', bool otherMember = false) =>
        AcceptanceCohortIdentity.Create([Binding(new string('1', 32), candidate), Binding(new string(otherMember ? '3' : '2', 32), 'b')],
            new string(main, 40), new string(tree, 40), "manifest");

    private static AcceptanceCohortMemberBinding Binding(string id, char candidate) => new(new GoalId(id),
        new string(candidate, 40), new string('c', 40), ["src/Example.cs"], ["resource:example"],
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    internal static TestResult Record(string id, string status) => new(id, id, null, [], status switch
    {
        "failed" => TestResultStatus.Failed, "error" => TestResultStatus.Error,
        "skipped" => TestResultStatus.Skipped, _ => TestResultStatus.Passed
    }, 1, null, null, [], null, null, "crosswalk");

    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
