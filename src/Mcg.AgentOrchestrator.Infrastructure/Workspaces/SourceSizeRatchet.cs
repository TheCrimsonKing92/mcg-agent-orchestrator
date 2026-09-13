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
            // Raised for goal cdde61cc: live phase/target capture and owned-child cleanup ordering must
            // remain at the verifier execution boundary; lifecycle implementation stays extracted.
            // Raised for the acceptance-popup suppression repair: hermetic verification must disable
            // descendant MSBuild-server reuse where the verifier owns the child environment.
            // Raised by six lines for goal 13630c9f: TRX failure-cause receipt extraction and typed apparatus
            // attribution belong where raw check output becomes final acceptance evidence.
            // Raised for goal 344d20c0: typed shard completion, retry evidence custody, and semantic-plan
            // deduplication must remain at the verifier boundary that owns execution and retry decisions.
            // Reconciled after landing goal 1441c61c: its failure-cause evidence custody shares this same
            // execution boundary. This is the measured combined size after rebasing both reviewed changes.
            // Three-line increase adds explicit current-process exclusion at the final remediation boundary.
            // Raised for goal d5fcf981: canonical TRX identity production and merge-base failure
            // attribution meet where the verifier converts a test receipt into final gate evidence.
            // Raised for goal fc109f3f: typed seeded-repository receipt validation and mixed-owner
            // classification belong at the verifier boundary that converts TRX output into gate attribution.
            // Reconciled after main 4ba0dba0 and dda4a341 added reviewed cancellation-boundary and
            // gate-wide attribution batching behavior; 9688 is the measured combined post-rebase size.
            // Raised for goal 6f9ddf54: the verifier must carry the job-owned command-child identity rather
            // than the Windows shell wrapper identity into acceptance evidence used for root correlation.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", 9710),
            // Goal 682f25a1 re-derived this row after integrating goal c2eae988, whose acceptance
            // cancellation seam had already added 74 net lines before the multi-file ratchet landed.
            // Raised for goal a22c7293: slice admission must run where all dispatch paths converge.
            // Raised for goal 8b6de491: the slice-batch parent execution guard must be consulted where the
            // driver decides a goal's advance, which is the only point holding both the goal and the guard.
            // Goal 0e0aa816 extracted acceptance landing to its own partial-class source file.
            // Goal ce3c3917 adds only the injectable inline-landing lease seam here; lease behavior remains
            // extracted in ConductorDriver.AcceptanceLanding.cs.
            // Raised for goal 13630c9f: typed apparatus attribution, bounded hold release, and candidate
            // retry suppression must meet at the existing acceptance disposition seam.
            // Reconciled after main 0d44d054 added 12 reviewed journal-cache lines that eliminate eager
            // full-journal retention; this is the measured combined size, not new goal behavior.
            // Raised for goal fd252fe4: typed retry causes must be selected and passed at the existing
            // retry-decision call sites; classification and admission persistence remain extracted.
            // Raised for goal d5fcf981: changed-scope attribution and no-worker holds must meet at the
            // existing acceptance retry decision that owns task reopening and lifecycle disposition.
            // Raised for goal 52590d3e: writable-blocker priority and typed suppression emission must run
            // before request normalization at the finding-evidence orchestration boundary they govern.
            // Reconciled after integrating main e1f6f11c: reviewed conductor changes already on main
            // brought this file to the measured post-merge size.
            // Raised for goal 8263a08c: exited-dispatch reconciliation must precede the existing failure
            // retry ladder and guard the shared dispatch-start boundary before LastProcess is replaced.
            // Goal 1592104a extracted acceptance-owned evidence recording and outstanding-obligation diagnostics.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs", 6774),
            // Raised for goal dadadfac: set-aside re-admission must be decided where the loop already holds
            // the goal, its recorded set-aside entry, and the current sweep blockers together.
            // Raised for goal 289b469d: registration-fault hold/escalation must be decided at the existing
            // parallel acceptance completion seam; parsing and durable counting remain in its collaborator.
            // Goal 0e0aa816 extracted the fallback acceptance-start transition to its own partial-class source file.
            // Raised for goal cdde61cc: bounded gate-engine fault reconciliation belongs at the terminal
            // state seam that owns retry exhaustion and prevents candidate-failure classification.
            // Tightened after live acceptance reservation logic moved to its partial-class collaborator.
            // Reconciled after integrating main cc4d7b58 and goal f46ecbee at their measured combined size:
            // the goal's state/run-event maintenance leases remain scoped to the active conductor loop.
            // Raised for goal ae54b5eb: set-aside fingerprints retain lifecycle rechecks while comparing
            // retry/candidate task state independently of the kernel goal instance reloaded by admission.
            // Goal 1592104a moved unloaded-intent disposition to a collaborator that retains reload evidence.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", 5193),
            // Raised for goal 18afe5f2: missing-build-evidence rejection and commit suppression
            // must run where parsed worker results and authoritative changed paths meet. Raised again
            // for goal 0d39b5a3, which adds the narrow Planner sample launch and completion-selection
            // call sites; the fan-out and selection behavior remain extracted into dedicated components.
            // Both goals grew this file, so this row is the measured size of the merged result, not
            // either goal's individual ceiling.
            // Goal 6a3d0fd4 raised this row to 3262 for the bounded planner-sample wait, which must run
            // between the primary dispatch completing and candidate collection.
            // Goal 1441c61c extracted post-reap cancellation evidence classification so the runner
            // only sequences process reaping, lifecycle recording, and resource accounting.
            // Goal fd252fe4 adds the durable retry-admission start claim and worker-release gate at
            // that process-start boundary. Goal d8a889bd then extracted worktree build-daemon
            // discovery, identity revalidation, and reaping to WorktreeBuildDaemonReaper; 3137 is
            // the measured combined post-rebase size.
            // Goal 52049d08 adds only context-usage measurement and typed no-change admission at the
            // existing dispatch-completion boundary; projection and classification remain extracted.
            // Goal 17d96426 adds Hermes to the existing provider-to-sandbox mapping and writability
            // decision; both checks belong at this dispatch boundary and add five measured lines.
            // Goal 3b9d6b12 moved complete-log authority, decision selection, normalization, caching, and
            // bounded snapshots to ProcessLogReader; the runner now retains only completion sequencing.
            // Goal b3cba804 adds the authoritative-output-only scope-completion observation at the
            // existing dispatch-completion boundary; the runner retains output-artifact authority
            // and verification persistence while parsing stays with WorkerResultBlockers.
            // Goal 0d92976e adds the BeforeRetryAdmission checkpoint phase to preserve
            // current-tick evidence before retry admission replaces the goal snapshot; 2669 is
            // the measured post-change size of the existing dispatch-state contract.
            // Raised for goal df9ddb05: the Claude credential source the dispatch preflight selected must
            // be transported from the dispatch record at the single process-start boundary that already
            // owns the dispatch parameters; selection, validation, and seeding remain in
            // ClaudeCredentialSource. 2682 is the measured size of that call site.
            // Raised by one line for goal bb2d2d5a: the completion state carries the typed
            // HumanWaitKind the worker's human-input directive declared, so the kind the planner
            // raised survives to the recording boundary that decides whether an answer is
            // prerequisite evidence. There is nothing to extract - it is a single named argument on
            // the existing DispatchProcessCompletionState construction, and moving the construction
            // itself would split the completion contract the runner owns.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs", 2683),
            // Goal 5a75fed0 extracted typed projection parsing and literal restoration into
            // WorkerContextProjectionResidual, leaving the dispatcher to sequence package assembly.
            // Goal fd252fe4 adds retry fingerprints for the resolved provider, model, paid route, and
            // repository identities; 3229 is the measured combined post-rebase size.
            // Goal 52049d08 adds profile-aware policy selection, immutable receipt reuse, and compact
            // retry-projection wiring at this package-assembly boundary; each behavior remains extracted.
            // Goal df9ddb05 LOWERED this from 3234: ClaudeCliAuthState and ClaudeCliAuthProbe moved out
            // to ClaudeCredentialSource.cs so the Claude credential selection rule and both consumers'
            // views of it live in one file and cannot drift apart again. The dispatcher keeps only the
            // preflight finding call site. 3200 is the measured post-extraction size, and 3241 is that
            // size plus the preflight-to-dispatch handoff at both preparation paths: one shared auth probe
            // per prepared task, its injection seam for fixture credential sources, and the reported
            // credential source carried out of preflight onto the dispatch record so the dispatch start
            // boundary transports it instead of resolving a second source. Selection, validation, and
            // seeding all remain in ClaudeCredentialSource.
            // Raised for goal 7fb91813 by 14 lines: the Claude reasoning-effort vocabulary, the refusal and
            // warning text, the invocation-time stale-built-in repair, and the decision of whether an
            // invocation materializes a configured effort all live in ClaudeCliEffortPolicy, beside the
            // template segment they govern, so a finding cannot drift from the command actually built. The
            // dispatcher keeps only what it alone holds: the preflight finding call site (the profile,
            // resolved provider kind, and configured effort meet nowhere else), the refusal code's row in
            // the preflight error-code priority order, and the one-line resolved-profile call plus its
            // diagnostic sink at the single command-construction boundary. 3255 is the measured size of
            // those call sites after that extraction.
            // Raised by one line for goal bb2d2d5a: when the prerequisite-evidence section was trimmed to fit
            // the prompt budget, the trimmed request ids must be recorded as a TaskNote against the task that
            // was actually dispatched. Detection, id selection, note text, and idempotence all live in
            // PrerequisiteEvidenceTrimNote; the dispatcher keeps only the call site, because this is the one
            // point that holds the assembled brief, the kernel, and the dispatched goal/task identity
            // together after the dispatch record is written. The CLI worker-dispatch path is a separate
            // entry point and carries its own call site. 3256 is the measured size with that call site.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs", 3256),
            // Raised for goal fd252fe4: the single typed retry-cause argument belongs at the durable CLI
            // command-application boundary; classification and admission behavior remain elsewhere.
            // Goal 17d96426 classifies the stateless Hermes trial beside the existing stateless commands.
            // Reconciled after integrating main e1f6f11c at its measured post-merge size.
            // Goal 9fe7200e journals LOOP_READY at the existing pre-loop orchestration boundary;
            // 4855 is the measured post-merge size after that required readiness signal was wired.
            // Goal 1592104a extracted evidence intent submission, status output, and metadata flag parsing.
            // Goal 5daaa1db passes the operation-owned cleanup context through CLI acceptance and
            // terminal sweeps; policy and cadence remain in WorktreeCleanupContext, not this runner.
            // Raised eight lines for metadata-only goals and terminal-sweep caller ownership;
            // five further lines preserve conduct-loop and global-reconcile scheduler cadence.
            // The operation owner threads through this runner's own call sites.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs", 4863),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs", 1566),
            // Goal 682f25a1 re-derived this row after integrating goal c2eae988, whose fault-isolation
            // eligibility coverage had already added 173 net lines before the multi-file ratchet landed.
            // Goal ce3c3917 adds the two MakeDriver forwarding seams required to inject and capture the
            // inline-landing lease lifecycle without changing unrelated driver tests.
            // Raised for goal 13630c9f: the shared driver fixture forwards the apparatus-hold release
            // collaborators used by the focused lifecycle controls.
            // Raised for goal d5fcf981: the shared fixture forwards only the landing-file-scope seam.
            // Raised for goal 52590d3e: the shared driver fixture forwards the typed suppression recorder
            // so tests can prove no focused or paid downstream execution starts.
            // Reconciled after integrating main e1f6f11c at the measured shared-fixture size.
            // Raised for goal 8263a08c: the shared fixture forwards the exited-dispatch reconciler used by
            // the focused failure-state and dispatch-start invariant tests.
            // Lowered after extracting admission-refusal regressions and their fixture to
            // ConductorDriverTestsAdmissionRefusal; the original assertions and collection are preserved.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs", 1508),
            // Goal ce3c3917 keeps acquired, unavailable, and exceptional lease-lifecycle coverage together
            // in the acceptance-coordination owner rather than splitting one behavioral contract.
            // Raised for goal 13630c9f: acceptance coordination owns the structured apparatus cause
            // mapping and rejects mixed candidate-owned failure routing.
            // Reconciled after main 0d44d054 added the 27-line no-eager-journal-read regression; this is
            // the measured combined test-owner size, not additional goal coverage.
            // Reconciled after integrating main e1f6f11c at its measured post-merge test-owner size.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsAcceptanceCoordination.cs", 1279),
            // Raised for goal 13630c9f: lifecycle coverage keeps unchanged-candidate holds, bounded
            // operator release, and main/candidate HEAD regating in one state-transition decision table.
            // Raised for goal d5fcf981: acceptance attribution controls prove worker retry versus held
            // lifecycle transitions, including canonical identities produced from a TRX receipt.
            // Goal fc109f3f keeps both apparatus classifications in the five-role lifecycle decision table
            // that proves unchanged candidates preserve all completed worker evidence during re-gating.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsLifecycleStates.cs", 2002),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsReviewRetryConvergence.cs", 778),
            // Raised for goal 4e3cdbb8: behavioral coverage now pins every permanent and transient
            // finding-evidence refusal disposition plus receipt-id priority on the next-round request loop.
            // Raised for goal 52590d3e: the finding-evidence owner now carries both recorded incident shapes,
            // invalid-normalization suppression, ownership separation, and new-SHA/resolution controls.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidence.cs", 2713),
            // Reconciled after integrating main e1f6f11c at its measured post-merge test-owner size.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsPreReviewEvidence.cs", 1299),
            // Reconciled after integrating main e1f6f11c at its measured post-merge test-owner size.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsContractRepairBounds.cs", 1190),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsDispatchRecovery.cs", 537),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommands.cs", 1489),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsAcceptance.cs", 697),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsConductLoopHydration.cs", 817),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsDispatchRecovery.cs", 455),
            // Raised for goal 8b6de491: the slice-batch preview test needs an available Developer agent in
            // its roster now that creating a batch requires one, and the roster is declared inline.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement.cs", 2780),
            // Reconciled after integrating main e1f6f11c at its measured post-merge CLI test-owner size.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsGoalQueriesAndLanding.cs", 983),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsStartupAndMetadata.cs", 275),
            // Raised for goals fd252fe4 and b8dde431: worker-result fixtures supply the required typed provider-
            // interruption cause and PID-identity evidence; owning-suite assertions preserve coverage.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs", 4843),
            // Goal 46ff9f83 adds unequal logical-width/build-permit controls and the typed maximum diagnostic.
            // Goal 289b469d adds the per-stage registration-fault decision table, bounded-cap proof, durable
            // fault/verdict state distinction, and paid-round negative controls owned by this existing class.
            // Raised for goal cdde61cc: diagnostic round-trip, bounded retry, and candidate-failure
            // negative controls jointly verify the existing parallel-acceptance state contract.
            // Raised for the acceptance-popup suppression repair: the outer gate launch contract now
            // pins the descendant MSBuild-server environment alongside the native GUI suppression seam.
            // Raised by two lines for goal 13630c9f: parallel acceptance controls prove apparatus-only holds
            // do not reopen completed worker evidence while mixed candidate failures retain normal routing.
            // Goal fc109f3f extends the cross-tick acceptance control to the production five-role pipeline,
            // proving a typed apparatus re-gate preserves every completed task rather than only Developer.
            // Reconciled after main 4ba0dba0 added the reviewed target-boundary cancellation regression;
            // 5188 is the measured combined post-rebase size.
            // Reconciled after integrating main e1f6f11c at its measured post-merge test-owner size.
            // Goal 5daaa1db replaces ambient build-root mutation with explicit child-launch ownership
            // and disposes the owned root; the real-process transport assertions remain in this fixture.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptance.cs", 5284),
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
