using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SourceSizeRatchet
{
    internal const string DocumentationPath = "docs/god-class-decomposition-plan.md";
    internal const string DocumentationSectionHeading = "## Guarded source size ratchet";
    internal const string SourcePath = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SourceSizeRatchet.cs";

    // Seeded at 6e7a90d7b3fe192ae4f1e430cb7452710f88b73c using File.ReadLines(path).Count().
    // Each row is the only place its guarded path and ceiling value live. When extraction shrinks a guarded
    // file, lower its ceiling here. When growth is unavoidable, put the goal-specific justification directly
    // above that row and raise only that value. Never derive these ceilings from current files or add an opt-out.
    internal static IReadOnlyList<SourceSizeCeiling> SeededCeilings { get; } = Array.AsReadOnly(
        new[]
        {
            // Raised from 8713 for goal 75b85ca1: effective lane selection and structural-coverage
            // threading belong to the gate-plan owner, so extracting them would split that invariant.
            // Raised again for goal 85f0b81d: 227 new behavior lines were extracted to
            // AcceptanceLaneDurationStore, so only call-site lines remained here.
            // Raised for goal 4c366f95: the preflight must run at the gate's single entry point before
            // any process-launching phase; parsing and evaluation remain in dedicated collaborators.
            // Raised for goal 8c7fb174: declaration failure and partition preservation belong to the
            // effective-plan and structural-coverage owner; the check no longer rewrites lane policy.
            // Raised for goal 604b8a93: OperatorComms must join the base-build-cache project registry
            // where the gate plan owns cacheable project ordering and complete build-receipt coverage.
            // Raised for goal 98430a7c: invocation-scoped TRX/heartbeat identity and prior-run heartbeat
            // reaping must stay at the verifier's check-execution boundary so one immutable ordinal is
            // shared by telemetry resolution, result identity, and managed-child lifecycle handling.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", 8471),
            // Goal 682f25a1 re-derived this row after integrating goal c2eae988, whose acceptance
            // cancellation seam had already added 74 net lines before the multi-file ratchet landed.
            // Raised for goal a22c7293: slice admission must run where all dispatch paths converge.
            // Raised for goal 8b6de491: the slice-batch parent execution guard must be consulted where the
            // driver decides a goal's advance, which is the only point holding both the goal and the guard.
            // Goal 0e0aa816 extracted acceptance landing to its own partial-class source file.
            // Goal ce3c3917 adds only the injectable inline-landing lease seam here; lease behavior remains
            // extracted in ConductorDriver.AcceptanceLanding.cs.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs", 6142),
            // Raised for goal dadadfac: set-aside re-admission must be decided where the loop already holds
            // the goal, its recorded set-aside entry, and the current sweep blockers together.
            // Raised for goal 289b469d: registration-fault hold/escalation must be decided at the existing
            // parallel acceptance completion seam; parsing and durable counting remain in its collaborator.
            // Goal 0e0aa816 extracted the fallback acceptance-start transition to its own partial-class source file.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", 5132),
            // Raised for goal 18afe5f2: missing-build-evidence rejection and commit suppression
            // must run where parsed worker results and authoritative changed paths meet. Raised again
            // for goal 0d39b5a3, which adds the narrow Planner sample launch and completion-selection
            // call sites; the fan-out and selection behavior remain extracted into dedicated components.
            // Both goals grew this file, so this row is the measured size of the merged result, not
            // either goal's individual ceiling.
            // Goal 6a3d0fd4 raised this row to 3262 for the bounded planner-sample wait, which must run
            // between the primary dispatch completing and candidate collection.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs", 3262),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs", 3220),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs", 4696),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs", 1566),
            // Goal 682f25a1 re-derived this row after integrating goal c2eae988, whose fault-isolation
            // eligibility coverage had already added 173 net lines before the multi-file ratchet landed.
            // Goal ce3c3917 adds the two MakeDriver forwarding seams required to inject and capture the
            // inline-landing lease lifecycle without changing unrelated driver tests.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs", 1400),
            // Goal ce3c3917 keeps acquired, unavailable, and exceptional lease-lifecycle coverage together
            // in the acceptance-coordination owner rather than splitting one behavioral contract.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsAcceptanceCoordination.cs", 984),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsLifecycleStates.cs", 1524),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsReviewRetryConvergence.cs", 778),
            // Raised for goal 4e3cdbb8: behavioral coverage now pins every permanent and transient
            // finding-evidence refusal disposition plus receipt-id priority on the next-round request loop.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidence.cs", 2092),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsPreReviewEvidence.cs", 1187),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsContractRepairBounds.cs", 1097),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsDispatchRecovery.cs", 537),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommands.cs", 1489),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsAcceptance.cs", 697),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsConductLoopHydration.cs", 817),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsDispatchRecovery.cs", 455),
            // Raised for goal 8b6de491: the slice-batch preview test needs an available Developer agent in
            // its roster now that creating a batch requires one, and the roster is declared inline.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement.cs", 2780),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsGoalQueriesAndLanding.cs", 574),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsStartupAndMetadata.cs", 275),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs", 4834),
            // Goal 46ff9f83 adds unequal logical-width/build-permit controls and the typed maximum diagnostic.
            // Goal 289b469d adds the per-stage registration-fault decision table, bounded-cap proof, durable
            // fault/verdict state distinction, and paid-round negative controls owned by this existing class.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptance.cs", 4698),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTests.cs", 1480),
        });

    internal static IReadOnlyList<SourceSizeViolation> Evaluate(
        string repositoryRoot,
        IEnumerable<SourceSizeCeiling> ceilings,
        Func<string, IEnumerable<string>>? readLines = null)
    {
        readLines ??= File.ReadLines;
        var violations = new List<SourceSizeViolation>();
        foreach (var ceiling in ceilings)
        {
            var sourcePath = Path.Combine(
                repositoryRoot,
                ceiling.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                violations.Add(new SourceSizeViolation(
                    ceiling.RelativePath,
                    null,
                    ceiling.MaximumLineCount,
                    BuildMissingFileMessage(ceiling.RelativePath)));
                continue;
            }

            try
            {
                var actualLineCount = readLines(sourcePath).Count();
                if (actualLineCount > ceiling.MaximumLineCount)
                {
                    violations.Add(new SourceSizeViolation(
                        ceiling.RelativePath,
                        actualLineCount,
                        ceiling.MaximumLineCount,
                        BuildOverCeilingMessage(ceiling, actualLineCount)));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                violations.Add(new SourceSizeViolation(
                    ceiling.RelativePath,
                    null,
                    ceiling.MaximumLineCount,
                    BuildUnreadableFileMessage(ceiling.RelativePath, exception)));
            }
        }

        return violations;
    }

    internal static IReadOnlyList<SourceSizeDocumentationViolation> EvaluateDocumentation(
        IEnumerable<string> documentationLines,
        IEnumerable<SourceSizeCeiling> ceilings)
    {
        var lines = documentationLines.ToArray();
        var sectionStart = Array.FindIndex(
            lines,
            line => string.Equals(line.Trim(), DocumentationSectionHeading, StringComparison.Ordinal));
        if (sectionStart < 0)
        {
            return new[]
            {
                new SourceSizeDocumentationViolation(
                    "missing-ratchet-section",
                    null,
                    $"{DocumentationPath} must contain the '{DocumentationSectionHeading}' section."),
            };
        }

        var sectionEnd = lines.Length;
        for (var lineIndex = sectionStart + 1; lineIndex < lines.Length; lineIndex++)
        {
            if (!lines[lineIndex].StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            sectionEnd = lineIndex;
            break;
        }

        var section = lines[sectionStart..sectionEnd];
        var violations = new List<SourceSizeDocumentationViolation>();
        AddMissingAuthorityPointerViolation(section, SeededCeilingsSymbol, violations);
        AddMissingAuthorityPointerViolation(section, SourcePath, violations);

        foreach (var ceiling in ceilings)
        {
            var windowsPath = ceiling.RelativePath.Replace('/', '\\');
            for (var sectionLineIndex = 0; sectionLineIndex < section.Length; sectionLineIndex++)
            {
                var line = section[sectionLineIndex];
                if (!line.Contains(ceiling.RelativePath, StringComparison.Ordinal) &&
                    !line.Contains(windowsPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var isRequiredAuthorityPointer =
                    string.Equals(ceiling.RelativePath, SourcePath, StringComparison.Ordinal) &&
                    line.Contains(SeededCeilingsSymbol, StringComparison.Ordinal) &&
                    line.Contains(SourcePath, StringComparison.Ordinal);
                if (isRequiredAuthorityPointer)
                {
                    continue;
                }

                var lineNumber = sectionStart + sectionLineIndex + 1;
                violations.Add(new SourceSizeDocumentationViolation(
                    "duplicated-ceiling-record",
                    lineNumber,
                    $"{DocumentationPath} line {lineNumber} duplicates guarded path '{ceiling.RelativePath}'. " +
                    $"Keep guarded paths and ceiling values only in {SeededCeilingsSymbol} at {SourcePath}."));
            }
        }

        return violations;
    }

    private static void AddMissingAuthorityPointerViolation(
        IReadOnlyList<string> section,
        string requiredText,
        ICollection<SourceSizeDocumentationViolation> violations)
    {
        if (section.Any(line => line.Contains(requiredText, StringComparison.Ordinal)))
        {
            return;
        }

        violations.Add(new SourceSizeDocumentationViolation(
            "missing-authority-pointer",
            null,
            $"The '{DocumentationSectionHeading}' section in {DocumentationPath} must point to " +
            $"{SeededCeilingsSymbol} at {SourcePath}; missing '{requiredText}'."));
    }

    private static string BuildOverCeilingMessage(SourceSizeCeiling ceiling, int actualLineCount)
    {
        return $"{ceiling.RelativePath} has {actualLineCount.ToString(CultureInfo.InvariantCulture)} lines, " +
            $"exceeding the recorded ceiling of {ceiling.MaximumLineCount.ToString(CultureInfo.InvariantCulture)}. " +
            "Extract behavior to a collaborator and lower the ceiling for this entry, or raise the recorded " +
            "ceiling for this entry deliberately with justification in the same change by editing " +
            $"{SeededCeilingsSymbol}. See {DocumentationPath}.";
    }

    private static string BuildMissingFileMessage(string relativePath)
    {
        return $"Guarded file '{relativePath}' does not exist. A rename or delete must update " +
            $"{SeededCeilingsSymbol} in the same change. See {DocumentationPath}.";
    }

    private static string BuildUnreadableFileMessage(string relativePath, Exception exception)
    {
        return $"Guarded file '{relativePath}' could not be read ({exception.GetType().Name}: {exception.Message}). " +
            $"The size ratchet cannot pass without checking every entry. See {DocumentationPath}.";
    }

    internal static string SeededCeilingsSymbol =>
        $"{nameof(SourceSizeRatchet)}.{nameof(SeededCeilings)}";
}

internal sealed record SourceSizeCeiling(string RelativePath, int MaximumLineCount);

internal sealed record SourceSizeViolation(
    string RelativePath,
    int? ActualLineCount,
    int MaximumLineCount,
    string Message);

internal sealed record SourceSizeDocumentationViolation(string Rule, int? LineNumber, string Message);
