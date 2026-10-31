using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each instance owns its SQLite stores; console output is captured through AsyncLocal routing.
internal sealed class CliAttentionShowDecisionsFixture : CliTaskQueryTestSupport, IDisposable
{
    private readonly string _root = CreateTempDirectory();
    private readonly SeedClock _clock = new();
    internal OrchestratorWorkspace Workspace { get; }
    internal AgentOrchestratorKernel Kernel { get; }
    internal string HumanId { get; private set; } = "";
    internal string ClarificationId { get; private set; } = "";
    internal string HiddenId { get; private set; } = "";

    internal CliAttentionShowDecisionsFixture()
    {
        Workspace = OrchestratorWorkspace.ForDirectory(_root);
        StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
        Kernel = new AgentOrchestratorKernel(_clock);
        _ = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
    }

    internal async Task SeedAsync(bool empty = false)
    {
        var token = TestContext.Current.CancellationToken;
        if (!empty)
        {
            var clarificationGoal = AddGoal("a1000000aaaaaaaaaaaaaaaaaaaaaaaa");
            var humanGoal = AddGoal("b2000000bbbbbbbbbbbbbbbbbbbbbbbb");
            var evidenceGoal = AddGoal("c3000000cccccccccccccccccccccccc");
            var store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            var clarification = await store.RaiseAsync(CollaborationItemType.Clarification,
                clarificationGoal.Id.Value, "Refinement interface", "Question: Which read-only artifact is authoritative?",
                $"spec-clarification:{clarificationGoal.Id.Value}:duration-history-contract", token);
            ClarificationId = clarification.Id;
            // Console order puts human requests first, even when a clarification was raised earlier.
            _clock.UtcNow = clarification.RaisedAt.AddMinutes(1);
            HumanId = Kernel.RequestHumanInput(humanGoal.Id, null,
                "May we proceed? " + new string('x', 180), kind: HumanWaitKind.Other).Id.Value;
            HiddenId = Kernel.RequestHumanInput(evidenceGoal.Id, null,
                "Wait for prospective acceptance evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence).Id.Value;
            var resolved = await store.RaiseAsync(CollaborationItemType.Clarification,
                clarificationGoal.Id.Value, "Resolved clarification", "Question: Already answered?",
                $"spec-clarification:{clarificationGoal.Id.Value}:resolved", token);
            Assert.True(await store.TryResolveByIdAsync(resolved.Id, "Already answered", token));
        }
        await new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel, token);
        Assert.True(File.Exists(Workspace.SqliteStatePath));
        Assert.True(File.Exists(Path.Combine(Workspace.OrchestratorDirectory, "collaboration-items.db")));
    }

    internal async Task<OwnerConsoleViewModel> ConsoleModelAsync()
    {
        var state = SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath);
        var builder = new OwnerConsoleViewModelBuilder(state,
            OwnerQuestionReadModelFactory.ForWorkspace(Workspace), new Stopped(), new NoEpics(), TimeProvider.System);
        return await builder.BuildAsync(new(_clock.UtcNow, null, [], 0), TestContext.Current.CancellationToken);
    }

    internal string Run(string[] args, bool readOnly = true)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? selected = null;
        return CaptureConsole(() =>
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            var providers = new InMemoryModelProviderRegistry([]);
            if (readOnly)
            {
                var state = SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath);
                Assert.True(CliReadOnlyCommandRunner.TryExecute(args, state, Workspace, providers, null,
                    ref agents, ref profiles, ref selected, out var changed));
                Assert.False(changed);
            }
            else
                Assert.False(CliCommandDispatcher.ExecuteCommand(args, Kernel, Workspace,
                    ref agents, providers, ref profiles, ref selected));
            Assert.Null(selected);
        });
    }

    private Goal AddGoal(string id)
    {
        var goal = Kernel.CreateGoal(new GoalId(id), $"Goal {id[..8]}");
        Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        Assert.Equal(GoalStatus.Active, goal.Status);
        return goal;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class SeedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Stopped : IConductorLiveness
    {
        public bool IsRunning() => false;
    }

    private sealed class NoEpics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken token) => Task.FromResult("");
    }
}
