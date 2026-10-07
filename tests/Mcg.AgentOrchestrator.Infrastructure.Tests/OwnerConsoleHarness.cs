using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class OwnerConsoleHarness
{
    internal readonly AgentOrchestratorKernel Kernel = new();
    internal readonly FakeState State;
    internal readonly FakeQuestions Questions = new();
    internal readonly FakeAnswers Answers = new();
    internal readonly FakeOutput Output = new();
    internal readonly FakeDigest Digest = new();
    internal readonly FakeTail Tail = new();
    internal readonly FakeLiveness Liveness = new();
    internal readonly FakeConductor Conductor = new();
    internal readonly FakeDigestReport DigestReport = new();
    internal readonly TimeProvider Clock = new FixedClock();

    internal OwnerConsoleHarness() => State = new FakeState(Kernel);

    internal Goal AddGoal(string id, string title, AgentRole role)
    {
        var goal = Kernel.CreateGoal(new GoalId(id), title,
            [new TaskSpec(TaskId.New(), "Work", role)]);
        Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return goal;
    }

    internal OwnerConsoleSession Session() => new(
        State, Questions, Answers, Liveness, Digest, Tail, Output, Clock, Conductor, DigestReport);

    internal sealed class FakeConductor : IOwnerConsoleConductor
    {
        internal readonly List<string[]> Calls = [];
        public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
        {
            Calls.Add(args.ToArray());
            output.WriteLine($"conductor output {Calls.Count}");
            error.WriteLine($"conductor error {Calls.Count}");
            return 0;
        }
    }

    internal sealed class FakeDigestReport : IOwnerConsoleDigestReport
    {
        internal int Calls;
        public int Run(TextWriter output)
        {
            Calls++;
            output.WriteLine("digest first");
            output.WriteLine("digest second");
            output.WriteLine("digest third");
            return 0;
        }
    }

    internal sealed class FakeState(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        internal readonly List<string> Calls = [];
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("metadata");
            return Task.FromResult<IReadOnlyList<GoalSummary>>(kernel.Goals.Select(goal =>
                new GoalSummary(goal.Id.Value, goal.Status.ToString(), goal.Objective, "2026-01-01T00:00:00Z")).ToArray());
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("goals");
            var selected = ids.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var snapshot = kernel.ExportSnapshot();
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Where(goal => selected.Contains(goal.Id)).ToArray(),
                HumanInputRequests = snapshot.HumanInputRequests.Where(request => selected.Contains(request.GoalId)).ToArray()
            }));
        }

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>(kernel.ExportSnapshot().HumanInputRequests
                .Where(request => !request.IsCompleted).ToArray());

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>(kernel.Goals.Where(goal =>
                goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded &&
                goal.CurrentHold?.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase) == true)
                .Select(goal => new TerminalOwnerQuestionHold(goal.Id.Value, goal.CurrentHold!.Identity,
                    goal.CurrentHold.State, goal.CurrentHold.Blocker, goal.CurrentHold.StartedAt)).ToArray());
    }

    internal sealed class FakeQuestions : IOwnerQuestionSource
    {
        internal readonly List<OwnerQuestion> Items = [];
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OwnerQuestion>>(Items.ToArray());
    }

    internal sealed class FakeAnswers : IOwnerAnswerSubmitter
    {
        internal readonly List<(string Id, string Text)> Calls = [];
        public void Submit(OwnerQuestion question, string answer) => Calls.Add((question.ItemId, answer));
    }

    internal sealed class FakeOutput : IOwnerConsoleOutput
    {
        private readonly System.Text.StringBuilder _buffer = new();
        internal string Text => _buffer.ToString();
        public void Write(string text) => _buffer.Append(text);
        public void WriteLine(string text) => _buffer.AppendLine(text);
    }

    internal sealed class FakeDigest : IOwnerDigestSummary
    {
        public IReadOnlyList<string> ReadSummaryLines() => ["Owner digest: landed=2 pending=1"];
    }

    internal sealed class FakeTail : IGoalEventTail
    {
        internal int RequestedCount;
        public IReadOnlyList<string> ReadLast(string goalId, int count)
        { RequestedCount = count; return ["recent event"]; }
    }

    internal sealed class FakeLiveness : IConductorLiveness
    {
        public bool IsRunning() => true;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);
    }
}
