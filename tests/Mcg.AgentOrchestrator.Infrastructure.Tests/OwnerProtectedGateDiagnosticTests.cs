using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temp root/databases; git is injected and stdout is a StringWriter.
public sealed class OwnerProtectedGateDiagnosticTests : IDisposable
{
    private const string ChangedFields = "config/acceptance-manifest.json: changed field(s) engine.maxConcurrentShards";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Theory]
    [InlineData("owner-protected configuration")]
    [InlineData("acceptance manifest trusted dimensions")]
    public void Classify_OwnerFailureWithoutTrx_NamesReasonAndChangedField(string name)
    {
        var check = new AcceptanceCheckResult(name, false, 1, ChangedFields);
        var result = ConductorDriver.ClassifyCohortVerificationResult(
            new AcceptanceVerificationResult(false, false, 1, "generic result tail", Checks: [check]));

        Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, result.Outcome);
        Assert.Equal("owner-protected-configuration", result.InfrastructureReasonCode);
        Assert.Contains("engine.maxConcurrentShards", result.InfrastructureDetail);
        Assert.Contains(ChangedFields, result.InfrastructureDetail);
    }

    [Fact]
    public void Classify_OtherFailureWithoutTrx_PreservesIncoherentReason()
    {
        var result = ConductorDriver.ClassifyCohortVerificationResult(
            new AcceptanceVerificationResult(false, false, 1, "missing trx",
                Checks: [new("dotnet test", false, 1, "missing trx")]));

        Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, result.Outcome);
        Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent, result.InfrastructureReasonCode);
    }

    [Fact]
    public void Classify_SourceSizeFailureWithoutTrx_PreservesFailedOutcome()
    {
        var result = ConductorDriver.ClassifyCohortVerificationResult(
            new AcceptanceVerificationResult(false, false, 1, "source size limit",
                Checks: [new(SourceSizeRatchetPreflight.CheckName, false, 1, "source size limit"),
                    new("owner-protected configuration", false, 1, ChangedFields)]));

        Assert.Equal(AcceptanceCohortGateOutcome.Failed, result.Outcome);
        Assert.Null(result.InfrastructureReasonCode);
    }

    [Fact]
    public void Classify_CoherentPassingTrx_PreservesPassedOutcome()
    {
        var trx = Path.Combine(_root, "passing.trx");
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Passes" outcome="Passed" />
              </Results>
              <ResultSummary outcome="Completed">
                <Counters total="1" executed="1" passed="1" failed="0" />
              </ResultSummary>
            </TestRun>
            """);

        var result = ConductorDriver.ClassifyCohortVerificationResult(
            new AcceptanceVerificationResult(true, false, 0, "passed", TestResultPaths: [trx]));

        Assert.Equal(AcceptanceCohortGateOutcome.Passed, result.Outcome);
        Assert.Null(result.InfrastructureReasonCode);
    }

    [Theory]
    [InlineData("owner-protected configuration")]
    [InlineData(" ACCEPTANCE MANIFEST TRUSTED DIMENSIONS ")]
    public void Find_MixedChecks_ReturnsFirstBlockingOwnerFailure(string name)
    {
        var expected = new AcceptanceCheckResult(name, false, 1, ChangedFields);
        var result = new AcceptanceVerificationResult(false, false, 1, null, Checks:
            [new("dotnet test", false, 1, "other failure"),
             new(name, true, 0, null), new(name, false, 1, null, Advisory: true), expected,
             new(name, false, 1, "later owner failure")]);

        Assert.Same(expected, OwnerProtectedGateDiagnostic.Find(result));
    }

    [Fact]
    public void Find_NoBlockingOwnerFailure_ReturnsNull()
    {
        Assert.Null(OwnerProtectedGateDiagnostic.Find(new(false, false, 1, null)));
        Assert.Null(OwnerProtectedGateDiagnostic.Find(new(false, false, 1, null, Checks:
            [new("dotnet test", false, 1, "other failure"),
             new("owner-protected configuration", true, 0, null),
             new("acceptance manifest trusted dimensions", false, 1, null, Advisory: true)])));
    }

    [Fact]
    public void Write_MultilineDetail_EmitsExactlyOneNormalizedLine()
    {
        using var writer = new StringWriter();
        OwnerProtectedGateDiagnostic.Write(writer, "cohort-identity",
            new("owner-protected configuration", false, 1, ChangedFields + "\r\n\t owner decision required"));

        Assert.Equal("ACCEPTANCE_OWNER_PROTECTED_FAILURE gate=cohort-identity " +
            "check=owner-protected configuration detail=" + ChangedFields +
            " owner decision required" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void Write_LongDetail_BoundsLineAndKeepsChangedField()
    {
        using var writer = new StringWriter();
        OwnerProtectedGateDiagnostic.Write(writer, "cohort-identity",
            new("owner-protected configuration", false, 1, ChangedFields + "\n" + new string('x', 10_000)));

        var line = Assert.Single(writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        const string prefix = "ACCEPTANCE_OWNER_PROTECTED_FAILURE gate=cohort-identity check=owner-protected configuration detail=";
        Assert.StartsWith(prefix, line);
        Assert.Contains(ChangedFields, line);
        Assert.Equal(prefix.Length + 512, line.Length);
        Assert.EndsWith("...", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\t', line);
    }

    [Fact]
    public async Task Solo_ManifestChange_RequiresApprovalForExactGoalAndSha()
    {
        const string manifest = "config/acceptance-manifest.json";
        const string mainJson = """{"engine":{"maxConcurrentShards":2}}""";
        const string candidateJson = """{"engine":{"maxConcurrentShards":3}}""";
        var sha = new string('a', 40);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Manifest approval control", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var decisions = CollaborationItemStore.ForDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, manifest), candidateJson);
        string? Git(string _, string[] args) => args[0] == "diff" ? manifest + "\n" : mainJson;

        var held = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, goal.Id,
            [manifest], null, Git, decisions, () => throw new InvalidDataException("No equivalent partition"), sha);

        var failure = Assert.IsType<AcceptanceCheckResult>(held.Failure);
        Assert.Equal("owner-protected configuration", failure.Name);
        Assert.Equal("operator review required", failure.ResultSummary);
        Assert.False(failure.Passed);
        Assert.False(held.OwnerApprovalSatisfied);

        var intents = new SqliteOperatorIntentStore(Path.Combine(_root, "operator-intents.db"), Path.Combine(_root, "logs"));
        await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, goal.Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(sha, "Owner approved candidate.")),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));
        new OperatorIntentCoordinator(intents, decisions: decisions, goalStateVersionResolver: _ => 0).ExecutePending(kernel, goal);

        var approved = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, goal.Id,
            [manifest], null, Git, decisions, () => throw new InvalidDataException("No equivalent partition"), sha);

        Assert.Null(approved.Failure);
        Assert.True(approved.OwnerApprovalSatisfied);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
