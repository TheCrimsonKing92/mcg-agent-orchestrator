using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its registry, checkout, metadata and SQLite store; no child is spawned.
public sealed class OwnedChildProjectStateResolutionTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("gate")]
    [InlineData("pre-review-evidence")]
    [InlineData("cohort")]
    [InlineData("train")]
    [InlineData("follower")]
    public async Task NamedProjectRecordLoadsParentGoalWithoutTargetState(string kind)
    {
        using var fixture = new Fixture();
        var parent = fixture.Registry.CreateProject("alpha", fixture.Target, "trunk").ResolveWorkspace();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Owned child reads project state");
        await CreateMigratedStateRepository(parent.SqliteStatePath).SaveAsync(kernel);
        Assert.True(File.Exists(Path.Combine(fixture.Data, "projects", "alpha", "state.db")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));

        // A different registry data root proves the recorded absolute state location is honoured.
        var childRegistry = new OrchestratorProjectRegistry(fixture.Registry.RegistryDirectory,
            Path.Combine(fixture.Root, "different-data"));
        OrchestratorWorkspace child;
        if (IsParallel(kind))
        {
            var record = WriteParallelAttempt(parent, goal, kind);
            Assert.Equal(kind, record.Kind);
            Assert.Equal(parent.OrchestratorDirectory, record.StateDirectory);
            Assert.Equal("alpha", record.ProjectName);
            child = OwnedChildWorkspaceResolver.ForParallelAcceptanceAttempt(record, childRegistry);
        }
        else
        {
            var record = WriteGroupedAttempt(parent, goal, kind);
            Assert.Equal(kind, record.Kind);
            Assert.Equal(goal.Id.Value, Assert.Single(record.Members).GoalId);
            Assert.Equal(parent.OrchestratorDirectory, record.StateDirectory);
            Assert.Equal("alpha", record.ProjectName);
            child = OwnedChildWorkspaceResolver.ForGroupedGateAttempt(record, childRegistry);
        }

        Assert.Equal(parent.SqliteStatePath, child.SqliteStatePath);
        Assert.Equal(parent.OrchestratorDirectory, child.OrchestratorDirectory);
        Assert.Equal(parent.IntegrationBranch, child.IntegrationBranch);
        Assert.Equal(fixture.Target, child.ExecutionDirectory);
        var loaded = await SqliteOrchestratorStateRepository.OpenReadOnly(child.SqliteStatePath)
            .LoadGoalsAsync([goal.Id]);
        Assert.Equal(goal.Id, Assert.Single(loaded.Goals).Id);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));

        // Negative control: the original directory-only resolution cannot open this goal's store.
        var wrong = OrchestratorWorkspace.ForDirectory(fixture.Target, fixture.Target, null, fixture.Registry);
        Assert.NotEqual(child.SqliteStatePath, wrong.SqliteStatePath);
        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            SqliteOrchestratorStateRepository.OpenReadOnly(wrong.SqliteStatePath).LoadGoalsAsync([goal.Id]));
        Assert.Equal(14, error.SqliteErrorCode);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyAndNewHomeRecordsPreserveDirectoryResolution(bool grouped, bool relocated)
    {
        using var fixture = new Fixture();
        if (relocated)
            fixture.Registry.RecordDefaultStateLocation(new DefaultProjectStateLocation(
                fixture.Target, Path.Combine("default-project", "home"), ".orchestrator.backup-test",
                DateTimeOffset.UnixEpoch));
        var expected = OrchestratorWorkspace.ForDirectory(fixture.Target, fixture.Target, null, fixture.Registry);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Legacy home store");
        await CreateMigratedStateRepository(expected.SqliteStatePath).SaveAsync(kernel);

        OrchestratorWorkspace legacy;
        OrchestratorWorkspace current;
        if (grouped)
        {
            var record = WriteGroupedAttempt(expected, goal, "cohort");
            Assert.Null(record.ProjectName);
            var json = JsonNode.Parse(File.ReadAllText(record.MetadataPath))!.AsObject();
            Assert.True(json.Remove("StateDirectory"));
            Assert.True(json.Remove("ProjectName"));
            var old = JsonSerializer.Deserialize<ConductorGroupedGateAttempt>(json.ToJsonString())!;
            Assert.Null(old.StateDirectory);
            Assert.Null(old.ProjectName);
            legacy = OwnedChildWorkspaceResolver.ForGroupedGateAttempt(old, fixture.Registry);
            current = OwnedChildWorkspaceResolver.ForGroupedGateAttempt(record, fixture.Registry);
        }
        else
        {
            var record = WriteParallelAttempt(expected, goal, "gate");
            Assert.Null(record.ProjectName);
            var json = JsonNode.Parse(File.ReadAllText(record.MetadataPath))!.AsObject();
            Assert.True(json.Remove("stateDirectory"));
            Assert.True(json.Remove("projectName"));
            var old = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(json.ToJsonString(), WebJson)!;
            Assert.Null(old.StateDirectory);
            Assert.Null(old.ProjectName);
            legacy = OwnedChildWorkspaceResolver.ForParallelAcceptanceAttempt(old, fixture.Registry);
            current = OwnedChildWorkspaceResolver.ForParallelAcceptanceAttempt(record, fixture.Registry);
        }
        Assert.Equal(expected, legacy);
        Assert.Equal(expected, current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidRecordedWorkspaceFailsClosedWithStateDiagnostic(bool grouped)
    {
        using var fixture = new Fixture();
        var parent = fixture.Registry.CreateProject("alpha", fixture.Target, "trunk").ResolveWorkspace();
        var goal = new AgentOrchestratorKernel().CreateGoal("Fail closed");
        var parallel = grouped ? null : WriteParallelAttempt(parent, goal, "gate");
        var group = grouped ? WriteGroupedAttempt(parent, goal, "cohort") : null;

        OrchestratorWorkspace Resolve(string directory, OrchestratorProjectRegistry registry) => grouped
            ? OwnedChildWorkspaceResolver.ForGroupedGateAttempt(group! with { StateDirectory = directory }, registry)
            : OwnedChildWorkspaceResolver.ForParallelAcceptanceAttempt(parallel! with { StateDirectory = directory }, registry);

        // Writing metadata creates the directory, but must never create an empty state database.
        Assert.True(Directory.Exists(parent.OrchestratorDirectory));
        Assert.False(File.Exists(parent.SqliteStatePath));
        AssertDiagnostic(Assert.Throws<InvalidOperationException>(() =>
            Resolve(parent.OrchestratorDirectory, fixture.Registry)), parent.OrchestratorDirectory, parent.SqliteStatePath);
        var missing = OrchestratorWorkspace.ForProject("alpha", fixture.Target,
            dataRootDirectory: Path.Combine(fixture.Root, "missing-data"));
        AssertDiagnostic(Assert.Throws<InvalidOperationException>(() =>
            Resolve(missing.OrchestratorDirectory, fixture.Registry)), missing.OrchestratorDirectory, missing.SqliteStatePath);
        Assert.False(Directory.Exists(missing.OrchestratorDirectory));

        var mismatch = Path.Combine(fixture.Data, "projects", "wrong");
        AssertDiagnostic(Assert.Throws<InvalidOperationException>(() =>
            Resolve(mismatch, fixture.Registry)), mismatch, parent.SqliteStatePath);

        var unknownRegistry = new OrchestratorProjectRegistry(Path.Combine(fixture.Root, "empty-registry"), fixture.Data);
        var unknown = Assert.Throws<InvalidOperationException>(() =>
            Resolve(parent.OrchestratorDirectory, unknownRegistry));
        AssertDiagnostic(unknown, parent.OrchestratorDirectory, parent.SqliteStatePath);
        Assert.Contains("Unknown project", unknown.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));
        Assert.False(File.Exists(parent.SqliteStatePath));
    }

    private static void AssertDiagnostic(InvalidOperationException error, string recordedDirectory, string statePath)
    {
        Assert.Contains("alpha", error.Message);
        Assert.Contains(recordedDirectory, error.Message);
        Assert.Contains(statePath, error.Message);
    }

    private static bool IsParallel(string kind) => kind is "gate" or "pre-review-evidence";

    private static ConductorParallelAcceptanceAttempt WriteParallelAttempt(
        OrchestratorWorkspace parent, Goal goal, string kind)
    {
        var launchObserved = false;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(parent.OrchestratorDirectory, kind + "-attempts"), parent.IntegrationBranch,
            parent.ExecutionDirectory,
            isProcessAlive: pid => pid == 7102,
            launchOwnedProcess: launch =>
            {
                Assert.True(File.Exists(launch.Attempt.MetadataPath));
                launchObserved = true;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7102);
            },
            stateDirectory: parent.OrchestratorDirectory,
            projectName: OwnedChildWorkspaceResolver.RecordedProjectName(parent));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], new string('a', 40), new string('b', 40));
        var started = kind == "gate"
            ? coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
                (_, _, _, _, _) => throw new InvalidOperationException("Detached gate must not run inline."))
            : coordinator.EvaluateFocusedEvidence(candidate, ConductorAutonomyPolicy.Permissive,
                "Infrastructure.Tests: OwnedChildProjectStateResolutionTests",
                (_, _, _, _) => throw new InvalidOperationException("Detached evidence must not run inline."),
                new ConductorFocusedEvidenceRequestContext("round", "batch", []));
        Assert.True(launchObserved);
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
        return JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(started.Attempt.MetadataPath), WebJson)!;
    }

    private static ConductorGroupedGateAttempt WriteGroupedAttempt(OrchestratorWorkspace parent, Goal goal, string kind)
    {
        var coordinator = new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(parent.OrchestratorDirectory, "grouped-gate-attempts"),
            stateDirectory: parent.OrchestratorDirectory,
            projectName: OwnedChildWorkspaceResolver.RecordedProjectName(parent));
        var member = new GateReadyCandidateProjection(goal.Id, GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            ["owned.cs"], ["owned"], new GateReadyMergeEvidence(new string('a', 40), new string('b', 40),
                GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
        var record = coordinator.Create(kind, [member], new string('b', 40), new string('c', 40),
            "manifest", "identity", parent.ExecutionDirectory, ConductorAutonomyPolicy.Permissive);
        ConductorGroupedGateAttemptCoordinator.Save(record);
        return ConductorGroupedGateAttemptCoordinator.Read(record.MetadataPath);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string Target { get; }
        internal string Data { get; }
        internal OrchestratorProjectRegistry Registry { get; }

        internal Fixture()
        {
            Target = Directory.CreateDirectory(Path.Combine(Root, "target")).FullName;
            Data = Path.Combine(Root, "data");
            Registry = new OrchestratorProjectRegistry(Path.Combine(Root, "registry"), Data);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
