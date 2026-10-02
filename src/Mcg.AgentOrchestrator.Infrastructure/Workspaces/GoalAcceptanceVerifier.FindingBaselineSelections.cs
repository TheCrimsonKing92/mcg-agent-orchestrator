using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private AcceptanceFailureAttributionPlanner.BaselineSourceSelectionPlan SelectBaselineFocusedChecks(
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        string baselineSha,
        string baselinePath,
        bool classifyMissingSelectionsAsAbsent,
        bool partitionCandidateOnlySelections)
    {
        if (classifyMissingSelectionsAsAbsent)
        {
            return AcceptanceFailureAttributionPlanner.BuildBaselineSourceSelections(
                baselineSha, focusedChecks, ProjectLabel, EngineSettings, baselinePath);
        }

        if (!partitionCandidateOnlySelections)
        {
            return new AcceptanceFailureAttributionPlanner.BaselineSourceSelectionPlan(focusedChecks, []);
        }

        var executable = new List<AcceptanceManifestCheck>();
        var classified = new List<AcceptanceCheckResult>();
        foreach (var check in focusedChecks)
        {
            if (!check.IsFocusedEvidenceSelection || check.Project is null)
            {
                executable.Add(check);
                continue;
            }

            var survivingGroups = new List<string>();
            var absent = new List<FocusedEvidenceFilterToken>();
            string? apparatusDetail = null;
            foreach (var group in check.FocusedEvidenceSelections)
            {
                var surviving = new List<FocusedEvidenceFilterToken>();
                foreach (var token in group)
                {
                    if (token.Kind is not (FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method))
                    {
                        surviving.Add(token);
                        continue;
                    }

                    try
                    {
                        var classFiles = FocusedEvidenceRequestResolver.FindFocusedEvidenceClassFiles(
                            baselinePath, check.Project, token.ContainingClass);
                        var classExists = classFiles.Count > 0;
                        var methodExists = token.Kind != FocusedEvidenceTokenKind.Method ||
                            (classExists && FocusedEvidenceRequestResolver.FindFocusedEvidenceTestMethodNames(
                                classFiles, token.ContainingClass).Any(method =>
                                    method.StartsWith(token.Value.Split('.').Last(),
                                        StringComparison.OrdinalIgnoreCase)));
                        var resolves = classExists && methodExists;
                        if (resolves && token.Kind == FocusedEvidenceTokenKind.Method &&
                            !FocusedEvidenceRequestResolver.TryResolveFocusedEvidenceSelection(
                                baselinePath, check.Project, token.CanonicalToken, token.Value,
                                out _, out var rejection))
                        {
                            apparatusDetail = rejection.Detail;
                            break;
                        }
                        if (resolves)
                        {
                            surviving.Add(token);
                        }
                        else
                        {
                            absent.Add(token);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        apparatusDetail = ex.Message;
                        break;
                    }
                }

                if (apparatusDetail is not null)
                {
                    break;
                }

                if (surviving.Any(token => token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method))
                {
                    survivingGroups.Add($"{ProjectLabel(check.Project)}: " +
                        string.Join("|", surviving.Select(token => token.CanonicalToken)));
                }
            }

            if (apparatusDetail is not null)
            {
                classified.Add(BaselineSourceClassification(check, baselineSha,
                    AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
                    $"Focused baseline source selection was unavailable: {apparatusDetail}"));
                continue;
            }

            if (absent.Count == 0)
            {
                executable.Add(check);
                continue;
            }

            if (survivingGroups.Count > 0)
            {
                if (!FocusedEvidenceRequestResolver.TryBuildFocusedEvidenceChecks(
                        string.Join("; ", survivingGroups), EngineSettings, baselinePath,
                        out var rebuilt, out _, out var rejection) || rebuilt.Count != 1)
                {
                    classified.Add(BaselineSourceClassification(check, baselineSha,
                        AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
                        $"Focused baseline source selection was unavailable: {rejection?.Detail ?? "invalid exact selector"}"));
                    continue;
                }

                executable.Add(rebuilt[0]);
            }

            classified.AddRange(absent.Select(token => BaselineSourceClassification(
                check, baselineSha, AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline,
                $"Focused identity {token.CanonicalToken} does not exist in baseline source at {baselineSha}.",
                token.CanonicalToken)));
        }

        return new AcceptanceFailureAttributionPlanner.BaselineSourceSelectionPlan(executable, classified);
    }

    private static AcceptanceCheckResult BaselineSourceClassification(
        AcceptanceManifestCheck check,
        string baselineSha,
        string classification,
        string detail,
        string? token = null) => new(
            token is null ? check.Name : $"{check.Name} [{token}]",
            Passed: false,
            ExitCode: null,
            OutputTail: detail,
            FailureClassification: classification,
            ExecutedTestCount: classification == AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline ? 0 : null,
            TestProjectPath: check.Project);

    private static FocusedEvidenceArmRunResult CombineFindingBaselineArm(
        FocusedEvidenceArmRunResult executedArm,
        IReadOnlyList<AcceptanceCheckResult> sourceClassifications)
    {
        if (sourceClassifications.Count == 0)
        {
            return executedArm;
        }

        var absentCount = sourceClassifications.Count(check =>
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline);
        var hasApparatusFailure = absentCount != sourceClassifications.Count;
        var disposition = hasApparatusFailure
            ? FindingEvidenceArmDisposition.ApparatusFailure
            : executedArm.Checks.Count == 0
                ? FindingEvidenceArmDisposition.Green
                : executedArm.Disposition;
        return executedArm with
        {
            Disposition = disposition,
            Passed = disposition == FindingEvidenceArmDisposition.Green,
            Summary = $"{executedArm.Summary}; absent-at-baseline={absentCount}",
            Checks = executedArm.Checks.Concat(sourceClassifications).ToArray()
        };
    }
}
