using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns guard augmentation, resume membership, and changed-file guard attribution together.
internal sealed class PreTesterGuardSelections
{
    private readonly DeveloperDeferredTestSelection _changedFileGuards;
    internal IReadOnlyList<FindingEvidenceSelection> Selections { get; }
    internal IReadOnlyList<string> NotRun => _changedFileGuards.NotRun;

    private PreTesterGuardSelections(DeveloperDeferredTestSelection changedFileGuards,
        IReadOnlyList<FindingEvidenceSelection> selections)
    {
        _changedFileGuards = changedFileGuards;
        Selections = selections;
    }

    internal static PreTesterGuardSelections Create(PreTesterChangedFileGuardClasses cache,
        string worktreePath, IReadOnlyList<string> changedPaths, string mainRevision)
    {
        var changedFileGuards = cache.Select(worktreePath, changedPaths, mainRevision);
        return new(changedFileGuards, PreTesterAlwaysRunGuardTestClasses.Select(worktreePath)
            .Concat(PreTesterTestSourceGuardClasses.Select(worktreePath, changedPaths))
            .Concat(changedFileGuards.Selections).ToArray());
    }

    internal static string ReadMainRevision(string worktreePath, OrchestratorWorkspace? workspace)
    {
        // Delegate-only driver fixtures have no workspace or integration ref to resolve.
        if (workspace is null) return "fixture-main-revision";
        return GoalWorktrees.TryResolveCandidateMergeBase(worktreePath, workspace.IntegrationBranch, out var revision)
            ? revision
            : throw new InvalidDataException("Pre-Tester changed-file guards could not resolve the candidate merge base.");
    }

    internal IReadOnlyList<FindingEvidenceSelection> Normalize(AcceptanceGateEngineSettings settings)
    {
        var selected = new List<FindingEvidenceSelection>();
        foreach (var selection in Selections)
        {
            if (FindingEvidenceRequestNormalization.TryNormalize(
                    new FindingEvidenceRequest([selection]), settings, (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
        }
        return selected;
    }

    internal FindingEvidenceSelection[] RetainStartedSelections(
        IEnumerable<FindingEvidenceSelection> selected, IReadOnlyList<string>? priorSelections,
        Func<FindingEvidenceSelection, string> format)
    {
        // Pre-change attempts keep their original request identity and per-class TRX obligation.
        return selected.DistinctBy(format).Where(selection => priorSelections is null ||
            !(PreTesterAlwaysRunGuardTestClasses.ContainsClass(selection.TestClass) ||
              PreTesterTestSourceGuardClasses.ContainsClass(selection.TestClass) ||
              PreTesterChangedFileGuardClasses.ContainsClass(_changedFileGuards.Selections, selection.TestClass)) ||
            priorSelections.Contains(format(selection), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal bool EveryFailureIsInsideChanges(IReadOnlyList<string> failures,
        Func<IReadOnlyList<string>, bool> otherFailuresAreInsideChanges)
    {
        var otherFailures = failures.Where(test => !PreTesterChangedFileGuardClasses.ContainsClass(
            _changedFileGuards.Selections, AcceptanceTestSourceResolver.ExtractClassName(test))).ToArray();
        return otherFailures.Length == 0 || otherFailuresAreInsideChanges(otherFailures);
    }
}
