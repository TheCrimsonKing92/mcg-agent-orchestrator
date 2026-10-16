using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SourceSizeRatchet
{
    internal const string DocumentationPath = "docs/god-class-decomposition-plan.md";
    internal const string DocumentationSectionHeading = "## Guarded source size ratchet";
    internal const string SourcePath = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SourceSizeRatchet.cs";

    // Seeded at 6e7a90d7b3fe192ae4f1e430cb7452710f88b73c using File.ReadLines(path).Count().
    // Goals do not lower rows or add measured-size comments; integration lowers rows to the tree's size before the gate.
    // Edit a row only to raise it, with goal-specific justification directly above it. Never raise ceilings
    // automatically or add an opt-out. A rename or removal updates the authority in the same change.
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
            // Goal 3ca963ea extracted candidate rerun execution to the verifier's partial collaborator.
            // Goal 6c6778ce extracted test-tamper analysis into TestTamperAnalysis.
            // Goal 29c53339 extracted remaining dotnet and MTP argument construction into AcceptanceCheckCommandBuilder.
            // Goal d2ed62af extracted focused-evidence execution into its own type.
            // Goal c0cfa8ae extracted 445 lines of test telemetry and TRX receipt inspection.
            // Goal efdff7b2 extracted 268 lines of build-artifact lock waiting and attribution.
            // Goal 78053cf4 extracted 313 lines of capture custody and process start-info construction.
            // Goal 28ea80c3 extracted 540 lines of acceptance process running.
            // Goal 1c226573 extracted build-phase planning, execution and cache receipts into AcceptanceDotnetBuildPhase.
            // Goal 9797ec59 extracted failure-cause receipt, codec and adjudication into AcceptanceFailureCauseAdjudicator.
            // Goal 402c1005 extracted shard completion adjudication into AcceptanceShardCompletionAdjudicator.
            // Goal 936b5a43 extracted structural coverage inputs into AcceptanceStructuralCoverageInputs.
            // Goal 3b52938f extracted heartbeat context, heartbeat runtime and capture-limit stop into top-level types.
            // Goal 982098a9 moves build failure results and cache identity lifetime to AcceptanceDotnetBuildPhase; measured 4969 lines.
            // Goal b7ad094e adds four call-site lines binding cleanup attribution to the selected verdict's
            // candidate/attempt before cache publication; inspection stays in AcceptanceAssemblyCleanupAttributionReader.
            // Goal 56ff780e adds three lines at the existing shard-progress seam to carry the result,
            // outcome and failed-test count to the owner gate view; measured 4976 lines, no extra headroom.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", 4976),
            // Goal c0cfa8ae test telemetry collaborator measured at 423 lines.
            // Goal 402c1005 moved TRX completion inspection and shard decisions into AcceptanceShardCompletionAdjudicator.
            // Goal ad3fafc0 extracted run ownership publication and stale-receipt clearing into AcceptanceTestRunPreparation.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.TestTelemetry.cs", 247),
            // Goal efdff7b2 build-artifact lock collaborator measured at 334 lines.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.BuildArtifactLock.cs", 334),
            // Goal 78053cf4 capture custody collaborator measured at 378 lines.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.CaptureCustody.cs", 378),
            // Goal c60a7cb5 split focused-evidence request resolution into its own type.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.FocusedEvidenceRequestResolver.cs", 696),
            // Goal 40cc558d keeps direct SDK selection, bounded missing-executable fallback, and pipe recustody at the capture lifecycle boundary; measured candidate is 635 lines.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.ProcessRunner.cs", 198),
            // Goal e3bb2a77 extracted acceptance child invocation into its own owner.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCheckProcessInvoker.cs", 555),
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
            // Lowered for goal 5d57fe95: clean-baseline attention subject formatting moved to CleanTestBaseline,
            // which already owns receipt wire values and journal/attestation text; the driver keeps only the
            // correlation-key lifecycle and passes its own short-sha rendering to that formatter.
            // Goal 42115646 extracted Failed-lifecycle selection to a deterministic Core policy; the
            // driver retains attributed observation, stale-authority revalidation, and effect application.
            // Review correction moved rung-8 candidate, route, target, cap, and retry-cause selection
            // behind the policy; the split effect owner is pinned at its measured 369-line size.
            // Raised again for goal cf64014b, integrated by operator squash on 2026-09-22: goal-evidence
            // lease recovery is admitted at the existing acceptance ownership seam, and classification and
            // journal parsing remain extracted. Ceiling re-measured at the integrated head.
            // Raised deliberately by six for goal e46c3d92: the only addition is the candidate-currency
            // guard that defers to VerifyingFindingCurrency.IsCurrent, whose logic lives in the model.
            // Goal 3ca963ea moved attribution exclusion types and formatting to the apparatus-red partial.
            // Goal 98952a12 (backlog c35852c4) moved finding-evidence request construction unchanged to its partial.
            // Goal db18d0d0 (backlog aedc7a0a) moved acceptance-cohort execution unchanged to its partial.
            // Goal 4fac1b84 (backlog 28b37515) moved landing completion and acceptance-retry evidence unchanged to its partial.
            // Goal 868fd04a (backlog c758b2c3) moved review-contract and verifying-finding recovery unchanged to its partial.
            // Goal 6f38e132 (backlog 6d658f46) moved pre-review evidence unchanged to its partial.
            // Goal 0079c3a4 moved dispatch-start effects into DispatchStartExecutor.
            // Goal 47f09f71 adds three lines to pass workspace project-home context at the existing
            // preflight and acceptance orchestration seams; manifest resolution stays in AcceptanceManifestLocator.
            // Goal b6abf7a1 extracts dispatch checkpoint sequencing and hold lifetime to
            // CriticalDispatchLifecycleCheckpoint; goal 69c9d936 then threads the workspace integration
            // branch through it (one field and its wiring). Measured 1789 lines after both; no extra headroom.
            // Goal f62c7f67 extracts owned gate/evidence coordinator construction into
            // OwnedChildAttemptCoordinatorFactory; measured 1783 lines, no extra headroom.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs", 1735),
            // Goal 0e30d5e9 extracted impact-plan mapping to PreReviewEvidenceContextBuilder.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.PreReviewEvidence.cs", 467),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.ReviewRecovery.cs", 439),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.LandingCompletion.cs", 645),
            // Goal eade84cb preserves published cohort receipts while holder cleanup still reserves capacity.
            // Custody remains separately owned; after integrating main f34678bcb, measured 645 lines, with no extra headroom.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.AcceptanceCohortExecution.cs", 645),
            // Goal 5a07b0d6 retains three orchestration lines carrying reattached receipt ids to the
            // closure-first route; the reused-green branch owns this delivery handoff, not receipt selection.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.FindingEvidenceRequests.cs", 754),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.FailedGoalRecovery.cs", 346),
            // Goal 4ff31b70 (ConductorDriver size relief) moved parallel acceptance members unchanged to this partial.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.ParallelAcceptance.cs", 627), // def5cd48: planner conflict probe and existing-policy escalation.
            // Goal 4ff31b70 (ConductorDriver size relief) moved dispatch diagnostics unchanged to this partial.
            // Goal 0079c3a4 moved dispatch-start effects into DispatchStartExecutor.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.DispatchDiagnostics.cs", 378),
            // Goal 4ff31b70 (ConductorDriver size relief) moved git and lease helpers unchanged to this partial.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.GitAndLeaseHelpers.cs", 130),
            // Goal 4ff31b70 (ConductorDriver size relief) moved pre-review evidence routing unchanged to this partial.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.PreReviewEvidenceRouting.cs", 184),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Core/Application/FailedGoalRecoveryPolicy.cs", 748),
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
            // Goal 0285f012 attributes all nine pre-tick sweep operations without changing their order.
            // Goal 5a8a1fc8 adds only max-duration deferral state and drain-only admission call sites;
            // the decision, snapshot mapping, and event formatting remain in the dedicated collaborator.
            // Goal fe962d2e extracted hold tracking unchanged to ConductorBatchLoop.HoldTracking.cs,
            // keeping owned-hold suppression, observation, and clearing together in its partial collaborator.
            // Goal 354522f1 extracted tick persistence unchanged; measured at 4716 lines.
            // Goal dcb10a1c extracted dependency completion, set-aside readmission and unscoped reconciliation; measured at 4192 lines.
            // Goal 0b944cc5 extracted parallel-acceptance fairness state and helpers; measured at 4049 lines.
            // Goal af61f4a6 (backlog 964a1df7) moved terminal reconciliation and run completion unchanged; measured at 3592 lines.
            // Goal 6e664238 (backlog c7f9845c) moved conduct-event classification and progress formatting unchanged; measured at 3338 lines.
            // Goal 8c677dd2 (backlog 21450794) split RunParallelAcceptanceBatch into named admission phases; measured at 2686 lines.
            // Goal ae7d1201 extracted shared per-goal operator-intent application; measured at 2675 lines.
            // Goal 7b54219d adds nine startup-wiring lines: the injectable load seam and its invocation
            // belong before LOOP_START; hash-keyed persistence remains in TrxCoherenceVerdictStore.
            // Goal 8cc45279 extracts live-gate drain hold policy and polling to ConductorSelfRelaunchDrainHold; measured 2694.
            // Goal f8d371e2 adds the tick-local live suppression closure and admission checks where
            // pendingSelfRelaunch is owned; hold formatting remains separately owned. Measured 2707, no headroom.
            // Goal 920a6429 adds one serial-walk binding to the separately owned GoalWalkOrder;
            // acceptance admission keeps the original eligible list. Goal eade84cb wires acceptance-holder
            // detach before loop exit at the existing dispatch boundary; holder transfer stays in its coordinator.
            // After integrating main b8da8f673, measured 2710 lines; no extra headroom.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", 2710),
            // Goal 354522f1 tick persistence partial measured at 467 lines.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.TickPersistence.cs", 467),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.DependencyReadmission.cs", 375),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.ParallelAcceptanceFairness.cs", 150),
            // Goal 33b9b7a6 extracts reconciled-pass application to PassedAcceptanceReceiptReplay.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.ParallelAcceptanceCompletion.cs", 462),
            // Goal 6e664238 conduct-event partial measured at 260 lines.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.ConductEvents.cs", 250),
            // Goal f8d371e2 threads live suppression through the existing admission phases and rechecks
            // it between inline solo completions, preserving in-flight gates. Measured 804, no headroom.
            // Goal 33b9b7a6 extracts receipt replay admission and progress to PassedAcceptanceReceiptReplay.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.ParallelAcceptanceAdmission.cs", 803), // def5cd48: conflict escalation before width admission.
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
            // Goal bb2d2d5a added the typed HumanWaitKind, and goal be5e06b0 adds the typed
            // EvidenceOwner, so both parts of the planner's evidence obligation survive to the
            // recording boundary. Each increase is one named argument on the existing
            // DispatchProcessCompletionState construction; moving that construction would split
            // the completion contract the runner owns rather than extract independent behavior.
            // 2684 is the measured post-change size of that contract-preserving handoff.
            // Goal cd39be97 raised this row for the bounded runtime-ownership handoff. The runner must
            // fail and checkpoint before releasing the worker start gate when any owned process cannot
            // detach. 2707 is the measured size after integrating that handoff with the rows above; the
            // branch measured 2678 against a main that has since grown this file to 2684.
            // Goal 5077a6b8 extracted process reaping for the typed operator cancel path.
            // Goal 207369f1 keeps pending completion, candidate revalidation and cancellation at the dispatch seam;
            // build lifecycle/lease/process behavior lives in CompletionBuildEvidenceCoordinator. Measured 2739; no headroom.
            // Goal c5b71650 integrates terminal-contract enforcement and extracted apparatus holds; measured 2719.
            // Goal 1d995e6d adds exact-preparation validation, admission-reference handoff and suppression
            // reconciliation at the runner-owned start/completion boundaries. Store admission and receipt
            // reading remain separately owned; moving these call sites would split dispatch lifecycle ownership.
            // Goal ffa0471e adds orphan completion-build cancellation routing before normal worker cancellation;
            // operation lifecycle/custody stays in CompletionBuildEvidenceCoordinator. Measured 2728; no headroom.
            // Goal ffa0471e extracts historical/current completion-build policy to its owning collaborator.
            // Integrated build-scope extraction and task-intent admission: measured 2736, no headroom.
            // Goal e7c4bc06 adds Tester submission ingestion: measured 2743, no headroom.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Execution/Processes/BackgroundDispatchRunner.cs", 2743),
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
            // Lowered for goal 29423867: typed brief selection now crosses the dispatcher boundary without
            // marker stripping or header/current text re-parsing; those responsibilities moved to the typed
            // renderer and versioned legacy ingress. 3037 is the measured post-extraction size.
            // Raised two lines for goal 31fa5aec: the dispatcher adds the interrupted-work checkpoint
            // context source at its single call site; projection and metrics live in
            // InterruptedWorkCheckpointContextProjector. 3039 is the measured size with that call site.
            // Lowered for goal 3cf50b77 after provider-budget preflight finding construction moved to
            // WorkerSubscriptionPreflightFindings; 3000 includes the typed exhaustion error-code mapping.
            // Lowered for goal 754f3c5f after role and model selection moved to
            // WorkerSubscriptionModelResolver; 2605 is the measured post-extraction size.
            // Raised two lines for goal ed9b76cf: the dispatcher's context-artifact registry must declare
            // Developer/Tester and Planner standing-rule files as mandatory delivery sources. Rule text
            // and artifact rendering remain in WorkerStandingRules; 2607 is the measured integrated size.
            // Lowered for goal a67de0fa after rejected-Planner eligibility and authoritative output
            // projection moved to WorkerVerificationEvidence; 2605 is the measured post-extraction size.
            // Lowered for goal 81851438 after effective subscription alias selection and normalization
            // moved to WorkerSubscriptionModelResolver; 2603 is the measured integrated size, no headroom.
            // Raised one line for goal 139b8c97: finding-format failures must count as review history
            // at the existing context-artifact selection seam so bounded repairs receive rejected findings.
            // Finding projection remains in ReviewFindingContextProjector; 2606 is the measured size.
            // Goal 6e82a00d extracts build-scope preparation to WorkerBuildScopeDispatchPreparation and
            // integration-ref lookup to WorkerGitContext. Measured 2596 after extraction; no extra headroom.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Execution/Workers/WorkerProfileDispatcher.cs", 2596),
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
            // Goal 798f1c58 adds only adjudicate routing and typed actor-kind attribution call sites;
            // payload parsing and version lookup live in CliCommandHandlers.Adjudicate.
            // Raised by five lines for goal 10abca19: scoped CLI writes are extracted to
            // CliPersistentStateRunner.GoalScopedWrite, while the shared dispatch boundary
            // must retain the single route-selection call so existing fallback semantics stay centralized.
            // Goal 28397f6a adds the store-only epic command routes; retain the runner's
            // shared dispatch seam and account for its two-line net increase.
            // Goal ec8903af moved the goal-prefix parser to CliGoalPrefixArguments.
            // Goal cd1f6380 adds four transaction-boundary wiring lines for post-commit actions;
            // ordered registration/execution lives in the separate PostCommitActions type.
            // Both transaction branches must register here and run only after commit; no extra headroom.
            // Goal 09a3023d adds four entry-point lines to preserve the authenticated submission source
            // for outcome/compatibility mutations; parsing and application live in dedicated collaborators.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs", 4803),
            // Goal 03aaf13e adds the explicit-root isolation fact at the verifier's existing execution-
            // environment seam; the production verifier remains at its prior ceiling.
            // Goal 56ff780e keeps passed/failed/unknown shard-completion producer coverage in its owning
            // test class as required by criterion 3; measured 1643 lines, no extra headroom.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs", 1643),
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
            // Raised by two lines for goal 678fa66f: the shared fixture forwards only the apparatus-RED
            // gate seam so the re-gate facts can inject a census store; the facts themselves live in
            // ConductorDriverTestsApparatusRedRegate.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs", 1510),
            // Goal ce3c3917 keeps acquired, unavailable, and exceptional lease-lifecycle coverage together
            // in the acceptance-coordination owner rather than splitting one behavioral contract.
            // Raised for goal 13630c9f: acceptance coordination owns the structured apparatus cause
            // mapping and rejects mixed candidate-owned failure routing.
            // Reconciled after main 0d44d054 added the 27-line no-eager-journal-read regression; this is
            // the measured combined test-owner size, not additional goal coverage.
            // Reconciled after integrating main e1f6f11c at its measured post-merge test-owner size.
            // Raised by one line for goal 5d57fe95: the observed-correlation control must assert both that
            // origin stays Unattributed and that the apparatus hold still classifies, because the defect being
            // guarded is exactly the pair coming apart; splitting them would lose the contract they compare.
            // Reconciled again for goal cf64014b on 2026-09-22, which adds one lease-recovery assertion at
            // this acceptance seam; ceiling re-measured at the integrated head.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsAcceptanceCoordination.cs", 1242),
            // Raised for goal 13630c9f: lifecycle coverage keeps unchanged-candidate holds, bounded
            // operator release, and main/candidate HEAD regating in one state-transition decision table.
            // Raised for goal d5fcf981: acceptance attribution controls prove worker retry versus held
            // lifecycle transitions, including canonical identities produced from a TRX receipt.
            // Goal fc109f3f keeps both apparatus classifications in the five-role lifecycle decision table
            // that proves unchanged candidates preserve all completed worker evidence during re-gating.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsLifecycleStates.cs", 1942),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsReviewRetryConvergence.cs", 777),
            // Raised for goal 4e3cdbb8: behavioral coverage now pins every permanent and transient
            // finding-evidence refusal disposition plus receipt-id priority on the next-round request loop.
            // Raised for goal 52590d3e: the finding-evidence owner now carries both recorded incident shapes,
            // invalid-normalization suppression, ownership separation, and new-SHA/resolution controls.
            // Goal aab291fd extracted manifest-declared project resolution controls to their own partial.
            // Raised from 2658 to 2675 for goal 5a07b0d6: the updated Tester receipt integration test
            // keeps Developer routing, NewSourceFinding, zero delivery retries, and finding-bound
            // brief evidence assertions together; extracting only the added assertions splits that contract.
            // Goal 01ea3f4d extracts the passing-Tester receipt integration contract and its brief helper
            // into ConductorDriverTestsPassingFindingEvidence; the remaining file measures 2559 lines.
            // Goal 046ea7d3 separates focused-evidence planning and apparatus fixtures.
            // Integrated finding-evidence file measures 2490 lines; no extra headroom.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidence.cs", 2490),
            // Goal 1a43cb08 adds clause-boundary, broker-validation, and split-coverage regression facts.
            // Goal d62efe7a freezes the 36-class premise list so unrelated test inventory cannot change the split test.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsPreReviewEvidence.cs", 1498),
            // Goal 42115646 pins the migrated rung-8 escalation warning at the real Driver note-effect seam.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsContractRepairBounds.cs", 1220),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsDispatchRecovery.cs", 536),
            // Goal 42115646 pins pure-policy precedence/equality separately from the Driver effect seam.
            // Review correction adds the rung-8 phase counterfactual, walks policy-owned helper IL
            // transitively, and pins the policy/interpreter owners at measured post-review sizes.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Core.Tests/FailedGoalRecoveryPolicyTests.cs", 384),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Core.Tests/FailedGoalRecoveryPolicyBoundaryTests.cs", 114),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FailedGoalRecoveryExtractionTests.cs", 59),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFailedGoalRecoveryInterpreter.cs", 192),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommands.cs", 1488),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsAcceptance.cs", 695),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsConductLoopHydration.cs", 801),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsDispatchRecovery.cs", 417),
            // Raised for goal 8b6de491: the slice-batch preview test needs an available Developer agent in
            // its roster now that creating a batch requires one, and the roster is declared inline.
            // Raised for goal 152bd7c9: source-claim replacement regressions add 7 lines of assertions;
            // the broader intake/replacement suite remains scheduled for decomposition.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement.cs", 2748),
            // Raised for goal a2289b27: the strict-checkpoint fixture explicitly names its initial
            // branch main before provisioning a goal worktree, so it works with non-main Git defaults.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsGoalQueriesAndLanding.cs", 983),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTestsPersistentRunnerCommandsStartupAndMetadata.cs", 274),
            // Raised for goals fd252fe4 and b8dde431: worker-result fixtures supply the required typed provider-
            // interruption cause and PID-identity evidence; owning-suite assertions preserve coverage.
            // Raised for goal 2dd0ece6: the real dispatch leak guard tests exercise the launched-host
            // ownership path without splitting this already consolidated classification fixture.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs", 4824),
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
            // Goal 2c245bc3 waits for the primer holder's capacity release before asserting next-slot admission.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptance.cs", 5285),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTests.cs", 1357),
        });

    // Reseeded at main 584b86d13 using complete File.ReadLines counts for files declaring each partial class.
    // Goals do not lower rows or add measured-size comments; integration lowers rows to the tree's size before the gate.
    // Raise only with goal-specific justification directly above; renames/removals update the authority in the same change.
    internal static IReadOnlyList<SourceClassCeiling> SeededClassCeilings { get; } = Array.AsReadOnly(
        new[]
        {
            // Goal b67746ef adds 24 lines to return recorded cohort/train verdicts and preserve failed
            // gate-only train receipts; goal d6033045 extracted passed-train receipt selection to
            // PassedMergeTrainReceiptSelector. Measured 15271 after integrating both; no extra headroom.
            // Goal 1f4782de extracted train RED classification into MergeTrainRedAttribution; goal def5cd48 adds
            // the planner conflict probe and its wiring. Measured 15297 across the same 70 partial files after integrating both.
            // Goal 47f09f71 adds nine routing lines across the same partial files so preflight, cohort,
            // focused evidence and acceptance share the workspace manifest; no resolver behavior enters this class.
            // Goal 5a07b0d6 extracts receipt qualification and closure selection into FindingEvidenceReceiptSelector.
            // Measured 15290 lines across the existing 70 partial files; no extra headroom.
            // Goal 01ea3f4d extracts passing-evidence routing and re-raise identity into
            // PassingFindingEvidenceRouter; measured 15271 lines across the same 70 partial files.
            // Goal b6abf7a1 extracts dispatch checkpoint sequencing to CriticalDispatchLifecycleCheckpoint;
            // goal 69c9d936 passes the configured integration branch through existing collaborators.
            // Measured 15256 lines across the same 70 partial files after both; no extra headroom.
            // Goal 8d620666 adds bounded partition/train slot admission and partition deferral propagation.
            // Measured 15270 lines across the same 70 partial files; no extra headroom.
            // Goal f62c7f67 extracts owned coordinator construction to a separately owned factory.
            // Measured 15266 lines across the same 70 partial files; no extra headroom.
            // Goal 87740668 adds three lines of member-objective wiring at the train gate options boundary;
            // removal parsing and union logic live in MemberDeclaredTestRemovals. Measured 15269 across
            // the same 70 partial files; no extra headroom.
            // Goal 3068f4ce adds nine lines of ownership-first impact fallback wiring and attribution-source
            // propagation through the existing bisection/event lifecycle; impact analysis remains separately
            // owned by MergeTrainImpactAttribution. Measured 15278 across the same 70 partial files; no extra headroom.
            // Goal 943427e4 extracts cohort verification classification into AcceptanceCohortVerificationClassifier.
            // Measured 15205 lines across the same 70 partial files; no extra headroom.
            // Goal 33b9b7a6 extracts receipt application to a collaborator; replay custody uses the existing landing partial.
            // Goal 33b9b7a6 extracts Git ancestry/path evidence into AcceptanceMainAdvanceGitEvidence.
            // Goal 33b9b7a6 extracts bounded carry receipt formatting into AcceptanceMainAdvanceReceipt.
            // Goal 33b9b7a6 also extracts revalidation hold-identity formatting into that receipt owner.
            // Measured 14962 lines across the same 70 partial files; no extra headroom.
            // Goal eade84cb adds holder-detach wiring and preserves cohort/train receipts during cleanup.
            // Custody lives in AcceptanceHolderLease.
            // Goal ffa0471e wires attached-operation reconciliation when current worker metadata is absent;
            // lifecycle remains in CompletionBuildEvidenceCoordinator. Measured 14970 in the same 70 partials; no headroom.
            // Goal 6a4ec362 moves exact-pair/context validation to AcceptanceCriterionEvidence before
            // recording rebound gate evidence. Measured 14968 across the same 70 partials; no headroom.
            // Goal 046ea7d3 extracts executed-member disposition finalization into FindingEvidenceRequestCompletion.
            // After integrating main 790560adf, measured 14876 lines across the same 70 partial files; no extra headroom.
            // Goal 046ea7d3 extracts filter validation and class expansion into FindingEvidenceFilterSyntax.
            // After integrating main 79b67570c, measured 14807 lines across the same 70 partial files; no extra headroom.
            // Goal 8d040dde adds landing-completion handling in ConductorDriver.LandingCompletion; after integrating
            // main 3732befa8, measured 14823 lines across the same 70 partial files; no extra headroom.
            // Goal 4cad184f adds three lines of grouped-receipt RED handling; after integrating main fd7b91785,
            // measured 14826 lines across the same 70 partial files; no extra headroom.
            // Goal eade84cb adds acceptance-admission holder checks; after integrating main cb6e071fa,
            // measured 14839 lines across the same 70 partial files; no extra headroom.
            // Goal 2c245bc3 restores overlapping holders and derives a recoverable admission hold.
            // Measured 14884 lines across the same 70 partial files; no extra headroom.
            new SourceClassCeiling("ConductorDriver", 14884, 70),
            // Goal b7c2f833: epic-at-creation validation and post-commit inheritance call sites
            // require handler glue; resolution/assignment remain in the separate EpicAtCreation type.
            // Measured 12223 total lines across the existing 30 partial files after integrating main; no extra headroom.
            // Goal cb35d012 extracts epic list/show validation, reads and rendering into EpicProgressCommands;
            // goals 69c9d936 (+6 integration-branch call-site lines) and f1045cfb (+5 dogfood-log missing-file lines) add to it.
            // Measured 12228 total lines across the same 30 partial files after all three; no extra headroom.
            // Goal e8cccefa extracts loop store setup and policy resolution into CliConductLoopStartup.
            // Measured 12139 total lines across the same 30 partial files; no extra headroom.
            // Goal 5b573f47 keeps shared human/JSON status and label rules in the scoped diagnostic helpers;
            // read-only lifecycle reads replace the writable monitoring path without changing human status text.
            // Measured 12156 total lines across the same 30 partial files; no extra headroom.
            // Goal cd1f6380 adds five lifecycle call-site lines for early epic validation and
            // new-goal-only inheritance; resolution and assignment stay in EpicAtCreation.
            // These checks belong beside lifecycle creation/reuse; same 30 partial files, no extra headroom.
            // Goal 09a3023d extracts portfolio projection assembly into EpicProgressCommands.
            // Measured 12157 total lines across the same 30 partial files; no extra headroom.
            // Goal 1d995e6d passes the durable task admission reference at the existing manual-start call site;
            // reservation/version behavior remains in SqliteOperatorIntentStore. The two added wiring lines
            // keep manual dispatch on the same protected launch path.
            // Goal ffa0471e validates an exact completion-build selector and enqueues typed cancellation without
            // stopping a historical worker; lifecycle remains in the coordinator.
            // Integrated size is 12167 lines in the same 30 partials; no extra headroom.
            // Goal 109d0d1d adds the backlog-list JSON branch beside the existing claim/readiness
            // resolution so both routes share filtering and human rendering stays byte-identical.
            // Envelope serialization remains in BacklogListAgentReadView. Goal 920a6429 adds only the
            // two-line route to the separately owned CliGoalPriorityCommand; intent submission stays in
            // that type. Goal f3df9ed9 adds two wiring lines; program-health logic lives in
            // ConductorProgramHealthMonitor. After integrating main b8da8f673, measured 12194 lines across
            // the same 30 partial files, with no extra headroom.
            // Goal 6cf5a8ce routes the backlog-premise-check/close verbs to their separately owned command type.
            new SourceClassCeiling("CliCommandHandlers", 12195, 30),
            // Goal ae77554f extracts bounded remote offer history parsing into RemoteLaneOfferHistoryReader.
            // Measured 9053 total lines across the existing 32 partial files; no extra headroom.
            // Goal 03580ad1 extracts source-mutation edits to AcceptanceSourceMutation; measured 8938 lines in 31 partials.
            // Goal 140fb304 extracts hermetic git configuration to AcceptanceHermeticGitConfiguration; measured 8848 lines in 30 partials.
            // Goal 80b085c6 extracts owner-protected fingerprints to AcceptanceOwnerProtectedFingerprint; measured 8774 lines in 29 partials.
            // Goal c2c58d62 extracts launch-lock summary preservation to AcceptanceLaunchLockSummary; measured 8725 lines in 28 partials.
            // Goal b6d32304 extracts generated names to AcceptanceArtifactFileNames; measured 8685 lines in 28 partials.
            // Goal 982098a9 extracts build failure results into AcceptanceDotnetBuildPhase and cache identity lifetime
            // into DotnetBaseBuildCacheIdentityResolver; measured 8678 total lines in 28 partial files, no extra headroom.
            // Goal b7ad094e keeps the four cleanup-attribution binding lines at the verifier's verdict/cache
            // boundary; receipt inspection is separately owned. Measured 8682 lines in the same 28 partials, no headroom.
            // Goal 56ff780e adds only the three result/outcome/count lines at the existing shard-progress
            // seam; gate read-model behavior is separately owned. Measured 8685 lines in the same 28 partials.
            new SourceClassCeiling("GoalAcceptanceVerifier", 8685, 28),
            // Goal d647b5f3 extracts premise-invalid clarification construction to PremiseInvalidClarification.
            // Goal 439c80ae extracts Reviewer evidence brief projection to ReviewerEvidenceBriefSection;
            // measured 9319 total lines across the existing 27 partial files, with no extra headroom.
            // Goal 776d3dc0 extracts changed-file and conflict preview emission and typed coordinates
            // into ReviewerChangedFileScopeBriefSection. Measured 9246 lines across the same 27 partial files.
            // Goal 139b8c97 extracts authoritative finding context attachment to ReviewFindingDispatchContext.
            // Measured 9244 lines across the same 27 partial files; no extra headroom.
            // Goal 139b8c97 adds one line to pass rejected-advisory diagnostics into existing coverage validation;
            // parsing and coverage policy remain separately owned. Measured 9245 lines in the same 27 partials; no headroom.
            // Goal 207369f1 adds goal-validated completion-evidence recording and pending-aware refresh/cancel routing;
            // operation state belongs to TaskSpec. Measured 9254 across the same 27 partial files; no extra headroom.
            // Goal c5b71650 integrates terminal contracts with advisory isolation using the shared dispatch-context owner.
            // Measured 9266 lines across 27 partial files; no extra headroom.
            // Goal 1d995e6d adds the UnstartedDispatch lifecycle partial: validate the current attempt identity,
            // restore task/retry admission and refresh goal status when a paid child never started. These
            // transitions require the kernel-owned clock, goal refresh and progress append; TaskSpec owns
            // retry admission accounting and Execution owns persisted denial evidence. Keeping the transaction
            // here preserves lifecycle ownership. Measured 9305 lines in 28 partials; no extra headroom.
            // Goal 920a6429 keeps priority mutation and its audit append at the owning kernel lifecycle seam;
            // parsing and ordering live in separate types. Goal c55f943c adds one Reviewer binding call before
            // finding deferral. After integrating main b8da8f673, measured 9309 lines in the same 28 partials; no headroom.
            new SourceClassCeiling("AgentOrchestratorKernel", 9309, 28),
            // Goal d6033045 extracted the pre-admission receipt pass to PassedMergeTrainReceiptAdmission; goal 1d0f2317
            // adds one admission predicate to exclude stream-complete children before slot reservation. Measured 9255.
            // Goal 679eee57 added a one-line tick call to the separately owned ConductorExperimentWatch (+1); goal 80de5cb2 extracted
            // dependency hold evaluation to its own type (-42). Measured 9231 across 56 files after both, no extra headroom.
            // Goal 7b54219d retains only the nine startup-wiring lines needed to load before LOOP_START;
            // persistence is separately owned. Measured 9267 total lines across the same 56 partial files.
            // Goal 8cc45279 extracts drain hold policy and polling to a separately owned type; measured 9263 across the same 56 partials.
            // Goal 943427e4 extracts cohort exit formatting into AcceptanceCohortExitDiagnostic.
            // Measured 9246 lines across 55 partial files; no extra headroom.
            // Goal f8d371e2 keeps pending-relaunch observation and once-per-tick emission at the owning
            // loop seam, with live checks at each admission phase; formatter stays in its own static type.
            // Measured 9265 across the same 55 partial files; no extra headroom or drain changes.
            // Goal 33b9b7a6 extracts replay admission, completion and reconciliation into PassedAcceptanceReceiptReplay.
            // Goal 207369f1 counts pending completion builds in existing relaunch draining and prevents detaching them;
            // lifecycle remains in CompletionBuildEvidenceCoordinator. Measured 9270 across the same 55 partials; no headroom.
            // Integrated receipt replay and completion-build draining: measured 9239 across 54 partial files, no headroom.
            // Goal 920a6429 adds one binding to GoalWalkOrder for the serial walk only;
            // sorting lives in that collaborator. Goal eade84cb adds acceptance-holder detach wiring before
            // checkpoint/exit; the coordinator owns transfer. After integrating main b8da8f673, measured 9242 lines
            // in the same 54 partials; no headroom.
            new SourceClassCeiling("ConductorBatchLoop", 9242, 54),
            // Goal ec8903af moved the goal-prefix parser to CliGoalPrefixArguments.
            // Goal cd1f6380 adds four lines at the owning transaction boundary, forwarding both
            // registrars and invoking actions after commit; list behavior lives in PostCommitActions.
            // Same 18 partial files; no extra headroom beyond the required transaction wiring.
            // Goal 09a3023d extracts transport attribution construction into CliOperatorIntentAttributionResolver.
            // Measured 5964 total lines across the same 18 partial files; no extra headroom.
            // Goal ddab9e7f adds three lines to forward the startup loop lease and run
            // WorkspaceStoreSetup under that ownership; migration planning stays in WorkspaceStoreSetup.
            // Goal 920a6429 routes priority submission without a kernel-save transaction, preventing stale writes;
            // the command owns intent submission. After integrating main, measured 5972 lines in the same 18 partials; no headroom.
            new SourceClassCeiling("CliPersistentStateRunner", 5972, 18),
            // Goal 5935c270 extracts subscription-plan output into SubscriptionPlanTextView
            // (2026-10-09 console architecture review, finding 4, taming slice 13).
            // Goal c0ff6203 extracts six small text views (taming slice 14).
            // Goal 1a8bb30d extracts portfolio output (slice 11): measured 3102 lines in 38 partials; no headroom.
            // Goal c860b9ff extracts verification and acceptance command advice (slice 12, first family): measured 3047 lines in 38 partials; no headroom.
            // Goal ed7254a6 extracts next-action command advice (slice 12, family 2): measured 2930 lines in 38 partials; no headroom.
            // Goal b50fdb00 extracts environment health inspection (slice 13 step 2): measured 2865 lines in 38 partials; no headroom.
            // Goal f968e488 extracts supervisor and acceptance queue output (slice 14): measured 2804 lines in 36 partials; no headroom.
            // Goal 85702c95 extracts gate-status rendering to GateStatusTextView; measured 2767 lines in 36 partials, no headroom.
            // Goal 7ba60507 extracts process-log presentation to ProcessLogsTextView; measured 2727 lines in 35 partials, no headroom.
            // Goal 920a6429 adds conditional priority text at the existing board and detail rendering seams;
            // Normal output stays identical. Measured 2730 lines in the same 35 partials; no headroom.
            new SourceClassCeiling("ConsoleViews", 2730, 35),
        });

    internal static IReadOnlyList<SourceSizeViolation> EvaluateClasses(
        string repositoryRoot,
        IEnumerable<SourceClassCeiling> classCeilings,
        Func<string, IEnumerable<string>>? readLines = null,
        Func<string, IEnumerable<string>>? enumerateSourceFiles = null)
    {
        var ceilings = classCeilings.ToArray();
        if (ceilings.Length == 0)
        {
            return [];
        }

        var measurements = MeasureClasses(repositoryRoot, ceilings.Select(ceiling => ceiling.ClassName),
            out var violations, readLines, enumerateSourceFiles);
        foreach (var ceiling in ceilings)
        {
            var (total, count) = measurements.GetValueOrDefault(ceiling.ClassName);
            if (total > ceiling.MaximumTotalLineCount)
            {
                violations.Add(new SourceSizeViolation(
                    $"class:{ceiling.ClassName}", total, ceiling.MaximumTotalLineCount,
                    BuildClassOverCeilingMessage(ceiling, total, ceiling.MaximumTotalLineCount, "class total-line")));
            }

            if (count > ceiling.MaximumPartialFileCount)
            {
                violations.Add(new SourceSizeViolation(
                    $"class:{ceiling.ClassName}", count, ceiling.MaximumPartialFileCount,
                    BuildClassOverCeilingMessage(ceiling, count, ceiling.MaximumPartialFileCount, "partial-file-count")));
            }
        }

        return violations;
    }

    internal static IReadOnlyDictionary<string, (int TotalLines, int PartialFiles)> MeasureClasses(
        string repositoryRoot, IEnumerable<string> classNames, out List<SourceSizeViolation> violations,
        Func<string, IEnumerable<string>>? readLines = null,
        Func<string, IEnumerable<string>>? enumerateSourceFiles = null,
        IDictionary<string, List<string>>? partialFiles = null)
    {
        var names = classNames.Distinct(StringComparer.Ordinal).ToArray();
        violations = [];
        if (names.Length == 0) return new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        readLines ??= File.ReadLines;
        enumerateSourceFiles ??= root => Directory.Exists(Path.Combine(root, "src"))
            ? Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            : [];
        var patterns = names.Select(name => new Regex(
            $@"\bpartial\s+class\s+{Regex.Escape(name)}\b",
            RegexOptions.CultureInvariant)).ToArray();
        var totals = new int[names.Length];
        var counts = new int[names.Length];
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var sourcePath in enumerateSourceFiles(repositoryRoot))
            {
                var normalizedPath = sourcePath.Replace('\\', '/');
                var relativePath = Path.IsPathRooted(normalizedPath)
                    ? Path.GetRelativePath(repositoryRoot, normalizedPath).Replace('\\', '/')
                    : normalizedPath;
                var segments = relativePath.Split('/');
                if (segments.Length < 2 || !string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase) ||
                    !relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    segments.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment == "..") ||
                    !seenPaths.Add(relativePath))
                {
                    continue;
                }

                try
                {
                    var lines = readLines(sourcePath).ToArray();
                    var text = string.Join("\n", lines);
                    for (var index = 0; index < names.Length; index++)
                    {
                        if (patterns[index].IsMatch(text))
                        {
                            totals[index] += lines.Length;
                            counts[index]++;
                            if (partialFiles is not null)
                            {
                                if (!partialFiles.TryGetValue(names[index], out var paths))
                                    partialFiles[names[index]] = paths = [];
                                paths.Add(relativePath);
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    violations.Add(new SourceSizeViolation(
                        relativePath, null, 0, BuildUnreadableFileMessage(relativePath, exception)));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            violations.Add(new SourceSizeViolation(
                "src/", null, 0, BuildUnreadableFileMessage("src/", exception)));
        }

        return Enumerable.Range(0, names.Length).Where(index => counts[index] > 0)
            .ToDictionary(index => names[index], index => (totals[index], counts[index]), StringComparer.Ordinal);
    }

    private static string BuildClassOverCeilingMessage(SourceClassCeiling ceiling, int actual, int maximum, string limit)
    {
        return $"class:{ceiling.ClassName} has {actual.ToString(CultureInfo.InvariantCulture)}, " +
            $"exceeding the {limit} ceiling of {maximum.ToString(CultureInfo.InvariantCulture)}. " +
            "Moving members into another partial file of the same class does not satisfy this limit. " +
            "Extract members into a separately owned type; the conductor lowers the row when the change integrates. Otherwise raise the row with " +
            $"goal-specific justification directly above it in {SeededClassCeilingsSymbol}. See {DocumentationPath}.";
    }

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
        IEnumerable<SourceSizeCeiling> ceilings,
        IEnumerable<SourceClassCeiling>? classCeilings = null)
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
        AddMissingAuthorityPointerViolation(section, SeededClassCeilingsSymbol, violations);

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

        foreach (var ceiling in classCeilings ?? SeededClassCeilings)
        {
            var namePattern = $@"\b{Regex.Escape(ceiling.ClassName)}\b";
            var values = new[] { ceiling.MaximumTotalLineCount, ceiling.MaximumPartialFileCount };
            var valuePatterns = values.SelectMany(value => new[]
                {
                    value.ToString(CultureInfo.InvariantCulture),
                    value.ToString("N0", CultureInfo.InvariantCulture),
                })
                .Distinct(StringComparer.Ordinal)
                .Select(value => $@"(?<![\w,]){Regex.Escape(value)}(?![\w,])").ToArray();
            for (var index = 0; index < section.Length; index++)
            {
                if (Regex.IsMatch(section[index], namePattern, RegexOptions.CultureInvariant) &&
                    valuePatterns.Any(pattern => Regex.IsMatch(section[index], pattern, RegexOptions.CultureInvariant)))
                {
                    var lineNumber = sectionStart + index + 1;
                    violations.Add(new SourceSizeDocumentationViolation(
                        "duplicated-ceiling-record", lineNumber,
                        $"{DocumentationPath} line {lineNumber} duplicates a class ceiling for '{ceiling.ClassName}'. " +
                        $"Keep class ceiling values only in {SeededClassCeilingsSymbol} at {SourcePath}."));
                }
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
            "Extract behavior to a collaborator; the conductor lowers the row to the measured size when the change integrates. Otherwise raise the recorded " +
            "ceiling for this entry deliberately with justification in the same change, with the justification directly above the row in " +
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

    internal static string SeededClassCeilingsSymbol =>
        $"{nameof(SourceSizeRatchet)}.{nameof(SeededClassCeilings)}";
}

internal sealed record SourceSizeCeiling(string RelativePath, int MaximumLineCount);

internal sealed record SourceClassCeiling(string ClassName, int MaximumTotalLineCount, int MaximumPartialFileCount);

internal sealed record SourceSizeViolation(
    string RelativePath,
    int? ActualLineCount,
    int MaximumLineCount,
    string Message);

internal sealed record SourceSizeDocumentationViolation(string Rule, int? LineNumber, string Message);
