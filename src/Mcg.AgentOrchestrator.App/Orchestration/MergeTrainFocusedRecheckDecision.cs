using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A decision belongs to the first red three-member attempt. It never changes
// attribution precedence or authorizes a drop-newest split without attribution.
internal sealed record MergeTrainFocusedRecheckDecision(
    MergeTrainReceipt RedReceipt, MergeTrainMemberBinding Culprit, string AttributionSource,
    string TrainAttemptId, IReadOnlyList<string> DependencyClasses,
    ConductorCohortFocusedSelection? Selection, string? FallbackReason)
{
    internal delegate RepositoryRecheckTestSelection DependencySelector(string path, IReadOnlyList<string> changedPaths);

    // Deterministic precedence: attribution, red evidence, dependency resolution,
    // overlap, bounds, execution evidence, not-run selections, execution failure.
    internal static MergeTrainFocusedRecheckDecision Plan(MergeTrainReceipt red, string workspacePath,
        MergeTrainMemberBinding culprit, string? source, DependencySelector dependencySelector)
    {
        MergeTrainFocusedRecheckDecision Fallback(string reason) =>
            new(red, culprit, source ?? "", red.GateAttemptId ?? "", [], null, reason);
        if (source is null) return Fallback("no-attribution");
        if (red.Identity.Members.Count != 3 || red.Outcome != MergeTrainGateOutcome.Failed || string.IsNullOrWhiteSpace(red.GateAttemptId) ||
            red.FailedChecks.Contains(SourceSizeRatchetPreflight.CheckName, StringComparer.Ordinal) ||
            !TryReadFailingIdentities(red, out var failing) ||
            !MergeTrainGateReceiptExecution.HasExplainedFailedChecks(red, failing)) return Fallback("invalid-evidence");
        RepositoryRecheckTestSelection dependency;
        try
        {
            // Gate-ready scopes can contain only a rename destination. Refuse that
            // incomplete removal scope rather than miss its source endpoint.
            if (!HasCompleteLandingPaths(workspacePath, red.Identity)) return Fallback("dependency-abandoned");
            dependency = dependencySelector(workspacePath, culprit.LandingPaths);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Fallback("dependency-abandoned"); }
        if (dependency is not { Resolved: true }) return Fallback("dependency-abandoned");
        if (!PathsAreDisjoint(red.Identity.Members, culprit)) return Fallback("overlapping-paths");
        var selection = Select(failing, dependency.TestClasses);
        return selection is null ? Fallback("focused-bounds") :
            new(red, culprit, source, red.GateAttemptId, dependency.TestClasses.ToArray(), selection, null);
    }

    internal MergeTrainReceipt? Execute(string workspacePath, MergeTrainIdentity identity,
        MergeTrainAcceptanceStore store, MergeTrainFocusedRecheckRunner.Runner runner,
        DotnetBuildEnvironmentLease lease, DateTimeOffset now, CancellationToken cancellationToken,
        Action<string> log)
    {
        string? reason = FallbackReason;
        MergeTrainReceipt? receipt = null;
        if (reason is null && !MatchesRemaining(RedReceipt, Culprit.GoalId, identity)) reason = "invalid-evidence";
        if (reason is null)
        {
            MergeTrainFocusedRecheckRunner.RunResult? run;
            try { run = runner(workspacePath, identity, Selection!, lease, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { run = null; }
            reason = ExecutionRefusal(run, Selection!);
            if (reason is null && (run!.MainRevision != identity.ObservedMainRevision ||
                run.Verification.TreeRevision != identity.TrainTreeRevision)) reason = "invalid-evidence";
            if (reason is null)
            {
                var candidate = MergeTrainFocusedRecheckReceipt.Compose(this, identity, run!, now);
                // Validate both before storage and again at the landing boundary.
                if (!MergeTrainFocusedRecheckReceiptAcceptance.Accepts(candidate, store, requireContentBound: false))
                    reason = "invalid-evidence";
                else
                    receipt = store.SaveGateReceipt(candidate);
            }
        }
        log($"MERGE_TRAIN_FOCUSED_RECHECK outcome={(receipt is null ? "fallback" : "landed")} " +
            $"reason={reason ?? "passed"} recheck={Selection?.Identities.Count ?? 0} culprit={Culprit.GoalId.Value[..8]}");
        return receipt;
    }

    internal static string? ExecutionRefusal(MergeTrainFocusedRecheckRunner.RunResult? run,
        ConductorCohortFocusedSelection selection)
    {
        if (run?.Verification.Kind == ConductorCohortFocusedPassKind.SelectionRejected) return "focused-bounds";
        if (run is null || string.IsNullOrWhiteSpace(run.AttemptId) ||
            MergeTrainFocusedRecheckRunner.ReadExecution(run.Verification.TestResultPaths) is not { } results)
            return "invalid-evidence";
        if (run.NotRunSelectionCount != 0 || selection.Identities.Any(selector =>
            !results.ExecutedTests.Any(test => Covers(selector, test)))) return "not-run-selections";
        if (run.Verification.Kind != ConductorCohortFocusedPassKind.Executed) return "invalid-evidence";
        return run.Verification.AllChecksPassed && run.Verification.FailingIdentities.Count == 0 &&
            run.Verification.FailedChecks.Count == 0 && results.Failures.Count == 0 ? null : "recheck-failed";
    }

    internal static bool TryReadFailingIdentities(MergeTrainReceipt red, out IReadOnlyList<string> identities)
    {
        identities = [];
        var reads = red.GateTestResultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        if (reads.Length == 0 || reads.Any(read => read.Status != AcceptanceTrxReadStatus.Readable)) return false;
        var failures = reads.SelectMany(read => read.Failures).Where(failure =>
            AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome)).ToArray();
        if (failures.Length == 0 || failures.Any(failure => MergeTrainRedAttribution.IsMessageSubjectGuard(failure) ||
            ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null ||
            string.IsNullOrWhiteSpace(failure.TestName))) return false;
        identities = failures.Select(failure => AcceptanceTrxTestIdentityResolver.NormalizeSelector(failure.TestName!))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return true;
    }

    internal static ConductorCohortFocusedSelection? Select(IReadOnlyList<string> failing, IReadOnlyList<string> classes)
    {
        // Keep the existing identity cap independently of the reader's class cap.
        var failures = ConductorAcceptanceCohortFocusedAttribution.TrySelect(failing.ToArray(), []);
        if (failures is null || classes.Count > 96 || classes.Any(name =>
            string.IsNullOrWhiteSpace(name) || name != name.Trim() ||
            !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$"))) return null;
        var selectors = failures.Identities.Concat(classes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var requests = selectors.Select(selector => $"FullyQualifiedName~{selector}").ToArray();
        if (requests.Any(request => request.Length > GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)) return null;
        return new(string.Join("; ", requests), selectors);
    }

    internal static bool Covers(string selector, string identity)
    {
        identity = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
        return identity.Equals(selector, StringComparison.Ordinal) || identity.StartsWith(selector + ".", StringComparison.Ordinal) ||
            (!selector.Contains('.') && AcceptanceTestSourceResolver.ExtractClassName(identity) == selector);
    }

    internal static bool PathsAreDisjoint(IReadOnlyList<MergeTrainMemberBinding> members, MergeTrainMemberBinding culprit)
    {
        var paths = culprit.LandingPaths.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !members.Where(member => member.GoalId != culprit.GoalId)
            .SelectMany(member => member.LandingPaths).Select(NormalizePath).Any(paths.Contains);
    }

    private static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/').Trim();
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path;
    }

    private static bool HasCompleteLandingPaths(string workspacePath, MergeTrainIdentity identity)
    {
        foreach (var member in identity.Members)
        {
            var diff = GitCli.Run(workspacePath, "diff", "--no-ext-diff", "--name-only", "--no-renames", "-z",
                $"{identity.ObservedMainRevision}...{member.CandidateRevision}", "--");
            if (!diff.Succeeded || diff.DrainTimedOut || !member.LandingPaths.Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(
                    diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(NormalizePath))) return false;
        }
        return true;
    }

    internal static bool MatchesRemaining(MergeTrainReceipt red, GoalId culprit, MergeTrainIdentity remaining)
    {
        var members = red.Identity.Members.Where(member => member.GoalId != culprit).ToArray();
        return red.Identity.Members.Count == 3 && members.Length == 2 && remaining.Members.Count == 2 &&
            red.Identity.ObservedMainRevision == remaining.ObservedMainRevision &&
            members.Zip(remaining.Members).All(pair => pair.First.GoalId == pair.Second.GoalId &&
                pair.First.CandidateRevision == pair.Second.CandidateRevision &&
                pair.First.BranchRevision == pair.Second.BranchRevision && pair.First.BriefVersion == pair.Second.BriefVersion &&
                pair.First.LandingPaths.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(pair.Second.LandingPaths.Select(NormalizePath)));
    }
}
