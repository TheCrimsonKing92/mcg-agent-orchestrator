# Architecture and recovery audit — 2026-09-06

**Scope correction:** this is an incident-focused recovery review, not a general architecture audit. Its architectural conclusion is superseded by [the system architecture audit](system-architecture-audit-2026-09-06.md), which examines responsibility, authority, hosting, verification topology, evidence, extensibility and quality economics against broader evidence.

Scope: source at `3d3270c5`, live recovery on 2026-09-06 UTC, and selected SQLite backlog records. This is an initial design assessment, not an acceptance verdict or a mandate for a broad refactor. Priority order: reliability/stability, landing throughput, then token efficiency without quality loss.

## Assessment

**Inference:** preserve the modular monolith, SQLite, isolated goal worktrees, heterogeneous worker roles, and deterministic acceptance. The largest near-term return is making existing execution, evidence, and recovery boundaries compose reliably. A new database, event-sourcing system, generic agent framework, or large assembly split has no demonstrated benefit in this inspection.

**Fact:** the repository already has `DispatchOutcome` with typed cause and recovery recommendation, `IWorkerProvider` with identity/capabilities/outcome parsing, goal-scoped persistence and merge saves, and a durable operator-intent inbox. Older roadmap language saying these do not exist is obsolete. The remaining question is which callers still bypass or weaken those boundaries.

Sources: `src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs:15`, `src/Mcg.AgentOrchestrator.Infrastructure/Workers/IWorkerProvider.cs:5`, `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs:990`, and `docs/architecture.md`.

## 1. Close interrupted-work recovery before adding throughput

**Fact, live reproduction:** DNS failure interrupted Developer rounds for refined criteria (`13f5400a`), gate speed (`a1088fcb`), and Reviewer routing (`e9fb9f63`) at approximately 2026-09-05 20:43 UTC. Their stderr logs report failed Codex WebSocket connections with Windows error 11001. Partial source changes remained. The conductor kept ticking but Developer admission rejected the dirty worktrees.

**Fact, source mechanism:** `ConductorDriver.IntegrateMainBeforeDeveloperDispatch` rejects dirty worktrees before inspecting/integrating main (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs:1083`). The ordinary commit-on-behalf path excludes `PreserveInterruptedWork` (`src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs:1521`) and may convert a successful ordinary commit into exit code zero (`:1546`). Simply extending that success condition to provider failures would conflate preservation with completion.

**Fact, initial recovery:** operator-created WIP commits `8d7cfc25`, `44cac687`, and `c939d299` preserved the three candidates. Mechanical `ProviderInterruption` retries were applied, and new dispatches started at 00:38:05, 00:38:07, and 00:38:28 UTC. Each emitted prompt contains `OUTAGE_RECOVERY_20260906`. At this initial restart, no task was manually verified or accepted.

**Fact, second recovery boundary exposed at 00:46 UTC:** the resumed criteria Developer verified checkpoint `8d7cfc25` without further edits, and was rejected with `DISPATCH_REJECTED reason=no-change-evidence`, `post_dispatch_commits=0`, `tests_status=Pass`. Its raw JSONL records successful Core 75/75 and Infrastructure 161/161 execution. An independent operator run of the same Infrastructure owner filter at 00:50 UTC also passed 161/161 with exit 0 and process exit confirmed. Developer completion and manual verification were then submitted; Tester, Reviewer, and acceptance remain required. Receipt: `.orchestrator/logs/13f5400a-4bedc613-20260906003805.out.log.jsonl`.

**Fact, subsequent progress:** the conductor dispatched Tester for `13f5400a` at 00:52:38 UTC and for `a1088fcb` at 00:54:10 UTC. The Reviewer-routing Developer remains in progress as of 01:01 UTC. Existing backlog `f7f1e4fd` contains both fresh failure receipts and the checkpoint-provenance requirement.

**Cross-family correction, verified against source:** `PreserveInterruptedWork` is an action, not evidence of a provider cause. `DispatchRecoveryPolicy.cs:184` selects it for a synthetic exit, and `BackgroundDispatchRunner.cs:842` also selects it for a disappeared process with dirty edits. The latter reports `InterruptedDispatchWorkPreserved` without creating a checkpoint. Eligibility must come from a typed provider outcome, and dispositions must distinguish retained dirty edits from a committed checkpoint.

**Cross-family correction, source-proven insufficient liveness signal:** `src/Mcg.AgentOrchestrator.Infrastructure/Workers/DispatchStateSurface.cs:187` reports no identity-bound live nodes when the heartbeat is unavailable. The synthetic-exit branch at `DispatchRecoveryPolicy.cs:184` precedes the unavailable-heartbeat hold at `:216`. That signal cannot authorize a file mutation. Positive owned-job termination and an identity-based survivor inventory are required; an unreadable observation is not proof of death. No claim is made that this race caused the observed DNS outage.

**Cross-family correction, existing completion seam:** `DispatchFailureClassifier.IsDeveloperVerifiedNoChangeRound` already admits verified no-change redispatches (`:1658`), including operator retries (`:1703`). Its `HasEarlyConvergenceEvidenceFor(BaseCommit)` guard requires context evidence currently sourced from review findings (`WorkerContextPackageBuilder.cs:223`; `src/Mcg.AgentOrchestrator.Core/Models/WorkerContextPackageContracts.cs:373`). Extend that existing contract with a distinct checkpoint-provenance evidence source; do not add a second completion bypass or call checkpoints review findings. The specific guard responsible for the live rejection remains an inference until its packaged receipt is inspected.

**Recommendation:** intake existing backlog `f7f1e4fd` as a narrow recovery slice, with `ef458215` retained as the broader parent problem. Add a distinct interrupted-work checkpoint operation at the existing dispatch recovery/committer boundary. Reuse the typed provider outcome and provenance machinery; keep the task retryable and the candidate unverified.

Required properties:

- Only a proven terminal typed provider interruption qualifies; `PreserveInterruptedWork` alone, an unknown exit, content/test failure, or a diagnostic quoted in worker content does not.
- Validate dispatch/goal/worktree/branch identity and exclusive ownership. Confirm the owned process tree has terminated before touching its files; a dead parent PID alone is insufficient. Make the ownership check and commit resistant to another dispatch starting between them.
- Preserve source and intended fixtures, using existing generated-file filtering. Never stash, reset, broadly clean, or commit an arbitrary dirty checkout.
- Record checkpoint SHA, parent SHA, dispatch identity, cause, and preserved paths durably. The Infrastructure checkpoint collaborator owns an idempotency key of dispatch-attempt identity plus parent SHA; write that identity into the commit itself and validate it on replay before applying the existing durable dispatch receipt. Reconciliation replay or crash after commit must find the same checkpoint, not create another. Conflicting or unresolved evidence holds.
- Carry the continuation pointer to the next Developer prompt. Existing retry admission and provider cooldown remain authoritative. Checkpoint success must never imply successful worker output, verification, or landing.
- Preserve attributed checkpoint source evidence across the retry baseline. A resumed Developer may validate an already-finished implementation without a gratuitous new commit; only an eligible checkpoint plus current completion/verification evidence can qualify. An unrelated empty Developer round must still fail.
- Unknown ownership, live descendants, branch drift, merge conflict, or commit failure produces an actionable hold with evidence and leaves the edits intact.

**Verification ownership:** Developer owns implementation and build evidence; Tester specifies the positive and negative matrix; Acceptance executes real temporary-worktree and lifecycle/replay tests; operator observes a controlled interrupted round and successor reconciliation. Stop on false completion, lost edits, duplicate checkpoint, or a write while a worker remains alive.

## 2. Make admission and recovery explain the same decision

**Fact:** task status, operational disposition, process receipts, and goal readiness are distinct surfaces. During recovery, `readiness` returned `Start allowed: True` for goals at different task phases. The speculative acceptance cohort log reported `ready=0` even after Developer workers started. These fields describe different stages; none is a complete answer to whether a goal is progressing.

**Inference:** operators currently reconstruct a cross-stage decision from multiple surfaces. This raises the risk of calling a held goal active or diagnosing an idle conductor as dead. Claude's retained handoff records exactly such a monitoring mistake for Planner evidence requests (`HANDOFF.md:1065`).

**Recommendation:** extend an existing admission projection to expose one stage-specific disposition per active goal: current attempt identity, evidence freshness, whether it is executing/waiting/recoverable/action-required, blocking owner, and the condition that triggers re-evaluation. Have CLI, dashboard, and monitoring render this projection. Keep process liveness and acceptance readiness as named subsidiary facts. Do not introduce another cached lifecycle state or another free-form reason classifier.

**Cross-family correction:** `DispatchStateSurface` / `DispatchAuthoritativeState` already supplies a shared dispatch projection, consumed by CLI, dashboard and recovery (`ConsoleViews.DispatchAndNextActions.cs:147`, `DashboardRenderer.cs:102`, `CliCommandHandlers.Goals.Recovery.cs:54`). The remaining increment is goal-level rollup and an explicit re-evaluation trigger, not research into a new owner. Existing orphan/exit application goal `99f7b1f1` should land before an overlapping slice. The old architecture roadmap (`16ea5b17`) is historical context, not a current specification.

## 3. Finish the mutation boundary incrementally

**Fact:** `IsInboxBackedGoalScopedTaskMutationCommand` enumerates only progress, retry, and manual verification (`CliPersistentStateRunner.cs:613`). Other paths retain direct transaction/dispatch execution, including a general `TransactWithOutboxAsync` callback around command execution (`:285`). Goal-scoped CAS/merge persistence already exists (`:990`, `:1182`).

**Inference:** the architecture supports a safer boundary than every caller currently expresses. The useful increment is one command family at a time: prepare/read, perform external work without a database write transaction, then commit the exact effect against an expected goal version with a durable receipt. Preserve independently usable recovery commands.

**Recommendation:** inventory the remaining broad callback paths and migrate only a measured offending family. Define the command's effect, idempotency key, version conflict behavior, and outcome before moving it. Retain SQLite and existing goal partitioning. Do not make the CLI dependent on a healthy conductor merely to obtain a cleaner-looking topology.

**Unknown:** current transaction hold-time distribution and the worst remaining caller. July backlog measurements are historical. A fresh transaction receipt is required before choosing the first migration or blaming a latency problem on database contention.

## 4. Shorten the feedback cycle without weakening acceptance

**Fact:** Developer and Tester normally provide sanctioned build evidence and request conductor-side tests; raw worker-side .NET test execution is restricted (`docs/role-capability-matrix.md`). The acceptance verifier runs policy synthesis and structural coverage after check execution (`GoalAcceptanceVerifier.cs:947`, `:967`). Process spawning and Mtp managed project rebuild share exclusive resource `xunit:ProcessSpawning` (`config/acceptance-manifest.json:65-74`); their serial contribution must be measured together.

**Recommendation:** finish the already active structural precheck (`9a3cc346`), gate speed (`a1088fcb`), and apparatus-failure routing (`678fa66f`) work. Validate their integration before adding overlapping gate changes. Run cheap deterministic rejection checks at the earliest point their inputs exist, route focused test evidence to the role that can act on it, and reuse evidence only under its existing candidate/input identity rules.

Measure time from Developer finish to first actionable evidence, time in admission queues, longest executed lane, post-lane tail, re-gate reuse, and p50/p95 time-to-land. Keep cold and reused gates separate. A shorter test run accompanied by more rework or post-landing defects is not a win.

**Unknown:** today's exact serial fraction. The handoff's 320-second tail and 464-second lane are prior measurements, not a fresh baseline. Await the recovered candidate's gate receipts before attributing a speedup.

**Fact:** the largest inspected coordination files remain substantial: verifier 9,710 lines, driver 6,784, batch loop 5,194, persistent CLI runner 4,855. **Inference:** this increases review and overlap costs, but line count alone does not identify a runtime bottleneck. Extract behavior only when an active change exposes a coherent invariant and owning data; avoid a separate mass decomposition campaign.

## 5. Measure useful work per token; make Hermes a controlled experiment

**Fact:** backlog `86cac021` already defines an appropriate quality-preserving measurement program: matched accepted outcomes, separate cached/uncached/output usage, interventions, and time-to-land. Prompt budgeting already protects mandatory packaged context (`WorkerPromptInputBudget.cs:86`). Several prior retry/context improvements are marked landed, so their old before-values cannot be reused as current baselines.

**Recommendation:** first remove redundant rounds and repeated evidence delivery. Measure provider-reported token vectors per accepted goal and per role/attempt. Experiment with candidate/criterion/finding-addressed context deltas while retaining immutable receipts and missing-evidence fallback. Only then test cheaper model routing on matched work. Preserve frontier review and all acceptance requirements.

**Fact:** Hermes adapter/trial infrastructure landed under backlog `1e6dce8e`, goal `17d96426`, commit `786ec266`. The checked policy is trial-only and pins release `v2026.8.27`, commit `5fc308a70719a83cccdbba4c0e39c23f5a8239d5`. Its thresholds include 20 prompt/output probes, 10 lifecycle cycles, and 8 paired tasks. The current shell did not resolve a `hermes` executable on PATH; this does not prove there is no installation elsewhere.

**Fact, live preflight:** the isolated clone at `.orchestrator/trials/hermes-acp-v2026.8.27/source` resolves to the exact pinned commit. `uv sync --frozen --extra acp --no-dev --python 3.12` succeeded. Native `hermes --version` exited zero and reports `Hermes Agent v0.20.6 (2026.8.27)`, its installation directory and method, Python and SDK. It does not print the full commit SHA or literal `v2026.8.27`. The current `HermesAcpAdapter.ValidateVersionOutput` requires both strings (`:144-149`), so its predicate rejects the real pinned candidate before a model call. The proper fix must prove the identity of the executable and installation actually launched; neither loosening to a date/package version nor appending expected constants establishes that identity. Receipt: `.orchestrator/operator-evidence/architecture-audit-20260906/hermes-version-exact.log`. Earlier restricted-shell version launches were stopped after no output; the same executable succeeded outside that shell, and the restricted-launch mechanism remains undetermined.

**Unknown:** disabled-feature enforcement, terminal ACP usage, model/provider compatibility, and clean ACP cancellation/teardown remain unproven. No model call or ACP session was started in this preflight. The backlog's Done status proves adapter delivery, not Hermes fit. The pinned upstream CLI is [available at its immutable source](https://github.com/NousResearch/hermes-agent/blob/5fc308a70719a83cccdbba4c0e39c23f5a8239d5/hermes_cli/main.py); local execution supplied the decisive format receipt.

**Recommendation:** follow-up backlog `aa9c7fd6fb204121b6ca574f71f9a697` now owns executable preflight and live fit evidence. First prove pin/model/provider identity, minimum tool configuration, prompt fidelity, terminal usage, and owned teardown in disposable trial roots. Then compare the same underlying model/provider and identical brief/base/acceptance criteria across native and Hermes harnesses. Adopt only under the checked quality and improvement thresholds; record inconclusive results as such. The previously withdrawn Hermes-only egress-proxy prerequisite must not be resurrected (backlog `1e6dce8e`, operator note 2026-08-23).

## Operating sequence and limits

1. Keep the recovered three-goal pipeline advancing, followed by its existing dependency chain. Review conflicts before increasing concurrent work on shared orchestration files.
2. Intake the demonstrated interrupted-work checkpoint fix after the required cross-family check and at a repository-safe intake window.
3. Prepare the Hermes preflight/fit follow-up; avoid default adoption and workload/model confounds.
4. Use fresh gate and usage receipts to select the next throughput or efficiency increment. Update existing backlog records when they own the defect.
5. Revert only when a reproducible current defect is tied to a breaking commit and the rollback preserves later fixes and durable-state compatibility. This incident supplied provider/DNS failure evidence, not evidence that rolling back main would repair the network outage.

Cross-family review: Opus 5 completed a read-only source review with no denied tool calls. Its material corrections to eligibility, liveness, existing early convergence and projection ownership were checked against source and incorporated above. The interrupted-work brief is corrected before intake. Fable 5.1 remains a candidate for measured comparison; no model's concurrence substitutes for execution evidence. The review receipt is `.orchestrator/operator-evidence/architecture-audit-20260906/opus-review-authorized.json`.

## Intake and restart receipts

- Recovery goal `31fa5aeceb6746d18392105b528a7226` was created at 01:08:31 UTC with five roles and slice coverage of `f7f1e4fd`; it depends on existing orphan-attempt goal `99f7b1f1` to limit overlap. A second Opus review confirmed the corrected brief with two final constraints, both included before intake: source-discriminated context evidence and no mutation of the interrupted attempt's ordinary success/commit flags. Receipt: `opus-brief-review.json` in the evidence directory above.
- Hermes identity goal `bbf6fe7cc48d4b25bfeb4edb0ee1c3dd` was created at 01:12:49 UTC with five roles and slice coverage of `aa9c7fd6`. It owns native version compatibility backed by independently verified executable/source identity, with an operator-owned version-only real-world acceptance check. The remaining full fit experiment stays open. Cross-family Researcher and Reviewer must challenge its design before implementation and landing.
- The current gate-speed Tester found a production permit-composition defect and automatically routed `a1088fcb` back to Developer at 01:05:11 UTC. Its isolated scheduling tests had not exercised structural-coverage preparation. This is an active correction, not a completed throughput gain.
- Reviewer-routing `e9fb9f63` completed Developer at commit `cc6f47dd148b` and advanced to Tester at 01:07:16 UTC. Criteria `13f5400a` advanced to Reviewer at 01:02:26 UTC.
- Conductor 21868 stopped orderly at 01:07:51 UTC through `.conduct-stop`. The three dispatch hosts remained alive. Intake occurred while its state-writer lease was absent. The sentinel was removed, and successor 9420 emitted `LOOP_READY lock=acquired state=loaded goals=13` at 01:13:43 UTC. Post-start reconciliation is checked separately; readiness alone is not proof of landing.
- At 01:14 UTC, the successor reconciled the criteria Reviewer and started normal acceptance for `13f5400a` (attempt `13f5400a-0-20260906011406701-7d01496bf73646ee999ab7cfa62ade95`). Gate heartbeats report running Core tests with increasing captured output. It retained the existing `e9fb9f63` Tester and `a1088fcb` corrective Developer, started the Hermes Researcher, and reports `31fa5aec` dependency-held as intended. Startup stderr is empty. These observations establish restored progression; no new goal is claimed landed in this audit.
