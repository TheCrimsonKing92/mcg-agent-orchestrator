using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ProspectiveEvidenceGateResolverTests
{
    [Fact]
    public void IncidentClassSelectorCompletesWaitWithAttemptAndTrxProvenance() => VerifySatisfied(false);

    [Fact]
    public void SixExactIncidentRowsCompleteWaitWithAttemptAndTrxProvenance() => VerifySatisfied(true);

    private static void VerifySatisfied(bool exactRows)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(exactRows ? ProspectiveEvidenceGateFixture.IncidentRows : [ProspectiveEvidenceGateFixture.IncidentClass]);
        var (attemptId, trxPath, _) = fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.True(wait.IsCompleted);
        Assert.Contains(attemptId, wait.Answer, StringComparison.Ordinal);
        Assert.Contains(trxPath, wait.Answer, StringComparison.Ordinal);
        Assert.Equal(HumanInputAnswerOrigin.Worker, wait.AuthoritativeAnswer!.Origin);
        Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        Assert.Equal(0, GoalAcceptanceStatusProjector.Build(fixture.Kernel, fixture.Goal,
            fixture.Root, "main").PendingHumanInputCount);
    }

    [Fact]
    public void MethodSelectorUsesDefinitionsAndCombinesPathsWithinOneAttempt()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(["GateTests.Check", "OtherTests"]);
        var extra = Path.Combine(fixture.Root, "definitions.trx");
        new XDocument(new XElement("TestRun",
            new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "id1"),
                new XElement("TestMethod", new XAttribute("className", "GateTests"), new XAttribute("name", "Check")))),
            new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "id1"),
                new XAttribute("testName", "Check(value: 1)"), new XAttribute("outcome", "Passed")))))
            .Save(extra);
        var (id, trx, _) = fixture.WriteAttempt([("OtherTests.Check", "Passed")], additionalPaths: [extra]);

        fixture.Resolve();

        Assert.True(wait.IsCompleted);
        Assert.Contains(id, wait.Answer, StringComparison.Ordinal);
        Assert.Contains(trx, wait.Answer, StringComparison.Ordinal);
        Assert.Contains(extra, wait.Answer, StringComparison.Ordinal);
    }
}

// Each fixture owns an explicit, unique root; no process or environment state is shared.
internal sealed class ProspectiveEvidenceGateFixture : IDisposable
{
    internal const string IncidentClass = "ReadOnlyDispatchIntegrationEquivalenceTests";
    // Verbatim rows from the answered prerequisite in the packaged goal/timeline.json.
    internal static readonly string[] IncidentRows =
    [
        "ReadOnlyDispatchIntegrationEquivalenceTests.AdvanceOnce_SameInputs_PreserveEventsAndReadOnlyOutcomes(scenario: \"lease\", recovery: null, expectedRecovery: null)",
        "ReadOnlyDispatchIntegrationEquivalenceTests.AdvanceOnce_SameInputs_PreserveEventsAndReadOnlyOutcomes(scenario: \"allowed\", recovery: null, expectedRecovery: null)",
        "ReadOnlyDispatchIntegrationEquivalenceTests.AdvanceOnce_SameInputs_PreserveEventsAndReadOnlyOutcomes(scenario: \"held\", recovery: \"Reclaimed\", expectedRecovery: \"reclaimed\")",
        "ReadOnlyDispatchIntegrationEquivalenceTests.AdvanceOnce_SameInputs_PreserveEventsAndReadOnlyOutcomes(scenario: \"held\", recovery: null, expectedRecovery: \"state-unavailable\")",
        "ReadOnlyDispatchIntegrationEquivalenceTests.AdvanceOnce_SameInputs_PreserveEventsAndReadOnlyOutcomes(scenario: \"refused\", recovery: null, expectedRecovery: null)",
        "ReadOnlyDispatchIntegrationEquivalenceTests.AdvanceOnce_SameInputs_PreserveEventsAndReadOnlyOutcomes(scenario: \"held\", recovery: \"Live\", expectedRecovery: \"live\")"
    ];
    internal static IEnumerable<(string Name, string Outcome)> PassingRows => IncidentRows.Select(name => (name, "Passed"));
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-prospective-evidence", Guid.NewGuid().ToString("N"));
    internal AgentOrchestratorKernel Kernel { get; } = new();
    internal Goal Goal { get; }
    internal string CandidateSha { get; } = new('a', 40);
    internal string OrchestratorDirectory => Path.Combine(Root, ".orchestrator");

    internal ProspectiveEvidenceGateFixture()
    {
        Directory.CreateDirectory(Root);
        Goal = Kernel.CreateGoal("Resolve named gate evidence", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
    }

    internal HumanInputRequest CreateWait(IReadOnlyList<string>? names,
        string? owner = "acceptance-gate", HumanWaitKind kind = HumanWaitKind.ProspectiveAcceptanceEvidence) =>
        Kernel.RequestHumanInputDeduplicated(Goal.Id, null, $"Gate evidence {Guid.NewGuid():N}",
            kind: kind, evidenceOwner: owner, testNames: names).Request;

    internal ProspectiveEvidenceGateResolution Resolve() =>
        ProspectiveEvidenceGateResolver.Resolve(Kernel, Goal, OrchestratorDirectory, CandidateSha);

    internal (string Id, string TrxPath, string MetadataPath) WriteAttempt(
        IEnumerable<(string Name, string Outcome)> rows, string? candidateSha = null,
        ConductorParallelAcceptanceAttemptOutcome outcome = ConductorParallelAcceptanceAttemptOutcome.Passed,
        DateTimeOffset? completedAt = null, IReadOnlyList<string>? additionalPaths = null) =>
        WriteAttempt(OrchestratorDirectory, Goal, candidateSha ?? CandidateSha, rows, outcome, completedAt, additionalPaths);

    internal static (string Id, string TrxPath, string MetadataPath) WriteAttempt(
        string orchestratorDirectory, Goal goal, string candidateSha, IEnumerable<(string Name, string Outcome)> rows,
        ConductorParallelAcceptanceAttemptOutcome outcome = ConductorParallelAcceptanceAttemptOutcome.Passed,
        DateTimeOffset? completedAt = null, IReadOnlyList<string>? additionalPaths = null)
    {
        var directory = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts", goal.Id.Value);
        Directory.CreateDirectory(directory);
        var id = $"{goal.Id.Value[..8]}-0-{Guid.NewGuid():N}";
        var trx = Path.Combine(directory, id + ".trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", rows.Select(row =>
            new XElement(ns + "UnitTestResult", new XAttribute("testName", row.Name), new XAttribute("outcome", row.Outcome))))))
            .Save(trx);
        var metadata = Path.Combine(directory, id + ".attempt.json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(new
        {
            attemptId = id, goalId = goal.Id.Value, kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
            branchHeadSha = candidateSha, outcome, completedAt = completedAt ?? DateTimeOffset.UtcNow,
            testResultPaths = new[] { Path.GetFileName(trx) }.Concat(additionalPaths ?? []).ToArray()
        }));
        return (id, trx, metadata);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
