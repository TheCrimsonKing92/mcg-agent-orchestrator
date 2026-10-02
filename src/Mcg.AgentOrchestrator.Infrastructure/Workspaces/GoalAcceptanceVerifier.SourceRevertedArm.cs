using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    public Task<FocusedEvidenceRunResult> RunNegativeControlFocusedEvidenceOwnedAsync(
        string worktreePath, GoalId? goalId, string request,
        IAcceptanceFocusedVerificationOwner executionOwner,
        FindingEvidenceNegativeControl negativeControl,
        int? stableSlotIndex = null, DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false, IReadOnlyList<string>? revertPaths = null)
    {
        EnsureTestOverridesUnchanged();
        if (negativeControl != FindingEvidenceNegativeControl.RevertSrc)
            throw new ArgumentOutOfRangeException(nameof(negativeControl));
        return executionOwner is AcceptanceFocusedVerificationOwner owner
            ? owner.ExecuteAsync(this, worktreePath, goalId, request, stableSlotIndex,
                stableSlotLease, runBaselineArm, negativeControl, revertPaths)
            : throw new ArgumentException("A focused-verification execution owner is required.", nameof(executionOwner));
    }

    private static string? ResolveFocusedEvidenceMergeBase(string worktreePath) =>
        ResolveGitScalar(worktreePath, "merge-base", "HEAD", "main");

    private async Task<FocusedEvidenceRunResult> AddSourceRevertedEvidenceAsync(
        FocusedEvidenceRunResult evidence, FindingEvidenceNegativeControl? mode,
        string worktreePath, GoalId? goalId, IReadOnlyList<AcceptanceManifestCheck> checks,
        FocusedEvidenceCoverage coverage, int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease, IAcceptanceRunExecutionContext executionOwner,
        string? mergeBase = null, IReadOnlyList<string>? revertPaths = null)
    {
        if (mode is null) return evidence;
        if (mode != FindingEvidenceNegativeControl.RevertSrc)
            throw new ArgumentOutOfRangeException(nameof(mode));
        var candidate = evidence.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        EmitFocusedEvidenceArmStarted(FindingEvidenceArm.SourceReverted, candidate.Sha);
        var reverted = candidate.Disposition == FindingEvidenceArmDisposition.Green
            ? await RunSourceRevertedFocusedEvidenceArmAsync(worktreePath, candidate.Sha,
                mergeBase ?? ResolveFocusedEvidenceMergeBase(worktreePath), goalId, checks, coverage,
                stableSlotIndex, stableSlotLease, executionOwner, revertPaths).ConfigureAwait(false)
            : InconclusiveSourceReverted(candidate.Sha,
                $"Candidate arm is {candidate.Disposition}; source-reverted arm not run");
        EmitFocusedEvidenceArmResolved(reverted);
        var compilerErrors = ExtractSourceRevertedCompilerErrors(reverted);
        var outcome = reverted.Disposition switch
        {
            FindingEvidenceArmDisposition.Red when candidate.Disposition == FindingEvidenceArmDisposition.Green =>
                FindingEvidenceNegativeControlOutcome.Demonstrated,
            FindingEvidenceArmDisposition.Green => FindingEvidenceNegativeControlOutcome.NotDemonstrated,
            FindingEvidenceArmDisposition.Inconclusive when compilerErrors.Count > 0 &&
                candidate.Disposition == FindingEvidenceArmDisposition.Green => FindingEvidenceNegativeControlOutcome.CompileRed,
            _ => FindingEvidenceNegativeControlOutcome.Inconclusive
        };
        var failure = reverted.Checks.FirstOrDefault(check => !check.Passed)?.OutputTail;
        return evidence with
        {
            Arms = [.. evidence.Arms!, reverted],
            NegativeControlOutcome = outcome,
            RevertPathsRejection = reverted.RevertPathsRejection,
            Summary = $"{evidence.Summary}; {FindingEvidenceNegativeControlOutcomeJsonConverter.ToWireValue(outcome)}; " +
                $"source-reverted={reverted.Disposition} ({reverted.Summary})" +
                (compilerErrors.Count > 0 ? $"; compiler errors: {string.Join("; ", compilerErrors)}" :
                    string.IsNullOrWhiteSpace(failure) ? string.Empty : $"; {TrimForReceipt(failure)}")
        };
    }

    private async Task<FocusedEvidenceArmRunResult> RunSourceRevertedFocusedEvidenceArmAsync(
        string candidatePath, string candidateSha, string? mergeBase, GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> checks, FocusedEvidenceCoverage coverage,
        int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
        IAcceptanceRunExecutionContext executionOwner, IReadOnlyList<string>? revertPaths)
    {
        string? revertedPath = null;
        var root = OrchestratorTempRoot.GetPurposeDirectory("mcg-focused-evidence-baselines");
        DotnetBuildEnvironment? environment = null;
        GoalId? environmentId = null;
        try
        {
            if (string.IsNullOrWhiteSpace(mergeBase))
                return InconclusiveSourceReverted(candidateSha, "merge-base could not be resolved");
            var status = GitCli.Run(candidatePath, "status", "--porcelain", "--untracked-files=all");
            if (!status.Succeeded || status.DrainTimedOut || GitCli.ParseCommitWorthyStatusPaths(status.Output).Length > 0)
                return InconclusiveSourceReverted(candidateSha,
                    "candidate has tracked uncommitted changes, untracked candidate files, or unreadable status");
            if (!string.Equals(ResolveGitScalar(candidatePath, "rev-parse", "HEAD"), candidateSha, StringComparison.Ordinal))
                return InconclusiveSourceReverted(candidateSha, "candidate HEAD changed after the Candidate arm");
            var diff = GitCli.Run(candidatePath, "diff", "--name-status", "--no-renames", "-z",
                mergeBase, candidateSha, "--", "src/");
            if (!diff.Succeeded || diff.DrainTimedOut)
                return InconclusiveSourceReverted(candidateSha, $"source diff failed: {TrimForReceipt(diff.Error)}");
            var fields = diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length % 2 != 0)
                return InconclusiveSourceReverted(candidateSha, "malformed source name-status diff");
            var selected = revertPaths is null ? null : FindingEvidenceRevertPaths.Canonicalize(revertPaths);
            if (selected is not null && ValidateSourceRevertPaths(selected, fields) is { } rejection)
                return InconclusiveSourceReverted(candidateSha,
                    FindingEvidenceRevertPathsRejectionJsonConverter.ToWireValue(rejection), rejection);
            var restore = new List<string>();
            var added = new List<string>();
            for (var i = 0; i < fields.Length; i += 2)
            {
                var path = fields[i + 1];
                if (!path.StartsWith("src/", StringComparison.Ordinal))
                    return InconclusiveSourceReverted(candidateSha, "source diff contained a path outside src/");
                if (selected is not null && !selected.Contains(path, StringComparer.Ordinal)) continue;
                switch (fields[i])
                {
                    case "A": added.Add(path); break;
                    case "M": case "D": case "T": restore.Add(path); break;
                    default: return InconclusiveSourceReverted(candidateSha, $"unsupported source diff status: {fields[i]}");
                }
            }
            if (restore.Count + added.Count == 0)
                return InconclusiveSourceReverted(candidateSha, "no candidate-changed src/ paths were reverted");
            Directory.CreateDirectory(root);
            var prefix = goalId?.Value ?? "operator";
            revertedPath = Path.Combine(root, $"{prefix[..Math.Min(8, prefix.Length)]}-src-{Guid.NewGuid():N}");
            var add = GitCli.Run(candidatePath, "worktree", "add", "--detach", revertedPath, candidateSha);
            if (!add.Succeeded)
                return InconclusiveSourceReverted(candidateSha, $"source-reverted worktree creation failed: {TrimForReceipt(add.Error)}");
            if (TryLabelFocusedEvidenceBaselineWorktree(revertedPath) is { } integrityFailure)
                return InconclusiveSourceReverted(candidateSha, integrityFailure);
            if (restore.Count > 0)
            {
                // NUL pathspecs preserve whitespace/newlines; literal mode prevents wildcard expansion.
                var pathspec = Path.Combine(root, $"{Guid.NewGuid():N}.pathspec");
                try
                {
                    File.WriteAllText(pathspec, string.Join('\0', restore) + '\0', new UTF8Encoding(false));
                    var restored = GitCli.Run(revertedPath, "--literal-pathspecs", "restore",
                        $"--source={mergeBase}", "--worktree", $"--pathspec-from-file={pathspec}", "--pathspec-file-nul");
                    if (!restored.Succeeded)
                        return InconclusiveSourceReverted(candidateSha, $"source restore failed: {TrimForReceipt(restored.Error)}");
                }
                finally { File.Delete(pathspec); }
            }
            foreach (var path in added)
            {
                var target = Path.GetFullPath(Path.Combine(revertedPath, path));
                if (!target.StartsWith(Path.GetFullPath(revertedPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Source deletion escaped the reverted worktree.");
                File.Delete(target);
            }
            environmentId = GoalId.New();
            environment = DotnetBuildEnvironmentManager.CreateAttempt(environmentId,
                $"focused-evidence-source-reverted-{candidateSha[..Math.Min(8, candidateSha.Length)]}", storageRoot: _storageRoot);
            // Tests remain at the candidate, including new selections. Never partition them as baseline-only.
            return await RunFocusedEvidenceArmAsync(FindingEvidenceArm.SourceReverted, candidateSha,
                revertedPath, null, checks, coverage, stableSlotIndex, stableSlotLease, environment,
                executionOwner.CancellationToken, executionOwner: executionOwner).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (DotnetBuildSlotsBusyException or BuildLockBlockedException or OperationCanceledException))
        {
            return InconclusiveSourceReverted(candidateSha, $"source-reverted apparatus failure: {TrimForReceipt(ex.Message)}");
        }
        finally
        {
            if (revertedPath is not null)
            {
                _ = GitCli.Run(candidatePath, "worktree", "remove", "--force", revertedPath);
                TryDeleteFocusedEvidenceBaselineDirectory(root, revertedPath);
            }
            if (environment is not null)
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(environment, _storageRoot);
            if (environmentId is not null)
                DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(environmentId, _storageRoot);
        }
    }

    private static FindingEvidenceRevertPathsRejection? ValidateSourceRevertPaths(string[] paths, string[] diff)
    {
        if (paths.Length == 0) return FindingEvidenceRevertPathsRejection.EmptyList;
        var changed = diff.Where((_, index) => index % 2 == 1).ToHashSet(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path.StartsWith("tests/", StringComparison.Ordinal)) return FindingEvidenceRevertPathsRejection.UnderTests;
            if (Path.IsPathRooted(path) || !path.StartsWith("src/", StringComparison.Ordinal) ||
                path.Split('/').Any(part => part is "." or ".." or ""))
                return FindingEvidenceRevertPathsRejection.OutsideSrc;
            if (!changed.Contains(path)) return FindingEvidenceRevertPathsRejection.NotChangedByGoal;
        }
        return null;
    }

    private static IReadOnlyList<string> ExtractSourceRevertedCompilerErrors(FocusedEvidenceArmRunResult arm)
    {
        var failed = arm.Checks.Where(check => !check.Passed).ToArray();
        if (failed.Any(check => check.FailingTestIdentities?.Count > 0)) return [];
        var errors = new List<string>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in failed)
        foreach (var output in new[] { check.OutputTail, check.ProcessStderr })
        foreach (Match match in Regex.Matches(output ?? string.Empty, @"\berror (CS[0-9]{4}):[ \t]*([^\r\n]*)"))
        {
            if (!codes.Add(match.Groups[1].Value)) continue;
            errors.Add($"error {match.Groups[1].Value}: {TrimForReceipt(match.Groups[2].Value.Trim())}");
            if (errors.Count == 3) return errors;
        }
        return errors;
    }

    private static FocusedEvidenceArmRunResult InconclusiveSourceReverted(string sha, string summary,
        FindingEvidenceRevertPathsRejection? rejection = null) =>
        new(FindingEvidenceArm.SourceReverted, sha, FindingEvidenceArmDisposition.Inconclusive,
            Accepted: false, Passed: false, summary, [], RevertPathsRejection: rejection);
}
