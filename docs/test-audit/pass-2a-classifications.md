# Test audit pass 2a: failing-test classifications

Date: 2026-08-15

Population: the twelve tests that failed at least once in the 24-hour acceptance-receipt window

Scope: classification only; this goal makes no test, product, fixture, or manifest fix

## Decision summary

The available evidence supports nine test defects, one awaiting-receipt classification, and two undecidable classifications. It supports no product-intermittent or environmental classification. In particular, failure frequency alone is not causal evidence. The two undecidable cases require their original failing TRX assertion/message and per-goal/per-commit distribution before they can be distinguished from a branch regression, product intermittency, or apparatus failure.

Because no test is classified as environmental, the requirement for every environmental classification to cite cross-goal distribution is vacuous. Any later reclassification to environmental must cite that distribution rather than a single receipt or aggregate frequency.

## Classifications

### 1. `InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory` (7/31)

- **Classification:** Test defect.
- **Evidence strength:** Fact.
- **Cause and evidence:** The 30-second process bound enclosed a real MTP launch and discovery over the 83 MB Infrastructure.Tests assembly and approximately 3,187 test methods. It failed in unrelated goals whose changes did not touch this path. `docs/test-design-discipline.md:56-60` records the diagnosis, and the current minimal probe is visible in `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs:1630-1668`. Goal `5a9a14e0` replaced the heavyweight fixture with `IsolatedDotnetProbe`; commit `fb84bdff` landed it, and two subsequent gates passed.
- **Discipline:** Rule (n), because the bounded process used a heavyweight general-purpose artifact rather than the smallest artifact capable of exercising reuse behavior.
- **Fix/outcome:** The minimal-probe correction was already made. No fix is made in this goal.

### 2. `CreateActivateAndHandoffGoal_starts_first_subscription_dispatch` (4/31)

- **Classification:** Awaiting receipt; cause undecidable from current evidence.
- **Evidence strength:** Fact about the evidence gap, not a causal finding.
- **Cause and evidence:** Goal `d1bacaae` added instrumentation to preserve the previously discarded `StopReason` and `Failure`, but no failure receipt containing those diagnostics exists. Earlier occurrences therefore cannot identify the stopped transition or its reason.
- **Discipline:** Not assigned while the cause is unknown.
- **Would settle:** The next real failure's instrumented `StopReason` and `Failure.Reason` from a `d1bacaae`-era run. This evidence was never recorded for the earlier failures; the new instrumentation makes it recordable on recurrence.
- **Fix/outcome:** Instrumentation is already present. No behavioral fix is justified or made in this goal.

### 3. `Developer_context_receives_complete_ingested_Planner_plan_without_paid_start` (3/30)

- **Classification:** Test defect.
- **Evidence strength:** Fact.
- **Cause and evidence:** The fixture's `PrepareTask` call omitted provider and model, producing a null `ContextPackageReceipt`; the incomplete call is visible at `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs:1144-1151`. The fixture therefore did not establish the context-package precondition required by the assertion.
- **Discipline:** Rule (a), because the test did not establish and assert the precondition selecting the intended context-packaging path.
- **Fix/outcome:** The correction belongs to in-flight goal `ea3d7766`. No duplicate fix is made in this goal.

### 4. `GoalWorktree_acceptance_contention_reconciles_blocked_attempt_before_clean_regate` (2/30)

- **Classification:** Test defect.
- **Evidence strength:** Inference; the original failing assertion output was not packaged.
- **Cause and evidence:** Commit `f4d4c350` changed only the test arrangement from holding one assigned permit to saturating every stable permit and using a zero busy timeout. The current fixture at `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs:1923-1958` now establishes the claimed all-capacity-contended state before checking reconciliation. The prior arrangement could observe partial admission capacity rather than the intended gate outcome.
- **Discipline:** Rules (a) and (d), because the fixture neither established the contended-path precondition nor pinned the all-or-nothing admission result.
- **Fix/outcome:** The permit-saturation correction is already present. No fix is made in this goal.

### 5. `Dispatch_admission_rejects_contending_gate_before_preflight_or_paid_start` (2/30)

- **Classification:** Test defect.
- **Evidence strength:** Inference; the original failing assertion output was not packaged.
- **Cause and evidence:** The same test-only correction in commit `f4d4c350` starts a holder for every build slot before asserting `AllPermitsBusy`, at `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs:2282-2344`. Holding only one assigned permit did not prove that admission had no remaining capacity.
- **Discipline:** Rules (a) and (d), because the test did not establish complete contention or pin the precise admission failure mode before asserting that preflight and paid start were skipped.
- **Fix/outcome:** The permit-saturation correction is already present. No fix is made in this goal.

### 6. `SqliteOrchestratorStateRepository_tick_merge_preserves_retry_tombstone_over_stale_completion` (1/31)

- **Classification:** Undecidable.
- **Evidence strength:** Unknown.
- **Cause and evidence:** The test uses a unique temporary database and deterministic snapshots at `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SqliteOrchestratorStateRepositoryTests.cs:2029-2089`. Source inspection does not discriminate a branch regression, product intermittency, or SQLite test-apparatus failure, and the 1/31 rate does not supply that missing causal evidence.
- **Discipline:** Not assigned while the cause is unknown.
- **Would settle:** The original failing assertion/message plus the failure's per-goal/per-commit distribution from `.orchestrator/acceptance-gate-attempts/**/*.trx`. That corpus is retrievable but was not packaged for this task.
- **Fix/outcome:** No fix is justified or made until the discriminating receipt evidence is reviewed.

### 7. `GoalAcceptanceVerifier_slot_gate_uses_base_build_cache_for_unchanged_projects` (1/30)

- **Classification:** Test defect; shared verifier fixture-census cause described below.
- **Evidence strength:** Inference.
- **Cause and evidence:** Commit `6e8202ed` added Dashboard.Tests and TestSupport to the fixture's seeded unchanged-project cache. Before that correction, the fixture claimed unchanged-cache coverage without seeding the full unchanged-project population introduced by dashboard extraction.
- **Discipline:** Rule (a), because the cache-state precondition did not represent every project that the asserted path treated as unchanged.
- **Fix/outcome:** The cache seed was already corrected. No fix is made in this goal.

### 8. `GoalAcceptanceVerifier_runs_core_and_dependent_infrastructure_shards_for_core_scope` (1/30)

- **Classification:** Test defect; shared verifier fixture-census cause described below.
- **Evidence strength:** Fact.
- **Cause and evidence:** After the shared manifest fixture gained a dashboard check, commit `d7a27197` changed only the incidental total-call census from `laneCount + 4` to `laneCount + 5`. The intended assertions about core and dependent infrastructure shards did not change.
- **Discipline:** Rule (c), because an incidental integration-wide call census was asserted in a test whose owned invariant was the narrower core/dependent-infrastructure shard selection.
- **Fix/outcome:** The expected census was already corrected. No fix is made in this goal.

### 9. `DotnetBuildEnvironmentManagerTests.FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry` (1/31)

- **Classification:** Test defect.
- **Evidence strength:** Fact.
- **Cause and evidence:** The old test gave the runner a five-second budget and then asserted that `descendant.json` had been created by the process being killed. `docs/test-design-discipline.md:41-52` records that this made the verdict depend on how far the losing process progressed; six goals failed while the runner behaved correctly. The negative control is recorded in `docs/negative-controls/a43bb588.md`, and current assertions use the terminator receipt at `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTests.cs:3117-3147`.
- **Discipline:** Rule (m), because the test asserted the victim process's partial progress rather than the bounding code's deterministic termination record.
- **Fix/outcome:** The budget and tree-kill concerns were already decomposed and the assertions corrected. No fix is made in this goal.

### 10. `BudgetAwareRouting_scorecard_lookup_accepts_same_model_in_multiple_lanes` (1/30)

- **Classification:** Undecidable.
- **Evidence strength:** Unknown.
- **Cause and evidence:** The test and lookup are deterministic and lane-keyed at `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/BudgetAwareRoutingTests.cs:127-173` and `src/Mcg.AgentOrchestrator.App/SubscriptionPlanning/SubscriptionPlanBuilder.cs:628-662`. Source alone does not show whether the receipt represents a branch regression or test-apparatus failure, and the 1/30 frequency does not decide that question.
- **Discipline:** Not assigned while the cause is unknown.
- **Would settle:** The original failing assertion/message plus the failure's per-goal/per-commit distribution from `.orchestrator/acceptance-gate-attempts/**/*.trx`. That corpus is retrievable but was not packaged for this task.
- **Fix/outcome:** No fix is justified or made until the discriminating receipt evidence is reviewed.

### 11. `GoalAcceptanceVerifier_base_build_cache_receipts_show_structural_cold_then_warm_attempts` (1/30)

- **Classification:** Test defect; shared verifier fixture-census cause described below.
- **Evidence strength:** Inference.
- **Cause and evidence:** Commit `6e8202ed` added Dashboard.Tests and TestSupport to both the cacheable-project setup and the expected cold/warm receipts. The old receipt expectations retained the pre-extraction project population.
- **Discipline:** Rule (a), because the arranged cacheable-project population and asserted structural receipts did not cover the path's actual dependency population.
- **Fix/outcome:** The fixture and receipt expectations were already corrected. No fix is made in this goal.

### 12. `GoalAcceptanceVerifier_slot_gate_uses_base_build_cache_for_verifier_source_scope` (1/30)

- **Classification:** Test defect; shared verifier fixture-census cause described below.
- **Evidence strength:** Inference.
- **Cause and evidence:** Commit `6e8202ed` updated the expected dependency closure from five to seven builds and added Dashboard.Tests and TestSupport expectations. The prior fixture retained the old dependency census after dashboard extraction.
- **Discipline:** Rule (a), because the asserted verifier-source dependency closure did not include all projects selected by the production path.
- **Fix/outcome:** The expected dependency closure was already corrected. No fix is made in this goal.

## Shared `GoalAcceptanceVerifier` cause

The four verifier failures are not four independent intermittent or environmental events. They share a branch-caused fixture-census drift: dashboard extraction added Dashboard.Tests, TestSupport, and a dashboard manifest check, while cache seeds, dependency and receipt expectations, and one total-call census temporarily retained the previous population. Commits `6e8202ed` and `d7a27197` corrected those test-only expectations. This goal records the shared cause and makes no further change.

## Follow-up boundary

No environmental or product-intermittent defect is established by this pass. The only unresolved follow-up is evidence collection: inspect the next instrumented handoff failure receipt, and recover the SQLite and scorecard assertion messages and cross-goal distributions from the TRX corpus before assigning either a causal category or a fix.
