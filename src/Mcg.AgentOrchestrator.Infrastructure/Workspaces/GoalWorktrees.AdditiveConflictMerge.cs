using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Per-integration authorization and evidence sink supplied by the conductor.</summary>
public sealed record AdditiveConflictMergeOptions(IReadOnlyCollection<string> FrozenPaths, Action<string> RecordEvent);

public enum AdditiveConflictProbeOutcome { Resolvable, Unresolvable, Indeterminate }

public sealed record AdditiveConflictProbeResult(
    string? BranchRevision, string? MainRevision, AdditiveConflictProbeOutcome Outcome, string Reason);

public static partial class GoalWorktrees
{
    // Reuse the landing classifier in a detached, disposable checkout; never integrate either ref.
    public static AdditiveConflictProbeResult ProbeAdditiveConflictMerge(
        string executionDirectory, GoalId goalId, IReadOnlyCollection<string> frozenPaths, string? trunkBranch = null)
    {
        string? branch = null, main = null, checkout = null;
        var outcome = AdditiveConflictProbeOutcome.Indeterminate;
        var reason = "probe-unavailable";
        try
        {
            var worktree = TryResolve(executionDirectory, goalId);
            if (worktree is null) return new(null, null, outcome, "worktree-missing");
            branch = ResolveRequiredRef(worktree, "HEAD");
            var baseBranch = GetCurrentBranchName(executionDirectory) ?? TrunkBranchName.Resolve(trunkBranch);
            main = ResolveRequiredRef(executionDirectory, $"{baseBranch}^{{commit}}");
            checkout = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("additive-conflict-probes"), Guid.NewGuid().ToString("N"));
            var added = RunAdditiveGit(executionDirectory, "worktree", "add", "--detach", "--quiet", checkout, branch);
            if (CompleteGit(added))
            {
                var prepared = TryPrepareAdditiveMerge(checkout, branch, main, frozenPaths,
                    out _, out _, out var files, out _, out reason, out var observationFailed);
                outcome = prepared || reason == "merge-clean" ? AdditiveConflictProbeOutcome.Resolvable
                    : files.Length > 0 && !observationFailed ? AdditiveConflictProbeOutcome.Unresolvable
                    : AdditiveConflictProbeOutcome.Indeterminate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidOperationException)
        {
            outcome = AdditiveConflictProbeOutcome.Indeterminate;
            reason = ex.Message;
        }
        finally
        {
            if (checkout is not null)
            {
                try
                {
                    _ = RunAdditiveGit(checkout, "merge", "--abort");
                    var removed = RunAdditiveGit(executionDirectory, "worktree", "remove", "--force", checkout);
                    if (!CompleteGit(removed))
                    {
                        outcome = AdditiveConflictProbeOutcome.Indeterminate;
                        reason = "probe-cleanup-failed";
                    }
                }
                catch (Exception ex)
                {
                    outcome = AdditiveConflictProbeOutcome.Indeterminate;
                    reason = $"probe-cleanup-failed: {ex.Message}";
                }
            }
        }
        return new(branch, main, outcome, reason);
    }

    private static GoalWorktreeRebaseResult? TryMergeAdditiveConflict(
        string worktreePath, string branch, string baseBranch, GoalId goalId, string integratedMain,
        string[] rebaseConflictFiles, AdditiveConflictMergeOptions options)
    {
        var files = rebaseConflictFiles;
        var hunks = 0;
        var reason = "merge-failed";
        var original = RunAdditiveGit(worktreePath, "rev-parse", branch);
        var oldHead = original.Output.Trim();
        GoalWorktreeRebaseResult? result = null;
        string? mergeSha = null;
        if (!CompleteGit(original) || oldHead.Length == 0 ||
            !TryEnsureNoGitOperation(worktreePath, (dir, args) => RunAdditiveGit(dir, args), out _) ||
            !IsAdditiveCheckoutClean(worktreePath, branch, oldHead))
        {
            reason = "rebase-abort-unverified";
            result = AdditiveMergeRecoveryFailure(branch, goalId, reason);
        }
        else
        {
            try
            {
                if (TryPrepareAdditiveMerge(worktreePath, oldHead, integratedMain, options.FrozenPaths,
                        out var resolved, out var registryPlans, out var mergeFiles, out hunks, out reason, out _))
                {
                    files = mergeFiles;
                    foreach (var (path, bytes) in resolved)
                        File.WriteAllBytes(Path.Combine(worktreePath, path), bytes);
                    reason = RegistryAwareConflictMerge.SameKeyConflict;
                    if (RegistryAwareConflictMerge.WritePlans(worktreePath, registryPlans))
                    {
                        var add = RunAdditiveGit(worktreePath, ["add", "--", .. files]);
                        reason = "stage-failed";
                        var unmerged = RunAdditiveGit(worktreePath, "ls-files", "--unmerged", "-z");
                        if (CompleteGit(add) && CompleteGit(unmerged) && unmerged.Output.Length == 0)
                        {
                            if (RegistryAwareConflictMerge.TryValidatePlans(worktreePath, registryPlans, out reason))
                            {
                                reason = "commit-failed";
                                var commit = RunAdditiveGit(worktreePath, "-c", "user.name=mcg-orchestrator",
                                    "-c", "user.email=mcg-orchestrator@localhost", "-c", "commit.gpgSign=false",
                                    "commit", "-m", $"Integrate main into {branch} (additive conflict auto-merge)");
                                if (CompleteGit(commit))
                                {
                                    reason = "merge-commit-unverified";
                                    var parents = RunAdditiveGit(worktreePath, "rev-list", "--parents", "-n", "1", "HEAD");
                                    var ids = parents.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                    if (CompleteGit(parents) && ids.Length == 3 && ids[1] == oldHead && ids[2] == integratedMain)
                                    {
                                        mergeSha = ids[0];
                                        reason = "materialization-failed";
                                        var materialized = ValidatePostRebaseMaterialization(worktreePath, branch, baseBranch, goalId);
                                        if (materialized.Status == GoalWorktreeRebaseStatus.Rebased)
                                        {
                                            SourceSizeRatchetRetightener.RetightenAndCommit(worktreePath, integratedMain, Prefix(goalId));
                                            if (IsAdditiveCheckoutClean(worktreePath, branch, ResolveRequiredRef(worktreePath, "HEAD")))
                                                result = materialized with { Detail = "additive-conflict-auto-merge" };
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else if (mergeFiles.Length > 0)
                    files = mergeFiles;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidOperationException)
            {
                reason = "integration-failed";
            }

            if (result is null)
            {
                // A commit can have succeeded before later validation failed. Reset also covers that case.
                _ = RunAdditiveGit(worktreePath, "merge", "--abort");
                var reset = RunAdditiveGit(worktreePath, "reset", "--hard", oldHead);
                if (!CompleteGit(reset) || !IsAdditiveCheckoutClean(worktreePath, branch, oldHead) ||
                    !TryEnsureNoGitOperation(worktreePath, (dir, args) => RunAdditiveGit(dir, args), out _))
                    result = AdditiveMergeRecoveryFailure(branch, goalId, reason);
            }
        }

        var merged = result?.Status == GoalWorktreeRebaseStatus.Rebased;
        options.RecordEvent($"REBASE_CONFLICT_AUTOMERGE goal={Prefix(goalId)} result={(merged ? "merged" : "refused")} " +
            $"files={string.Join(',', files.Select(path => path.Replace("\r", "%0D").Replace("\n", "%0A")))} " +
            $"hunks={hunks} reason={(merged ? "none" : reason)} merge={(merged ? mergeSha : "none")}");
        return result;
    }

    private static GoalWorktreeRebaseResult AdditiveMergeRecoveryFailure(string branch, GoalId goalId, string reason) =>
        new(GoalWorktreeRebaseStatus.Failed, branch,
            $"Additive conflict auto-merge could not verify restoration of {branch} ({reason}); inspect the worktree before retrying.",
            [], $"goal-recovery {Prefix(goalId)}", Detail: "additive-conflict-restore-failed");

    private static GitCli.GitResult RunAdditiveGit(string path, params string[] args)
    {
        var result = GitCli.Run(path, args);
        return IsRebaseStatPathFailure(result) ? RunGitDirect(path, args) : result;
    }

    private static bool CompleteGit(GitCli.GitResult result) => result.Succeeded && !result.DrainTimedOut;

    private static bool IsAdditiveCheckoutClean(string path, string branch, string head)
    {
        var status = RunAdditiveGit(path, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var currentHead = RunAdditiveGit(path, "rev-parse", "HEAD");
        var currentBranch = RunAdditiveGit(path, "symbolic-ref", "--short", "HEAD");
        return CompleteGit(status) && FilterInternalArtifactStatusEntries(status.Output).Length == 0 && CompleteGit(currentHead) &&
            currentHead.Output.Trim() == head && CompleteGit(currentBranch) && currentBranch.Output.Trim() == branch;
    }

    private static bool TryPrepareAdditiveMerge(
        string path, string oldHead, string integratedMain, IReadOnlyCollection<string> frozenPaths,
        out Dictionary<string, byte[]> resolved, out List<RegistryAwareConflictMerge.Plan> registryPlans,
        out string[] files, out int hunks, out string reason, out bool observationFailed)
    {
        observationFailed = false;
        resolved = new(StringComparer.Ordinal);
        registryPlans = [];
        files = [];
        hunks = 0;
        reason = "merge-failed";
        var merge = RunAdditiveGit(path, "-c", "merge.conflictStyle=diff3", "-c", "rerere.enabled=false",
            "-c", "rerere.autoupdate=false", "merge", "--no-ff", "--no-commit", integratedMain);
        if (CompleteGit(merge)) { reason = "merge-clean"; return false; }
        if (merge.DrainTimedOut || !merge.ProcessStarted) return false;
        var status = RunAdditiveGit(path, "status", "--porcelain=v2", "-z", "--untracked-files=all");
        if (!CompleteGit(status)) return false;
        var entries = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith("u ", StringComparison.Ordinal))
            .Select(entry => entry.Split(' ', 11)).ToArray();
        if (entries.Length == 0) return false;
        reason = "not-content-conflict";
        if (entries.Any(entry => entry.Length != 11)) return false;
        files = entries.Select(entry => entry[10]).Order(StringComparer.Ordinal).ToArray();
        if (entries.Any(entry => entry[1] != "UU" || entry[2] != "N..." ||
            entry[3] is not ("100644" or "100755") || entry[4] != entry[3] ||
            entry[5] != entry[3] || entry[6] != entry[3])) return false;

        var mergeBase = RunAdditiveGit(path, "merge-base", "--all", oldHead, integratedMain);
        var bases = mergeBase.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!CompleteGit(mergeBase) || bases.Length != 1)
        { observationFailed = !CompleteGit(mergeBase); reason = "ambiguous-merge-base"; return false; }
        // UU alone can also describe a conflict at a renamed destination. Both sides must modify
        // the existing path, with no rename involving it, and all index modes must agree above.
        foreach (var side in new[] { oldHead, integratedMain })
        {
            var diff = RunAdditiveGit(path, "diff", "--name-status", "-z", "--find-renames", bases[0], side, "--");
            if (!CompleteGit(diff)) { observationFailed = true; return false; }
            if (!AreAdditivePathsContentChanges(diff.Output, files)) return false;
        }
        foreach (var file in files)
        {
            reason = "disallowed-path";
            if (!file.StartsWith("src/", StringComparison.Ordinal) || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                file.Split('/').Any(segment => segment is "." or "..")) return false;
            reason = "frozen-path";
            if (frozenPaths.Any(frozen => frozen.Replace('\\', '/').Equals(file, StringComparison.OrdinalIgnoreCase))) return false;
            reason = "not-content-conflict";
            var numstat = RunAdditiveGit(path, "diff", "--numstat", oldHead, integratedMain, "--", file);
            if (!CompleteGit(numstat)) { observationFailed = true; return false; }
            if (numstat.Output.StartsWith("-\t-\t", StringComparison.Ordinal)) return false;
            var bytes = File.ReadAllBytes(Path.Combine(path, file));
            if (RegistryAwareConflictMerge.IsRegistry(file))
            {
                var baseline = RunAdditiveGit(path, "show", $"{bases[0]}:{file}");
                var main = RunAdditiveGit(path, "show", $"{integratedMain}:{file}");
                var goal = RunAdditiveGit(path, "show", $"{oldHead}:{file}");
                if (!CompleteGit(baseline) || !CompleteGit(main) || !CompleteGit(goal)) { observationFailed = true; return false; }
                var plan = RegistryAwareConflictMerge.TryPlan(file, baseline.Output, main.Output, goal.Output, bytes, out reason);
                if (reason != "none") { hunks += RegistryAwareConflictMerge.CountHunks(bytes); return false; }
                if (plan is not null)
                {
                    hunks += RegistryAwareConflictMerge.CountHunks(bytes);
                    registryPlans.Add(plan);
                    continue;
                }
            }
            if (!TryResolveAdditiveHunks(bytes, out var kept, out var count, out reason))
            {
                hunks += count;
                return false;
            }
            hunks += count;
            resolved.Add(file, kept);
        }
        reason = "none";
        return true;
    }

    private static bool AreAdditivePathsContentChanges(string output, string[] files)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var modified = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fields.Length;)
        {
            var kind = fields[index++];
            if (index == fields.Length) return false;
            var first = fields[index++];
            if (kind.StartsWith('R') || kind.StartsWith('C'))
            {
                if (index == fields.Length) return false;
                var second = fields[index++];
                if (files.Contains(first, StringComparer.Ordinal) || files.Contains(second, StringComparer.Ordinal)) return false;
            }
            else if (files.Contains(first, StringComparer.Ordinal))
            {
                if (kind != "M") return false;
                modified.Add(first);
            }
        }
        return files.All(modified.Contains);
    }

    private static bool TryResolveAdditiveHunks(byte[] bytes, out byte[] resolved, out int hunks, out string reason)
    {
        resolved = [];
        hunks = 0;
        reason = "unparseable-markers";
        var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var encoding = new UTF8Encoding(false, true);
        var text = encoding.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        if (text.Contains('\0')) { reason = "not-content-conflict"; return false; }
        var kept = new StringBuilder();
        var goal = new StringBuilder();
        var main = new StringBuilder();
        var section = 0; // outside, goal, empty base, main
        var markerSize = 0;
        foreach (Match match in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\r|\n|$)"))
        {
            if (match.Length == 0) continue;
            var line = match.Value;
            var content = line.TrimEnd('\r', '\n');
            var marker = content.Length >= 7 && "<|=>".Contains(content[0]) &&
                content.Take(7).All(character => character == content[0]) ? content[0] : '\0';
            if (marker != '\0')
            {
                var size = content.TakeWhile(character => character == marker).Count();
                if (size < content.Length && content[size] != ' ') return false;
                if (marker == '<' && section == 0)
                {
                    markerSize = size;
                    section = 1;
                    hunks++;
                    goal.Clear();
                    main.Clear();
                }
                else if (size != markerSize) return false;
                else if (marker == '|' && section == 1) section = 2;
                else if (marker == '=' && section == 2 && content.Length == size) section = 3;
                else if (marker == '>' && section == 3)
                {
                    if (main.Length == 0 || goal.Length == 0) { reason = "empty-side"; return false; }
                    kept.Append(main).Append(goal); // theirs is main; ours is the goal in a merge.
                    section = 0;
                }
                else return false;
            }
            else if (section == 0) kept.Append(line);
            else if (section == 1) goal.Append(line);
            else if (section == 3) main.Append(line);
            else { reason = "non-empty-base"; return false; }
        }
        if (section != 0 || hunks == 0) return false;
        resolved = [.. hasBom ? new byte[] { 0xef, 0xbb, 0xbf } : Array.Empty<byte>(), .. encoding.GetBytes(kept.ToString())];
        reason = "none";
        return true;
    }
}
