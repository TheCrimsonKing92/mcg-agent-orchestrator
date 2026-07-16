using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum RemoteMirrorOutcomeKind
{
    MirrorSucceeded,
    MirrorFailed,
    MirrorDeferred,
    MirrorSkipped
}

internal sealed record RemoteMirrorOutcome(
    GoalId GoalId,
    string GoalPrefix,
    string Remote,
    RemoteMirrorOutcomeKind Kind,
    string Detail);

internal sealed record RemoteMirrorProcessResult(IReadOnlyList<RemoteMirrorOutcome> Outcomes)
{
    public bool HasOutcomes => Outcomes.Count > 0;
}

internal static class RemoteGitMirror
{
    private const int StoreVersion = 1;
    private const string OperationPrefix = "conductor:mirror";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    internal static Func<int, TimeSpan> BackoffForAttempt { get; set; } = DefaultBackoffForAttempt;
    internal static Func<string, IReadOnlyList<string>, GitCli.GitResult> GitRunner { get; set; } =
        (workingDirectory, args) => GitCli.Run(workingDirectory, args.ToArray());

    static RemoteGitMirror()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static void EnqueueAfterLanding(string executionDirectory, Goal goal)
    {
        var config = LoadConfiguration(executionDirectory);
        if (!config.Enabled || config.Remotes.Count == 0)
        {
            return;
        }

        var state = LoadState(executionDirectory);
        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        var enqueued = 0;
        var changed = false;
        foreach (var remote in config.Remotes)
        {
            var existing = state.Entries.FirstOrDefault(entry =>
                entry.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase) &&
                entry.Remote.Equals(remote, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                state.Entries.Add(RemoteMirrorStateEntry.Create(goal.Id.Value, goalBranch, remote, config.Push));
                enqueued++;
                changed = true;
                continue;
            }

            if (existing.Status != RemoteMirrorEntryStatus.Succeeded)
            {
                existing.GoalBranch = goalBranch;
                existing.PushMain = config.Push.Main;
                existing.PushGoalBranch = config.Push.GoalBranch;
                existing.PushTags = config.Push.Tags;
                existing.NextAttemptUtc = null;
                existing.UpdatedUtc = UtcNow();
                changed = true;
            }
        }

        if (changed)
        {
            SaveState(executionDirectory, state);
        }

        if (enqueued > 0)
        {
            GoalOperationJournal.Begin(
                executionDirectory,
                goal,
                OperationPrefix,
                $"Mirror enqueued for {enqueued} remote(s): {string.Join(",", config.Remotes)}");
        }
    }

    public static RemoteMirrorProcessResult ProcessDue(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null)
    {
        var config = LoadConfiguration(executionDirectory);
        if (!config.Enabled)
        {
            return new RemoteMirrorProcessResult([]);
        }

        var state = LoadState(executionDirectory);
        if (state.Entries.Count == 0)
        {
            return new RemoteMirrorProcessResult([]);
        }

        var now = UtcNow();
        var outcomes = new List<RemoteMirrorOutcome>();
        var goals = kernel.Goals.ToDictionary(goal => goal.Id.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Entries
            .Where(entry => entry.Status != RemoteMirrorEntryStatus.Succeeded)
            .Where(entry => onlyGoalId is null || entry.GoalId.Equals(onlyGoalId.Value, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.NextAttemptUtc is null || entry.NextAttemptUtc <= now)
            .ToArray())
        {
            if (!goals.TryGetValue(entry.GoalId, out var goal))
            {
                outcomes.Add(new RemoteMirrorOutcome(
                    new GoalId(entry.GoalId),
                    Prefix(entry.GoalId),
                    entry.Remote,
                    RemoteMirrorOutcomeKind.MirrorSkipped,
                    "goal no longer exists in the conductor kernel"));
                continue;
            }

            outcomes.Add(ProcessEntry(executionDirectory, goal, entry, now));
        }

        if (outcomes.Count > 0)
        {
            SaveState(executionDirectory, state);
        }

        return new RemoteMirrorProcessResult(outcomes);
    }

    public static RemoteMirrorState ReadState(string executionDirectory) => LoadState(executionDirectory);

    internal static string ConfigPath(string executionDirectory) =>
        Path.Combine(Path.GetFullPath(executionDirectory), "config", "mirror.json");

    internal static string StatePath(string executionDirectory) =>
        Path.Combine(Path.GetFullPath(executionDirectory), ".orchestrator", "git-mirror-state.json");

    private static RemoteMirrorOutcome ProcessEntry(
        string executionDirectory,
        Goal goal,
        RemoteMirrorStateEntry entry,
        DateTimeOffset now)
    {
        var remote = entry.Remote;
        var operation = $"{OperationPrefix}:{remote}";
        GoalOperationJournal.Begin(executionDirectory, goal, operation, $"Mirroring {entry.GoalBranch} to {remote}.");

        var pushResults = RunPushes(executionDirectory, entry).ToArray();
        var failed = pushResults.FirstOrDefault(result => !result.Result.Succeeded);
        if (failed is null)
        {
            entry.Status = RemoteMirrorEntryStatus.Succeeded;
            entry.LastError = null;
            entry.NextAttemptUtc = null;
            entry.UpdatedUtc = now;
            var detail = $"MirrorSucceeded remote={remote} refs={FormatPushedRefs(entry)}";
            GoalOperationJournal.Completed(executionDirectory, goal, operation, detail);
            return new RemoteMirrorOutcome(goal.Id, Prefix(goal.Id.Value), remote, RemoteMirrorOutcomeKind.MirrorSucceeded, detail);
        }

        entry.Status = RemoteMirrorEntryStatus.Deferred;
        entry.Attempts++;
        entry.LastError = TrimDetail(failed.Result.Error, failed.Result.Output);
        entry.NextAttemptUtc = now + BackoffForAttempt(entry.Attempts);
        entry.UpdatedUtc = now;
        var failureDetail =
            $"MirrorFailed remote={remote} classification=TRANSIENT attempt={entry.Attempts} nextAttemptUtc={entry.NextAttemptUtc:O} " +
            $"step={failed.Step} stderr={entry.LastError}";
        GoalOperationJournal.Failed(executionDirectory, goal, operation, failureDetail);
        return new RemoteMirrorOutcome(goal.Id, Prefix(goal.Id.Value), remote, RemoteMirrorOutcomeKind.MirrorFailed, failureDetail);
    }

    private static IEnumerable<RemoteMirrorPushResult> RunPushes(string executionDirectory, RemoteMirrorStateEntry entry)
    {
        if (entry.PushMain)
        {
            yield return RunPush(executionDirectory, entry.Remote, "main", "main");
        }

        if (entry.PushGoalBranch)
        {
            yield return RunPush(executionDirectory, entry.Remote, entry.GoalBranch, entry.GoalBranch);
        }

        if (entry.PushTags)
        {
            yield return new RemoteMirrorPushResult(
                "tags",
                GitRunner(executionDirectory, ["push", entry.Remote, "--tags"]));
        }
    }

    private static RemoteMirrorPushResult RunPush(
        string executionDirectory,
        string remote,
        string step,
        string refspec) =>
        new(step, GitRunner(executionDirectory, ["push", remote, refspec]));

    private static RemoteMirrorConfiguration LoadConfiguration(string executionDirectory)
    {
        var path = ConfigPath(executionDirectory);
        if (!File.Exists(path))
        {
            return RemoteMirrorConfiguration.Disabled;
        }

        try
        {
            var file = JsonSerializer.Deserialize<RemoteMirrorConfigurationFile>(File.ReadAllText(path), JsonOptions);
            if (file is not { Enabled: true })
            {
                return RemoteMirrorConfiguration.Disabled;
            }

            var remotes = (file.Remotes ?? [])
                .Where(remote => !string.IsNullOrWhiteSpace(remote))
                .Select(remote => remote.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return remotes.Length == 0
                ? RemoteMirrorConfiguration.Disabled
                : new RemoteMirrorConfiguration(true, remotes, file.Push ?? RemoteMirrorPushSelection.Default);
        }
        catch
        {
            return RemoteMirrorConfiguration.Disabled;
        }
    }

    private static RemoteMirrorState LoadState(string executionDirectory)
    {
        var path = StatePath(executionDirectory);
        if (!File.Exists(path))
        {
            return new RemoteMirrorState(StoreVersion, []);
        }

        try
        {
            return JsonSerializer.Deserialize<RemoteMirrorState>(File.ReadAllText(path), JsonOptions)
                ?? new RemoteMirrorState(StoreVersion, []);
        }
        catch
        {
            return new RemoteMirrorState(StoreVersion, []);
        }
    }

    private static void SaveState(string executionDirectory, RemoteMirrorState state)
    {
        var path = StatePath(executionDirectory);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(state with { Version = StoreVersion }, JsonOptions) + Environment.NewLine);
    }

    private static TimeSpan DefaultBackoffForAttempt(int attempt) =>
        attempt switch
        {
            <= 1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromMinutes(30)
        };

    private static string FormatPushedRefs(RemoteMirrorStateEntry entry)
    {
        var refs = new List<string>();
        if (entry.PushMain) refs.Add("main");
        if (entry.PushGoalBranch) refs.Add(entry.GoalBranch);
        if (entry.PushTags) refs.Add("tags");
        return string.Join(",", refs);
    }

    private static string TrimDetail(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        text = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return text.Length <= 600 ? text : text[..600] + "...";
    }

    private static string Prefix(string goalId) => goalId[..Math.Min(8, goalId.Length)];

    private sealed record RemoteMirrorPushResult(string Step, GitCli.GitResult Result);
}

internal sealed record RemoteMirrorConfiguration(
    bool Enabled,
    IReadOnlyList<string> Remotes,
    RemoteMirrorPushSelection Push)
{
    public static RemoteMirrorConfiguration Disabled { get; } =
        new(false, [], RemoteMirrorPushSelection.Default);
}

internal sealed record RemoteMirrorPushSelection(bool Main = true, bool GoalBranch = true, bool Tags = true)
{
    public static RemoteMirrorPushSelection Default { get; } = new();
}

internal sealed record RemoteMirrorConfigurationFile(
    bool Enabled = false,
    IReadOnlyList<string>? Remotes = null,
    RemoteMirrorPushSelection? Push = null);

internal enum RemoteMirrorEntryStatus
{
    Pending,
    Succeeded,
    Deferred
}

internal sealed record RemoteMirrorState(int Version, List<RemoteMirrorStateEntry> Entries);

internal sealed class RemoteMirrorStateEntry
{
    public string GoalId { get; set; } = string.Empty;
    public string GoalBranch { get; set; } = string.Empty;
    public string Remote { get; set; } = string.Empty;
    public RemoteMirrorEntryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public bool PushMain { get; set; } = true;
    public bool PushGoalBranch { get; set; } = true;
    public bool PushTags { get; set; } = true;
    public DateTimeOffset UpdatedUtc { get; set; }

    public static RemoteMirrorStateEntry Create(
        string goalId,
        string goalBranch,
        string remote,
        RemoteMirrorPushSelection push)
    {
        return new RemoteMirrorStateEntry
        {
            GoalId = goalId,
            GoalBranch = goalBranch,
            Remote = remote,
            Status = RemoteMirrorEntryStatus.Pending,
            PushMain = push.Main,
            PushGoalBranch = push.GoalBranch,
            PushTags = push.Tags,
            UpdatedUtc = RemoteGitMirror.UtcNow()
        };
    }
}
