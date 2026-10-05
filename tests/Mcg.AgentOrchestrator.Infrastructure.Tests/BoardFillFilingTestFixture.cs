using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each fixture owns its draft, store and event log; all mutation seams are fakes.
internal sealed class BoardFillFilingTestFixture : IDisposable
{
    internal BoardFillAssessmentTestFixture Drafts { get; } = new();
    internal ConductorAutonomyPolicy Policy { get; set; } = ConductorAutonomyPolicy.Permissive with
        { BoardFillMode = ConductorBoardFillMode.File, BoardFillMaxDraftsPerDay = 1 };
    internal BoardFillGoalBoard Board { get; set; } = new(0, new HashSet<string>());
    internal string? Head { get; set; } = BoardFillAssessmentTestFixture.Head;
    internal Action? BeforeHead { get; set; }
    internal IntakeFake Intake { get; } = new();
    internal ConductorBoardFillHost Host { get; }
    internal BoardFillFilingTestFixture()
    {
        Host = new(Drafts.Store, (_, _) => throw new InvalidOperationException("No drafting expected."),
            () => [Drafts.Item], (_, _) => BoardFillAssessmentTestFixture.Ready, () => Policy,
            new(Drafts.EventPath, utcNow: () => Drafts.Clock.UtcNow), () => Drafts.Clock.UtcNow,
            filing: new(Intake, () => Board, () => { BeforeHead?.Invoke(); return Head; }));
    }

    internal BoardFillDraftRound Assessed(bool fileable = true, BoardFillScopeProposal? scope = null)
    {
        var round = Drafts.Finished();
        Drafts.Store.Assess(round.Id, new(BoardFillAssessmentTestFixture.Verified,
            BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief), scope ?? BoardFillAssessmentTestFixture.NoDepends,
            fileable, fileable ? [] : ["changed"]));
        return Drafts.Store.ReadAll().Single(row => row.Id == round.Id);
    }

    internal string[] FilingEvents() => !System.IO.File.Exists(Drafts.EventPath) ? [] :
        System.IO.File.ReadAllLines(Drafts.EventPath).Where(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.GetProperty("eventKind").GetString() == "board-fill-filed";
        }).ToArray();

    internal sealed class IntakeFake : IBoardFillGoalIntake
    {
        internal List<BoardFillIntakeRequest> Requests { get; } = [];
        internal List<(string Goal, string Dependency)> Dependencies { get; } = [];
        internal Dictionary<string, string> Goals { get; } = new();
        internal Func<BoardFillIntakeRequest, BoardFillIntakeResult>? Filing { get; set; }
        internal Func<string, BoardFillIntakeResult>? Dependency { get; set; }
        public BoardFillIntakeResult File(BoardFillIntakeRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (Filing is not null) return Filing(request);
            var replay = Goals.TryGetValue(request.RequestKey, out var goal);
            goal ??= "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            Goals[request.RequestKey] = goal;
            return new(replay ? "replayed" : "filed", goal, "intake stdout", "intake stderr", 0);
        }
        public BoardFillIntakeResult Depend(string goalId, string dependencyGoalId, CancellationToken token)
        {
            Dependencies.Add((goalId, dependencyGoalId));
            return Dependency?.Invoke(dependencyGoalId) ?? new("applied", goalId, "dependency stdout", "", 0);
        }
    }

    public void Dispose() { Host.Stop(); Drafts.Dispose(); }
}
