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
    private static readonly object BackgroundGate = new();
    private static readonly object StateGate = new();
    private static readonly Dictionary<string, Task> BackgroundTasksByDirectory = new(StringComparer.OrdinalIgnoreCase);
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
    internal static Action<string> StatusWriter { get; set; } = Console.WriteLine;

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

        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        var goalBranchCommit = ResolveCommit(executionDirectory, goalBranch);
        var enqueued = 0;
        lock (StateGate)
        {
            var state = LoadState(executionDirectory);
            var changed = false;
            foreach (var remote in config.Remotes)
            {
                var existing = state.Entries.FirstOrDefault(entry =>
                    entry.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase) &&
                    entry.Remote.Equals(remote, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    state.Entries.Add(RemoteMirrorStateEntry.Create(goal.Id.Value, goalBranch, goalBranchCommit, remote, config.Push));
                    enqueued++;
                    changed = true;
                    continue;
                }

                if (existing.Status != RemoteMirrorEntryStatus.Succeeded)
                {
                    existing.GoalBranch = goalBranch;
                    existing.GoalBranchCommit = goalBranchCommit;
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
        }

        if (enqueued > 0)
        {
            GoalOperationJournal.Completed(
                executionDirectory,
                goal,
                OperationPrefix,
                $"Mirror enqueued for {enqueued} remote(s): {string.Join(",", config.Remotes)}");
        }
    }

    public static bool TryStartBackgroundProcessing(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null, string? integrationBranch = null)
    {
        if (!HasDueWork(executionDirectory, onlyGoalId))
        {
            return false;
        }

        var normalizedDirectory = Normalize(executionDirectory);
        var goalsSnapshot = kernel.Goals.ToArray();
        lock (BackgroundGate)
        {
            if (BackgroundTasksByDirectory.TryGetValue(normalizedDirectory, out var existing) &&
                !existing.IsCompleted)
            {
                return false;
            }

            Task? task = null;
            task = Task.Run(() =>
            {
                try
                {
                    var result = ProcessDue(goalsSnapshot, normalizedDirectory, onlyGoalId, integrationBranch);
                    foreach (var outcome in result.Outcomes)
                    {
                        StatusWriter(FormatStatusLine(outcome));
                    }
                }
                catch (Exception ex)
                {
                    StatusWriter($"[mirror] result=failed exception={ex.GetType().Name} message={TrimDetail(ex.Message, string.Empty)}");
                }
                finally
                {
                    lock (BackgroundGate)
                    {
                        if (BackgroundTasksByDirectory.TryGetValue(normalizedDirectory, out var current) &&
                            ReferenceEquals(current, task))
                        {
                            BackgroundTasksByDirectory.Remove(normalizedDirectory);
                        }
                    }
                }
            });
            BackgroundTasksByDirectory[normalizedDirectory] = task;
        }

        return true;
    }

    public static RemoteMirrorProcessResult ProcessDue(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null, string? integrationBranch = null) =>
        ProcessDue(kernel.Goals.ToArray(), executionDirectory, onlyGoalId, integrationBranch);

    public static RemoteMirrorProcessResult ProcessDue(
        IReadOnlyCollection<Goal> goalsSnapshot,
        string executionDirectory,
        GoalId? onlyGoalId = null, string? integrationBranch = null)
    {
        integrationBranch = TrunkBranchName.Resolve(integrationBranch);
        var config = LoadConfiguration(executionDirectory);
        if (!config.Enabled)
        {
            return new RemoteMirrorProcessResult([]);
        }

        RemoteMirrorStateEntry[] dueEntries;
        lock (StateGate)
        {
            var state = LoadState(executionDirectory);
            if (state.Entries.Count == 0)
            {
                return new RemoteMirrorProcessResult([]);
            }

            var dueNow = UtcNow();
            dueEntries = state.Entries
                .Where(entry => entry.Status != RemoteMirrorEntryStatus.Succeeded)
                .Where(entry => onlyGoalId is null || entry.GoalId.Equals(onlyGoalId.Value, StringComparison.OrdinalIgnoreCase))
                .Where(entry => entry.NextAttemptUtc is null || entry.NextAttemptUtc <= dueNow)
                .Select(CloneEntry)
                .ToArray();
        }

        var now = UtcNow();
        var outcomes = new List<RemoteMirrorOutcome>();
        var processedEntries = new List<RemoteMirrorProcessedEntry>();
        var goals = goalsSnapshot.ToDictionary(goal => goal.Id.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dueEntries)
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

            var original = CloneEntry(entry);
            outcomes.Add(ProcessEntry(executionDirectory, goal, entry, now, integrationBranch));
            processedEntries.Add(new RemoteMirrorProcessedEntry(original, entry));
        }

        if (processedEntries.Count > 0)
        {
            ApplyProcessedEntries(executionDirectory, processedEntries);
        }

        return new RemoteMirrorProcessResult(outcomes);
    }

    public static RemoteMirrorState ReadState(string executionDirectory)
    {
        lock (StateGate)
        {
            return LoadState(executionDirectory);
        }
    }

    internal static string ConfigPath(string executionDirectory) =>
        Path.Combine(Path.GetFullPath(executionDirectory), "config", "mirror.json");

    internal static string StatePath(string executionDirectory) =>
        Path.Combine(Path.GetFullPath(executionDirectory), ".orchestrator", "git-mirror-state.json");

    private static bool HasDueWork(string executionDirectory, GoalId? onlyGoalId)
    {
        var config = LoadConfiguration(executionDirectory);
        if (!config.Enabled)
        {
            return false;
        }

        lock (StateGate)
        {
            var now = UtcNow();
            return LoadState(executionDirectory).Entries.Any(entry =>
                entry.Status != RemoteMirrorEntryStatus.Succeeded &&
                (onlyGoalId is null || entry.GoalId.Equals(onlyGoalId.Value, StringComparison.OrdinalIgnoreCase)) &&
                (entry.NextAttemptUtc is null || entry.NextAttemptUtc <= now));
        }
    }

    private static RemoteMirrorOutcome ProcessEntry(
        string executionDirectory,
        Goal goal,
        RemoteMirrorStateEntry entry,
        DateTimeOffset now, string integrationBranch)
    {
        var remote = entry.Remote;
        var operation = $"{OperationPrefix}:{remote}";
        GoalOperationJournal.Begin(executionDirectory, goal, operation, $"Mirroring {entry.GoalBranch} to {remote}.");

        var pushResults = RunPushes(executionDirectory, entry, integrationBranch).ToArray();
        var failed = pushResults.FirstOrDefault(result => !result.Result.Succeeded);
        if (failed is null)
        {
            entry.Status = RemoteMirrorEntryStatus.Succeeded;
            entry.LastError = null;
            entry.NextAttemptUtc = null;
            entry.UpdatedUtc = now;
            var detail = $"MirrorSucceeded remote={remote} refs={FormatPushedRefs(entry, integrationBranch)}";
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

    private static IEnumerable<RemoteMirrorPushResult> RunPushes(string executionDirectory, RemoteMirrorStateEntry entry, string integrationBranch)
    {
        if (entry.PushMain)
        {
            yield return RunPush(executionDirectory, entry.Remote, integrationBranch, integrationBranch);
        }

        if (entry.PushGoalBranch)
        {
            var source = string.IsNullOrWhiteSpace(entry.GoalBranchCommit) ? entry.GoalBranch : entry.GoalBranchCommit;
            yield return RunPush(executionDirectory, entry.Remote, entry.GoalBranch, $"{source}:refs/heads/{entry.GoalBranch}");
        }

        if (entry.PushTags)
        {
            yield return new RemoteMirrorPushResult(
                "tags",
                RunTagPush(executionDirectory, entry.Remote));
        }
    }

    private static RemoteMirrorPushResult RunPush(
        string executionDirectory,
        string remote,
        string step,
        string refspec)
    {
        var result = GitRunner(executionDirectory, ["push", remote, refspec]);
        return new RemoteMirrorPushResult(
            step,
            TryRunLocalFilesystemPushFallback(executionDirectory, remote, step, refspec, result));
    }

    private static GitCli.GitResult RunTagPush(string executionDirectory, string remote)
    {
        var result = GitRunner(executionDirectory, ["push", remote, "--tags"]);
        return TryRunLocalFilesystemTagPushFallback(executionDirectory, remote, result);
    }

    private static GitCli.GitResult TryRunLocalFilesystemPushFallback(
        string executionDirectory,
        string remote,
        string step,
        string refspec,
        GitCli.GitResult original)
    {
        if (original.Succeeded ||
            !IsBlockedMsysLocalTransportFailure(original) ||
            !TryResolveLocalBareRemote(executionDirectory, remote, out var remotePath))
        {
            return original;
        }

        var destinationRef = DestinationRefFor(step, refspec);
        if (string.IsNullOrWhiteSpace(destinationRef))
        {
            return original;
        }

        var source = SourceFor(refspec);
        var tempRef = $"refs/mcg-mirror/{Guid.NewGuid():N}";
        var bundlePath = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("git-mirror"), $"mcg-git-mirror-{Guid.NewGuid():N}.bundle");
        try
        {
            var update = GitRunner(executionDirectory, ["update-ref", tempRef, source]);
            if (!update.Succeeded)
            {
                return WithFallbackError(original, "update-ref", update);
            }

            var bundle = GitRunner(executionDirectory, ["bundle", "create", bundlePath, tempRef]);
            if (!bundle.Succeeded)
            {
                return WithFallbackError(original, "bundle create", bundle);
            }

            var fetch = GitRunner(remotePath, ["fetch", bundlePath, $"{tempRef}:{destinationRef}"]);
            return fetch.Succeeded ? fetch : WithFallbackError(original, "bundle fetch", fetch);
        }
        finally
        {
            GitRunner(executionDirectory, ["update-ref", "-d", tempRef]);
            TryDeleteFile(bundlePath);
        }
    }

    private static GitCli.GitResult TryRunLocalFilesystemTagPushFallback(
        string executionDirectory,
        string remote,
        GitCli.GitResult original)
    {
        if (original.Succeeded ||
            !IsBlockedMsysLocalTransportFailure(original) ||
            !TryResolveLocalBareRemote(executionDirectory, remote, out var remotePath))
        {
            return original;
        }

        var tagRefs = GitRunner(executionDirectory, ["for-each-ref", "--format=%(refname)", "refs/tags"]);
        if (!tagRefs.Succeeded)
        {
            return WithFallbackError(original, "list tags", tagRefs);
        }

        var refs = tagRefs.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(refName => refName.StartsWith("refs/tags/", StringComparison.Ordinal))
            .ToArray();
        if (refs.Length == 0)
        {
            return new GitCli.GitResult(0, string.Empty, string.Empty);
        }

        var bundlePath = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("git-mirror"), $"mcg-git-mirror-tags-{Guid.NewGuid():N}.bundle");
        try
        {
            var bundleArgs = new List<string> { "bundle", "create", bundlePath };
            bundleArgs.AddRange(refs);
            var bundle = GitRunner(executionDirectory, bundleArgs);
            if (!bundle.Succeeded)
            {
                return WithFallbackError(original, "tag bundle create", bundle);
            }

            var fetch = GitRunner(remotePath, ["fetch", bundlePath, "refs/tags/*:refs/tags/*"]);
            return fetch.Succeeded ? fetch : WithFallbackError(original, "tag bundle fetch", fetch);
        }
        finally
        {
            TryDeleteFile(bundlePath);
        }
    }

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

    private static void ApplyProcessedEntries(
        string executionDirectory,
        IReadOnlyCollection<RemoteMirrorProcessedEntry> processedEntries)
    {
        lock (StateGate)
        {
            var state = LoadState(executionDirectory);
            var changed = false;
            foreach (var processed in processedEntries)
            {
                var current = state.Entries.FirstOrDefault(entry =>
                    entry.GoalId.Equals(processed.Original.GoalId, StringComparison.OrdinalIgnoreCase) &&
                    entry.Remote.Equals(processed.Original.Remote, StringComparison.OrdinalIgnoreCase));
                if (current is null || !CanApplyProcessedEntry(current, processed.Original))
                {
                    continue;
                }

                CopyEntry(processed.Updated, current);
                changed = true;
            }

            if (changed)
            {
                SaveState(executionDirectory, state);
            }
        }
    }

    private static bool CanApplyProcessedEntry(RemoteMirrorStateEntry current, RemoteMirrorStateEntry original) =>
        current.GoalBranch.Equals(original.GoalBranch, StringComparison.Ordinal) &&
        current.GoalBranchCommit.Equals(original.GoalBranchCommit, StringComparison.Ordinal) &&
        current.Status == original.Status &&
        current.Attempts == original.Attempts &&
        current.NextAttemptUtc == original.NextAttemptUtc &&
        current.LastError == original.LastError &&
        current.PushMain == original.PushMain &&
        current.PushGoalBranch == original.PushGoalBranch &&
        current.PushTags == original.PushTags &&
        current.UpdatedUtc == original.UpdatedUtc;

    private static RemoteMirrorStateEntry CloneEntry(RemoteMirrorStateEntry source)
    {
        var clone = new RemoteMirrorStateEntry();
        CopyEntry(source, clone);
        return clone;
    }

    private static void CopyEntry(RemoteMirrorStateEntry source, RemoteMirrorStateEntry target)
    {
        target.GoalId = source.GoalId;
        target.GoalBranch = source.GoalBranch;
        target.GoalBranchCommit = source.GoalBranchCommit;
        target.Remote = source.Remote;
        target.Status = source.Status;
        target.Attempts = source.Attempts;
        target.NextAttemptUtc = source.NextAttemptUtc;
        target.LastError = source.LastError;
        target.PushMain = source.PushMain;
        target.PushGoalBranch = source.PushGoalBranch;
        target.PushTags = source.PushTags;
        target.UpdatedUtc = source.UpdatedUtc;
    }

    private static string ResolveCommit(string executionDirectory, string reference)
    {
        var result = GitRunner(executionDirectory, ["rev-parse", "--verify", reference]);
        return result.Succeeded ? result.Output.Trim() : string.Empty;
    }

    private static TimeSpan DefaultBackoffForAttempt(int attempt) =>
        attempt switch
        {
            <= 1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromMinutes(30)
        };

    private static string FormatPushedRefs(RemoteMirrorStateEntry entry, string integrationBranch)
    {
        var refs = new List<string>();
        if (entry.PushMain) refs.Add(integrationBranch);
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

    private static string FormatStatusLine(RemoteMirrorOutcome outcome)
    {
        var result = outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded ? "pushed" :
            outcome.Kind == RemoteMirrorOutcomeKind.MirrorFailed ? "deferred" :
            outcome.Kind == RemoteMirrorOutcomeKind.MirrorDeferred ? "deferred" :
            "skipped";
        return $"[mirror] goal={outcome.GoalPrefix} remote={outcome.Remote} result={result} detail={outcome.Detail}";
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string SourceFor(string refspec)
    {
        var separator = refspec.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? refspec : refspec[..separator];
    }

    private static string DestinationRefFor(string step, string refspec)
    {
        var separator = refspec.IndexOf(':', StringComparison.Ordinal);
        if (separator >= 0)
        {
            return refspec[(separator + 1)..];
        }

        return $"refs/heads/{step}";
    }

    private static bool IsBlockedMsysLocalTransportFailure(GitCli.GitResult result) =>
        result.ExitCode != 0 &&
        result.Error.Contains("NtCreateDirectoryObject", StringComparison.OrdinalIgnoreCase) &&
        result.Error.Contains("Could not read from remote repository", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveLocalBareRemote(string executionDirectory, string remote, out string remotePath)
    {
        remotePath = string.Empty;
        var remoteUrl = GitRunner(executionDirectory, ["remote", "get-url", remote]);
        if (!remoteUrl.Succeeded)
        {
            return false;
        }

        if (!TryResolveLocalPath(executionDirectory, remoteUrl.Output.Trim(), out var candidate) ||
            !Directory.Exists(candidate))
        {
            return false;
        }

        var bare = GitRunner(candidate, ["rev-parse", "--is-bare-repository"]);
        if (!bare.Succeeded || !bare.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        remotePath = candidate;
        return true;
    }

    private static bool TryResolveLocalPath(string executionDirectory, string url, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                return false;
            }

            path = Path.GetFullPath(uri.LocalPath);
            return true;
        }

        if (Path.IsPathFullyQualified(url))
        {
            path = Path.GetFullPath(url);
            return true;
        }

        if (url.StartsWith(".", StringComparison.Ordinal))
        {
            path = Path.GetFullPath(Path.Combine(executionDirectory, url));
            return true;
        }

        return false;
    }

    private static GitCli.GitResult WithFallbackError(
        GitCli.GitResult original,
        string step,
        GitCli.GitResult fallback) =>
        new(
            fallback.ExitCode,
            fallback.Output,
            $"local filesystem mirror fallback failed at {step}: {TrimDetail(fallback.Error, fallback.Output)}; " +
            $"original push failed: {TrimDetail(original.Error, original.Output)}");

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string Prefix(string goalId) => goalId[..Math.Min(8, goalId.Length)];

    private sealed record RemoteMirrorPushResult(string Step, GitCli.GitResult Result);
    private sealed record RemoteMirrorProcessedEntry(RemoteMirrorStateEntry Original, RemoteMirrorStateEntry Updated);
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
    public string GoalBranchCommit { get; set; } = string.Empty;
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
        string goalBranchCommit,
        string remote,
        RemoteMirrorPushSelection push)
    {
        return new RemoteMirrorStateEntry
        {
            GoalId = goalId,
            GoalBranch = goalBranch,
            GoalBranchCommit = goalBranchCommit,
            Remote = remote,
            Status = RemoteMirrorEntryStatus.Pending,
            PushMain = push.Main,
            PushGoalBranch = push.GoalBranch,
            PushTags = push.Tags,
            UpdatedUtc = RemoteGitMirror.UtcNow()
        };
    }
}
