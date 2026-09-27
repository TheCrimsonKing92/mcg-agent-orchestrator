using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorStewardHost
{
    internal const string EnabledEnvironmentVariable = "MCG_ORCHESTRATOR_STEWARD_ENABLED";
    private readonly ConductorStewardTriggerStore _triggers;
    private readonly ConductorStewardTriggerDetector _detector;
    private readonly IConductorStewardModelRound _model;
    private readonly IOperatorIntentStore _intents;
    private readonly AdjudicationEvidenceResolver _evidence;
    private readonly Func<GoalId, long?> _version;
    private readonly Func<Goal, string> _workingDirectory;
    private readonly IGoalLifecycleEventWriter _lifecycle;
    private readonly ConductEventLogWriter _conduct;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly CancellationTokenSource _shutdown = new();
    private Task<string>? _round;
    private ConductorStewardStoredTrigger? _running;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    internal ConductorStewardHost(
        ConductorStewardTriggerStore triggers,
        ConductorStewardTriggerDetector detector,
        IConductorStewardModelRound model,
        IOperatorIntentStore intents,
        AdjudicationEvidenceResolver evidence,
        Func<GoalId, long?> version,
        Func<Goal, string> workingDirectory,
        IGoalLifecycleEventWriter lifecycle,
        ConductEventLogWriter conduct,
        Func<DateTimeOffset>? utcNow = null,
        bool enabled = true)
    {
        _triggers = triggers;
        _detector = detector;
        _model = model;
        _intents = intents;
        _evidence = evidence;
        _version = version;
        _workingDirectory = workingDirectory;
        _lifecycle = lifecycle;
        _conduct = conduct;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        Enabled = enabled;
    }

    internal bool Enabled { get; set; }
    internal Task? CurrentRound => _round;

    internal void Stop()
    {
        _shutdown.Cancel();
        var drained = true;
        try { drained = _round?.Wait(TimeSpan.FromSeconds(30)) ?? true; }
        catch (AggregateException) { }
        if (!drained)
            throw new InvalidOperationException("Steward model round did not drain after conductor shutdown cancellation.");
        if (_running is { } running)
        {
            _triggers.MarkServiced(running.Key, "model-failure", null);
            Record(running.Trigger, "model-failure", "conductor-stop", null);
            _running = null;
            _round = null;
        }
    }

    internal static ConductorStewardHost CreateDefault(OrchestratorWorkspace workspace)
    {
        var versionReader = OperatorChannelComposition.BuildGoalStateVersionReader(workspace.OrchestratorDirectory);
        var enabledSetting = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        return new ConductorStewardHost(
            new ConductorStewardTriggerStore(Path.Combine(workspace.OrchestratorDirectory, "steward-triggers.db")),
            new ConductorStewardTriggerDetector(
                (goal, className) => CandidateAddedClassCollection(workspace, goal, className),
                goal => ConductorStewardAcceptanceTrxResolver.Resolve(workspace.OrchestratorDirectory, goal)),
            new ClaudeConductorStewardModelRound(),
            SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory),
            new AdjudicationEvidenceResolver(workspace.OrchestratorDirectory),
            goalId => versionReader(goalId.Value, CancellationToken.None).GetAwaiter().GetResult(),
            goal => GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id) ?? workspace.ExecutionDirectory,
            new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory),
            new ConductEventLogWriter(workspace.ConductEventsLogPath),
            enabled: !string.Equals(enabledSetting, "false", StringComparison.OrdinalIgnoreCase) &&
                     enabledSetting != "0");
    }

    internal IReadOnlySet<GoalId> ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId = null)
    {
        if (!Enabled) return new HashSet<GoalId>();
        var changed = new HashSet<GoalId>();
        var now = _utcNow();
        foreach (var expired in _triggers.ExpireStale(now.AddMinutes(-15)))
            Record(expired.Trigger, "model-failure", "timeout", null);
        ReconcileSubmittedRoutes();
        Harvest(kernel, changed);

        foreach (var goal in kernel.Goals.Where(goal => !goal.IsTerminal &&
                     (onlyGoalId is null || goal.Id.Value == onlyGoalId)))
        foreach (var trigger in _detector.Detect(goal))
        {
            var stored = _triggers.Observe(trigger);
            if (stored.Status == "serviced" && stored.Outcome == "route-applied" &&
                trigger.OccurredAt > stored.FirstOccurrence && !stored.RepeatQuestionRaised)
            {
                Question(kernel, goal, trigger,
                    "The same recovery trigger recurred after a Steward retry. Owner decision required.",
                    trigger.EvidenceReferences, changed);
                _triggers.MarkRepeatQuestionRaised(stored.Key);
                continue;
            }
            if (stored.Status == "pending" && _triggers.HasAppliedRoute(trigger))
            {
                Question(kernel, goal, trigger,
                    "A Steward route was already applied for this task and candidate. Owner decision required.",
                    trigger.EvidenceReferences, changed);
                _triggers.MarkServiced(stored.Key, "owner-question", null);
            }
        }

        if (_round is null)
        {
            var claim = _triggers.ClaimNext(now);
            if (claim is not null)
            {
                _running = claim;
                var goal = kernel.Goals.FirstOrDefault(item => item.Id.Value == claim.Trigger.GoalId);
                if (goal is null || goal.IsTerminal)
                {
                    _triggers.MarkServiced(claim.Key, "no-action", null);
                    Record(claim.Trigger, "no-action", "goal-unavailable", null);
                    _running = null;
                }
                else
                {
                    var worktree = _workingDirectory(goal);
                    _round = Task.Run(() => _model.DispatchAsync(claim.Trigger, worktree, _shutdown.Token), _shutdown.Token);
                }
            }
        }
        return changed;
    }

    private void ReconcileSubmittedRoutes()
    {
        foreach (var route in _triggers.SubmittedRoutes())
        {
            if (route.IntentId is null) continue;
            var intent = _intents.GetAsync(route.IntentId).GetAwaiter().GetResult();
            if (intent?.Status == OperatorIntentStatus.Applied)
                _triggers.MarkRouteApplied(route.Key);
            else if (intent?.Status == OperatorIntentStatus.Rejected)
                _triggers.MarkRouteRejected(route.Key);
        }
    }

    private void Harvest(AgentOrchestratorKernel kernel, HashSet<GoalId> changed)
    {
        if (_round is null || !_round.IsCompleted || _running is null) return;
        var stored = _running;
        var round = _round;
        _round = null;
        _running = null;
        var trigger = stored.Trigger;
        var goal = kernel.Goals.FirstOrDefault(item => item.Id.Value == trigger.GoalId);
        if (goal is null || goal.IsTerminal)
        {
            _triggers.MarkServiced(stored.Key, "no-action", null);
            Record(trigger, "no-action", "goal-unavailable", null);
            return;
        }
        if (round.IsFaulted || round.IsCanceled)
        {
            var reason = round.Exception?.GetBaseException() is OperationCanceledException ? "timeout" : "model-failure";
            _triggers.MarkServiced(stored.Key, "model-failure", null);
            Record(trigger, "model-failure", reason, null);
            return;
        }
        var adjudication = ConductorStewardAdjudicationParser.Parse(round.Result);
        if (adjudication.Kind == "no-action")
        {
            var reason = adjudication.Text == "unparseable-output" ? "unparseable-output" : adjudication.Text;
            _triggers.MarkServiced(stored.Key, "no-action", null);
            Record(trigger, "no-action", reason, null);
            return;
        }
        if (adjudication.Kind == "ask-owner")
        {
            Question(kernel, goal, trigger, adjudication.Text, adjudication.EvidenceReferences ?? [], changed);
            _triggers.MarkServiced(stored.Key, "ask-owner", null);
            return;
        }

        var worktree = _workingDirectory(goal);
        var rejection = ConductorStewardRoutePolicy.RejectionReason(trigger, adjudication, goal, worktree, _evidence);
        if (rejection is null && _triggers.HasAppliedRoute(trigger)) rejection = "route-bound-exceeded";
        if (rejection is not null)
        {
            Question(kernel, goal, trigger,
                $"Steward proposal needs owner decision ({rejection}): {adjudication.Text}",
                adjudication.EvidenceReferences ?? [], changed);
            _triggers.MarkServiced(stored.Key, "owner-question", null);
            return;
        }
        var feedback = ConductorStewardRetryTemplate.Compose(trigger, adjudication);
        if (feedback is null)
        {
            _triggers.MarkServiced(stored.Key, "no-action", null);
            Record(trigger, "no-action", "incomplete-template", null);
            return;
        }
        var version = _version(goal.Id);
        if (version is null)
        {
            _triggers.MarkServiced(stored.Key, "no-action", null);
            Record(trigger, "no-action", "goal-version-unavailable", null);
            return;
        }
        var references = trigger.EvidenceReferences.Concat(adjudication.EvidenceReferences ?? [])
            .Append($"steward-trigger={stored.Key}").Append($"steward-case={trigger.CaseLetter}");
        if (!string.IsNullOrWhiteSpace(adjudication.Precedent))
            references = references.Append($"model-precedent={adjudication.Precedent}");
        var payload = new AdjudicateOperatorIntentPayload("route", feedback,
            references.Distinct(StringComparer.Ordinal).ToArray(), version.Value, worktree,
            adjudication.Cause, adjudication.Reversibility,
            $"steward-case={trigger.CaseLetter} trigger={trigger.Identity}");
        var intentId = Guid.NewGuid().ToString("N");
        var intent = _intents.EnqueueAsync(new OperatorIntentRecord(
            intentId, $"steward-{stored.Key}", OperatorIntentVerbs.Adjudicate,
            trigger.GoalId, trigger.TaskId, JsonSerializer.Serialize(payload, _json), [],
            "steward", "conductor-steward", OperatorIntentAdjudication.StewardAssurance,
            _utcNow(), ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();
        _triggers.MarkServiced(stored.Key, "route-submitted", intent.Id);
        Record(trigger, "route", "route-submitted", intent.Id);
    }

    private void Question(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorStewardTrigger trigger,
        string question,
        IReadOnlyList<string> modelReferences,
        HashSet<GoalId> changed)
    {
        var text = $"steward-owner-question case={trigger.CaseLetter} trigger={trigger.Identity} " +
                   $"question={question} evidence=[{string.Join(", ", trigger.EvidenceReferences.Concat(modelReferences))}]";
        var state = goal.Status == GoalStatus.AcceptanceFailed ? GoalLifecycleState.AcceptanceFailed : GoalLifecycleState.Failed;
        var observation = kernel.ObserveGoalHold(goal.Id, "steward-owner-question", text, _utcNow(),
            TimeSpan.MaxValue, $"steward-owner-question:{ConductorStewardTriggerStore.KeyFor(trigger)}");
        if (observation.StateChanged) changed.Add(goal.Id);
        _conduct.Append("goal-escalation", goal.Id.Value, text);
        _lifecycle.AppendGoalEscalated(goal.Id, state, goal.Status, text, "steward-owner-question");
        Record(trigger, "ask-owner", question, null);
    }

    private void Record(ConductorStewardTrigger trigger, string kind, string reason, string? intentId) =>
        _conduct.Append("steward", trigger.GoalId,
            $"trigger={trigger.Identity} kind={kind} case={trigger.CaseLetter} reason={reason} " +
            $"intent={intentId ?? "none"}");

    private static string? CandidateAddedClassCollection(OrchestratorWorkspace workspace, Goal goal, string className)
    {
        var sha = goal.LatestAcceptanceFailure?.BranchHeadSha;
        if (string.IsNullOrWhiteSpace(sha)) return null;
        var result = GitCli.Run(workspace.ExecutionDirectory, "diff", "--name-only", $"main...{sha}");
        if (result.ExitCode != 0) return null;
        var stem = className.Split('.').Last();
        var source = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.Ordinal));
        if (source is null) return null;
        var worktree = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
        if (worktree is null) return null;
        var path = Path.GetFullPath(Path.Combine(worktree, source));
        if (!path.StartsWith(Path.GetFullPath(worktree) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path),
            @"\[\s*(?:Xunit\.)?Collection\(\s*TestCollections\.(?<collection>[A-Za-z0-9_]+)");
        return match.Success ? match.Groups["collection"].Value : null;
    }
}
