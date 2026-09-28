using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorAuthorHost
{
    internal const string EnabledEnvironmentVariable = "MCG_ORCHESTRATOR_AUTHOR_ENABLED";
    private readonly ConductorAuthorClaimStore _claims;
    private readonly ICollaborationItemStore _collaboration;
    private readonly IConductorAuthorModelRound _model;
    private readonly IOperatorIntentStore _intents;
    private readonly SpecRefinerPrecedentStore _precedents;
    private readonly Func<Goal, string> _workingDirectory;
    private readonly IGoalLifecycleEventWriter _lifecycle;
    private readonly ConductEventLogWriter _conduct;
    private readonly ConductorLessonSelector? _lessons;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, (ConductorAuthorItem Item, Task<string> Round)> _rounds = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal ConductorAuthorHost(
        ConductorAuthorClaimStore claims, ICollaborationItemStore collaboration,
        IConductorAuthorModelRound model, IOperatorIntentStore intents,
        SpecRefinerPrecedentStore precedents, Func<Goal, string> workingDirectory,
        IGoalLifecycleEventWriter lifecycle, ConductEventLogWriter conduct,
        Func<DateTimeOffset>? utcNow = null, bool enabled = true,
        ConductorLessonSelector? lessons = null)
    {
        _claims = claims;
        _collaboration = collaboration;
        _model = model;
        _intents = intents;
        _precedents = precedents;
        _workingDirectory = workingDirectory;
        _lifecycle = lifecycle;
        _conduct = conduct;
        _lessons = lessons;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        Enabled = enabled;
    }

    internal bool Enabled { get; set; }
    internal IReadOnlyCollection<Task<string>> CurrentRounds => _rounds.Values.Select(value => value.Round).ToArray();

    internal static bool ResolveEnabled(string? value) =>
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";

    internal static ConductorAuthorHost CreateDefault(OrchestratorWorkspace workspace) => new(
        new ConductorAuthorClaimStore(Path.Combine(workspace.OrchestratorDirectory, "author-claims.db")),
        CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
        new ClaudeConductorAuthorModelRound(Path.Combine(workspace.OrchestratorDirectory, "author-rounds")),
        SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory),
        new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath),
        goal => GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id) ?? workspace.ExecutionDirectory,
        new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory),
        new ConductEventLogWriter(workspace.ConductEventsLogPath),
        enabled: ResolveEnabled(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)),
        lessons: new ConductorLessonSelector(workspace.OperatorLessonsStorePath,
            message => new ConductEventLogWriter(workspace.ConductEventsLogPath)
                .Append("author-lessons", null, message)));

    internal IReadOnlySet<GoalId> ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId = null)
    {
        var changed = new HashSet<GoalId>();
        if (!Enabled) return changed;
        Harvest(kernel, onlyGoalId, changed);
        foreach (var goal in kernel.Goals.Where(goal => !goal.IsTerminal &&
                     (onlyGoalId is null || goal.Id.Value == onlyGoalId)))
        {
            if (ConductorOwnerQuestionHolds.ExcludesFromWalk(goal.CurrentHold?.State)) continue;
            var items = _collaboration.ListAsync(goal.Id.Value).GetAwaiter().GetResult();
            if (!ConductorAuthorItems.Detect(goal, items, kernel.HumanInputRequests, []).Any()) continue;
            var intents = _intents.ListForGoalAsync(goal.Id.Value, int.MaxValue).GetAwaiter().GetResult();
            foreach (var item in ConductorAuthorItems.Detect(goal, items, kernel.HumanInputRequests, intents))
            {
                if (!_claims.TryClaim(item, _utcNow())) continue;
                try
                {
                    var precedent = string.IsNullOrWhiteSpace(item.ForkKind) ? null :
                        _precedents.TryGetPrecedentAsync(item.ForkKind).GetAwaiter().GetResult();
                    var input = new ConductorAuthorRoundInput(item, goal.Objective,
                        JsonSerializer.Serialize(goal.RefinedSpec, Json), precedent,
                        _lessons?.Select(ConductorLessonSelector.AuthorTags(item.ForkKind)));
                    var directory = _workingDirectory(goal);
                    var round = Task.Run(() => _model.DispatchAsync(input, directory, _shutdown.Token), _shutdown.Token);
                    _rounds.Add(item.Identity, (item, round));
                }
                catch (Exception ex)
                {
                    _claims.Complete(item.Identity, "model-failure");
                    Record(item, "model-failure", $"dispatch:{ex.GetType().Name}");
                }
            }
        }
        Harvest(kernel, onlyGoalId, changed);
        HarvestReady(kernel, onlyGoalId, changed);
        return changed;
    }

    internal void Stop()
    {
        _shutdown.Cancel();
        var drained = true;
        try { drained = Task.WhenAll(_rounds.Values.Select(value => value.Round)).Wait(TimeSpan.FromSeconds(30)); }
        catch (AggregateException) { }
        foreach (var (identity, running) in _rounds)
        {
            if (running.Round.IsCompletedSuccessfully)
            {
                _claims.Preserve(running.Item, running.Round.Result);
                Record(running.Item, "ready", "conductor-stop");
            }
            else
            {
                _claims.Complete(identity, "model-failure");
                Record(running.Item, "model-failure", "conductor-stop");
            }
        }
        _rounds.Clear();
        if (!drained)
            throw new InvalidOperationException("Author model rounds did not drain after conductor shutdown cancellation.");
    }

    private void Harvest(AgentOrchestratorKernel kernel, string? onlyGoalId, HashSet<GoalId> changed)
    {
        foreach (var (identity, running) in _rounds.ToArray())
        {
            if (onlyGoalId is not null && running.Item.GoalId != onlyGoalId) continue;
            if (!running.Round.IsCompleted) continue;
            _rounds.Remove(identity);
            var item = running.Item;
            if (!running.Round.IsCompletedSuccessfully)
            {
                _claims.Complete(identity, "model-failure");
                Record(item, "model-failure", running.Round.IsCanceled ? "timeout" :
                    running.Round.Exception?.GetBaseException().GetType().Name ?? "model-failure");
                continue;
            }
            _claims.Preserve(item, running.Round.Result);
        }
    }

    private void HarvestReady(AgentOrchestratorKernel kernel, string? onlyGoalId, HashSet<GoalId> changed)
    {
        foreach (var ready in _claims.Ready()
                     .OrderByDescending(entry => RequiresOwner(entry.Item, entry.Output)))
        {
            if (onlyGoalId is not null && ready.Item.GoalId != onlyGoalId) continue;
            var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id.Value == ready.Item.GoalId);
            if (goal is not null && ConductorOwnerQuestionHolds.ExcludesFromWalk(goal.CurrentHold?.State)) continue;
            if (_rounds.Values.Any(running => running.Item.GoalId == ready.Item.GoalId)) continue;
            ProcessCompleted(kernel, ready.Item, ready.Output, changed);
        }
    }

    private static bool RequiresOwner(ConductorAuthorItem item, string output)
    {
        var result = ConductorAuthorResultParser.Parse(output);
        return result?.Kind == "ask-owner" || result?.Kind == "answer" &&
            (item.ForkKind == AcceptanceCriterionFeasibility.ForkKind ||
             ConductorAuthorOwnerClassCheck.Evaluate(item.Question, result.Text!, result.EvidenceReferences) is not null);
    }

    private void ProcessCompleted(AgentOrchestratorKernel kernel, ConductorAuthorItem item,
        string output, HashSet<GoalId> changed)
    {
            var identity = item.Identity;
            var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id.Value == item.GoalId);
            if (goal is null || goal.IsTerminal || !IsStillEligible(kernel, goal, item))
            {
                _claims.Complete(identity, "superseded");
                Record(item, "no-action", "item-superseded");
                return;
            }
            if (ConductorOwnerQuestionHolds.ExcludesFromWalk(goal.CurrentHold?.State))
                throw new InvalidOperationException($"Cannot process Author item {identity} while an owner question holds its goal.");
            var result = ConductorAuthorResultParser.Parse(output);
            if (result is null)
            {
                _claims.Complete(identity, "unparseable");
                Record(item, "model-failure", "unparseable-output");
                return;
            }
            if (result.Kind == "ask-owner")
            {
                Escalate(kernel, goal, item, result.Question!, result.Recommendation!, "model-ask-owner", changed);
                _claims.Complete(identity, "owner-question");
                return;
            }
            SubmitCheckedAnswer(kernel, goal, item, result, changed);
    }

    private bool IsStillEligible(AgentOrchestratorKernel kernel, Goal goal, ConductorAuthorItem item)
    {
        var items = _collaboration.ListAsync(goal.Id.Value).GetAwaiter().GetResult();
        var intents = _intents.ListForGoalAsync(goal.Id.Value, int.MaxValue).GetAwaiter().GetResult();
        return ConductorAuthorItems.Detect(goal, items, kernel.HumanInputRequests, intents)
            .Any(candidate => candidate.Identity == item.Identity);
    }

    private void SubmitCheckedAnswer(AgentOrchestratorKernel kernel, Goal goal,
        ConductorAuthorItem item, ConductorAuthorResult result, HashSet<GoalId> changed)
    {
        var reason = item.ForkKind == AcceptanceCriterionFeasibility.ForkKind
            ? "acceptance-weakening"
            : ConductorAuthorOwnerClassCheck.Evaluate(item.Question, result.Text!, result.EvidenceReferences);
        if (reason is not null)
        {
            Escalate(kernel, goal, item, item.Question, result.Text!, reason, changed);
            _claims.Complete(item.Identity, "owner-question");
            return;
        }
        var references = result.EvidenceReferences!.Append($"author-item={item.Identity}").ToArray();
        var payload = new AnswerOperatorIntentPayload(item.TargetKind, item.TargetId, item.GoalId,
            result.Text!, OperatorActorKind.Agent, references, result.Precedent);
        try
        {
            var intent = _intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                $"author-{item.Identity}", OperatorIntentVerbs.Answer, item.GoalId, null,
                JsonSerializer.Serialize(payload, Json), [], "author", "conductor-author", "author",
                _utcNow(), ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();
            _claims.Complete(item.Identity, "answer-submitted", intent.Id);
            Record(item, "answer", $"intent={intent.Id}");
        }
        catch
        {
            _claims.Complete(item.Identity, "submit-failed");
            throw;
        }
    }

    private void Escalate(AgentOrchestratorKernel kernel, Goal goal, ConductorAuthorItem item,
        string question, string recommendation, string reason, HashSet<GoalId> changed)
    {
        new ConductorAuthorOwnerQuestion(item.GoalId, item.TargetKind, item.TargetId,
            question, recommendation, reason)
            .Raise(kernel, goal, _lifecycle, _conduct, _utcNow(), changed);
        Record(item, "ask-owner", reason);
    }

    private void Record(ConductorAuthorItem item, string kind, string reason) =>
        _conduct.Append("author", item.GoalId,
            $"item={item.Identity} kind={kind} reason={reason}");
}
