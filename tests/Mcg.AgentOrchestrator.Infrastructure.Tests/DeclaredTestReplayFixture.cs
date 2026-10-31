using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class DeclaredTestReplayFixture : IDisposable
{
    internal const string Version = "conduct-tail::version::version_flag_prints_package_version";
    internal const string Usage = "conduct-tail::tests::no_arguments_prints_usage_and_exits_2";
    internal ProspectiveEvidenceGateFixture Gate { get; } = new();
    internal string Path { get; }
    internal const string SourceId = "declared-replay";

    internal DeclaredTestReplayFixture([CallerFilePath] string source = "")
    {
        var root = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verified)
            ? System.IO.Path.Combine(verified, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")
            : System.IO.Path.GetDirectoryName(source)!;
        var directory = System.IO.Path.Combine(Gate.OrchestratorDirectory, "acceptance-gate-attempts", Gate.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "declared.ctrf.json");
        File.Copy(System.IO.Path.Combine(root, "Fixtures", "GateCtrf", "conduct-tail-declared-test.ctrf.json"), Path);
        GateTestObligationCheckOutcomeTests.WriteManifest(Gate, new GoalAcceptanceVerifier.AcceptanceManifestCheck()
        {
            Name = "declared-test", Type = "junit-command", Project = ".", Command = "cargo",
            Arguments = ["nextest", "run", "--profile", "ci"], Reports = ["target/nextest/ci/junit.xml"]
        });
        File.WriteAllText(System.IO.Path.Combine(directory, SourceId + ".attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = SourceId, goalId = Gate.Goal.Id.Value,
            kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
            branchHeadSha = Gate.CandidateSha, outcome = ConductorParallelAcceptanceAttemptOutcome.Passed,
            completedAt = DateTimeOffset.UnixEpoch, executionDirectory = Gate.Root,
            testResultPaths = new[] { System.IO.Path.GetFileName(Path) }
        }));
    }

    internal DeclaredTestReportRows.Report Read() => DeclaredTestReportRows.Read([Path], Gate.Root,
        Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(Gate.OrchestratorDirectory,
            Gate.Goal.Id, Gate.CandidateSha)).Checks);

    internal GateTestObligationEvaluator.Result Evaluate(params string[] selectors) =>
        GateTestObligationCheckOutcomeTests.Evaluate(Gate, selectors);

    public void Dispose() => Gate.Dispose();
}
