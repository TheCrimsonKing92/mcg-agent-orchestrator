# Acceptance-gate flake inventory

This is a generated, point-in-time census of the mutable receipt corpus. Do not hand-edit rows; regenerate the document from the receipts. Ranking includes only goal acceptance receipts. Operator and pre-review runs are scanned but excluded from gates-destroyed ranking because they include focused and negative-control executions.

- Scan started (UTC): `2026-08-21T18:05:27.4697456+00:00`
- Scan finished (UTC): `2026-08-21T18:06:44.4079652+00:00`
- Acceptance root (recursive, including rotated files): `C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator\acceptance-gate-attempts` — 90490 TRX
- Goal acceptance receipts ranked: 25570 TRX
- Operator receipts scanned but excluded: 64920 TRX
- Pre-review root (recursive, including rotated files): `C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator\pre-review-evidence-attempts` — 21843 TRX
- Combined TRX scanned: 112333
- Goal attempts identified by `*.attempt.json` metadata: 1372
- Goal failure results: 619; ranked (test, signature) rows: 558; distinct failed test names: 350
- Truncation: none; every observed goal failure result is listed below.

<!-- acceptance-gate-flake-inventory-supplements:BEGIN -->
## Generated post-census receipt supplements

These rows are regenerated from versioned normalized extracts of retained goal-acceptance receipts. They supplement the historical full-corpus census below; they do not reclassify its older rows or claim a cause for the observed failures.

- Regeneration scan (UTC): `2026-08-26T14:18:57.1549022+00:00`
- Corpus scope: goal acceptance receipts for `9c3885b2fcbb42cfac7e74fbe3c1fc58, a04cdfe958634087ac4ba457b1b155f1, d7585642849048e396b6c75b5a890a17, dd6ba0f8113a47eb8e596e32f0f95ac9`
- Input extract: `docs\acceptance-gate-flake-inventory-receipts\2026-08-24-goals.json` — SHA-256 `444873fd8407e254011120b8856a0f3ce7801114db4e56a84feab1629cb45680`
- Input counts after receipt-identity deduplication: 12 attempts; 7 pass; 5 fail
- Per-goal counts: `9c3885b2fcbb42cfac7e74fbe3c1fc58` 3 pass/1 fail; `a04cdfe958634087ac4ba457b1b155f1` 1 pass/1 fail; `d7585642849048e396b6c75b5a890a17` 1 pass/1 fail; `dd6ba0f8113a47eb8e596e32f0f95ac9` 2 pass/2 fail
- Source attestation: Operator human-input answer for goal 8be330e26b884612960c94848b87a5ff, derived from retained acceptance TRX, test-identity sidecar, and result receipts; raw artifact hashes were supplied as prefixes.
- Exact invocation: `.\scripts\Invoke-RepoScript.ps1 scripts\Update-AcceptanceGateFlakeInventory.ps1 -EvidencePath docs\acceptance-gate-flake-inventory-receipts\2026-08-24-goals.json -DocumentPath docs\acceptance-gate-flake-inventory.md -ScanTimestampUtc 2026-08-26T14:18:57.1549022Z`
- Completeness: validated; missing goals, duplicate identities, malformed timestamps, absent failure signatures, and count mismatches fail generation before the document is written.

| Test | Attempts | Pass | Goal fail | Observed failure rate | Failing goals | First | Last | Mechanism |
|---|---:|---:|---:|---:|---:|---|---|---|
| GoalAcceptanceEvidenceBundleTests.ChangedFiles_FailedBaseRefProbe_ThrowsInsteadOfReturningKnownEmpty | 12 | 7 | 5 | 41.67% | 4 | 2026-08-24 | 2026-08-24 | mechanism-undetermined |

### Supplement failure signatures

- `GoalAcceptanceEvidenceBundleTests.ChangedFiles_FailedBaseRefProbe_ThrowsInsteadOfReturningKnownEmpty`
  - 3x `git init -b topic failed (0):`
  - 1x `git commit -m Seed failed (0):`
  - 1x `git config user.email tests@example.invalid failed (0):`
<!-- acceptance-gate-flake-inventory-supplements:END -->

## Reproduction commands

The census was produced by recursively enumerating `*.trx`, streaming each goal TRX `UnitTestResult` once, grouping failures by decoded `(testName, first ErrorInfo Message line)`, and selecting operator/pre-review failed TRX files with ripgrep before streaming them. The exact census commands were:

```powershell
$acceptance = 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator\acceptance-gate-attempts'
$preReview = 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator\pre-review-evidence-attempts'
Get-ChildItem -LiteralPath $acceptance -Recurse -File -Filter *.trx
Get-ChildItem -LiteralPath $preReview -Recurse -File -Filter *.trx
rg -l 'outcome="Failed"' (Join-Path $acceptance 'operator') -g '*.trx'
rg -l 'outcome="Failed"' $preReview -g '*.trx'
```

Classification is applied per `(test name, signature)` row: multiple goals on one date are load-dependent and multiple goals across dates are spread. A regression claim additionally requires a failure in a gate outside the introducing goal or a failure after that goal landed; a single-goal cluster alone is labeled `single-goal-unresolved`. A newly added test whose failures occur only in its introducing goal is `new-test-development-failure`, not a regression. The two receipt-established exceptions are the native-MTP 51–362 second pass-duration spread and the snapshot sentinel pairs, both classified load-dependent. Unresolved provenance is never attributed to whichever goal happened to fail.

## Conversion decisions

- `InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory` / `Invoke-IsolatedDotnet.ps1 did not exit within 30 seconds.` — spread flake. Converted in GoalWorktreeTests to a named 90-second real-process hang guard. Control option 1 is available through the existing injectable guard seam: a readiness-gated five-second real shim runs against a two-second injected guard, proving the identical wait path fires when the dependency outlives its budget, then the same shim passes under the production guard. A literal 33-second old-budget control is not used because it adds material process-lane wall time without stronger evidence. A separate ready-gated never-exiting shim proves timeout and tree reaping. Historical exit-86 rows are separate non-timeout signatures.
- Native MTP Infrastructure/`ProcessStartInfoSourceGuardTests` and Dashboard/`DashboardValidationHarnessTests` / `dotnet did not exit within 300 seconds.` — load-dependent, not regression. Observed passing durations on unchanged code span 51.15–362.49 seconds. The shared theory's existing 15-minute budget becomes a named guard for both ranked rows. Control option 1 injects a one-second guard while the same real child deliberately runs for five seconds, proving the guard fires when the dependency outlives its budget; the child then passes through the identical wait path under the production guard. A literal old-budget control would add at least 300 seconds (and a 900-second literal arm would add 15 minutes). The never-exiting arm separately proves timeout and tree reaping.
- Snapshot sentinel timeout — qualifying but explicitly excluded: converted upstream by goal `7135991f` / commit `d3d6706d`; LauncherScriptTests is not changed here. Its July `taskkill` signature is a separate regression fixed by `25aaa28c`.
- `AssemblyTempRedirectTests.ConcurrentTestHostsReceiveDistinctTempRoots` — `new-test-development-failure` and explicitly excluded. It has 41 passes and 0 failures in goal-acceptance receipts; its failures are pre-review runs while goal `4c0ee8cb` was introducing the test, so neither a failure outside that goal nor a post-landing failure exists.
- Build-lock, probe-output, access-denied, non-zero-exit, dirty-status, and other non-wait signatures remain inventoried but are not budget conversions.

## Ranked summary

| Rank | Test | Signature | Pass | Goal fail | Operator fail | Pre-review fail | Failing goals | First | Last | Pattern |
|---:|---|---|---:|---:|---:|---:|---:|---|---|---|
| 1 | AdvanceGoalWithSubscriptionsUntilBlocked_continues_prompt_below_new_large_threshold | Assert.True() Failure | 95 | 4 | 0 | 0 | 4 | 2026-08-20 | 2026-08-20 | load-dependent |
| 2 | BackgroundDispatchRunner_codex_dispatch_records_reported_session_tuple_in_receipt_and_heartbeat | Assert.Equal() Failure: Strings differ | 100 | 4 | 0 | 0 | 4 | 2026-07-21 | 2026-07-24 | spread |
| 3 | BudgetAwareRouting_simple_task_prefers_local_over_paid_when_both_available | Assert.True() Failure | 106 | 4 | 0 | 0 | 4 | 2026-08-20 | 2026-08-20 | load-dependent |
| 4 | DotnetBuildEnvironmentManager_marked_landing_fixture_from_other_process_is_transient | Assert.IsType() Failure: Value is not the exact type | 106 | 4 | 0 | 0 | 4 | 2026-07-22 | 2026-08-02 | spread |
| 5 | DotnetBuildEnvironmentManagerTests.FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry | Assert.True() Failure | 63 | 4 | 0 | 12 | 4 | 2026-08-13 | 2026-08-14 | spread |
| 6 | Goals_subscribe_once_waits_through_unrelated_events_and_emits_one_matching_human_event | Assert.False() Failure | 104 | 4 | 0 | 1 | 4 | 2026-08-20 | 2026-08-20 | load-dependent |
| 7 | Monitor_goal_local_ndjson_without_once_stays_attached_until_cancelled | Assert.Contains() Failure: Sub-string not found | 105 | 4 | 0 | 0 | 4 | 2026-08-20 | 2026-08-20 | load-dependent |
| 8 | OrchestratorHealthInspector_rejects_local_bridge_for_api_only_agent | Assert.False() Failure | 106 | 4 | 0 | 0 | 4 | 2026-08-20 | 2026-08-20 | load-dependent |
| 9 | ConductorSelfRelaunch_real_handoff_failure_stops_successor_and_reacquires_incumbent_lease | Assert.Equal() Failure: Strings differ | 90 | 3 | 5 | 0 | 3 | 2026-08-01 | 2026-08-10 | spread |
| 10 | GoalAcceptanceVerifier_partitions_checked_in_infrastructure_manifest_check | Assert.Contains() Failure: Filter not matched in collection | 107 | 3 | 0 | 1 | 3 | 2026-07-23 | 2026-08-21 | spread |
| 11 | GoalDependency_Chain_BHeldUntilADone | Assert.True() Failure | 107 | 3 | 0 | 0 | 3 | 2026-07-19 | 2026-07-19 | load-dependent |
| 12 | InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory | Invoke-IsolatedDotnet.ps1 did not exit within 30 seconds. | 87 | 3 | 0 | 0 | 3 | 2026-08-14 | 2026-08-15 | spread |
| 13 | Planner_dispatch_persists_canonical_receipt_for_plan_captured_on_stdout | Assert.Equal() Failure: Strings differ | 81 | 3 | 0 | 1 | 3 | 2026-08-20 | 2026-08-20 | load-dependent |
| 14 | Planner_output_contract_selects_latest_explicit_allowed_external_plan | Assert.Equal() Failure: Strings differ | 81 | 3 | 0 | 1 | 3 | 2026-08-20 | 2026-08-20 | load-dependent |
| 15 | Research_first_pipeline_blocks_Planner_then_injects_full_artifacts_without_survey_or_retry_trimming | Assert.Equal() Failure: Strings differ | 78 | 3 | 0 | 1 | 3 | 2026-08-20 | 2026-08-20 | load-dependent |
| 16 | WorkerDispatchTestSupport+WorkerDispatchPlannerHandoffTests.PlannerContract_AcceptanceMappingPlaceholder_Fails(body: "") | Assert.Contains() Failure: Sub-string not found | 76 | 3 | 0 | 2 | 3 | 2026-08-20 | 2026-08-20 | load-dependent |
| 17 | WorkerDispatchTestSupport+WorkerDispatchPlannerHandoffTests.PlannerContract_AcceptanceMappingPlaceholder_Fails(body: "TBD") | Assert.Contains() Failure: Sub-string not found | 76 | 3 | 0 | 2 | 3 | 2026-08-20 | 2026-08-20 | load-dependent |
| 18 | WorkerProfileDispatcher_rejects_over_limit_subscription_prompt_before_dispatch_mutation | Assert.ThrowsAny() Failure: No exception was thrown | 104 | 3 | 0 | 0 | 3 | 2026-08-20 | 2026-08-20 | load-dependent |
| 19 | Cli_attention_show_preserves_worker_owned_by_live_external_conductor | Assert.True() Failure | 66 | 2 | 0 | 0 | 2 | 2026-08-08 | 2026-08-08 | load-dependent |
| 20 | ConductorDriver_real_dispatch_checkpoint_rolls_back_then_notifies_on_success | Assert.Throws() Failure: No exception was thrown | 59 | 2 | 0 | 1 | 2 | 2026-08-07 | 2026-08-10 | spread |
| 21 | ConductorSelfRelaunch_real_self_check_failure_keeps_incumbent_authority | Assert.Equal() Failure: Strings differ | 66 | 2 | 0 | 0 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 22 | DotnetBuildEnvironmentManagerTests.FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry | Assert.Equal() Failure: Values differ | 63 | 2 | 0 | 12 | 2 | 2026-08-06 | 2026-08-11 | spread |
| 23 | GetRepoProcessInfo_reports_exact_pid_lineage_through_repo_prefix | Assert.Equal() Failure: Values differ | 101 | 2 | 5 | 0 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 24 | GoalAcceptanceVerifier_waits_for_no_holder_acceptance_output_lock_then_retries | Assert.Matches() Failure: Pattern not found in value | 99 | 2 | 0 | 0 | 2 | 2026-07-19 | 2026-07-20 | spread |
| 25 | GoalRefinementTests.ResolveOpenClarificationStampsCurrentAuthoritativeBriefVersion | Assert.Equal() Failure: Values differ | 54 | 2 | 0 | 0 | 2 | 2026-08-08 | 2026-08-08 | load-dependent |
| 26 | InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory | Invoke-IsolatedDotnet.ps1 exited 86. | 87 | 2 | 0 | 0 | 2 | 2026-08-01 | 2026-08-02 | spread |
| 27 | InvokeRepoScript_runs_FindOrchestratorLocks_without_synthetic_argument | Expected Find-OrchestratorLocks.ps1 to exit 0 or 2, got 1. stderr: ERROR: could not acquire build lock after 30 s | 101 | 2 | 5 | 0 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 28 | InvokeRepoScript_StartOrchestratorCommand_emits_parseable_launch_json | Assert.Equal() Failure: Values differ | 102 | 2 | 5 | 0 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 29 | LockAttribution_handle_probe_timeout_returns_unknown_without_wedging | Assert.Single() Failure: The collection was empty | 108 | 2 | 0 | 1 | 2 | 2026-08-10 | 2026-08-11 | spread |
| 30 | Native_MTP_dotnet_test_runs_every_repository_test_project_in_one_step(project: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/M"···, testClass: "ProcessStartInfoSourceGuardTests") | System.TimeoutException : dotnet did not exit within 300 seconds. | 21 | 2 | 2 | 1 | 2 | 2026-08-20 | 2026-08-20 | load-dependent |
| 31 | ProcessTreeGuiSuppression_sets_inherited_error_mode_and_hidden_console_for_descendants | Probe did not write output. stdout=Windows PowerShell | 108 | 2 | 0 | 0 | 2 | 2026-08-01 | 2026-08-02 | spread |
| 32 | StartOrchestratorCommand_emits_json_pid_and_log_path_through_repo_script | Assert.Equal() Failure: Values differ | 82 | 2 | 0 | 0 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 33 | StartOrchestratorCommand_forwards_double_dash_arguments_to_background_process | Assert.Equal() Failure: Values differ | 109 | 2 | 5 | 1 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 34 | StartOrchestratorCommand_launch_failure_exits_nonzero_with_error_json_on_stderr | Access to the path '.orchestrator' is denied. | 109 | 2 | 5 | 0 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 35 | StopRepoProcess_refuses_exact_pid_when_command_guard_mismatches | Assert.Equal() Failure: Values differ | 101 | 2 | 5 | 0 | 2 | 2026-08-10 | 2026-08-10 | load-dependent |
| 36 | A runner process fault preserves SHA-named stdout and stderr logs | Assert.False() Failure | 53 | 1 | 0 | 3 | 1 | 2026-08-18 | 2026-08-18 | single-goal-unresolved |
| 37 | acceptance-retry_allows_cancelled_task_and_caps_operator_regates_at_three | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6c9e82e46bf54bce9d673de3958e2f99' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 38 | acceptance-retry_allows_cancelled_task_and_caps_operator_regates_at_three | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\890817abff4742a3932933e19981a452' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 39 | acceptance-retry_migrates_legacy_escalation_before_resolving_it | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1e2a569e240c449b93474a500492d165' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 40 | acceptance-retry_migrates_legacy_escalation_before_resolving_it | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\23621d7a80204ab79470d70db73b7781' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 41 | acceptance-retry_names_incomplete_task_and_correct_recovery_verb | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3fe2b58775044194b9a5ca4567e35198' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 42 | acceptance-retry_names_incomplete_task_and_correct_recovery_verb | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f969f5d571924f17a6997d9c684b6854' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 43 | acceptance-retry_old_resolution_callback_does_not_resolve_newer_failure | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5caf0eaac88d429fbbbced0c1ae09875' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 44 | acceptance-retry_old_resolution_callback_does_not_resolve_newer_failure | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9d21efbf6ff241d5a3cd74975e1f97d5' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 45 | acceptance-retry_outbox_quarantines_poison_without_blocking_unrelated_command | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\aa82ec2bdce64726bce6fbdd2a3414d3' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 46 | acceptance-retry_outbox_quarantines_poison_without_blocking_unrelated_command | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ada9b1b9aa5d4b6fbde73fa620414f3b' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 47 | acceptance-retry_outbox_rolls_back_with_commit_and_replays_pending_audit | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\485d16ffe2eb4d34aa43c49c7f328348' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 48 | acceptance-retry_outbox_rolls_back_with_commit_and_replays_pending_audit | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60a33a54462f4f44a8ad1608d238051e' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 49 | acceptance-retry_outbox_simultaneous_drainers_apply_audit_once | System.InvalidOperationException : acceptance-retry could not resolve the prior failing gate's main HEAD SHA. | 87 | 1 | 5 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 50 | acceptance-retry_outbox_simultaneous_drainers_apply_audit_once | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\422e3a4b24f6471bb3d95e9929f3002a' is denied. | 87 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 51 | acceptance-retry_outbox_simultaneous_drainers_apply_audit_once | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\faa15fc503e644e886d3c4b88f2e7268' is denied. | 87 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 52 | acceptance-retry_refuses_corrupt_escalation_store_without_mutating_goal | System.InvalidOperationException : git commit -m Seed failed: | 87 | 1 | 5 | 0 | 1 | 2026-08-12 | 2026-08-12 | single-goal-unresolved |
| 53 | acceptance-retry_refuses_corrupt_escalation_store_without_mutating_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\26bd453399a342baae004f19fbdef46b' is denied. | 87 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 54 | acceptance-retry_refuses_corrupt_escalation_store_without_mutating_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9e45aea2a0a840d586ec4476f6f0eb33' is denied. | 87 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 55 | acceptance-retry_requires_confirmation_before_goal_validation | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\63b901b2871542eea1674a594ea5449e' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 56 | acceptance-retry_requires_confirmation_before_goal_validation | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0e6bcd99949f413ebe532c46b0ebed22' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 57 | acceptance-retry_requires_passed_verification_for_completed_tasks | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d3a56f222448437e864e68c0bff20431' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 58 | acceptance-retry_requires_passed_verification_for_completed_tasks | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7b6eaae0bd7149a3a3ef65ca17289c6b' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 59 | acceptance-retry_requires_prior_gate_main_sha_before_mutating_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d233376fd65e4d3eb83acd6a4c86bbfb' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 60 | acceptance-retry_requires_prior_gate_main_sha_before_mutating_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\71946a20d8e44571a4259ae80a967092' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 61 | acceptance-retry_returns_failed_goal_to_verified_without_mutating_tasks | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d7496d9faf5d403ab37e995c4f946fb5' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 62 | acceptance-retry_returns_failed_goal_to_verified_without_mutating_tasks | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f2cc84d18a564958a69b16eeb7094f22' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 63 | acceptance-retry_stale_acceptance_failed_snapshot_cannot_reopen_resolved_escalation | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ea2fd291739344bf8bd0e33701184cfb' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 64 | acceptance-retry_stale_acceptance_failed_snapshot_cannot_reopen_resolved_escalation | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48a3ac2fb8954787a504ccd21f066679' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 65 | AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource | Disabled collection 'GoalWorktreeCleanupHooks' spans acceptance lanes [Goal worktree cleanup, Cli, Goal lifecycle commands] without a shared exclusive resource key. Mapped classes: [AcceptanceCohortWorkflowTests -> Goal worktree cleanup, CliCommandTestsGoalBoard -> Cli, CliCommandTestsGoalLifecycleCleanupHooksAbandon -> Goal lifecycle commands, CliCommandTestsGoalLifecycleCleanupHooksAcceptance -> Goal lifecycle commands, CliCommandTestsGoalLifecycleCleanupHooks -> Goal lifecycle commands, CliCommandTestsPersistentRunnerCommands -> Goal lifecycle commands, CliCommandTestsSubscriptionDispatchCommands -> Goal lifecycle commands, GoalsPruneTests -> Goal lifecycle commands, GoalWorktreeTestsAcceptanceLanding -> Goal worktree cleanup, GoalWorktreeTestsOrphanEphemeralSweep -> Goal worktree cleanup, GoalWorktreeTestsRebaseMerge -> Goal worktree cleanup, GoalWorktreeTestsRemoveCleanup -> Goal worktree cleanup, GoalWorktreeTestsCleanupHookDelegates -> Goal worktree cleanup, LandingExecutorTests -> Goal lifecycle commands]. | 78 | 1 | 0 | 5 | 1 | 2026-08-18 | 2026-08-18 | single-goal-unresolved |
| 66 | AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource | Disabled collection 'ProcessSpawning' spans acceptance lanes [Remainder, Process spawning, Remainder balance B] without a shared exclusive resource key. Mapped classes: [CitedPriorEvidenceResolverTests -> Remainder, CliHelpTests -> Process spawning, ConductorDriverTests -> Process spawning, ConductorSelfRelaunchTests -> Process spawning, ConductorSuccessorSelfCheckTests -> Process spawning, DashboardRenderingTests -> Process spawning, DispatchProcessHostTests -> Process spawning, GitCliTests -> Process spawning, GoalWorktreeIsolatedDotnetTests -> Remainder balance B, LauncherScriptTests -> Process spawning, MtpTestRunnerScriptTests -> Process spawning, ProcessTreeGuiSuppressionTests -> Process spawning, SemanticAcceptanceTests -> Process spawning, WorkerContextArtifactsCharacterizationTests -> Process spawning, WorkerDispatchTestsModelSelection -> Process spawning]. | 78 | 1 | 0 | 5 | 1 | 2026-08-09 | 2026-08-09 | single-goal-unresolved |
| 67 | AcceptanceQueuePlanner_batches_goal_branch_facts_once_per_plan | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\96a56e1130bd49ebb89a5095b002950f' is denied. | 102 | 1 | 5 | 1 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 68 | AcceptanceQueuePlanner_batches_goal_branch_facts_once_per_plan | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9940327b934b478180ba8daf6802ecfc' is denied. | 102 | 1 | 5 | 1 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 69 | Advance subscription start does not spawn scoped real worker process | Assert.True() Failure | 102 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 70 | AutoReviewRetryConvergenceBriefBuilder_SQLite_round_diff_reopens_exact_regressed_anchor | System.InvalidOperationException : git commit -m Add review target failed: | 90 | 1 | 0 | 0 | 1 | 2026-07-26 | 2026-07-26 | single-goal-unresolved |
| 71 | AutoReviewRetryConvergenceBriefBuilder_SQLite_rounds_shrink_A_B_to_accept | Assert.Contains() Failure: Sub-string not found | 90 | 1 | 0 | 2 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 72 | BackgroundDispatchRunner_child_zero_wrapper_one_without_usable_worker_result_fails_on_wrapper_exit | Assert.Contains() Failure: Sub-string not found | 50 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 73 | BackgroundDispatchRunner_hung_wrappers_complete_read_only_roles_with_valid_worker_result(role: Reviewer, codexWrapper: False) | Assert.Equal() Failure: Values differ | 56 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 74 | BackgroundDispatchRunner_hung_wrappers_complete_read_only_roles_with_valid_worker_result(role: Reviewer, codexWrapper: True) | Assert.Equal() Failure: Values differ | 56 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 75 | BackgroundDispatchRunner_non_local_dispatch_records_resource_accounting | Assert.NotNull() Failure: Value is null | 215 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 76 | BackgroundDispatchRunner_progress_stall_completes_read_only_role_with_valid_worker_result | Assert.Equal() Failure: Values differ | 55 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 77 | BackgroundDispatchRunner_read_only_hung_wrapper_rescue_is_dispatch_agnostic | Assert.Equal() Failure: Values differ | 56 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 78 | BackgroundDispatchRunner_reaped_startup_hang_records_job_accounting | System.TimeoutException : Timed out waiting for test condition. | 108 | 1 | 0 | 0 | 1 | 2026-07-21 | 2026-07-21 | single-goal-unresolved |
| 79 | BackgroundDispatchRunner_reconciled_developer_without_change_still_fails_change_evidence | Assert.Contains() Failure: Sub-string not found | 50 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 80 | BackgroundDispatchRunner_refresh_completes_when_exit_file_exists_even_if_wrapper_is_running | Assert.Equal() Failure: Values differ | 106 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 81 | BackgroundDispatchRunner_refresh_trusts_exit_zero_file_before_usage_limit_stderr | Assert.Equal() Failure: Values differ | 106 | 1 | 0 | 1 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 82 | BackgroundDispatchRunner_stale_no_exit_does_not_consume_empty_output_flake_budget | Assert.Equal() Failure: Values differ | 108 | 1 | 0 | 2 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 83 | BackgroundDispatchRunner_stale_no_exit_surfaces_budget_exhaustion_after_stale_retry_spent | Assert.Equal() Failure: Values differ | 108 | 1 | 0 | 2 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 84 | BackgroundDispatchRunner_startup_hang_completes_read_only_role_with_valid_worker_result | Assert.Equal() Failure: Values differ | 55 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 85 | BackgroundDispatchRunner_sweep_completes_role_boundary_exited_worker_from_exit_file | Assert.Equal() Failure: Values differ | 106 | 1 | 0 | 1 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 86 | BatchLoop_dispatch_record_contention_escalates_goal_at_visible_bound_without_stopping_loop | Assert.Equal() Failure: Values differ | 60 | 1 | 0 | 2 | 1 | 2026-08-07 | 2026-08-07 | single-goal-unresolved |
| 87 | BatchLoop_parallel_acceptance_capacity_reduces_measured_makespan | Assert.All() Failure: 2 out of 2 items in the collection did not pass. | 0 | 1 | 0 | 1 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 88 | BatchLoop_serializes_overlapping_gate_ready_acceptance | Assert.Equal() Failure: Values differ | 100 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 89 | BatchLoop_unclassified_pre_process_dispatch_record_write_is_visible_and_skipped | Assert.Equal() Failure: Values differ | 60 | 1 | 0 | 1 | 1 | 2026-08-07 | 2026-08-07 | single-goal-unresolved |
| 90 | BuildTaskBrief_acceptance_retry_includes_structured_failure_receipt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-taskbrief-tests\b3308dd66a9f431ba12688aef09a626c\.orchestrator\goal-operations' is denied. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 91 | BuildTaskBrief_acceptance_retry_includes_structured_failure_receipt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-taskbrief-tests\c3546e70f22a4f548f2c6be2478f77ee\.orchestrator\goal-operations' is denied. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 92 | BuildTaskBrief_retry_feedback_preserves_atomic_entry | System.InvalidOperationException : Sequence contains more than one element | 26 | 1 | 0 | 0 | 1 | 2026-08-15 | 2026-08-15 | single-goal-unresolved |
| 93 | BuildTaskBrief_reviewer_convergence_scope_preserves_finding_severity | Assert.Contains() Failure: Sub-string not found | 101 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 94 | ChaosGate3a_missing_worker_result_block_passes_advisory_when_git_evidence_present | System.InvalidOperationException : git commit -m Add src/Feature.cs failed: | 107 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 95 | Classify ignores bookkeeping stderr when identifying a silent launch failure | Assert.Equal() Failure: Values differ | 85 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 96 | Classify includes substantive stderr artifact tail in real failure receipt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpcgdh3y.tmp' is denied. | 34 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 97 | Classify includes substantive stderr artifact tail in real failure receipt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpm5xyv1.tmp' is denied. | 34 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 98 | Classify reads log path for verification evidence outside retained excerpt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-classify-2925fb8cd7cb438385340dd1eb2b93c8' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 99 | Classify reads log path for verification evidence outside retained excerpt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-classify-46e84f7a9bd74217b235f0efd4d22515' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 100 | Classify reads sandbox commit evidence from middle of large log artifacts | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpikldse.tmp' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 101 | Classify reads sandbox commit evidence from middle of large log artifacts | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpoh3dv4.tmp' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 102 | Classify reads WORKER_RESULT blockers from stdout artifact | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpm2ommq.tmp' is denied. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 103 | Classify reads WORKER_RESULT blockers from stdout artifact | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpnohkcn.tmp' is denied. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 104 | Classify reads WORKER_RESULT fields from middle of large stdout artifact | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpkpu4bb.tmp' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 105 | Classify reads WORKER_RESULT fields from middle of large stdout artifact | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpkzbwf0.tmp' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 106 | Classify treats scripting stderr without positive merit evidence as unknown | Assert.Equal() Failure: Values differ | 40 | 1 | 0 | 1 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 107 | Cli_acceptance_accepted_routes_stop_host_merge_mark_landed_before_deferred_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fc8c189477c54c818d6ad17c1684f0a6' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 108 | Cli_acceptance_accepted_routes_stop_host_merge_mark_landed_before_deferred_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\bf464eb57a844896967a2b4b30a227b7' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 109 | Cli_acceptance_auto_rebases_when_goal_branch_behind_main | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0cebabb6fcd846e8b9293acb042ab3c1' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 110 | Cli_acceptance_auto_rebases_when_goal_branch_behind_main | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\78ec9151680d46ea8fd457a5f093e820' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 111 | Cli_acceptance_auto_records_dogfood_entry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8d29929935a345449b274c2963ce94fb' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 112 | Cli_acceptance_auto_records_dogfood_entry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\40fc2ce0ee754246a07db93b2ce13c8f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 113 | Cli_acceptance_auto_verifies_from_git_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\adf28b64398c4851b0ddf1a1dec7b0f8' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 114 | Cli_acceptance_auto_verifies_from_git_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b78b1d998fbc40b98661f6263073803c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 115 | Cli_acceptance_blocks_merge_when_verification_fails | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3e06adcc03d3485daeff8b239ff5b2d1' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 116 | Cli_acceptance_blocks_merge_when_verification_fails | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3860fd6b19ed4af894b5dbe6b8e04410' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 117 | Cli_acceptance_cleanup_failure_reports_cleanup_blocker_after_landing_stays_accepted | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\861f236c72384c9180583e31a8102156' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 118 | Cli_acceptance_cleanup_failure_reports_cleanup_blocker_after_landing_stays_accepted | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2a67321a2c4e44a7af844305878248cc' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 119 | Cli_acceptance_defers_workspace_cleanup_after_successful_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\332723c18d0d4f98bbbd373957c35c13' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 120 | Cli_acceptance_defers_workspace_cleanup_after_successful_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5823bf85faac4480ae1c3fb1738ebb74' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 121 | Cli_acceptance_direct_merge_journals_landing_before_cleanup_for_later_missing_worktree_retry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7d11902c113a40b8a3210fe09905dce6' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 122 | Cli_acceptance_direct_merge_journals_landing_before_cleanup_for_later_missing_worktree_retry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6849b951723346fb82c01c41303cc8d1' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 123 | Cli_acceptance_does_not_auto_verify_without_committed_changes | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ecf4f887b36748fb87facbcc9d8e7d41' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 124 | Cli_acceptance_does_not_auto_verify_without_committed_changes | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\907752b1dc5c4d37a89c286b856f60df' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 125 | Cli_acceptance_evidence_auto_injects_policy_required_checks_and_merges | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0db44f49d17d4c29b7857e12667ab140' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 126 | Cli_acceptance_evidence_auto_injects_policy_required_checks_and_merges | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ca5426c5926649339ae311a7162df364' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 127 | Cli_acceptance_evidence_blocks_dirty_worktree_before_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2ded5d81bcc94288b42a806bfcd212f7' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 128 | Cli_acceptance_evidence_blocks_dirty_worktree_before_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9a0bc51cc137424d8ccc7bdf18dd588f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 129 | Cli_acceptance_evidence_blocks_generated_artifact_changes | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d03644f7ae124c6d82456272edbb69a8' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 130 | Cli_acceptance_evidence_blocks_generated_artifact_changes | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c0f2f93c1e62475bb741852f040acbe9' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 131 | Cli_acceptance_keep_workspace_retains_workspace_after_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\92afae4c6ba54f72b5bbe5dd213f2222' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 132 | Cli_acceptance_keep_workspace_retains_workspace_after_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1c8dc213f0f04beba451e3f65462b124' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 133 | Cli_acceptance_lands_after_transient_state_write_lock_releases | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\12a03721c0e44a0bbc6f5fe147ef112c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 134 | Cli_acceptance_lands_after_transient_state_write_lock_releases | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9a184a28b34943fe8e8d733ab90157af' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 135 | Cli_acceptance_lands_when_discord_token_is_invalid | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b3fdca62bdc3451aa34f85406cc209bb' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 136 | Cli_acceptance_lands_when_discord_token_is_invalid | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2517fe62f841483ba9e44c7b5f5a6e82' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 137 | Cli_acceptance_merge_conflict_blocks_and_leaves_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5cf3927bf05246d6bd10356cefe1ff35' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 138 | Cli_acceptance_merge_conflict_blocks_and_leaves_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c7cba0fb10264348980f1267e28a6567' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 139 | Cli_acceptance_merges_after_passing_verification | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\14a1005941994959b3a6909bcd7819f3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 140 | Cli_acceptance_merges_after_passing_verification | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7b047191a89d4a0e8d188cfadcdaee3a' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 141 | Cli_acceptance_no_record_skips_dogfood_entry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c329e3c4aa994de694a48ec7169e6b56' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 142 | Cli_acceptance_no_record_skips_dogfood_entry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2f7320f4926c44b5a59cb8aba0b303b6' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 143 | Cli_acceptance_non_accepted_verdict_does_not_stop_host_or_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2be3ca255a38477b9a61246b6ac75767' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 144 | Cli_acceptance_non_accepted_verdict_does_not_stop_host_or_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\cf52ea0993474ef782f9706381321ab5' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 145 | Cli_acceptance_queue_applies_ready_goals_sequentially | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7021a7312ec8400abafe32d3afc4b363' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 146 | Cli_acceptance_queue_applies_ready_goals_sequentially | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\210f564ee6bd4329aa9a545c83895d3a' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 147 | Cli_acceptance_queue_apply_persists_cleanup_after_outside_transaction_routing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0ae2224b7ac54bfaa2c385f506cd3240' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 148 | Cli_acceptance_queue_apply_persists_cleanup_after_outside_transaction_routing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\57583776fd5140149348a2eebc36e72e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 149 | Cli_acceptance_queue_holds_stale_branch_with_manual_merge_command | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d776fddac42f422dabe219b8335db363' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 150 | Cli_acceptance_queue_holds_stale_branch_with_manual_merge_command | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6e26c46093a54e32b77bdf4c32afc224' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 151 | Cli_acceptance_queue_safe_auto_holds_irreversible_actions | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\225b1f8e01424a028167b9bacdaa142d' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 152 | Cli_acceptance_queue_safe_auto_holds_irreversible_actions | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6070dd82c35e4429a0a582b61cf971a5' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 153 | Cli_acceptance_rebase_conflict_blocks_before_verification_and_restores_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\11cc55bf60b24ef5913ce2131c2b55d3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 154 | Cli_acceptance_rebase_conflict_blocks_before_verification_and_restores_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\45ee230604404cf1b264292b19a36133' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 155 | Cli_acceptance_rebases_before_verification_and_lands_verified_head | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1d20b1a412dd4d2bac7539cb98aa694a' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 156 | Cli_acceptance_rebases_before_verification_and_lands_verified_head | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6ca569f621d241f99d651b101160f85b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 157 | Cli_acceptance_rejects_stale_goal_state_before_merge_commit | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cecaec89604b479dad1c2ad0bacad572' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 158 | Cli_acceptance_rejects_stale_goal_state_before_merge_commit | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8627785c836e46b2816f4bd9607f16fe' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 159 | Cli_acceptance_rejects_stale_worktree_head_before_merge_commit | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\34580d59dfdb4479ae6b1461f8606f88' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 160 | Cli_acceptance_rejects_stale_worktree_head_before_merge_commit | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f443b888a173419ba04be32a5e37f350' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 161 | Cli_acceptance_releases_state_write_lock_during_verification | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c1382f5293a146dd891c9aff4a32a507' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 162 | Cli_acceptance_releases_state_write_lock_during_verification | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60a1518ca8c54c8aa0ccf3227ad97290' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 163 | Cli_acceptance_repair_clears_stale_failure_only_with_landing_and_cleanup_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2cd39532ebb84bb6a5dd93a59cd69084' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 164 | Cli_acceptance_repair_clears_stale_failure_only_with_landing_and_cleanup_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b0b1273d0a994dad9e83547dc9b2fe7f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 165 | Cli_acceptance_retry_treats_landed_cleaned_missing_worktree_as_accepted | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d83ce1ab81ce469dbcddb206a877c1dc' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 166 | Cli_acceptance_retry_treats_landed_cleaned_missing_worktree_as_accepted | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7660a26ec6d24071bc65c97fac087fc6' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 167 | Cli_acceptance_safe_auto_blocks_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\998d0c932eae4d5fa728c88d1dc50e96' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 168 | Cli_acceptance_safe_auto_blocks_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d90dd40019cc49a28a4b8888f261c73d' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 169 | Cli_acceptance_skip_verify_bypasses_verification_and_merges | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4089116ab2c74b45a7e22cc51cb97605' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 170 | Cli_acceptance_skip_verify_bypasses_verification_and_merges | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0454ebe7653043d682cdeeff9cfd7723' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 171 | Cli_acceptance_stop_host_timeout_blocks_before_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5cbe234e78114a59a0f17071a6e180ff' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 172 | Cli_acceptance_stop_host_timeout_blocks_before_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\26e232771a1240b596ae1dd41aaa7ecb' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 173 | Cli_acceptance_verification_timeout_blocks_before_merge_with_diagnostics | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e0be79a3a593460ea2afa0eb82e85687' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 174 | Cli_acceptance_verification_timeout_blocks_before_merge_with_diagnostics | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c41f43eee27347c895255ff65eb79872' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 175 | Cli_backlog_help_does_not_create_backlog_or_goal_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\886d2dc199c94b6dbb0686c4766e2e9b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 176 | Cli_backlog_help_does_not_create_backlog_or_goal_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1f94825b78234ae08ca510a51e3e3067' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 177 | Cli_backlog_list_filters_do_not_change_goal_or_worktree_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a7e3ce450ade4eacbe012ca9787429e3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 178 | Cli_backlog_list_filters_do_not_change_goal_or_worktree_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7a7ecf2860fa43eb972cec0ed9160ada' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 179 | Cli_cleanup_status_lists_pending_debt_and_empty_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d193211efd5d4df6a33133966cd02865' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 180 | Cli_cleanup_status_lists_pending_debt_and_empty_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2fe0c05cb74b471d918e75dd5f7c9d0d' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 181 | Cli_conduct_completed_goal_lands_through_persistent_runner_without_command_transaction | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\67ebbf95474c4f0490688c6a60ec2549' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 182 | Cli_conduct_completed_goal_lands_through_persistent_runner_without_command_transaction | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\28eb6bfdc6d148d8b25f6c9e290a60c3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 183 | Cli_conduct_scoped_reconciles_exited_dispatch_before_advance | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b4c563d48ad04855b30d01d6e15c9518' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 184 | Cli_conduct_scoped_reconciles_exited_dispatch_before_advance | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\76e3be8625f4447dbd12403dd42b8c50' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 185 | Cli_goal_mark_landed_force_deletes_branch_kept_by_safe_worktree_remove | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\61f21226bd6545efa8539dd4f273f374' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 186 | Cli_goal_mark_landed_force_deletes_branch_kept_by_safe_worktree_remove | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\89cf8c92855d4686a4bcb01158ad622d' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 187 | Cli_goal_mark_landed_passes_remaining_cleanup_budget_to_worktree_remove | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cbd609d4900d48f5b306310737906c0c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 188 | Cli_goal_mark_landed_passes_remaining_cleanup_budget_to_worktree_remove | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\708c3e6efcbf4e84b06c63c8b6d451b9' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 189 | Cli_goal_mark_landed_records_landed_and_defers_workspace_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4334ace3f11a41178ada045016b471c0' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 190 | Cli_goal_mark_landed_records_landed_and_defers_workspace_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5bb473c2d87147f08259de3e92a87183' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 191 | Cli_goal_mark_landed_records_landed_state_before_deferred_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b4815ffa8ff74752a9aedcd2d19a387f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 192 | Cli_goal_mark_landed_records_landed_state_before_deferred_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a8e29fcd01cb433e97cdd5be87692ae5' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 193 | Cli_goal_recovery_prints_cleanup_backoff_skip_until | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\561f8512219d4d3c8c5cfcd88a9bb313' is denied. | 102 | 1 | 5 | 7 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 194 | Cli_goal_recovery_prints_cleanup_backoff_skip_until | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8041ac6277b8409086171d944e055558' is denied. | 102 | 1 | 5 | 7 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 195 | Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 205 | 1 | 5 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 196 | Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c42653154f344f9186055ac6017a1a74' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 197 | Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\aaa3145f50e24e4ea9f58657debac2e0' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 198 | Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 205 | 1 | 5 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 199 | Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ef11f01ac3d84033942d19de6eee78fd' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 200 | Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\fe7e38b07e544bcb8d6abbdc15ab8298' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 201 | Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 205 | 1 | 5 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 202 | Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\04273a0be7344fea97095ce0bca750ea' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 203 | Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1a23b111620540c3b7e176583341f309' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 204 | Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_times_out | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 103 | 1 | 0 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 205 | Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 205 | 1 | 5 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 206 | Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cdd145d57b8742619c83ce19af8a70d8' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 207 | Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60753be7d18749758793bd15fdf03412' is denied. | 205 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 208 | Cli_lifecycle_simple_goal_safe_auto_stops_before_acceptance | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\50a079619cc041119b166e08325abf2b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 209 | Cli_lifecycle_simple_goal_safe_auto_stops_before_acceptance | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6c5a643a1d49427c81456d2a0f800e95' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 210 | Cli_note_confirms_and_keeps_status_across_task_states | Assert.Contains() Failure: Filter not matched in collection | 105 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 211 | Cli_note_preserves_task_and_allows_subscription_dispatch_and_retry | Assert.Contains() Failure: Filter not matched in collection | 105 | 1 | 0 | 1 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 212 | Cli_profile_dispatch_auto_creates_workspace_when_missing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\34e987bbfb0d4743a73e2f3433af5c36' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 213 | Cli_profile_dispatch_auto_creates_workspace_when_missing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a38b852540054082b4f4348a40ec0c1e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 214 | Cli_project_create_seeds_independent_runnable_configuration | System.InvalidOperationException : Goal 'ca5ac37b' task 1: Subscription preflight failed: profile: codex-spark; dispatch-lane: codex-spark; model-selection: cheap-lane: Developer small-task uses codex-spark/gpt-5.3-codex-spark; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.3-codex-spark; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'codex-spark' is a real launcher; ok: worker profile 'codex-spark' pins selected model; reasoning-effort: low (base); ok: worker profile 'codex-spark' pins selected reasoning when required; capability: workspace-write - Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and stdin prompt delivery are configured.; blocked: missing required local skill(s): dotnet-windows-build-hygiene at .agents\skills\dotnet-windows-build-hygiene\SKILL.md, orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md, verification-before-completion at .agents\skills\verification-before-completion\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: goal lease not yet created artifacts=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-93cbca1a6446432da188ab0030e6d18b\goals\ca5ac37b\artifacts; warning: worktree cleanliness unavailable before dispatch (fatal: not a git repository: C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/mcg-orchestrator-tests/d93c4b05f45e4854bd58b79f1820fd0f/.orchestrator-worktrees/ca5ac37b/..); verify git status from the goal workspace; warn: git metadata index_lock=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-orchestrator-tests\d93c4b05f45e4854bd58b79f1820fd0f\.orchestrator-worktrees\ca5ac37b\.git\index.lock; current_identity=miles; current_process_can_write=False; worker_git_write=unavailable; commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf; git metadata warning: git rev-parse --git-path index.lock failed; reviewer-scope: changed-file contract not required for non-Reviewer role; reviewer-merge-tree: merge-tree contract not required for non-Reviewer role | 81 | 1 | 0 | 1 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 215 | Cli_recover_reconciles_dead_running_task_with_exit_file | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fcebe8c4e6d14fb0a7a69c07fc3fbb29' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 216 | Cli_recover_reconciles_dead_running_task_with_exit_file | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ffe350620b7e428f8b00f92a0501de17' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 217 | Cli_recover_resets_cancelled_tasks_without_disturbing_completed_or_running_tasks | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a0ac979db5aa492fadd54caa1cccaf4d' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 218 | Cli_recover_resets_cancelled_tasks_without_disturbing_completed_or_running_tasks | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d5bb8b14f25f4169a75970569563e951' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 219 | Cli_recover_resets_failed_task_to_dispatchable | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\453ac4e3dbf6443c827a043febeb5ea5' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 220 | Cli_recover_resets_failed_task_to_dispatchable | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\10bdf14caa1f4204907e588b1898da35' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 221 | Cli_repo_process_commands_skip_persistent_state_loading | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\662944907bf24b2d8adbf263801aeeb4' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 222 | Cli_repo_process_commands_skip_persistent_state_loading | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a49ce9db8af64516919ec953679305d7' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 223 | Cli_startup_help_and_backlog_commands_skip_orphan_worktree_cleanup | Assert.DoesNotContain() Failure: Sub-string found | 106 | 1 | 0 | 0 | 1 | 2026-07-31 | 2026-07-31 | single-goal-unresolved |
| 224 | Cli_text_file_task_commands_record_file_content | Assert.Contains() Failure: Filter not matched in collection | 105 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 225 | Cli_workspace_command_creates_and_removes_goal_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b360f97fff644f5f989b2a88106f6bea' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 226 | Cli_workspace_command_creates_and_removes_goal_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\26a2059abd914968821128551a115135' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 227 | Cli_workspace_help_does_not_create_goal_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6ccedf0f7f7e460bae5794b41442dbf4' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 228 | Cli_workspace_help_does_not_create_goal_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e5b2ec1d6fbb498abb3f6a8dc80c64c6' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 229 | Cli_workspace_rebase_updates_clean_stale_goal_branch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\452dca1269274e7ebb68870bce6cce8a' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 230 | Cli_workspace_rebase_updates_clean_stale_goal_branch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f66dbd67402147d3a69210b04f29a4b0' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 231 | Cli_workspace_remove_can_target_non_current_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d02103aaeff74f5c9f07f1c6f3975046' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 232 | Cli_workspace_remove_can_target_non_current_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5c5465079ddc4bc78212f349efe1648c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 233 | Cli_workspace_remove_does_not_complete_verified_goal_without_landing_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a8b453955dc84e48aec39c6b0b0a99af' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 234 | Cli_workspace_remove_does_not_complete_verified_goal_without_landing_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\de4a0a33daa3436d99884f8d49dd6663' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 235 | Cli_workspace_remove_force_terminal_cleanup_bypasses_escalated_backoff | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5d02f23c73de4f9e98150e732197ae15' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 236 | Cli_workspace_remove_force_terminal_cleanup_bypasses_escalated_backoff | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1fa273d77d15419b9c97e71bf79c38cb' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 237 | Cli_workspace_remove_keeps_stale_acceptance_failure_without_landing_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\54dd48b9418c4563a0196c1426f86cf3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 238 | Cli_workspace_remove_keeps_stale_acceptance_failure_without_landing_evidence | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8b23f7f198384bfd8856f9d3d1d5ae50' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 239 | Cli_workspace_remove_persists_provider_session_retirement | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6ee9c2533e3449be8552b74c8ff93023' is denied. | 95 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 240 | Cli_workspace_remove_persists_provider_session_retirement | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e66502fc43bf4017930cb27e2c604506' is denied. | 95 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 241 | Cli_workspace_remove_prints_cleanup_backoff_skip_until | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fada0bda04f344fe9dbdf6015ecec13f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 242 | Cli_workspace_remove_prints_cleanup_backoff_skip_until | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c89327fd643b4f48901c6710e7df9aa5' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 243 | Cli_workspace_remove_repairs_landed_cleaned_stale_acceptance_failure | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\083478e58ebf44d786d4b1a692a73365' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 244 | Cli_workspace_remove_repairs_landed_cleaned_stale_acceptance_failure | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\4a814b10156144aa8f140d5e52f6c7e2' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 245 | Cli_workspace_remove_safe_auto_blocks_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e5f5d83a2b2743e5a62735d8b25c6329' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 246 | Cli_workspace_remove_safe_auto_blocks_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\beae10acbb65450d817f0eb108fdaa61' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 247 | CliCommandTestsGoalRevision.CliReviseAcceptsBriefFromStandardInput | System.ArgumentException : Unknown option '-'. | 54 | 1 | 0 | 1 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 248 | CliCommandTestsPersistentRunnerCommands.ConductLoop_EvictedGoal_LaterIntentKeepsReason | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 71 | 1 | 0 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 249 | CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Cancelled, enqueueIntent: False) | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 71 | 1 | 0 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 250 | CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Cancelled, enqueueIntent: True) | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 71 | 1 | 0 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 251 | CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Failed, enqueueIntent: False) | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 71 | 1 | 0 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 252 | CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Superseded, enqueueIntent: False) | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 71 | 1 | 0 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 253 | CliCommandTestsPersistentRunnerCommands.ConductLoop_LiveGoal_ReloadsAndWalks | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process. | 71 | 1 | 0 | 1 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 254 | CliPersistentStateRunner_goal_create_rejects_competing_backlog_link_atomically | Assert.Null() Failure: Value is not null | 43 | 1 | 0 | 7 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 255 | CodexEgressProxy_tunnels_bytes_end_to_end | Assert.Equal() Failure: Values differ | 70 | 1 | 0 | 0 | 1 | 2026-07-28 | 2026-07-28 | single-goal-unresolved |
| 256 | Conductor default subscription start does not spawn scoped real worker process | Assert.IsType() Failure: Value is not the exact type | 102 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 257 | Conductor_cleanup_records_cleanup_needed_without_deleting_on_critical_path | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e5595fe0a9764dcc81a69b03dca8bee2' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 258 | Conductor_cleanup_records_cleanup_needed_without_deleting_on_critical_path | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a04215f87309446f8ebd319c38944c4a' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 259 | ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_loads_valid_file | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\0f96358f-d36a-4552-9d28-b5214731b014'. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 260 | ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_loads_valid_file | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\442f73a0-a4be-46f8-a3f1-34c2021c8a56'. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 261 | ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_returns_Conservative_when_no_file | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\c59d623c-d331-4f08-9459-2f817ec57de3'. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 262 | ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_returns_Conservative_when_no_file | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\e0974be0-1949-4be7-9839-3b41ff2aa4fb'. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 263 | ConductorBatchLoopTests.TransientSqliteCheckpoint_HeldGoalRecoversWithoutRepeatingSideEffect(sqliteErrorCode: 5) | Assert.Single() Failure: The collection did not contain any matching items | 10 | 1 | 0 | 0 | 1 | 2026-08-11 | 2026-08-11 | single-goal-unresolved |
| 264 | ConductorBatchLoopTests.TransientSqliteCheckpoint_HeldGoalRecoversWithoutRepeatingSideEffect(sqliteErrorCode: 6) | Assert.Single() Failure: The collection did not contain any matching items | 10 | 1 | 0 | 0 | 1 | 2026-08-11 | 2026-08-11 | single-goal-unresolved |
| 265 | ConductorDriver_unparseable_typed_evidence_finding_reports_typed_refusal | Assert.Equal() Failure: Values differ | 59 | 1 | 0 | 3 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 266 | ConductorDriverTests.TesterStructuredRequestReceivesFindingBoundReceiptInRetryContext | Assert.DoesNotContain() Failure: Sub-string found | 59 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 267 | ConductorLoopHandoff_successor_survives_parent_job_exit_and_emits_loop_start | Assert.Equal() Failure: Values differ | 100 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 268 | ConductorSelfRelaunch_real_binary_build_self_check_and_handoff | build merged conductor failed exit=1 timedOut=False: C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.Core\obj' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj]    Build FAILED.      0 Warning(s)      1 Error(s)    Time Elapsed 00:00:01.15 | 91 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 269 | ConductorSelfRelaunch_real_binary_build_self_check_and_handoff | build merged conductor failed exit=1 timedOut=False: C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.Core\obj\38010a3a-ed2b-47f0-a0a4-532c4a5efcd8.tmp' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj]    Build FAILED.      0 Warning(s)      1 Error(s)    Time Elapsed 00:00:01.22 | 91 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 270 | Dashboard_api_subscription_dispatch_acknowledges_limit_review | Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: profile: codex-spark; dispatch-lane: codex-spark; model-selection: cheap-lane: Developer small-task uses codex-spark/gpt-5.3-codex-spark; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.3-codex-spark; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'codex-spark' is a real launcher; ok: worker profile 'codex-spark' pins selected model; reasoning-effort: low (base); ok: worker profile 'codex-spark' pins selected reasoning when required; capability: workspace-write - Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and stdin prompt delivery are configured.; blocked: missing required local skill(s): dotnet-windows-build-hygiene at .agents\skills\dotnet-windows-build-hygiene\SKILL.md, orchestrator-dogfood at .agents\skills\orchestrator-dogfood\SKILL.md, orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md, verification-before-completion at .agents\skills\verification-before-completion\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: goal lease not yet created artifacts=C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\e782a7b8\artifacts; warning: worktree cleanliness unavailable before dispatch (fatal: not a git repository: C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/mcg-orchestrator-tests/418d177949a3496fa2af3d1456c25490/.orchestrator-worktrees/e782a7b8/..); verify git status from the goal workspace; warn: git metadata index_lock=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-orchestrator-tests\418d177949a3496fa2af3d1456c25490\.orchestrator-worktrees\e782a7b8\.git\index.lock; current_identity=miles; current_process_can_write=False; worker_git_write=unavailable; commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf; git metadata warning: git rev-parse --git-path index.lock failed; reviewer-scope: changed-file contract not required for non-Reviewer role; reviewer-merge-tree: merge-tree contract not required for non-Reviewer role | 105 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 271 | Dashboard_retry_records_capability_warning_for_gh_cli_instruction | Assert.Contains() Failure: Filter not matched in collection | 109 | 1 | 0 | 0 | 1 | 2026-07-27 | 2026-07-27 | single-goal-unresolved |
| 272 | Developer_context_receives_complete_ingested_Planner_plan_without_paid_start | System.InvalidOperationException : git commit -m Track repository ignore rules failed: | 83 | 1 | 0 | 1 | 1 | 2026-08-05 | 2026-08-05 | single-goal-unresolved |
| 273 | DispatchProcessHost_ApplyWorkerSandbox_creates_bin_shims_and_prepends_child_path_only | System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-apply-sandbox-test\933bee3d1e8f436da29d0e90bcad8f74'. | 104 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 274 | DispatchProcessHost_ApplyWorkerSandbox_leaves_unknown_provider_without_provider_home | System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-unknown-provider-sandbox-test\7a8759fd59ad477ea77a441d062f3e34'. | 104 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 275 | DispatchProcessHost_ApplyWorkerSandbox_scopes_codex_home_to_codex_provider | System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-provider-sandbox-test\233e3b46d3de4036876b95116ed53994'. | 104 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 276 | DispatchProcessHost_heartbeat_cpu_and_pids_reflect_wrapped_grandchild | System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-dispatch-host-grandchild-tests\360dd58ad7154a0a813866a334c64a04\grandchild-reap-probe.log' because it is being used by another process. | 90 | 1 | 0 | 1 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 277 | DispatchProcessHost_read_only_Claude_after_writable_round_denies_mutation_and_emits_worker_result | Writable Claude sandbox dispatch exited 1: {"event":"sandbox-prep","phase":"start","timestamp":"2026-08-10T21:12:32.5602221\u002B00:00","startedAt":"2026-08-10T21:12:32.5598122\u002B00:00","workingDirectory":"C:\\Users\\miles\\AppData\\Local\\Temp\\Low\\mcg-tests\\mcg-orchestrator-tests\\b10aff22fd0842aaa3b0695cbae90fb0\\worktree"} | 0 | 1 | 0 | 1 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 278 | DotnetBuildEnvironmentManager_no_holder_artifact_prep_lock_retries_and_acquires | Assert.IsType() Failure: Value is not the exact type | 109 | 1 | 0 | 0 | 1 | 2026-07-21 | 2026-07-21 | single-goal-unresolved |
| 279 | DotnetBuildEnvironmentManagerTests.FocusedRunner_AllSlotsHeld_ReportsNoSlotWithoutStartingDotnet | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\50055382\isolated\build-slots' is denied. | 69 | 1 | 0 | 4 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 280 | DotnetBuildEnvironmentManagerTests.FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\394455b7\isolated\goals\budget-test\focused-artifacts\build-0\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug' is denied. | 63 | 1 | 0 | 12 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 281 | DotnetBuildEnvironmentManagerTests.FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\ca52edc9\LocalLow\mcg-dotnet-isolated\goals\aaaaaaaa\focused-artifacts\build-0\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug' is denied. | 69 | 1 | 0 | 4 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 282 | Equal SHA identity violations salvage the prior ledger and converge normally | Assert.Equal() Failure: Values differ | 32 | 1 | 0 | 0 | 1 | 2026-08-13 | 2026-08-13 | single-goal-unresolved |
| 283 | ExcessWorkerRoundAnalysisScriptTests.TaskOutcomeReason_WithoutReadyBlockedProducer_IsNotProviderEvidence | System.ComponentModel.Win32Exception : An error occurred trying to start process 'pwsh' with working directory 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8304\mcg-excess-round-tests\015f3a05253c4d6e9cf9cfcb07402261'. Access is denied. | 37 | 1 | 0 | 0 | 1 | 2026-08-18 | 2026-08-18 | single-goal-unresolved |
| 284 | ExecuteAssignedTask_rejects_subscription_dispatch_evidence_without_calling_provider | Assert.True() Failure | 121 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 285 | FailureTriagePlanner_uses_typed_preflight_outcome_for_permission_repair | System.InvalidOperationException : git commit -m Seed failed: | 68 | 1 | 0 | 0 | 1 | 2026-08-05 | 2026-08-05 | single-goal-unresolved |
| 286 | Gated_progressive_glance_preserves_worktree_and_allows_acceptance_completion | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\27563d1048b147dbb10f201db4664297' is denied. | 71 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 287 | Gated_progressive_glance_preserves_worktree_and_allows_acceptance_completion | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1197e1639ad046aaa68c11e13f2c2102' is denied. | 71 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 288 | GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p73e0\snapshot-status-3d2a6a53aa2d4ae99520e678346c8697\status-child.pid | 106 | 1 | 0 | 0 | 1 | 2026-08-21 | 2026-08-21 | load-dependent |
| 289 | GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p7450\snapshot-status-b077f240aa8749429c242b5d1f132397\status-child.pid | 106 | 1 | 0 | 0 | 1 | 2026-08-21 | 2026-08-21 | load-dependent |
| 290 | GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p74bc\snapshot-status-6d5abd64cff1459aa1ffb581ee52adfc\status-child.pid | 106 | 1 | 0 | 0 | 1 | 2026-08-21 | 2026-08-21 | load-dependent |
| 291 | GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8e1c\snapshot-status-8fa08667c5c44c91b1d9f8752808b488\status-child.pid | 106 | 1 | 0 | 0 | 1 | 2026-08-21 | 2026-08-21 | load-dependent |
| 292 | GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\pbe4\snapshot-status-2409b775a46d47cf99a8aa2193dad8c2\status-child.pid | 106 | 1 | 0 | 0 | 1 | 2026-08-21 | 2026-08-21 | load-dependent |
| 293 | Goal_scope_collision_advisory_keeps_goal_creation_nonblocking_without_dispatch | System.InvalidOperationException : GOAL_CREATE_DELIVERY_INCOMPLETE goal=94064117a79042da9e771451d5ebed06 retry="goal-delivery-retry 94064117a79042da9e771451d5ebed06" detail=Assert.Equal() Failure: Values differ | 29 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 294 | GoalAbandon_removes_terminal_worktree_in_one_cleanup_cycle | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d9937b8a8d204279b0fef3b5cf74aff8' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 295 | GoalAbandon_removes_terminal_worktree_in_one_cleanup_cycle | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8c03b5d523a748db9763d2754b62d3f4' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 296 | GoalAcceptanceVerifier_full_shards_override_runs_all_policy_shards | Assert.Equal() Failure: Values differ | 109 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 297 | GoalAcceptanceVerifier_partition_verdict_cache_aggregate_ignores_unrelated_check_failures | Assert.Equal() Failure: Values differ | 103 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 298 | GoalAcceptanceVerifier_partition_verdict_cache_backstop_forces_full_rerun | Assert.Equal() Failure: Values differ | 102 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 299 | GoalAcceptanceVerifier_partition_verdict_cache_backstop_forces_full_rerun | Assert.False() Failure | 102 | 1 | 0 | 0 | 1 | 2026-08-15 | 2026-08-15 | single-goal-unresolved |
| 300 | GoalAcceptanceVerifier_partition_verdict_cache_filter_hash_change_runs_only_changed_partition | Assert.Equal() Failure: Values differ | 103 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 301 | GoalAcceptanceVerifier_partition_verdict_cache_invalidates_on_candidate_or_main_sha_change | Assert.Equal() Failure: Values differ | 103 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 302 | GoalAcceptanceVerifier_partition_verdict_cache_records_later_partitions_after_early_failure | Assert.Equal() Failure: Values differ | 102 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 303 | GoalAcceptanceVerifier_partition_verdict_cache_records_later_partitions_after_early_failure | Assert.False() Failure | 102 | 1 | 0 | 0 | 1 | 2026-08-15 | 2026-08-15 | single-goal-unresolved |
| 304 | GoalAcceptanceVerifier_partition_verdict_cache_reuses_green_partitions_on_reroll | Assert.Equal() Failure: Values differ | 103 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 305 | GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease | Assert.Equal() Failure: Values differ | 86 | 1 | 0 | 0 | 1 | 2026-08-15 | 2026-08-15 | single-goal-unresolved |
| 306 | GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease | Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-a66722f6477e40e6b541e1beb210d24f\runs\p28736-build-1\artifacts\bin\Mcg.AgentOrchestrator.Core\debug\Mcg.AgentOrchestrator.Core.dll; holder=unknown. | 86 | 1 | 0 | 0 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 307 | GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease | Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-c7c6751b99c749229a9251b0bbfc4eb6\runs\p33484-build-0\artifacts\bin\Mcg.AgentOrchestrator.Core\debug\Mcg.AgentOrchestrator.Core.dll; holder=unknown. | 86 | 1 | 0 | 0 | 1 | 2026-08-01 | 2026-08-01 | single-goal-unresolved |
| 308 | GoalAcceptanceVerifier_runs_core_and_dependent_infrastructure_shards_for_core_scope | Assert.Equal() Failure: Values differ | 109 | 1 | 0 | 3 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 309 | GoalAcceptanceVerifier_safety_valves_force_full_policy_shards | Assert.Equal() Failure: Values differ | 109 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 310 | GoalAcceptanceVerifier_selects_infrastructure_tests_from_default_plan | Assert.Equal() Failure: Values differ | 109 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 311 | GoalAcceptanceVerifier_slot_gate_prefers_completed_red_test_verdict_after_transient_build_lock | Assert.Matches() Failure: Pattern not found in value | 109 | 1 | 0 | 0 | 1 | 2026-07-21 | 2026-07-21 | single-goal-unresolved |
| 312 | GoalAcceptanceVerifier_substitutes_solution_check_with_partitioned_infrastructure_checks_for_infra_scope | Assert.Equal() Failure: Values differ | 109 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 313 | GoalAcceptanceVerifier_substitutes_solution_check_with_union_for_core_and_infra_scope | Assert.Equal() Failure: Values differ | 109 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 314 | GoalAcceptanceVerifier_test_tamper_guard_absent_when_no_test_files_in_diff | Assert.Equal() Failure: Values differ | 109 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 315 | GoalAcceptanceVerifier_within_attempt_rerun_tolerates_flaky_partition | Assert.Equal() Failure: Values differ | 99 | 1 | 0 | 0 | 1 | 2026-08-15 | 2026-08-15 | single-goal-unresolved |
| 316 | GoalAcceptanceVerifierDotnetBuildSlotTests.FocusedEvidence_DualArmRealChecksClassifyVacuousValidAndInconclusive | Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\NuGet\v3-cache\670c1461c29885f9aa22c281d8b7da90845b38e4$ps_api.nuget.org_v3_index.json\vuln_index.dat-new; holder=unknown. | 0 | 1 | 0 | 3 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 317 | GoalHealthEvaluator_prioritizes_dirty_worktree_before_next_action | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cd2a16598f1f4b32affe2a4e952e9328' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 318 | GoalHealthEvaluator_prioritizes_dirty_worktree_before_next_action | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9cfdb0b2f92b434e97d809535b3d449f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 319 | GoalHealthEvaluator_scores_ready_failed_stalled_provider_limited_and_healthy_states | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4aa122fbcbcf4695b7d41a77b0e4781a' is denied. | 101 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 320 | GoalHealthEvaluator_scores_ready_failed_stalled_provider_limited_and_healthy_states | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8df82c820f0c456687a0259b404f1101' is denied. | 101 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 321 | GoalRefinementTests.TryLaunchFirstPending_repairs_missing_outbox_then_launches | Assert.Equal() Failure: Values differ | 16 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 322 | Goals_subscribe_once_waits_through_unrelated_events_and_emits_one_matching_human_event | System.TimeoutException : The operation has timed out. | 104 | 1 | 0 | 1 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 323 | GoalWorktree_state_reads_construct_repository_while_write_lock_is_held | Microsoft.Data.Sqlite.SqliteException : SQLite Error 5: 'database is locked'. | 101 | 1 | 5 | 0 | 1 | 2026-07-20 | 2026-07-20 | single-goal-unresolved |
| 324 | GoalWorktree_state_reads_construct_repository_while_write_lock_is_held | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d6afca0a6db94b69b995f8c3bcf8b691' is denied. | 101 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 325 | GoalWorktree_state_reads_construct_repository_while_write_lock_is_held | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e5e1ac89f9ca4398a0f17ceb21a581eb' is denied. | 101 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 326 | GoalWorktreeOrphanSweepScheduler_sweep_now_deletes_orphaned_worktree_directory | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b766228bff4a4c2680057fd0ee301d62' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 327 | GoalWorktreeOrphanSweepScheduler_sweep_now_deletes_orphaned_worktree_directory | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\818653b0b8ce4d9083339f53184ca0a7' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 328 | GoalWorktrees rechecks mutation blocker immediately before fast-forward | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fcf6cca9ff63445e91990335d93d48f3' is denied. | 79 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 329 | GoalWorktrees rechecks mutation blocker immediately before fast-forward | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d60d31697b4b4d06baebc2929b052068' is denied. | 79 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 330 | GoalWorktrees_acceptance_failed_retry_clears_completed_task_evidence_before_redispatch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\65841d503971444e9fc61d443fd76967' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 331 | GoalWorktrees_acceptance_failed_retry_clears_completed_task_evidence_before_redispatch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7ebe3fc9a9dc4eb8913b2adfb1f522de' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 332 | GoalWorktrees_cleanup_failure_logs_warning_and_reports_resumable_leftover | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4f7f966410354c14b34947873412cae0' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 333 | GoalWorktrees_cleanup_failure_logs_warning_and_reports_resumable_leftover | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e4c07b19d1864328b9cff710a1f54e48' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 334 | GoalWorktrees_commit_on_behalf_after_worker_commit_leaves_worktree_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3ab8971b28004187af44423982982097' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 335 | GoalWorktrees_commit_on_behalf_after_worker_commit_leaves_worktree_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e8055b2ebff74b3a808dcc79fc584039' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 336 | GoalWorktrees_creates_and_resolves_worktree_per_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\117ffd553db0482197efc22f55936205' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 337 | GoalWorktrees_creates_and_resolves_worktree_per_goal | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48d6861e757644f3bf9df927cffebe3d' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 338 | GoalWorktrees_ensure_clears_existing_orphan_and_retries_once | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ffd8c382807a4e98b97a0f65d2d51e67' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 339 | GoalWorktrees_ensure_clears_existing_orphan_and_retries_once | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\45d23c01ea96464581d232002b29832f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 340 | GoalWorktrees_ensure_fast_forwards_undriven_stale_worktree_to_base | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f7740c3cbdc843afad6643dd671cfb95' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 341 | GoalWorktrees_ensure_fast_forwards_undriven_stale_worktree_to_base | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\772cb0b2067f425cb3a39a3925fb30e4' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 342 | GoalWorktrees_ensure_leaves_driven_divergent_worktree_untouched | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7ffeefa76db54549bdb96ddc4df78ce3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 343 | GoalWorktrees_ensure_leaves_driven_divergent_worktree_untouched | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3b716571ad2041368d1d3e8b5b1eedad' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 344 | GoalWorktrees_fast_forwards_goal_branch_on_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\07c668af8cd9426d81a20965dad25068' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 345 | GoalWorktrees_fast_forwards_goal_branch_on_merge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9c84900e9ecd4b7f93c72cffc00dcf22' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 346 | GoalWorktrees_git_metadata_access_marks_low_integrity_worker_non_committing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ea665fbaa58a44949346d41a1ea19b90' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 347 | GoalWorktrees_git_metadata_access_marks_low_integrity_worker_non_committing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2862f288f7dd490ea456793dc942a915' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 348 | GoalWorktrees_git_metadata_access_resolves_linked_index_lock_path | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3427a35c704a4ac295d73e17a30aab25' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 349 | GoalWorktrees_git_metadata_access_resolves_linked_index_lock_path | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1f3a87411c8e4f6d87ba65ab96f1b450' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 350 | GoalWorktrees_merge_returns_null_without_goal_branch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f5f3d2bfcda440df8e30a4013e028829' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 351 | GoalWorktrees_merge_returns_null_without_goal_branch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d069340eec764f099c9cb5c0e4e3907e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 352 | GoalWorktrees_owned_ephemeral_sweep_persists_cleanup_needed_backoff | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e32886f4c315424199ffa6747ac3e769' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 353 | GoalWorktrees_owned_ephemeral_sweep_persists_cleanup_needed_backoff | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\746ce1980d07415db92f62b946e9c36f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 354 | GoalWorktrees_owned_ephemeral_sweep_removes_goal_context_temp_and_scratch_only | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\61cb20fa2d1b4ce798f96c2c7a070908' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 355 | GoalWorktrees_owned_ephemeral_sweep_removes_goal_context_temp_and_scratch_only | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c6355e4fb3bb4c219bf01bcb0c52aea3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 356 | GoalWorktrees_rebases_stale_branch_onto_main_when_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\484077db74654aa0a38b8075c7b4f0b5' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 357 | GoalWorktrees_rebases_stale_branch_onto_main_when_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9d3e012c0a5e47d6bee8e4b0b9eafab0' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 358 | GoalWorktrees_refuses_rebase_when_worktree_dirty | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\291ced4dc7f3437688507ac714bd2f6f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 359 | GoalWorktrees_refuses_rebase_when_worktree_dirty | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\92256f5bd1a84fb095cafae90d20701f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 360 | GoalWorktrees_remove_aborts_branch_delete_when_branch_unmerged_at_deletion_time | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f30b55f2d6e74158b319d592eef6492b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 361 | GoalWorktrees_remove_aborts_branch_delete_when_branch_unmerged_at_deletion_time | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\573a1032300b487ca504ff2039b68466' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 362 | GoalWorktrees_remove_defers_when_acl_reset_is_access_denied | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\64251fb39b204367b04728921685d4bd' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 363 | GoalWorktrees_remove_defers_when_acl_reset_is_access_denied | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ce8d2d90c36a46e58b506bf0e51eb3a3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 364 | GoalWorktrees_remove_defers_when_build_server_cleanup_exhausts_budget | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\bc266a7e30194b47a77048b31807770e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 365 | GoalWorktrees_remove_defers_when_build_server_cleanup_exhausts_budget | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\4aaeca06d4b64820be0b5fefe8b24329' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 366 | GoalWorktrees_remove_deletes_receipt_only_dirty_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e01b629324954cedb96f68624d6e1bb1' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 367 | GoalWorktrees_remove_deletes_receipt_only_dirty_worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7402b9bf8fc44879b1285fc96303b14c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 368 | GoalWorktrees_remove_failure_records_cleanup_needed_without_throwing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1f5564e9c2a74ce893bec113f23e269b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 369 | GoalWorktrees_remove_failure_records_cleanup_needed_without_throwing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ba6508cb30254485ab5496b857ac8ebc' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 370 | GoalWorktrees_remove_honors_cleanup_needed_backoff_when_no_lock_holder_was_recorded | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\60112fa2a02748dbb5f157693bc6b568' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 371 | GoalWorktrees_remove_honors_cleanup_needed_backoff_when_no_lock_holder_was_recorded | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8c9a8bda39d84aeb8222db9614507ecb' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 372 | GoalWorktrees_remove_invokes_build_server_shutdown_before_directory_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5bdbcb58d5b146cdb8a19d1af459fb30' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 373 | GoalWorktrees_remove_invokes_build_server_shutdown_before_directory_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\75535b3f00cf487b8b2e8dbe5f5b3860' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 374 | GoalWorktrees_remove_is_idempotent_when_already_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\350edbb11ee140919f216911f5e4628b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 375 | GoalWorktrees_remove_is_idempotent_when_already_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6e4a22a244854993b67c03a4db53a18e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 376 | GoalWorktrees_remove_kills_unprotected_recorded_worker_process | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\dcb0578e417d409886d79809339d116c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 377 | GoalWorktrees_remove_kills_unprotected_recorded_worker_process | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\164e06d21ff346c580dbbd3fe5862b88' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 378 | GoalWorktrees_remove_partial_result_identifies_branch_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\227fc840bb24460aa68cb5eb3a3df65e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 379 | GoalWorktrees_remove_partial_result_identifies_branch_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\fcdf61e643a34646b3845be91b8c5493' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 380 | GoalWorktrees_remove_persists_cleanup_needed_when_goal_artifacts_delete_fails | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8522561c2b4d45ccb3002c094f71c036' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 381 | GoalWorktrees_remove_persists_cleanup_needed_when_goal_artifacts_delete_fails | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\75a6434fde174daba6e66516cd0f6d11' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 382 | GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8f39cc9bb4c64f5994d09af201f479b2' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 383 | GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\69b58d4375c84bfdbafc4272a5dcbeac' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 384 | GoalWorktrees_remove_rechecks_branch_ancestry_before_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b534c46760054ddd87f2969d224c1f07' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 385 | GoalWorktrees_remove_rechecks_branch_ancestry_before_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3d2a8d9585cb43daaec1cc6d3f87ab27' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 386 | GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b8cc5b7338ed491494469cdbb36b108c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 387 | GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\88c54e95050a49e58fad0dc5f6833bfd' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 388 | GoalWorktrees_remove_reports_owned_ephemeral_cleanup_leftover | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\10c5d22fc7004396961423f4704b2f34' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 389 | GoalWorktrees_remove_reports_owned_ephemeral_cleanup_leftover | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\99a58993e67d472d81ff8cb98a44bcdd' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 390 | GoalWorktrees_remove_reports_unregistered_leftover_directory_as_incomplete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c9fa33aea919496fa284afe16a90c08e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 391 | GoalWorktrees_remove_reports_unregistered_leftover_directory_as_incomplete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\adbe5d41b3eb4166bae0ed5732957aa4' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 392 | GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5360787df2e5483a91c3c3ea27dcb5a4' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 393 | GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7d987b4497db40dc921a0e472cd6d188' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 394 | GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c82a8da6c4504c41b20a26e6100264c7' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 395 | GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c5f0985e96184e07885fa25aeaa40db7' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 396 | GoalWorktrees_remove_retries_and_succeeds_when_transient_lock_releases | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\744cad95166348739c39f3bd1232aab5' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 397 | GoalWorktrees_remove_retries_and_succeeds_when_transient_lock_releases | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\bf836c56a59f4c91a0a65c350a852c6b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 398 | GoalWorktrees_remove_retries_cleanup_needed_immediately_after_lock_release | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8d5762d0de094510977bbb2c722961f8' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 399 | GoalWorktrees_remove_retries_cleanup_needed_immediately_after_lock_release | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\58c2cc0cba2e4ef59c80f87a903c9b06' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 400 | GoalWorktrees_remove_skips_protected_recorded_worker_process | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\765d8f6e55904de78e8b58f3b8da39a2' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 401 | GoalWorktrees_remove_skips_protected_recorded_worker_process | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c615e041b4334a7f89ef110a885e50e9' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 402 | GoalWorktrees_remove_threads_remaining_budget_into_nested_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\185a2cf13fa344bb85c4a4be2d489afe' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 403 | GoalWorktrees_remove_threads_remaining_budget_into_nested_cleanup | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e985c65b9f2946cf83b390aed3885f59' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 404 | GoalWorktrees_reports_conflict_files_and_aborts_rebase | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\df1f288fcb4a4631822b81587af3e2ea' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 405 | GoalWorktrees_reports_conflict_files_and_aborts_rebase | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6a4ff7e4fd954816b889eb725df3b5ca' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 406 | GoalWorktrees_resolve_all_matches_per_goal_try_resolve | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e89838358959402fb2e56fdd7adfa4b2' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 407 | GoalWorktrees_resolve_all_matches_per_goal_try_resolve | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\410c572a5513496ca0ca232375b43b46' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 408 | GoalWorktrees_suggests_manual_merge_when_branches_diverge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\87589b5df3424bde8e914cac9b2cf196' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 409 | GoalWorktrees_suggests_manual_merge_when_branches_diverge | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5f75b3b9870e4b05aac121dedbc201c3' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 410 | GoalWorktrees_sweep_deletes_orphaned_worktree_directory | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ca4fd38ac14d4b728535c3e18c5cfb30' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 411 | GoalWorktrees_sweep_deletes_orphaned_worktree_directory | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a34126db9c204f228d053658b0a293ee' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 412 | GoalWorktrees_sweep_escalates_consecutive_failures_and_auto_resolves_after_slow_retry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e406d0905ef049a6b5e82d966d83057f' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 413 | GoalWorktrees_sweep_escalates_consecutive_failures_and_auto_resolves_after_slow_retry | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6c341105f18843588dd307dab105bf39' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 414 | GoalWorktrees_sweep_quietly_journals_in_budget_backoff_skip | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f4013589db8941c1804d0c030d2cb019' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 415 | GoalWorktrees_sweep_quietly_journals_in_budget_backoff_skip | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8597b57900ac4f36a5615b19b8cb9f4c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 416 | GoalWorktrees_sweep_records_backoff_for_transient_orphan_delete_failure | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\191c73c4d9da4b67ad4b87dcd5b1a330' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 417 | GoalWorktrees_sweep_records_backoff_for_transient_orphan_delete_failure | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ad54e59e33e540b6b5f503903971036b' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 418 | GoalWorktrees_sweep_records_backoff_when_second_delete_fails_after_acl_reset | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\092528c62ab04273b1936ad398e71cb2' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 419 | GoalWorktrees_sweep_records_backoff_when_second_delete_fails_after_acl_reset | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b125fef4cbb64f48af5fb4d940dd0078' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 420 | GoalWorktrees_sweep_records_timeout_backoff_and_skips_repeat_acl_reset | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1007dccc6e8a4bd28a05f80ac420ad4d' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 421 | GoalWorktrees_sweep_records_timeout_backoff_and_skips_repeat_acl_reset | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\06c45f71baad4c95b33af7d021bbc8c4' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 422 | GoalWorktrees_sweep_resets_acl_only_after_access_denied_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0d648a2e03934fe1ba5cf87bbe3a4fbb' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 423 | GoalWorktrees_sweep_resets_acl_only_after_access_denied_delete | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c92b3b997dc04c74b3d5cc631f986a35' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 424 | GoalWorktrees_terminal_remove_deletes_long_path_and_prunes_registration | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-long-wt\67774ea65a7b49239cbe1dbb399cbbda\repo' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 425 | GoalWorktrees_terminal_remove_deletes_long_path_and_prunes_registration | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-long-wt\d58ddd0a845d47d590b9fc34c9a18356\repo' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 426 | GoalWorktrees_terminal_remove_reports_unsafe_prefix_collision_without_throwing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\51aafb56d18148b0bc39ef0e280e17c4' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 427 | GoalWorktrees_terminal_remove_reports_unsafe_prefix_collision_without_throwing | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d29ced9b7e244441944289cfc53b46ad' is denied. | 88 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 428 | GoalWorktreeTestsRemoveCleanup.Keyed_goal_replay_keeps_one_workspace_and_clean_repository | Assert.True() Failure | 25 | 1 | 5 | 0 | 1 | 2026-08-15 | 2026-08-15 | single-goal-unresolved |
| 429 | GoalWorktreeTestsRemoveCleanup.RemoveSupersededTerminal_ChangedTip_KeepsBranch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2c8c0b61218d4fb7a929e38a44a339ed' is denied. | 49 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 430 | GoalWorktreeTestsRemoveCleanup.RemoveSupersededTerminal_ChangedTip_KeepsBranch | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0ff4b021fb5249e48faf337a84cf303a' is denied. | 49 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 431 | Harness_docs_counterpart_contracts_stay_in_sync | System.InvalidOperationException : AGENTS.md shared-anchor list changed. Expected [output-discipline, retry-and-loop-control, repository-rules, architecture-and-design-discipline, specification-discipline, diagnosis-discipline, dashboard-dogfood-boundary, operating-the-goal-loop, safety, evidence], got [output-discipline, retry-and-loop-control, repository-rules, architecture-and-design-discipline, specification-discipline, diagnosis-discipline, dashboard-dogfood-boundary, operating-the-goal-loop, safety, evidence, dispositive-decision-discipline]. | 121 | 1 | 0 | 1 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 432 | Harness_docs_drift_check_rejects_unpaired_contract_or_anchor_edits | Assert.True() Failure | 121 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 433 | InquiryDispatcher_receipt_is_post_hoc_and_does_not_mutate_task_state | System.InvalidOperationException : git commit -m init failed: | 102 | 1 | 0 | 0 | 1 | 2026-07-22 | 2026-07-22 | single-goal-unresolved |
| 434 | InquiryDispatcher_seals_codex_resume_session_after_nonforked_inquiry | Assert.Equal() Failure: Values differ | 102 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 435 | InvokeGit_through_repo_script_preserves_hyphenated_git_arguments | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fd1f533907044c32b72e7ac0f9dddf11' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 436 | InvokeGit_through_repo_script_preserves_hyphenated_git_arguments | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48bad7134ec4474ea6d5974b34487992' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 437 | InvokeIsolatedDotnet_from_goal_worktree_leaves_repository_status_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\857d38d361fc41b993b7da298581af7e' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 438 | InvokeIsolatedDotnet_from_goal_worktree_leaves_repository_status_clean | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\47172e95081843499840a27c00609e84' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 439 | InvokeRepoScript_empty_argument_splat_forwards_zero_arguments | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\a24c1b56cb3442b98a17483ed9c6bfe3' is denied. | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 440 | InvokeRepoScript_empty_argument_splat_forwards_zero_arguments | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\79a3882ea75a485eb65317e8b9d1d9c2' is denied. | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 441 | InvokeRepoScript_no_trailing_arguments_forwards_zero_arguments | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\6d01c740dd8a496b87dcc8d656d4d1c9' is denied. | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 442 | InvokeRepoScript_no_trailing_arguments_forwards_zero_arguments | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\ecb352b6c3714626856e8ecef3c4b0bb' is denied. | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 443 | InvokeRepoScript_non_empty_arguments_are_forwarded_in_order | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\781ceeb378c241bdb0b68411ef229f3f' is denied. | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 444 | InvokeRepoScript_non_empty_arguments_are_forwarded_in_order | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\331715e703404bd58ed761bac7a8bc05' is denied. | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 445 | InvokeRepoScript_orchestrator_sqlite_tool_list_goals_smoke | exit=1; stdout=C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.Core\obj' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\scripts\OrchestratorSqliteTools\OrchestratorSqliteTools.csproj] | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 446 | InvokeRepoScript_orchestrator_sqlite_tool_list_goals_smoke | exit=1; stdout=C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.Core\obj\c0df1963-907a-44e6-9f89-32476bfdf12c.tmp' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\scripts\OrchestratorSqliteTools\OrchestratorSqliteTools.csproj] | 109 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 447 | IsSandboxCommitBlockedFailure_false_for_low_integrity_1312_logon_session_evidence | Assert.False() Failure | 56 | 1 | 0 | 0 | 1 | 2026-08-09 | 2026-08-09 | single-goal-unresolved |
| 448 | IsSandboxCommitBlockedFailure_false_for_low_integrity_git_1312_evidence | Assert.False() Failure | 56 | 1 | 0 | 0 | 1 | 2026-08-09 | 2026-08-09 | single-goal-unresolved |
| 449 | IsSandboxCommitBlockedFailure_reads_log_paths_for_evidence_outside_retained_excerpt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-sandbox-blocked-8cc9514ede7f4ae88de23e2552adbdf9' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 450 | IsSandboxCommitBlockedFailure_reads_log_paths_for_evidence_outside_retained_excerpt | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-sandbox-blocked-e5cc41ecda25461ab479de5e1132d3e6' is denied. | 111 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 451 | IsTransientEmptyOutputDispatchFlake_false_for_exit0_with_populated_outlog_and_empty_inmemory_stdout | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-emptyflake-661dce1378cc43268733b06cfdfae718.out.log' is denied. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 452 | IsTransientEmptyOutputDispatchFlake_false_for_exit0_with_populated_outlog_and_empty_inmemory_stdout | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-emptyflake-a1c412dd24544d1ea04453c0c44d7bde.out.log' is denied. | 120 | 1 | 0 | 0 | 1 | 2026-07-30 | 2026-07-30 | single-goal-unresolved |
| 453 | Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout | Assert.Equal() Failure: Strings differ | 58 | 1 | 0 | 7 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 454 | Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\post-landing-canary-dirty-9d96111b47a54fb882cf5fc8fb4d3c02.sentinel' is denied. | 58 | 1 | 0 | 7 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 455 | Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\post-landing-canary-dirty-7583e1709cbc4f9c91be71e993510744.sentinel' is denied. | 58 | 1 | 0 | 7 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 456 | Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main | Assert.False() Failure | 15 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 457 | Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main | BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-hvp\AppData\LocalLow\mcg-dotnet-isolated\runs\33816-acceptance-focused-changed-core-tests-build-04972e2eb6a74c429d4371dddf0935fe\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown. | 15 | 1 | 0 | 0 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 458 | Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main | BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated\runs\18080-acceptance-focused-changed-core-tests-build-f54a5b58f022489fa6afdcfa4c028ee5\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown. | 15 | 1 | 0 | 0 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 459 | Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main | BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated\runs\29128-acceptance-focused-changed-core-tests-build-c7e1c993a7a84d2596392b69e00ab9e4\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown. | 15 | 1 | 0 | 0 | 1 | 2026-08-01 | 2026-08-01 | single-goal-unresolved |
| 460 | Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main | System.InvalidOperationException : Post-landing canary expected main HEAD 0d05f061931328df9712275f788129201161aa47, but resolved ''. | 15 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 461 | Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main | System.InvalidOperationException : Post-landing canary repository precondition failed; main was dirty before the run. Baseline SHA: 335e6c14ddafdddd933a3fe8eaac8e455a525e0a. Current SHA: 335e6c14ddafdddd933a3fe8eaac8e455a525e0a. Current is at or ahead of baseline: True. Baseline dirty paths: ?? Microsoft/Windows/PowerShell/ModuleAnalysisCache. Current dirty paths: ?? Microsoft/Windows/PowerShell/ModuleAnalysisCache. | 15 | 1 | 0 | 0 | 1 | 2026-08-06 | 2026-08-06 | single-goal-unresolved |
| 462 | LaneTimingMeasurementScriptTests.MissingManifestLane_FailsAndNamesLane | System.ComponentModel.Win32Exception : An error occurred trying to start process 'pwsh' with working directory 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8818\'. Access is denied. | 21 | 1 | 0 | 1 | 1 | 2026-08-17 | 2026-08-17 | single-goal-unresolved |
| 463 | LauncherScriptTests.AutoResumeInstallerValidatesBeforeTaskMutationAndRestoresLastKnownGood | Assert.Equal() Failure: Values differ | 39 | 1 | 0 | 1 | 1 | 2026-08-13 | 2026-08-13 | single-goal-unresolved |
| 464 | LauncherScriptTests.ConductLoop_ConfiguredRoot_UsesChildStopPath | Assert.Contains() Failure: Sub-string not found | 0 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 465 | LauncherScriptTests.ConductLoop_StopFilePresent_RefusesBeforeSideEffects | Assert.Single() Failure: The collection contained 2 items | 0 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 466 | LauncherScriptTests.ConductLoop_StopPathDirectory_RefusesBeforeSideEffects | Assert.Contains() Failure: Sub-string not found | 0 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 467 | Leniency_MarkdownHeadingOpener_passes | System.InvalidOperationException : git commit -m Add src/Feature.cs failed: fatal: write failure on 'stdout': Bad file descriptor | 107 | 1 | 0 | 0 | 1 | 2026-07-18 | 2026-07-18 | single-goal-unresolved |
| 468 | MTP_no_build_missing_apphost_reports_path_and_build_command | Assert.Equal() Failure: Values differ | 79 | 1 | 5 | 1 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 469 | MTP_partition_build_failure_is_loud_and_never_launches_stale_apphost | Assert.Equal() Failure: Values differ | 79 | 1 | 5 | 1 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 470 | MTP_partition_build_stderr_does_not_override_a_successful_exit_code | Infrastructure partition: GoalWorktree | 79 | 1 | 5 | 0 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 471 | MTP_partition_runner_streams_output_and_passes_manifest_filter_arguments | Infrastructure partition: GoalWorktree | 79 | 1 | 5 | 1 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 472 | MTP_runner_preserves_child_and_caller_temp_roots_after_clean_run | Assert.Contains() Failure: Sub-string not found | 79 | 1 | 5 | 0 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 473 | MTP_summary_partition_defaults_to_the_infrastructure_test_project | RESULTS DIRECTORY FAILURE - Results directory 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\script-tests\81e9bf6098a540d29cd48179b958ed27' is not under the Low-integrity-writable root 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests'. Use -ResultsRoot beneath that root; an ordinary Medium-integrity directory is not writable by the MTP apphost. | 79 | 1 | 5 | 0 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 474 | MtpTestRunnerScriptTests.MtpCmdRunnerPreservesMetacharactersAndPercentExpansionsInPathsAsArgumentData | Infrastructure partition: GoalWorktree | 65 | 1 | 5 | 0 | 1 | 2026-08-13 | 2026-08-13 | single-goal-unresolved |
| 475 | MtpTestRunnerScriptTests.MtpPartitionRunnerSelectsDistinctFailureDiagnosis(behavior: "failed", expectedExitCode: 3, expectedDiagnosis: "TEST FAILURES", expectedDetail: "TRX:") | Infrastructure partition: GoalWorktree | 13 | 1 | 0 | 1 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 476 | MtpTestRunnerScriptTests.MtpPartitionRunnerSelectsDistinctFailureDiagnosis(behavior: "no-trx", expectedExitCode: 7, expectedDiagnosis: "RUNNER/TOOLING FAILURE", expectedDetail: "exited 7 without producing TRX") | Infrastructure partition: GoalWorktree | 13 | 1 | 0 | 1 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 477 | MtpTestRunnerScriptTests.MtpPartitionRunnerSelectsDistinctFailureDiagnosis(behavior: "zero", expectedExitCode: 27, expectedDiagnosis: "ZERO TESTS", expectedDetail: "filter matched no tests") | Infrastructure partition: GoalWorktree | 13 | 1 | 0 | 1 | 1 | 2026-08-02 | 2026-08-02 | single-goal-unresolved |
| 478 | Native_MTP_dotnet_test_runs_every_repository_test_project_in_one_step(project: "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.Ag"···, testClass: "DashboardValidationHarnessTests") | System.TimeoutException : dotnet did not exit within 300 seconds. | 22 | 1 | 2 | 0 | 1 | 2026-08-20 | 2026-08-20 | load-dependent |
| 479 | OrchestratorSqliteTool_list_goals_from_linked_worktree_reads_primary_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\781f5eeff58146858dd1115c159fe844' is denied. | 101 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 480 | OrchestratorSqliteTool_list_goals_from_linked_worktree_reads_primary_state | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\962528fcdea0497e91b43b134d3241f4' is denied. | 101 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 481 | OrchestratorSqliteTool_list_goals_omits_label_without_source_backlog_title | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f37e9ae573c94d399c0f23a7a4fae4be' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 482 | OrchestratorSqliteTool_list_goals_omits_label_without_source_backlog_title | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7fb806afa8214b2c82f67aae9039df3c' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 483 | OrchestratorSqliteTool_list_goals_prints_source_backlog_title_label | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\11c33237dc1f4433b59db6b12d672aef' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 484 | OrchestratorSqliteTool_list_goals_prints_source_backlog_title_label | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ec51bfb44f354cbc94d70dc447fd6f7f' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 485 | OrchestratorSqliteTool_list_goals_reads_repo_state_while_write_lock_is_held | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\eef8e1368cb24cad997ec12fbb6c3264' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 486 | OrchestratorSqliteTool_list_goals_reads_repo_state_while_write_lock_is_held | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\cea8250a08a248319f82fa60a51f8777' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 487 | PlannerOutputContractTests.PlannerContract_ExactLive485363d4EmDashNewFileMarkers_Pass | Planner output contract failed. Stdout plan reason: target citation 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliArgumentNormalizationTests.cs' does not exist and is not marked as a new file; source span [4896..4977). | 36 | 1 | 0 | 0 | 1 | 2026-08-13 | 2026-08-13 | single-goal-unresolved |
| 488 | PlannerOutputContractTests.PlannerContract_ExactRejectedE5c18520NewStoreMarker_Passes | Assert.Equal() Failure: Strings differ | 23 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 489 | Post-landing canary sink failure cannot skip successful-landing callbacks(breakSqliteStore: False) | Assert.Equal() Failure: Values differ | 9 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 490 | Post-landing canary sink failure cannot skip successful-landing callbacks(breakSqliteStore: True) | Assert.Equal() Failure: Values differ | 9 | 1 | 0 | 0 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 491 | ProcessTreeGuiSuppression_sets_inherited_error_mode_and_hidden_console_for_descendants | System.InvalidOperationException : StandardInputEncoding is only supported when standard input is redirected. | 108 | 1 | 0 | 0 | 1 | 2026-07-20 | 2026-07-20 | single-goal-unresolved |
| 492 | ProgramStartupLifecycle_handoff_configures_registry_without_sweeping_incumbent_processes | Assert.True() Failure | 90 | 1 | 0 | 0 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 493 | ProgramStartupLifecycle_non_cleanup_command_configures_registry_and_retains_live_worker | Assert.Single() Failure: The collection was empty | 67 | 1 | 0 | 0 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 494 | ProgressiveReviewGlance_dispatch_uses_read_only_profile_selection_and_bounded_inputs | Assert.DoesNotContain() Failure: Sub-string found | 103 | 1 | 0 | 1 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 495 | ProgressiveReviewSteering_cancels_confirms_dead_then_warm_resumes_with_guidance | Assert.Equal() Failure: Values differ | 102 | 1 | 0 | 7 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 496 | ProgressiveReviewSteering_records_receipt_and_attention_when_steer_start_throws | Assert.Contains() Failure: Sub-string not found | 102 | 1 | 0 | 1 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 497 | ProgressiveReviewSteering_requeues_and_preserves_goal_worktree_when_restart_preparation_throws | Assert.True() Failure | 72 | 1 | 0 | 1 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 498 | PrototypeWorkspaceSeeder_reuses_persistent_workspace_without_overwriting_state | Assert.Contains() Failure: Sub-string not found | 108 | 1 | 0 | 0 | 1 | 2026-07-28 | 2026-07-28 | single-goal-unresolved |
| 499 | Reconcile_sweep_retries_acceptance_after_untracked_rebase_blocker_is_removed | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3b3fd1a661ab4d4f9f50f847631231cc' is denied. | 60 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 500 | Reconcile_sweep_retries_acceptance_after_untracked_rebase_blocker_is_removed | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\649fe4e7960c4b399a73fd6aee9229ed' is denied. | 60 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 501 | RecordDispatchExecutionResult_counts_sandbox_preflight_failure_on_shared_empty_output_retry_budget | Assert.Equal() Failure: Values differ | 113 | 1 | 0 | 0 | 1 | 2026-07-23 | 2026-07-23 | single-goal-unresolved |
| 502 | RecordDispatchExecutionResult_does_not_reopen_task_on_quoted_usage_limit_fixture | Assert.Equal() Failure: Values differ | 121 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 503 | RecordDispatchExecutionResult_fails_task_on_typed_provider_rate_limit_without_output_text | Assert.Equal() Failure: Values differ | 117 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 504 | RecordDispatchExecutionResult_reopens_task_on_typed_provider_rate_limit_without_output_text | Assert.Equal() Failure: Values differ | 3 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 505 | Regression_89a2c42_deferred_verification_blocker_is_advisory_for_dirty_changed_work | Assert.Equal() Failure: Values differ | 107 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 506 | Research_first_pipeline_blocks_Planner_then_injects_full_artifacts_without_survey_or_retry_trimming | Assert.Contains() Failure: Sub-string not found | 78 | 1 | 0 | 1 | 1 | 2026-08-11 | 2026-08-11 | single-goal-unresolved |
| 507 | Research_first_pipeline_blocks_Planner_then_injects_full_artifacts_without_survey_or_retry_trimming | System.InvalidOperationException : git commit -m Seed failed: | 78 | 1 | 0 | 1 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 508 | RunGoalService_auto_failover_does_not_loop_back_to_failed_agent | Assert.Equal() Failure: Values differ | 108 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 509 | RunGoalService_auto_failover_heartbeat_stall_redelegates | stopReason=Subscription preflight failed: profile: alternate; dispatch-lane: alternate; model-selection: full-profile: role has custom subscription worker profile; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.5; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'alternate' is a real | 108 | 1 | 0 | 3 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 510 | RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues | stopReason=Assembled worker prompt for task 'a947c77de34f432db740442a5639c96e' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'a947c77de34f432db740442a5639c96e' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 511 | RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues | stopReason=Assembled worker prompt for task 'b01a9ff1cc7a484d9c3c464ccd0f15f6' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'b01a9ff1cc7a484d9c3c464ccd0f15f6' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 512 | RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues | stopReason=Assembled worker prompt for task 'c7c4fa247b9d46c0bd71a3a24701fc6c' is 1,574 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'c7c4fa247b9d46c0bd71a3a24701fc6c' is 1,574 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 513 | RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues | stopReason=Assembled worker prompt for task 'ed57665f8bc74493b00c8c1d35057e6b' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'ed57665f8bc74493b00c8c1d35057e6b' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 514 | RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues | stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi | 104 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 515 | RunGoalService_auto_failover_provider_model_rejection_redelegates | stopReason=Assembled worker prompt for task '1f46972a8e8f4adb9b265e4241c053c9' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '1f46972a8e8f4adb9b265e4241c053c9' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 516 | RunGoalService_auto_failover_provider_model_rejection_redelegates | stopReason=Assembled worker prompt for task '215b4911013843508e73807225a0bc2d' is 1,665 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '215b4911013843508e73807225a0bc2d' is 1,665 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 517 | RunGoalService_auto_failover_provider_model_rejection_redelegates | stopReason=Assembled worker prompt for task 'd50cb339cbbc4e738b58df0dfa37af6a' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'd50cb339cbbc4e738b58df0dfa37af6a' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 518 | RunGoalService_auto_failover_provider_model_rejection_redelegates | stopReason=Assembled worker prompt for task 'd5c5c2a249d54044853d8baa7d2b0034' is 1,570 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'd5c5c2a249d54044853d8baa7d2b0034' is 1,570 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 519 | RunGoalService_auto_failover_provider_model_rejection_redelegates | stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi | 104 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 520 | RunGoalService_auto_failover_usage_limit_redelegates_and_continues | Assert.True() Failure | 108 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 521 | RunGoalService_auto_failover_uses_added_same_role_catalog_alternate | stopReason=Assembled worker prompt for task '55fbae6c55e448efb42606b798ef3556' is 1,573 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '55fbae6c55e448efb42606b798ef3556' is 1,573 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 522 | RunGoalService_auto_failover_uses_added_same_role_catalog_alternate | stopReason=Assembled worker prompt for task 'c073088625f040819b34f3455f74da32' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'c073088625f040819b34f3455f74da32' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 523 | RunGoalService_auto_failover_uses_added_same_role_catalog_alternate | stopReason=Assembled worker prompt for task 'ccbf3243d9364bd4b2777b6b18fbbb2f' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'ccbf3243d9364bd4b2777b6b18fbbb2f' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 524 | RunGoalService_auto_failover_uses_added_same_role_catalog_alternate | stopReason=Assembled worker prompt for task 'f7658d28e9a84fd18f73b82b4bd5717d' is 1,668 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'f7658d28e9a84fd18f73b82b4bd5717d' is 1,668 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits. | 104 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 525 | RunGoalService_auto_failover_uses_added_same_role_catalog_alternate | stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi | 104 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 526 | RunGoalService_completes_all_tasks_sequentially_and_stops_with_no_actions | Assert.Equal() Failure: Values differ | 108 | 1 | 0 | 0 | 1 | 2026-08-11 | 2026-08-11 | single-goal-unresolved |
| 527 | StopRepoProcess_refuses_exact_pid_when_command_guard_mismatches | The process cannot access the file because it is being used by another process. | 101 | 1 | 5 | 0 | 1 | 2026-07-21 | 2026-07-21 | single-goal-unresolved |
| 528 | StorageRetention_persisted_terminal_goals_are_loaded_outside_conductor_working_set | Microsoft.Data.Sqlite.SqliteException : SQLite Error 1: 'no such table: goals'. | 10 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 529 | Subscription_dispatch_paths_use_workspace_configured_review_stop_round | Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: profile: claude-cli; dispatch-lane: claude-cli; model-selection: light-role: Reviewer uses claude-cli/claude-haiku-4-5; auth: Claude CLI Low-IL auth preflight not required because worker sandbox is disabled; model: Anthropic/claude-haiku-4-5; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'claude-cli' is a real launcher; ok: worker profile 'claude-cli' pins selected model; reasoning-effort: none (base); ok: worker profile 'claude-cli' pins selected reasoning when required; capability: read-only - Reviewer tasks run with read-only/plan sandbox settings.; blocked: missing required local skill(s): orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: not required for read-only role; worktree: clean check not required for read-only role; git metadata: write check not required for read-only role; reviewer-scope: git diff --name-only main...HEAD found 0 changed file(s) from merge-base 0b951451fa32; reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD is clean against current main | 50 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 530 | SubscriptionDispatch_override_profile_replaces_agent_default | Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: ERR_REVIEWER_MERGE_BASE_UNAVAILABLE: profile: alt-profile; dispatch-lane: alt-profile; model-selection: override: explicit dispatch profile/model selection; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.5; complexity: Simple; ok: worker profile 'alt-profile' is a real launcher; ok: worker profile 'alt-profile' pins selected model; reasoning-effort: low (base); ok: worker profile 'alt-profile' pins selected reasoning when required; capability: read-only - Reviewer tasks run with read-only/plan sandbox settings.; skills: local skill catalog not present; selected skills will be listed as missing in context artifacts; build environment: not required for read-only role; worktree: clean check not required for read-only role; git metadata: write check not required for read-only role; blocked: ERR_REVIEWER_MERGE_BASE_UNAVAILABLE: Reviewer changed-file scope unavailable because git merge-base main HEAD could not be computed.; reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD is clean against current main | 106 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 531 | TerminalGoalSweep_terminal_goal_cleans_owned_ephemeral_dirs_without_worker_start | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a9a0f99d58b34fae94fb0251ab87de90' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 532 | TerminalGoalSweep_terminal_goal_cleans_owned_ephemeral_dirs_without_worker_start | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a4d9f8fb519b4a659c552241d3c234a7' is denied. | 102 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 533 | Test process starts opt out of visible console windows | Test-side process starts must set CreateNoWindow=true or request a hidden/no-new PowerShell window. Offenders: WorkerProcessJobsTests.cs:954 | 108 | 1 | 0 | 1 | 1 | 2026-08-05 | 2026-08-05 | single-goal-unresolved |
| 534 | Test process starts opt out of visible console windows | Test-side ProcessStartInfo usage must set CreateNoWindow=true. Offenders: CodexEgressProxyTests.cs:133, CodexEgressProxyTests.cs:143, CodexEgressProxyTests.cs:156 | 108 | 1 | 0 | 1 | 1 | 2026-07-22 | 2026-07-22 | single-goal-unresolved |
| 535 | VerificationAndInputWorklistTests.Parked_completion_is_not_treated_as_an_answered_duplicate | Assert.DoesNotContain() Failure: Sub-string found | 88 | 1 | 0 | 1 | 1 | 2026-08-03 | 2026-08-03 | single-goal-unresolved |
| 536 | Windows post-landing capture passes exact argv without a shell | System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg canary argv & (tests)\0e41baaab2c14ebdb46520c5e3258849\native stdout & (capture).log' because it is being used by another process. | 50 | 1 | 0 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 537 | WorkerContextPackageTests.Utf8DiscoveryCapturePreservesUnicodeBytesThroughOwnedNamedPipes | System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\4fa6af44\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\4fa6af44\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe | 19 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 538 | WorkerContextPackageTests.Utf8DiscoveryCapturePreservesUnicodeBytesThroughOwnedNamedPipes | System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\6241ed3f\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\6241ed3f\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe | 19 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 539 | WorkerContextPackageTests.Utf8DiscoveryCapturePreservesUnicodeBytesThroughOwnedNamedPipes | System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\c30eb2fe\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\c30eb2fe\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe | 19 | 1 | 0 | 0 | 1 | 2026-08-20 | 2026-08-20 | single-goal-unresolved |
| 540 | WorkerDispatchTestsSubscriptionPreflight.WorkerProfileDispatcherPreflightBlocksMissingUpstreamRoleSkill(role: Researcher, expectedSkill: "research-evidence") | Assert.Contains() Failure: Filter not matched in collection | 38 | 1 | 0 | 0 | 1 | 2026-08-11 | 2026-08-11 | single-goal-unresolved |
| 541 | WorkerProcessJobs_concurrent_startup_sweeps_claim_exact_worker_once | Assert.True() Failure | 67 | 1 | 0 | 3 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 542 | WorkerProcessJobs_fallback_taskkill_tree_kills_unregistered_wrapper_and_grandchild | System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-worker-job-tests\20ee418502e64c77a2704043a40a6013.pid' because it is being used by another process. | 105 | 1 | 0 | 0 | 1 | 2026-07-25 | 2026-07-25 | single-goal-unresolved |
| 543 | WorkerProcessJobs_register_writes_durable_pid_identity | Assert.Single() Failure: The collection was empty | 105 | 1 | 0 | 0 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 544 | WorkerProcessJobs_startup_sweep_reaps_only_registry_owned_pid | Assert.Equal() Failure: Values differ | 37 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 545 | WorkerProcessJobs_startup_sweep_reaps_worker_only_after_owner_is_dead | Assert.True() Failure | 67 | 1 | 0 | 0 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 546 | WorkerProcessJobs_startup_sweep_retains_worker_when_owner_image_mismatches | Assert.True() Failure | 67 | 1 | 0 | 0 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 547 | WorkerProcessJobs_startup_sweep_retains_worker_with_unknown_legacy_owner | Assert.True() Failure | 67 | 1 | 0 | 0 | 1 | 2026-08-04 | 2026-08-04 | single-goal-unresolved |
| 548 | WorkerProfileDispatcher_afc62d88_Tester_round_derives_touch_proof_and_accepts_moved_finding_without_worker_start | Assert.Equal() Failure: Values differ | 51 | 1 | 0 | 3 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 549 | WorkerProfileDispatcher_includes_current_branch_and_head_in_dispatched_prompt | Assert.Contains() Failure: Sub-string not found | 106 | 1 | 0 | 0 | 1 | 2026-07-19 | 2026-07-19 | single-goal-unresolved |
| 550 | WorkerProfileDispatcher_pins_anthropic_subscription_model | Assert.Contains() Failure: Sub-string not found | 106 | 1 | 0 | 0 | 1 | 2026-07-28 | 2026-07-28 | single-goal-unresolved |
| 551 | WorkerProfileDispatcher_preflight_blocks_repo_scoped_skill_targets | Assert.Contains() Failure: Sub-string not found | 106 | 1 | 0 | 0 | 1 | 2026-08-11 | 2026-08-11 | single-goal-unresolved |
| 552 | WorkerProfileDispatcher_prepares_subscription_tasks_by_assigned_provider | Assert.Contains() Failure: Sub-string not found | 106 | 1 | 0 | 0 | 1 | 2026-07-28 | 2026-07-28 | single-goal-unresolved |
| 553 | WorkerProfileDispatcher_ready_batch_records_provider_from_claude_launcher | Assert.Contains() Failure: Sub-string not found | 106 | 1 | 0 | 0 | 1 | 2026-07-28 | 2026-07-28 | single-goal-unresolved |
| 554 | WorkerProfileDispatcher_rejects_moved_finding_when_identical_commits_provide_no_touch_proof | Assert.Equal() Failure: Values differ | 3 | 1 | 0 | 0 | 1 | 2026-08-08 | 2026-08-08 | single-goal-unresolved |
| 555 | Workspace merge carries authoritative changed paths without a goal worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4deac85c31234ca2b6d4212a427e618b' is denied. | 79 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 556 | Workspace merge carries authoritative changed paths without a goal worktree | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\23440d3f10f948e2afff802aafde407b' is denied. | 79 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 557 | Workspace merge fails closed when branch changed paths cannot be determined | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ee3d30d82ff540828d04b3bfd43431bb' is denied. | 79 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |
| 558 | Workspace merge fails closed when branch changed paths cannot be determined | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\32d594d1bea44791a60e12395241ac45' is denied. | 79 | 1 | 5 | 0 | 1 | 2026-08-10 | 2026-08-10 | single-goal-unresolved |

## Failure attempts

### 1. AdvanceGoalWithSubscriptionsUntilBlocked_continues_prompt_below_new_large_threshold

Signature: `Assert.True() Failure`. Pattern: **load-dependent**. Passes: 95; goal failures: 4; operator failures: 0; pre-review failures: 0.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.True() Failure
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | Assert.True() Failure
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.True() Failure
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.True() Failure

### 2. BackgroundDispatchRunner_codex_dispatch_records_reported_session_tuple_in_receipt_and_heartbeat

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **spread**. Passes: 100; goal failures: 4; operator failures: 0; pre-review failures: 0.
- 03f8a767-0-20260721054729133 | 2026-07-21 | goal 03f8a767 | Assert.Equal() Failure: Strings differ
- 53920957-0-20260722032215268 | 2026-07-22 | goal 53920957 | Assert.Equal() Failure: Strings differ
- 77c365b2-0-20260723220047594 | 2026-07-23 | goal 77c365b2 | Assert.Equal() Failure: Strings differ
- 58efc734-0-20260724013959563 | 2026-07-24 | goal 58efc734 | Assert.Equal() Failure: Strings differ

### 3. BudgetAwareRouting_simple_task_prefers_local_over_paid_when_both_available

Signature: `Assert.True() Failure`. Pattern: **load-dependent**. Passes: 106; goal failures: 4; operator failures: 0; pre-review failures: 0.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.True() Failure
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | Assert.True() Failure
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.True() Failure
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.True() Failure

### 4. DotnetBuildEnvironmentManager_marked_landing_fixture_from_other_process_is_transient

Signature: `Assert.IsType() Failure: Value is not the exact type`. Pattern: **spread**. Passes: 106; goal failures: 4; operator failures: 0; pre-review failures: 0.
- 510e14e5-0-20260722081511476 | 2026-07-22 | goal 510e14e5 | Assert.IsType() Failure: Value is not the exact type
- 1842aa59-0-20260730224921310 | 2026-07-30 | goal 1842aa59 | Assert.IsType() Failure: Value is not the exact type
- 0b81147a-0-20260802170736406 | 2026-08-02 | goal 0b81147a | Assert.IsType() Failure: Value is not the exact type
- cd8ebb40-0-20260802220544694 | 2026-08-02 | goal cd8ebb40 | Assert.IsType() Failure: Value is not the exact type

### 5. DotnetBuildEnvironmentManagerTests.FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry

Signature: `Assert.True() Failure`. Pattern: **spread**. Passes: 63; goal failures: 4; operator failures: 0; pre-review failures: 12.
- 732af577-0-20260813230810771 | 2026-08-13 | goal 732af577 | Assert.True() Failure
- 56991645-0-20260814022347076 | 2026-08-14 | goal 56991645 | Assert.True() Failure
- 788319b3-0-20260814002042962 | 2026-08-14 | goal 788319b3 | Assert.True() Failure
- d2aa7ab9-1-20260814003051761 | 2026-08-14 | goal d2aa7ab9 | Assert.True() Failure

### 6. Goals_subscribe_once_waits_through_unrelated_events_and_emits_one_matching_human_event

Signature: `Assert.False() Failure`. Pattern: **load-dependent**. Passes: 104; goal failures: 4; operator failures: 0; pre-review failures: 1.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.False() Failure
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | Assert.False() Failure
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.False() Failure
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.False() Failure

### 7. Monitor_goal_local_ndjson_without_once_stays_attached_until_cancelled

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **load-dependent**. Passes: 105; goal failures: 4; operator failures: 0; pre-review failures: 0.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.Contains() Failure: Sub-string not found
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | Assert.Contains() Failure: Sub-string not found
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.Contains() Failure: Sub-string not found
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Contains() Failure: Sub-string not found

### 8. OrchestratorHealthInspector_rejects_local_bridge_for_api_only_agent

Signature: `Assert.False() Failure`. Pattern: **load-dependent**. Passes: 106; goal failures: 4; operator failures: 0; pre-review failures: 0.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.False() Failure
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | Assert.False() Failure
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.False() Failure
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.False() Failure

### 9. ConductorSelfRelaunch_real_handoff_failure_stops_successor_and_reacquires_incumbent_lease

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **spread**. Passes: 90; goal failures: 3; operator failures: 5; pre-review failures: 0.
- d4897de1-0-20260801213910727 | 2026-08-01 | goal d4897de1 | Assert.Equal() Failure: Strings differ
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Assert.Equal() Failure: Strings differ
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Assert.Equal() Failure: Strings differ

### 10. GoalAcceptanceVerifier_partitions_checked_in_infrastructure_manifest_check

Signature: `Assert.Contains() Failure: Filter not matched in collection`. Pattern: **spread**. Passes: 107; goal failures: 3; operator failures: 0; pre-review failures: 1.
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Contains() Failure: Filter not matched in collection
- 1a270cd6-0-20260815151637204 | 2026-08-15 | goal 1a270cd6 | Assert.Contains() Failure: Filter not matched in collection
- 19d16954-0-20260821151833628 | 2026-08-21 | goal 19d16954 | Assert.Contains() Failure: Filter not matched in collection

### 11. GoalDependency_Chain_BHeldUntilADone

Signature: `Assert.True() Failure`. Pattern: **load-dependent**. Passes: 107; goal failures: 3; operator failures: 0; pre-review failures: 0.
- 59db7c34-0-20260719151628812 | 2026-07-19 | goal 59db7c34 | Assert.True() Failure
- 74e0ec9c-0-20260719053855675 | 2026-07-19 | goal 74e0ec9c | Assert.True() Failure
- f5ebb40e-0-20260719040014130 | 2026-07-19 | goal f5ebb40e | Assert.True() Failure

### 12. InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory

Signature: `Invoke-IsolatedDotnet.ps1 did not exit within 30 seconds.`. Pattern: **spread**. Passes: 87; goal failures: 3; operator failures: 0; pre-review failures: 0.
- 3ddb7c11-0-20260814175814516 | 2026-08-14 | goal 3ddb7c11 | Invoke-IsolatedDotnet.ps1 did not exit within 30 seconds.
- 457c03d1-0-20260815012012550 | 2026-08-15 | goal 457c03d1 | Invoke-IsolatedDotnet.ps1 did not exit within 30 seconds.
- 5a9a14e0-0-20260815025631172 | 2026-08-15 | goal 5a9a14e0 | Invoke-IsolatedDotnet.ps1 did not exit within 30 seconds.

### 13. Planner_dispatch_persists_canonical_receipt_for_plan_captured_on_stdout

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **load-dependent**. Passes: 81; goal failures: 3; operator failures: 0; pre-review failures: 1.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.Equal() Failure: Strings differ
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.Equal() Failure: Strings differ
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Equal() Failure: Strings differ

### 14. Planner_output_contract_selects_latest_explicit_allowed_external_plan

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **load-dependent**. Passes: 81; goal failures: 3; operator failures: 0; pre-review failures: 1.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.Equal() Failure: Strings differ
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.Equal() Failure: Strings differ
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Equal() Failure: Strings differ

### 15. Research_first_pipeline_blocks_Planner_then_injects_full_artifacts_without_survey_or_retry_trimming

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **load-dependent**. Passes: 78; goal failures: 3; operator failures: 0; pre-review failures: 1.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.Equal() Failure: Strings differ
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.Equal() Failure: Strings differ
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Equal() Failure: Strings differ

### 16. WorkerDispatchTestSupport+WorkerDispatchPlannerHandoffTests.PlannerContract_AcceptanceMappingPlaceholder_Fails(body: "")

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **load-dependent**. Passes: 76; goal failures: 3; operator failures: 0; pre-review failures: 2.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.Contains() Failure: Sub-string not found
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.Contains() Failure: Sub-string not found
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Contains() Failure: Sub-string not found

### 17. WorkerDispatchTestSupport+WorkerDispatchPlannerHandoffTests.PlannerContract_AcceptanceMappingPlaceholder_Fails(body: "TBD")

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **load-dependent**. Passes: 76; goal failures: 3; operator failures: 0; pre-review failures: 2.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.Contains() Failure: Sub-string not found
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.Contains() Failure: Sub-string not found
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Contains() Failure: Sub-string not found

### 18. WorkerProfileDispatcher_rejects_over_limit_subscription_prompt_before_dispatch_mutation

Signature: `Assert.ThrowsAny() Failure: No exception was thrown`. Pattern: **load-dependent**. Passes: 104; goal failures: 3; operator failures: 0; pre-review failures: 0.
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | Assert.ThrowsAny() Failure: No exception was thrown
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | Assert.ThrowsAny() Failure: No exception was thrown
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.ThrowsAny() Failure: No exception was thrown

### 19. Cli_attention_show_preserves_worker_owned_by_live_external_conductor

Signature: `Assert.True() Failure`. Pattern: **load-dependent**. Passes: 66; goal failures: 2; operator failures: 0; pre-review failures: 0.
- 35119666-0-20260808144041692 | 2026-08-08 | goal 35119666 | Assert.True() Failure
- 38d930d3-0-20260808040243645 | 2026-08-08 | goal 38d930d3 | Assert.True() Failure

### 20. ConductorDriver_real_dispatch_checkpoint_rolls_back_then_notifies_on_success

Signature: `Assert.Throws() Failure: No exception was thrown`. Pattern: **spread**. Passes: 59; goal failures: 2; operator failures: 0; pre-review failures: 1.
- 7136a027-0-20260807205748209 | 2026-08-07 | goal 7136a027 | Assert.Throws() Failure: No exception was thrown
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Assert.Throws() Failure: No exception was thrown

### 21. ConductorSelfRelaunch_real_self_check_failure_keeps_incumbent_authority

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **load-dependent**. Passes: 66; goal failures: 2; operator failures: 0; pre-review failures: 0.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Assert.Equal() Failure: Strings differ
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Assert.Equal() Failure: Strings differ

### 22. DotnetBuildEnvironmentManagerTests.FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **spread**. Passes: 63; goal failures: 2; operator failures: 0; pre-review failures: 12.
- 6b2085ed-0-20260806215204652 | 2026-08-06 | goal 6b2085ed | Assert.Equal() Failure: Values differ
- a840f9b6-0-20260811154216575 | 2026-08-11 | goal a840f9b6 | Assert.Equal() Failure: Values differ

### 23. GetRepoProcessInfo_reports_exact_pid_lineage_through_repo_prefix

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **load-dependent**. Passes: 101; goal failures: 2; operator failures: 5; pre-review failures: 0.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Assert.Equal() Failure: Values differ
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Assert.Equal() Failure: Values differ

### 24. GoalAcceptanceVerifier_waits_for_no_holder_acceptance_output_lock_then_retries

Signature: `Assert.Matches() Failure: Pattern not found in value`. Pattern: **spread**. Passes: 99; goal failures: 2; operator failures: 0; pre-review failures: 0.
- 70ced4ef-0-20260719231137950 | 2026-07-19 | goal 70ced4ef | Assert.Matches() Failure: Pattern not found in value
- 34f8ce24-0-20260720130024003 | 2026-07-20 | goal 34f8ce24 | Assert.Matches() Failure: Pattern not found in value

### 25. GoalRefinementTests.ResolveOpenClarificationStampsCurrentAuthoritativeBriefVersion

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **load-dependent**. Passes: 54; goal failures: 2; operator failures: 0; pre-review failures: 0.
- 5a911a70-0-20260808104004312 | 2026-08-08 | goal 5a911a70 | Assert.Equal() Failure: Values differ
- 85af9b2f-0-20260808021001537 | 2026-08-08 | goal 85af9b2f | Assert.Equal() Failure: Values differ

### 26. InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory

Signature: `Invoke-IsolatedDotnet.ps1 exited 86.`. Pattern: **spread**. Passes: 87; goal failures: 2; operator failures: 0; pre-review failures: 0.
- d4897de1-0-20260801213910727 | 2026-08-01 | goal d4897de1 | Invoke-IsolatedDotnet.ps1 exited 86.
- fcf8669a-0-20260802042053873 | 2026-08-02 | goal fcf8669a | Invoke-IsolatedDotnet.ps1 exited 86.

### 27. InvokeRepoScript_runs_FindOrchestratorLocks_without_synthetic_argument

Signature: `Expected Find-OrchestratorLocks.ps1 to exit 0 or 2, got 1. stderr: ERROR: could not acquire build lock after 30 s`. Pattern: **load-dependent**. Passes: 101; goal failures: 2; operator failures: 5; pre-review failures: 0.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Expected Find-OrchestratorLocks.ps1 to exit 0 or 2, got 1. stderr: ERROR: could not acquire build lock after 30 s
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Expected Find-OrchestratorLocks.ps1 to exit 0 or 2, got 1. stderr: ERROR: could not acquire build lock after 30 s

### 28. InvokeRepoScript_StartOrchestratorCommand_emits_parseable_launch_json

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **load-dependent**. Passes: 102; goal failures: 2; operator failures: 5; pre-review failures: 0.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Assert.Equal() Failure: Values differ
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Assert.Equal() Failure: Values differ

### 29. LockAttribution_handle_probe_timeout_returns_unknown_without_wedging

Signature: `Assert.Single() Failure: The collection was empty`. Pattern: **spread**. Passes: 108; goal failures: 2; operator failures: 0; pre-review failures: 1.
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Assert.Single() Failure: The collection was empty
- 77c21801-0-20260811220352643 | 2026-08-11 | goal 77c21801 | Assert.Single() Failure: The collection was empty

### 30. Native_MTP_dotnet_test_runs_every_repository_test_project_in_one_step(project: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/M"···, testClass: "ProcessStartInfoSourceGuardTests")

Signature: `System.TimeoutException : dotnet did not exit within 300 seconds.`. Pattern: **load-dependent**. Passes: 21; goal failures: 2; operator failures: 2; pre-review failures: 1.
- 602e109f-0-20260820211610446 | 2026-08-20 | goal 602e109f | System.TimeoutException : dotnet did not exit within 300 seconds.
- ae9dccd4-0-20260820200404620 | 2026-08-20 | goal ae9dccd4 | System.TimeoutException : dotnet did not exit within 300 seconds.

### 31. ProcessTreeGuiSuppression_sets_inherited_error_mode_and_hidden_console_for_descendants

Signature: `Probe did not write output. stdout=Windows PowerShell`. Pattern: **spread**. Passes: 108; goal failures: 2; operator failures: 0; pre-review failures: 0.
- d4897de1-0-20260801213910727 | 2026-08-01 | goal d4897de1 | Probe did not write output. stdout=Windows PowerShell
- fcf8669a-0-20260802042053873 | 2026-08-02 | goal fcf8669a | Probe did not write output. stdout=Windows PowerShell

### 32. StartOrchestratorCommand_emits_json_pid_and_log_path_through_repo_script

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **load-dependent**. Passes: 82; goal failures: 2; operator failures: 0; pre-review failures: 0.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Assert.Equal() Failure: Values differ
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Assert.Equal() Failure: Values differ

### 33. StartOrchestratorCommand_forwards_double_dash_arguments_to_background_process

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **load-dependent**. Passes: 109; goal failures: 2; operator failures: 5; pre-review failures: 1.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Assert.Equal() Failure: Values differ
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Assert.Equal() Failure: Values differ

### 34. StartOrchestratorCommand_launch_failure_exits_nonzero_with_error_json_on_stderr

Signature: `Access to the path '.orchestrator' is denied.`. Pattern: **load-dependent**. Passes: 109; goal failures: 2; operator failures: 5; pre-review failures: 0.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Access to the path '.orchestrator' is denied.
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Access to the path '.orchestrator' is denied.

### 35. StopRepoProcess_refuses_exact_pid_when_command_guard_mismatches

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **load-dependent**. Passes: 101; goal failures: 2; operator failures: 5; pre-review failures: 0.
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | Assert.Equal() Failure: Values differ
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Assert.Equal() Failure: Values differ

### 36. A runner process fault preserves SHA-named stdout and stderr logs

Signature: `Assert.False() Failure`. Pattern: **single-goal-unresolved**. Passes: 53; goal failures: 1; operator failures: 0; pre-review failures: 3.
Excluded from budget conversion: single-goal-unresolved; failing-goal=48bbd193; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 48bbd193-0-20260818033746242 | 2026-08-18 | goal 48bbd193 | Assert.False() Failure

### 37. acceptance-retry_allows_cancelled_task_and_caps_operator_regates_at_three

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6c9e82e46bf54bce9d673de3958e2f99' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6c9e82e46bf54bce9d673de3958e2f99' is denied.

### 38. acceptance-retry_allows_cancelled_task_and_caps_operator_regates_at_three

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\890817abff4742a3932933e19981a452' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\890817abff4742a3932933e19981a452' is denied.

### 39. acceptance-retry_migrates_legacy_escalation_before_resolving_it

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1e2a569e240c449b93474a500492d165' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1e2a569e240c449b93474a500492d165' is denied.

### 40. acceptance-retry_migrates_legacy_escalation_before_resolving_it

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\23621d7a80204ab79470d70db73b7781' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\23621d7a80204ab79470d70db73b7781' is denied.

### 41. acceptance-retry_names_incomplete_task_and_correct_recovery_verb

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3fe2b58775044194b9a5ca4567e35198' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3fe2b58775044194b9a5ca4567e35198' is denied.

### 42. acceptance-retry_names_incomplete_task_and_correct_recovery_verb

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f969f5d571924f17a6997d9c684b6854' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f969f5d571924f17a6997d9c684b6854' is denied.

### 43. acceptance-retry_old_resolution_callback_does_not_resolve_newer_failure

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5caf0eaac88d429fbbbced0c1ae09875' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5caf0eaac88d429fbbbced0c1ae09875' is denied.

### 44. acceptance-retry_old_resolution_callback_does_not_resolve_newer_failure

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9d21efbf6ff241d5a3cd74975e1f97d5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9d21efbf6ff241d5a3cd74975e1f97d5' is denied.

### 45. acceptance-retry_outbox_quarantines_poison_without_blocking_unrelated_command

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\aa82ec2bdce64726bce6fbdd2a3414d3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\aa82ec2bdce64726bce6fbdd2a3414d3' is denied.

### 46. acceptance-retry_outbox_quarantines_poison_without_blocking_unrelated_command

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ada9b1b9aa5d4b6fbde73fa620414f3b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ada9b1b9aa5d4b6fbde73fa620414f3b' is denied.

### 47. acceptance-retry_outbox_rolls_back_with_commit_and_replays_pending_audit

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\485d16ffe2eb4d34aa43c49c7f328348' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\485d16ffe2eb4d34aa43c49c7f328348' is denied.

### 48. acceptance-retry_outbox_rolls_back_with_commit_and_replays_pending_audit

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60a33a54462f4f44a8ad1608d238051e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60a33a54462f4f44a8ad1608d238051e' is denied.

### 49. acceptance-retry_outbox_simultaneous_drainers_apply_audit_once

Signature: `System.InvalidOperationException : acceptance-retry could not resolve the prior failing gate's main HEAD SHA.`. Pattern: **single-goal-unresolved**. Passes: 87; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=7f2a3b85; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 7f2a3b85-0-20260820134147648 | 2026-08-20 | goal 7f2a3b85 | System.InvalidOperationException : acceptance-retry could not resolve the prior failing gate's main HEAD SHA.

### 50. acceptance-retry_outbox_simultaneous_drainers_apply_audit_once

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\422e3a4b24f6471bb3d95e9929f3002a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 87; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\422e3a4b24f6471bb3d95e9929f3002a' is denied.

### 51. acceptance-retry_outbox_simultaneous_drainers_apply_audit_once

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\faa15fc503e644e886d3c4b88f2e7268' is denied.`. Pattern: **single-goal-unresolved**. Passes: 87; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\faa15fc503e644e886d3c4b88f2e7268' is denied.

### 52. acceptance-retry_refuses_corrupt_escalation_store_without_mutating_goal

Signature: `System.InvalidOperationException : git commit -m Seed failed:`. Pattern: **single-goal-unresolved**. Passes: 87; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=df30743b; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- df30743b-0-20260812122129363 | 2026-08-12 | goal df30743b | System.InvalidOperationException : git commit -m Seed failed:

### 53. acceptance-retry_refuses_corrupt_escalation_store_without_mutating_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\26bd453399a342baae004f19fbdef46b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 87; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\26bd453399a342baae004f19fbdef46b' is denied.

### 54. acceptance-retry_refuses_corrupt_escalation_store_without_mutating_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9e45aea2a0a840d586ec4476f6f0eb33' is denied.`. Pattern: **single-goal-unresolved**. Passes: 87; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9e45aea2a0a840d586ec4476f6f0eb33' is denied.

### 55. acceptance-retry_requires_confirmation_before_goal_validation

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\63b901b2871542eea1674a594ea5449e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\63b901b2871542eea1674a594ea5449e' is denied.

### 56. acceptance-retry_requires_confirmation_before_goal_validation

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0e6bcd99949f413ebe532c46b0ebed22' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0e6bcd99949f413ebe532c46b0ebed22' is denied.

### 57. acceptance-retry_requires_passed_verification_for_completed_tasks

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d3a56f222448437e864e68c0bff20431' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d3a56f222448437e864e68c0bff20431' is denied.

### 58. acceptance-retry_requires_passed_verification_for_completed_tasks

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7b6eaae0bd7149a3a3ef65ca17289c6b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7b6eaae0bd7149a3a3ef65ca17289c6b' is denied.

### 59. acceptance-retry_requires_prior_gate_main_sha_before_mutating_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d233376fd65e4d3eb83acd6a4c86bbfb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d233376fd65e4d3eb83acd6a4c86bbfb' is denied.

### 60. acceptance-retry_requires_prior_gate_main_sha_before_mutating_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\71946a20d8e44571a4259ae80a967092' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\71946a20d8e44571a4259ae80a967092' is denied.

### 61. acceptance-retry_returns_failed_goal_to_verified_without_mutating_tasks

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d7496d9faf5d403ab37e995c4f946fb5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d7496d9faf5d403ab37e995c4f946fb5' is denied.

### 62. acceptance-retry_returns_failed_goal_to_verified_without_mutating_tasks

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f2cc84d18a564958a69b16eeb7094f22' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f2cc84d18a564958a69b16eeb7094f22' is denied.

### 63. acceptance-retry_stale_acceptance_failed_snapshot_cannot_reopen_resolved_escalation

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ea2fd291739344bf8bd0e33701184cfb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ea2fd291739344bf8bd0e33701184cfb' is denied.

### 64. acceptance-retry_stale_acceptance_failed_snapshot_cannot_reopen_resolved_escalation

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48a3ac2fb8954787a504ccd21f066679' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48a3ac2fb8954787a504ccd21f066679' is denied.

### 65. AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource

Signature: `Disabled collection 'GoalWorktreeCleanupHooks' spans acceptance lanes [Goal worktree cleanup, Cli, Goal lifecycle commands] without a shared exclusive resource key. Mapped classes: [AcceptanceCohortWorkflowTests -> Goal worktree cleanup, CliCommandTestsGoalBoard -> Cli, CliCommandTestsGoalLifecycleCleanupHooksAbandon -> Goal lifecycle commands, CliCommandTestsGoalLifecycleCleanupHooksAcceptance -> Goal lifecycle commands, CliCommandTestsGoalLifecycleCleanupHooks -> Goal lifecycle commands, CliCommandTestsPersistentRunnerCommands -> Goal lifecycle commands, CliCommandTestsSubscriptionDispatchCommands -> Goal lifecycle commands, GoalsPruneTests -> Goal lifecycle commands, GoalWorktreeTestsAcceptanceLanding -> Goal worktree cleanup, GoalWorktreeTestsOrphanEphemeralSweep -> Goal worktree cleanup, GoalWorktreeTestsRebaseMerge -> Goal worktree cleanup, GoalWorktreeTestsRemoveCleanup -> Goal worktree cleanup, GoalWorktreeTestsCleanupHookDelegates -> Goal worktree cleanup, LandingExecutorTests -> Goal lifecycle commands].`. Pattern: **single-goal-unresolved**. Passes: 78; goal failures: 1; operator failures: 0; pre-review failures: 5.
Excluded from budget conversion: single-goal-unresolved; failing-goal=1856ee3e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 1856ee3e-0-20260818025217067 | 2026-08-18 | goal 1856ee3e | Disabled collection 'GoalWorktreeCleanupHooks' spans acceptance lanes [Goal worktree cleanup, Cli, Goal lifecycle commands] without a shared exclusive resource key. Mapped classes: [AcceptanceCohortWorkflowTests -> Goal worktree cleanup, CliCommandTestsGoalBoard -> Cli, CliCommandTestsGoalLifecycleCleanupHooksAbandon -> Goal lifecycle commands, CliCommandTestsGoalLifecycleCleanupHooksAcceptance -> Goal lifecycle commands, CliCommandTestsGoalLifecycleCleanupHooks -> Goal lifecycle commands, CliCommandTestsPersistentRunnerCommands -> Goal lifecycle commands, CliCommandTestsSubscriptionDispatchCommands -> Goal lifecycle commands, GoalsPruneTests -> Goal lifecycle commands, GoalWorktreeTestsAcceptanceLanding -> Goal worktree cleanup, GoalWorktreeTestsOrphanEphemeralSweep -> Goal worktree cleanup, GoalWorktreeTestsRebaseMerge -> Goal worktree cleanup, GoalWorktreeTestsRemoveCleanup -> Goal worktree cleanup, GoalWorktreeTestsCleanupHookDelegates -> Goal worktree cleanup, LandingExecutorTests -> Goal lifecycle commands].

### 66. AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource

Signature: `Disabled collection 'ProcessSpawning' spans acceptance lanes [Remainder, Process spawning, Remainder balance B] without a shared exclusive resource key. Mapped classes: [CitedPriorEvidenceResolverTests -> Remainder, CliHelpTests -> Process spawning, ConductorDriverTests -> Process spawning, ConductorSelfRelaunchTests -> Process spawning, ConductorSuccessorSelfCheckTests -> Process spawning, DashboardRenderingTests -> Process spawning, DispatchProcessHostTests -> Process spawning, GitCliTests -> Process spawning, GoalWorktreeIsolatedDotnetTests -> Remainder balance B, LauncherScriptTests -> Process spawning, MtpTestRunnerScriptTests -> Process spawning, ProcessTreeGuiSuppressionTests -> Process spawning, SemanticAcceptanceTests -> Process spawning, WorkerContextArtifactsCharacterizationTests -> Process spawning, WorkerDispatchTestsModelSelection -> Process spawning].`. Pattern: **single-goal-unresolved**. Passes: 78; goal failures: 1; operator failures: 0; pre-review failures: 5.
Excluded from budget conversion: single-goal-unresolved; failing-goal=31068e90; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 31068e90-0-20260809101258885 | 2026-08-09 | goal 31068e90 | Disabled collection 'ProcessSpawning' spans acceptance lanes [Remainder, Process spawning, Remainder balance B] without a shared exclusive resource key. Mapped classes: [CitedPriorEvidenceResolverTests -> Remainder, CliHelpTests -> Process spawning, ConductorDriverTests -> Process spawning, ConductorSelfRelaunchTests -> Process spawning, ConductorSuccessorSelfCheckTests -> Process spawning, DashboardRenderingTests -> Process spawning, DispatchProcessHostTests -> Process spawning, GitCliTests -> Process spawning, GoalWorktreeIsolatedDotnetTests -> Remainder balance B, LauncherScriptTests -> Process spawning, MtpTestRunnerScriptTests -> Process spawning, ProcessTreeGuiSuppressionTests -> Process spawning, SemanticAcceptanceTests -> Process spawning, WorkerContextArtifactsCharacterizationTests -> Process spawning, WorkerDispatchTestsModelSelection -> Process spawning].

### 67. AcceptanceQueuePlanner_batches_goal_branch_facts_once_per_plan

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\96a56e1130bd49ebb89a5095b002950f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\96a56e1130bd49ebb89a5095b002950f' is denied.

### 68. AcceptanceQueuePlanner_batches_goal_branch_facts_once_per_plan

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9940327b934b478180ba8daf6802ecfc' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9940327b934b478180ba8daf6802ecfc' is denied.

### 69. Advance subscription start does not spawn scoped real worker process

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Assert.True() Failure

### 70. AutoReviewRetryConvergenceBriefBuilder_SQLite_round_diff_reopens_exact_regressed_anchor

Signature: `System.InvalidOperationException : git commit -m Add review target failed:`. Pattern: **single-goal-unresolved**. Passes: 90; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b0137830; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b0137830-0-20260726003429104 | 2026-07-26 | goal b0137830 | System.InvalidOperationException : git commit -m Add review target failed:

### 71. AutoReviewRetryConvergenceBriefBuilder_SQLite_rounds_shrink_A_B_to_accept

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 90; goal failures: 1; operator failures: 0; pre-review failures: 2.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c93241d0; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c93241d0-0-20260730082751919 | 2026-07-30 | goal c93241d0 | Assert.Contains() Failure: Sub-string not found

### 72. BackgroundDispatchRunner_child_zero_wrapper_one_without_usable_worker_result_fails_on_wrapper_exit

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 50; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=0e4d357c; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 0e4d357c-0-20260810014943678 | 2026-08-10 | goal 0e4d357c | Assert.Contains() Failure: Sub-string not found

### 73. BackgroundDispatchRunner_hung_wrappers_complete_read_only_roles_with_valid_worker_result(role: Reviewer, codexWrapper: False)

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 56; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=cdd69052; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- cdd69052-0-20260808075121590 | 2026-08-08 | goal cdd69052 | Assert.Equal() Failure: Values differ

### 74. BackgroundDispatchRunner_hung_wrappers_complete_read_only_roles_with_valid_worker_result(role: Reviewer, codexWrapper: True)

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 56; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=cdd69052; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- cdd69052-0-20260808075121590 | 2026-08-08 | goal cdd69052 | Assert.Equal() Failure: Values differ

### 75. BackgroundDispatchRunner_non_local_dispatch_records_resource_accounting

Signature: `Assert.NotNull() Failure: Value is null`. Pattern: **single-goal-unresolved**. Passes: 215; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=77c365b2; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 77c365b2-0-20260723220047594 | 2026-07-23 | goal 77c365b2 | Assert.NotNull() Failure: Value is null

### 76. BackgroundDispatchRunner_progress_stall_completes_read_only_role_with_valid_worker_result

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 55; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c18e923b; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c18e923b-0-20260808085731441 | 2026-08-08 | goal c18e923b | Assert.Equal() Failure: Values differ

### 77. BackgroundDispatchRunner_read_only_hung_wrapper_rescue_is_dispatch_agnostic

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 56; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=cdd69052; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- cdd69052-0-20260808075121590 | 2026-08-08 | goal cdd69052 | Assert.Equal() Failure: Values differ

### 78. BackgroundDispatchRunner_reaped_startup_hang_records_job_accounting

Signature: `System.TimeoutException : Timed out waiting for test condition.`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=03f8a767; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 03f8a767-0-20260721171102546 | 2026-07-21 | goal 03f8a767 | System.TimeoutException : Timed out waiting for test condition.

### 79. BackgroundDispatchRunner_reconciled_developer_without_change_still_fails_change_evidence

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 50; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=0e4d357c; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 0e4d357c-0-20260810014943678 | 2026-08-10 | goal 0e4d357c | Assert.Contains() Failure: Sub-string not found

### 80. BackgroundDispatchRunner_refresh_completes_when_exit_file_exists_even_if_wrapper_is_running

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9be2c1b8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9be2c1b8-0-20260719140657984 | 2026-07-19 | goal 9be2c1b8 | Assert.Equal() Failure: Values differ

### 81. BackgroundDispatchRunner_refresh_trusts_exit_zero_file_before_usage_limit_stderr

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9be2c1b8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9be2c1b8-0-20260719140657984 | 2026-07-19 | goal 9be2c1b8 | Assert.Equal() Failure: Values differ

### 82. BackgroundDispatchRunner_stale_no_exit_does_not_consume_empty_output_flake_budget

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 2.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b3ada0bb; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b3ada0bb-0-20260804143449217 | 2026-08-04 | goal b3ada0bb | Assert.Equal() Failure: Values differ

### 83. BackgroundDispatchRunner_stale_no_exit_surfaces_budget_exhaustion_after_stale_retry_spent

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 2.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b3ada0bb; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b3ada0bb-0-20260804143449217 | 2026-08-04 | goal b3ada0bb | Assert.Equal() Failure: Values differ

### 84. BackgroundDispatchRunner_startup_hang_completes_read_only_role_with_valid_worker_result

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 55; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c18e923b; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c18e923b-0-20260808085731441 | 2026-08-08 | goal c18e923b | Assert.Equal() Failure: Values differ

### 85. BackgroundDispatchRunner_sweep_completes_role_boundary_exited_worker_from_exit_file

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9be2c1b8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9be2c1b8-0-20260719140657984 | 2026-07-19 | goal 9be2c1b8 | Assert.Equal() Failure: Values differ

### 86. BatchLoop_dispatch_record_contention_escalates_goal_at_visible_bound_without_stopping_loop

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 60; goal failures: 1; operator failures: 0; pre-review failures: 2.
Excluded from budget conversion: single-goal-unresolved; failing-goal=7136a027; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 7136a027-0-20260807205748209 | 2026-08-07 | goal 7136a027 | Assert.Equal() Failure: Values differ

### 87. BatchLoop_parallel_acceptance_capacity_reduces_measured_makespan

Signature: `Assert.All() Failure: 2 out of 2 items in the collection did not pass.`. Pattern: **single-goal-unresolved**. Passes: 0; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=cd8ebb40; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- cd8ebb40-0-20260802220544694 | 2026-08-02 | goal cd8ebb40 | Assert.All() Failure: 2 out of 2 items in the collection did not pass.

### 88. BatchLoop_serializes_overlapping_gate_ready_acceptance

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 100; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=57a4b3be; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 57a4b3be-0-20260719015104202 | 2026-07-19 | goal 57a4b3be | Assert.Equal() Failure: Values differ

### 89. BatchLoop_unclassified_pre_process_dispatch_record_write_is_visible_and_skipped

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 60; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=7136a027; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 7136a027-0-20260807205748209 | 2026-08-07 | goal 7136a027 | Assert.Equal() Failure: Values differ

### 90. BuildTaskBrief_acceptance_retry_includes_structured_failure_receipt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-taskbrief-tests\b3308dd66a9f431ba12688aef09a626c\.orchestrator\goal-operations' is denied.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-taskbrief-tests\b3308dd66a9f431ba12688aef09a626c\.orchestrator\goal-operations' is denied.

### 91. BuildTaskBrief_acceptance_retry_includes_structured_failure_receipt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-taskbrief-tests\c3546e70f22a4f548f2c6be2478f77ee\.orchestrator\goal-operations' is denied.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-taskbrief-tests\c3546e70f22a4f548f2c6be2478f77ee\.orchestrator\goal-operations' is denied.

### 92. BuildTaskBrief_retry_feedback_preserves_atomic_entry

Signature: `System.InvalidOperationException : Sequence contains more than one element`. Pattern: **single-goal-unresolved**. Passes: 26; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=34b9452a; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 34b9452a-0-20260815200911963 | 2026-08-15 | goal 34b9452a | System.InvalidOperationException : Sequence contains more than one element

### 93. BuildTaskBrief_reviewer_convergence_scope_preserves_finding_severity

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=cbf7b22e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- cbf7b22e-0-20260808233914719 | 2026-08-08 | goal cbf7b22e | Assert.Contains() Failure: Sub-string not found

### 94. ChaosGate3a_missing_worker_result_block_passes_advisory_when_git_evidence_present

Signature: `System.InvalidOperationException : git commit -m Add src/Feature.cs failed:`. Pattern: **single-goal-unresolved**. Passes: 107; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=f5ebb40e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- f5ebb40e-0-20260719040014130 | 2026-07-19 | goal f5ebb40e | System.InvalidOperationException : git commit -m Add src/Feature.cs failed:

### 95. Classify ignores bookkeeping stderr when identifying a silent launch failure

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 85; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=424f2d45; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 424f2d45-0-20260803174750745 | 2026-08-03 | goal 424f2d45 | Assert.Equal() Failure: Values differ

### 96. Classify includes substantive stderr artifact tail in real failure receipt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpcgdh3y.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 34; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpcgdh3y.tmp' is denied.

### 97. Classify includes substantive stderr artifact tail in real failure receipt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpm5xyv1.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 34; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpm5xyv1.tmp' is denied.

### 98. Classify reads log path for verification evidence outside retained excerpt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-classify-2925fb8cd7cb438385340dd1eb2b93c8' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-classify-2925fb8cd7cb438385340dd1eb2b93c8' is denied.

### 99. Classify reads log path for verification evidence outside retained excerpt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-classify-46e84f7a9bd74217b235f0efd4d22515' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-classify-46e84f7a9bd74217b235f0efd4d22515' is denied.

### 100. Classify reads sandbox commit evidence from middle of large log artifacts

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpikldse.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpikldse.tmp' is denied.

### 101. Classify reads sandbox commit evidence from middle of large log artifacts

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpoh3dv4.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpoh3dv4.tmp' is denied.

### 102. Classify reads WORKER_RESULT blockers from stdout artifact

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpm2ommq.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpm2ommq.tmp' is denied.

### 103. Classify reads WORKER_RESULT blockers from stdout artifact

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpnohkcn.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpnohkcn.tmp' is denied.

### 104. Classify reads WORKER_RESULT fields from middle of large stdout artifact

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpkpu4bb.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpkpu4bb.tmp' is denied.

### 105. Classify reads WORKER_RESULT fields from middle of large stdout artifact

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpkzbwf0.tmp' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\tmpkzbwf0.tmp' is denied.

### 106. Classify treats scripting stderr without positive merit evidence as unknown

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 40; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6a960b3f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6a960b3f-0-20260808170616823 | 2026-08-08 | goal 6a960b3f | Assert.Equal() Failure: Values differ

### 107. Cli_acceptance_accepted_routes_stop_host_merge_mark_landed_before_deferred_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fc8c189477c54c818d6ad17c1684f0a6' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fc8c189477c54c818d6ad17c1684f0a6' is denied.

### 108. Cli_acceptance_accepted_routes_stop_host_merge_mark_landed_before_deferred_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\bf464eb57a844896967a2b4b30a227b7' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\bf464eb57a844896967a2b4b30a227b7' is denied.

### 109. Cli_acceptance_auto_rebases_when_goal_branch_behind_main

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0cebabb6fcd846e8b9293acb042ab3c1' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0cebabb6fcd846e8b9293acb042ab3c1' is denied.

### 110. Cli_acceptance_auto_rebases_when_goal_branch_behind_main

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\78ec9151680d46ea8fd457a5f093e820' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\78ec9151680d46ea8fd457a5f093e820' is denied.

### 111. Cli_acceptance_auto_records_dogfood_entry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8d29929935a345449b274c2963ce94fb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8d29929935a345449b274c2963ce94fb' is denied.

### 112. Cli_acceptance_auto_records_dogfood_entry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\40fc2ce0ee754246a07db93b2ce13c8f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\40fc2ce0ee754246a07db93b2ce13c8f' is denied.

### 113. Cli_acceptance_auto_verifies_from_git_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\adf28b64398c4851b0ddf1a1dec7b0f8' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\adf28b64398c4851b0ddf1a1dec7b0f8' is denied.

### 114. Cli_acceptance_auto_verifies_from_git_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b78b1d998fbc40b98661f6263073803c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b78b1d998fbc40b98661f6263073803c' is denied.

### 115. Cli_acceptance_blocks_merge_when_verification_fails

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3e06adcc03d3485daeff8b239ff5b2d1' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3e06adcc03d3485daeff8b239ff5b2d1' is denied.

### 116. Cli_acceptance_blocks_merge_when_verification_fails

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3860fd6b19ed4af894b5dbe6b8e04410' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3860fd6b19ed4af894b5dbe6b8e04410' is denied.

### 117. Cli_acceptance_cleanup_failure_reports_cleanup_blocker_after_landing_stays_accepted

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\861f236c72384c9180583e31a8102156' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\861f236c72384c9180583e31a8102156' is denied.

### 118. Cli_acceptance_cleanup_failure_reports_cleanup_blocker_after_landing_stays_accepted

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2a67321a2c4e44a7af844305878248cc' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2a67321a2c4e44a7af844305878248cc' is denied.

### 119. Cli_acceptance_defers_workspace_cleanup_after_successful_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\332723c18d0d4f98bbbd373957c35c13' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\332723c18d0d4f98bbbd373957c35c13' is denied.

### 120. Cli_acceptance_defers_workspace_cleanup_after_successful_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5823bf85faac4480ae1c3fb1738ebb74' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5823bf85faac4480ae1c3fb1738ebb74' is denied.

### 121. Cli_acceptance_direct_merge_journals_landing_before_cleanup_for_later_missing_worktree_retry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7d11902c113a40b8a3210fe09905dce6' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7d11902c113a40b8a3210fe09905dce6' is denied.

### 122. Cli_acceptance_direct_merge_journals_landing_before_cleanup_for_later_missing_worktree_retry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6849b951723346fb82c01c41303cc8d1' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6849b951723346fb82c01c41303cc8d1' is denied.

### 123. Cli_acceptance_does_not_auto_verify_without_committed_changes

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ecf4f887b36748fb87facbcc9d8e7d41' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ecf4f887b36748fb87facbcc9d8e7d41' is denied.

### 124. Cli_acceptance_does_not_auto_verify_without_committed_changes

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\907752b1dc5c4d37a89c286b856f60df' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\907752b1dc5c4d37a89c286b856f60df' is denied.

### 125. Cli_acceptance_evidence_auto_injects_policy_required_checks_and_merges

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0db44f49d17d4c29b7857e12667ab140' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0db44f49d17d4c29b7857e12667ab140' is denied.

### 126. Cli_acceptance_evidence_auto_injects_policy_required_checks_and_merges

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ca5426c5926649339ae311a7162df364' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ca5426c5926649339ae311a7162df364' is denied.

### 127. Cli_acceptance_evidence_blocks_dirty_worktree_before_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2ded5d81bcc94288b42a806bfcd212f7' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2ded5d81bcc94288b42a806bfcd212f7' is denied.

### 128. Cli_acceptance_evidence_blocks_dirty_worktree_before_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9a0bc51cc137424d8ccc7bdf18dd588f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9a0bc51cc137424d8ccc7bdf18dd588f' is denied.

### 129. Cli_acceptance_evidence_blocks_generated_artifact_changes

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d03644f7ae124c6d82456272edbb69a8' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d03644f7ae124c6d82456272edbb69a8' is denied.

### 130. Cli_acceptance_evidence_blocks_generated_artifact_changes

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c0f2f93c1e62475bb741852f040acbe9' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c0f2f93c1e62475bb741852f040acbe9' is denied.

### 131. Cli_acceptance_keep_workspace_retains_workspace_after_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\92afae4c6ba54f72b5bbe5dd213f2222' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\92afae4c6ba54f72b5bbe5dd213f2222' is denied.

### 132. Cli_acceptance_keep_workspace_retains_workspace_after_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1c8dc213f0f04beba451e3f65462b124' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1c8dc213f0f04beba451e3f65462b124' is denied.

### 133. Cli_acceptance_lands_after_transient_state_write_lock_releases

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\12a03721c0e44a0bbc6f5fe147ef112c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\12a03721c0e44a0bbc6f5fe147ef112c' is denied.

### 134. Cli_acceptance_lands_after_transient_state_write_lock_releases

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9a184a28b34943fe8e8d733ab90157af' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9a184a28b34943fe8e8d733ab90157af' is denied.

### 135. Cli_acceptance_lands_when_discord_token_is_invalid

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b3fdca62bdc3451aa34f85406cc209bb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b3fdca62bdc3451aa34f85406cc209bb' is denied.

### 136. Cli_acceptance_lands_when_discord_token_is_invalid

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2517fe62f841483ba9e44c7b5f5a6e82' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2517fe62f841483ba9e44c7b5f5a6e82' is denied.

### 137. Cli_acceptance_merge_conflict_blocks_and_leaves_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5cf3927bf05246d6bd10356cefe1ff35' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5cf3927bf05246d6bd10356cefe1ff35' is denied.

### 138. Cli_acceptance_merge_conflict_blocks_and_leaves_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c7cba0fb10264348980f1267e28a6567' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c7cba0fb10264348980f1267e28a6567' is denied.

### 139. Cli_acceptance_merges_after_passing_verification

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\14a1005941994959b3a6909bcd7819f3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\14a1005941994959b3a6909bcd7819f3' is denied.

### 140. Cli_acceptance_merges_after_passing_verification

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7b047191a89d4a0e8d188cfadcdaee3a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7b047191a89d4a0e8d188cfadcdaee3a' is denied.

### 141. Cli_acceptance_no_record_skips_dogfood_entry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c329e3c4aa994de694a48ec7169e6b56' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c329e3c4aa994de694a48ec7169e6b56' is denied.

### 142. Cli_acceptance_no_record_skips_dogfood_entry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2f7320f4926c44b5a59cb8aba0b303b6' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2f7320f4926c44b5a59cb8aba0b303b6' is denied.

### 143. Cli_acceptance_non_accepted_verdict_does_not_stop_host_or_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2be3ca255a38477b9a61246b6ac75767' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2be3ca255a38477b9a61246b6ac75767' is denied.

### 144. Cli_acceptance_non_accepted_verdict_does_not_stop_host_or_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\cf52ea0993474ef782f9706381321ab5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\cf52ea0993474ef782f9706381321ab5' is denied.

### 145. Cli_acceptance_queue_applies_ready_goals_sequentially

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7021a7312ec8400abafe32d3afc4b363' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7021a7312ec8400abafe32d3afc4b363' is denied.

### 146. Cli_acceptance_queue_applies_ready_goals_sequentially

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\210f564ee6bd4329aa9a545c83895d3a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\210f564ee6bd4329aa9a545c83895d3a' is denied.

### 147. Cli_acceptance_queue_apply_persists_cleanup_after_outside_transaction_routing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0ae2224b7ac54bfaa2c385f506cd3240' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0ae2224b7ac54bfaa2c385f506cd3240' is denied.

### 148. Cli_acceptance_queue_apply_persists_cleanup_after_outside_transaction_routing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\57583776fd5140149348a2eebc36e72e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\57583776fd5140149348a2eebc36e72e' is denied.

### 149. Cli_acceptance_queue_holds_stale_branch_with_manual_merge_command

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d776fddac42f422dabe219b8335db363' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d776fddac42f422dabe219b8335db363' is denied.

### 150. Cli_acceptance_queue_holds_stale_branch_with_manual_merge_command

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6e26c46093a54e32b77bdf4c32afc224' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6e26c46093a54e32b77bdf4c32afc224' is denied.

### 151. Cli_acceptance_queue_safe_auto_holds_irreversible_actions

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\225b1f8e01424a028167b9bacdaa142d' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\225b1f8e01424a028167b9bacdaa142d' is denied.

### 152. Cli_acceptance_queue_safe_auto_holds_irreversible_actions

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6070dd82c35e4429a0a582b61cf971a5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6070dd82c35e4429a0a582b61cf971a5' is denied.

### 153. Cli_acceptance_rebase_conflict_blocks_before_verification_and_restores_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\11cc55bf60b24ef5913ce2131c2b55d3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\11cc55bf60b24ef5913ce2131c2b55d3' is denied.

### 154. Cli_acceptance_rebase_conflict_blocks_before_verification_and_restores_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\45ee230604404cf1b264292b19a36133' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\45ee230604404cf1b264292b19a36133' is denied.

### 155. Cli_acceptance_rebases_before_verification_and_lands_verified_head

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1d20b1a412dd4d2bac7539cb98aa694a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1d20b1a412dd4d2bac7539cb98aa694a' is denied.

### 156. Cli_acceptance_rebases_before_verification_and_lands_verified_head

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6ca569f621d241f99d651b101160f85b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6ca569f621d241f99d651b101160f85b' is denied.

### 157. Cli_acceptance_rejects_stale_goal_state_before_merge_commit

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cecaec89604b479dad1c2ad0bacad572' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cecaec89604b479dad1c2ad0bacad572' is denied.

### 158. Cli_acceptance_rejects_stale_goal_state_before_merge_commit

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8627785c836e46b2816f4bd9607f16fe' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8627785c836e46b2816f4bd9607f16fe' is denied.

### 159. Cli_acceptance_rejects_stale_worktree_head_before_merge_commit

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\34580d59dfdb4479ae6b1461f8606f88' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\34580d59dfdb4479ae6b1461f8606f88' is denied.

### 160. Cli_acceptance_rejects_stale_worktree_head_before_merge_commit

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f443b888a173419ba04be32a5e37f350' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f443b888a173419ba04be32a5e37f350' is denied.

### 161. Cli_acceptance_releases_state_write_lock_during_verification

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c1382f5293a146dd891c9aff4a32a507' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c1382f5293a146dd891c9aff4a32a507' is denied.

### 162. Cli_acceptance_releases_state_write_lock_during_verification

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60a1518ca8c54c8aa0ccf3227ad97290' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60a1518ca8c54c8aa0ccf3227ad97290' is denied.

### 163. Cli_acceptance_repair_clears_stale_failure_only_with_landing_and_cleanup_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2cd39532ebb84bb6a5dd93a59cd69084' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2cd39532ebb84bb6a5dd93a59cd69084' is denied.

### 164. Cli_acceptance_repair_clears_stale_failure_only_with_landing_and_cleanup_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b0b1273d0a994dad9e83547dc9b2fe7f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b0b1273d0a994dad9e83547dc9b2fe7f' is denied.

### 165. Cli_acceptance_retry_treats_landed_cleaned_missing_worktree_as_accepted

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d83ce1ab81ce469dbcddb206a877c1dc' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d83ce1ab81ce469dbcddb206a877c1dc' is denied.

### 166. Cli_acceptance_retry_treats_landed_cleaned_missing_worktree_as_accepted

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7660a26ec6d24071bc65c97fac087fc6' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7660a26ec6d24071bc65c97fac087fc6' is denied.

### 167. Cli_acceptance_safe_auto_blocks_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\998d0c932eae4d5fa728c88d1dc50e96' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\998d0c932eae4d5fa728c88d1dc50e96' is denied.

### 168. Cli_acceptance_safe_auto_blocks_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d90dd40019cc49a28a4b8888f261c73d' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d90dd40019cc49a28a4b8888f261c73d' is denied.

### 169. Cli_acceptance_skip_verify_bypasses_verification_and_merges

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4089116ab2c74b45a7e22cc51cb97605' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4089116ab2c74b45a7e22cc51cb97605' is denied.

### 170. Cli_acceptance_skip_verify_bypasses_verification_and_merges

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0454ebe7653043d682cdeeff9cfd7723' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0454ebe7653043d682cdeeff9cfd7723' is denied.

### 171. Cli_acceptance_stop_host_timeout_blocks_before_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5cbe234e78114a59a0f17071a6e180ff' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5cbe234e78114a59a0f17071a6e180ff' is denied.

### 172. Cli_acceptance_stop_host_timeout_blocks_before_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\26e232771a1240b596ae1dd41aaa7ecb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\26e232771a1240b596ae1dd41aaa7ecb' is denied.

### 173. Cli_acceptance_verification_timeout_blocks_before_merge_with_diagnostics

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e0be79a3a593460ea2afa0eb82e85687' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e0be79a3a593460ea2afa0eb82e85687' is denied.

### 174. Cli_acceptance_verification_timeout_blocks_before_merge_with_diagnostics

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c41f43eee27347c895255ff65eb79872' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c41f43eee27347c895255ff65eb79872' is denied.

### 175. Cli_backlog_help_does_not_create_backlog_or_goal_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\886d2dc199c94b6dbb0686c4766e2e9b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\886d2dc199c94b6dbb0686c4766e2e9b' is denied.

### 176. Cli_backlog_help_does_not_create_backlog_or_goal_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1f94825b78234ae08ca510a51e3e3067' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1f94825b78234ae08ca510a51e3e3067' is denied.

### 177. Cli_backlog_list_filters_do_not_change_goal_or_worktree_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a7e3ce450ade4eacbe012ca9787429e3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a7e3ce450ade4eacbe012ca9787429e3' is denied.

### 178. Cli_backlog_list_filters_do_not_change_goal_or_worktree_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7a7ecf2860fa43eb972cec0ed9160ada' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7a7ecf2860fa43eb972cec0ed9160ada' is denied.

### 179. Cli_cleanup_status_lists_pending_debt_and_empty_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d193211efd5d4df6a33133966cd02865' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d193211efd5d4df6a33133966cd02865' is denied.

### 180. Cli_cleanup_status_lists_pending_debt_and_empty_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2fe0c05cb74b471d918e75dd5f7c9d0d' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2fe0c05cb74b471d918e75dd5f7c9d0d' is denied.

### 181. Cli_conduct_completed_goal_lands_through_persistent_runner_without_command_transaction

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\67ebbf95474c4f0490688c6a60ec2549' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\67ebbf95474c4f0490688c6a60ec2549' is denied.

### 182. Cli_conduct_completed_goal_lands_through_persistent_runner_without_command_transaction

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\28eb6bfdc6d148d8b25f6c9e290a60c3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\28eb6bfdc6d148d8b25f6c9e290a60c3' is denied.

### 183. Cli_conduct_scoped_reconciles_exited_dispatch_before_advance

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b4c563d48ad04855b30d01d6e15c9518' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b4c563d48ad04855b30d01d6e15c9518' is denied.

### 184. Cli_conduct_scoped_reconciles_exited_dispatch_before_advance

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\76e3be8625f4447dbd12403dd42b8c50' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\76e3be8625f4447dbd12403dd42b8c50' is denied.

### 185. Cli_goal_mark_landed_force_deletes_branch_kept_by_safe_worktree_remove

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\61f21226bd6545efa8539dd4f273f374' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\61f21226bd6545efa8539dd4f273f374' is denied.

### 186. Cli_goal_mark_landed_force_deletes_branch_kept_by_safe_worktree_remove

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\89cf8c92855d4686a4bcb01158ad622d' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\89cf8c92855d4686a4bcb01158ad622d' is denied.

### 187. Cli_goal_mark_landed_passes_remaining_cleanup_budget_to_worktree_remove

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cbd609d4900d48f5b306310737906c0c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cbd609d4900d48f5b306310737906c0c' is denied.

### 188. Cli_goal_mark_landed_passes_remaining_cleanup_budget_to_worktree_remove

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\708c3e6efcbf4e84b06c63c8b6d451b9' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\708c3e6efcbf4e84b06c63c8b6d451b9' is denied.

### 189. Cli_goal_mark_landed_records_landed_and_defers_workspace_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4334ace3f11a41178ada045016b471c0' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4334ace3f11a41178ada045016b471c0' is denied.

### 190. Cli_goal_mark_landed_records_landed_and_defers_workspace_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5bb473c2d87147f08259de3e92a87183' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5bb473c2d87147f08259de3e92a87183' is denied.

### 191. Cli_goal_mark_landed_records_landed_state_before_deferred_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b4815ffa8ff74752a9aedcd2d19a387f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b4815ffa8ff74752a9aedcd2d19a387f' is denied.

### 192. Cli_goal_mark_landed_records_landed_state_before_deferred_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a8e29fcd01cb433e97cdd5be87692ae5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a8e29fcd01cb433e97cdd5be87692ae5' is denied.

### 193. Cli_goal_recovery_prints_cleanup_backoff_skip_until

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\561f8512219d4d3c8c5cfcd88a9bb313' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 7.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\561f8512219d4d3c8c5cfcd88a9bb313' is denied.

### 194. Cli_goal_recovery_prints_cleanup_backoff_skip_until

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8041ac6277b8409086171d944e055558' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 7.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8041ac6277b8409086171d944e055558' is denied.

### 195. Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 196. Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c42653154f344f9186055ac6017a1a74' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c42653154f344f9186055ac6017a1a74' is denied.

### 197. Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\aaa3145f50e24e4ea9f58657debac2e0' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\aaa3145f50e24e4ea9f58657debac2e0' is denied.

### 198. Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 199. Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ef11f01ac3d84033942d19de6eee78fd' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ef11f01ac3d84033942d19de6eee78fd' is denied.

### 200. Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\fe7e38b07e544bcb8d6abbdc15ab8298' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\fe7e38b07e544bcb8d6abbdc15ab8298' is denied.

### 201. Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 202. Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\04273a0be7344fea97095ce0bca750ea' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\04273a0be7344fea97095ce0bca750ea' is denied.

### 203. Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1a23b111620540c3b7e176583341f309' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1a23b111620540c3b7e176583341f309' is denied.

### 204. Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_times_out

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 103; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 205. Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 206. Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cdd145d57b8742619c83ce19af8a70d8' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cdd145d57b8742619c83ce19af8a70d8' is denied.

### 207. Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60753be7d18749758793bd15fdf03412' is denied.`. Pattern: **single-goal-unresolved**. Passes: 205; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\60753be7d18749758793bd15fdf03412' is denied.

### 208. Cli_lifecycle_simple_goal_safe_auto_stops_before_acceptance

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\50a079619cc041119b166e08325abf2b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\50a079619cc041119b166e08325abf2b' is denied.

### 209. Cli_lifecycle_simple_goal_safe_auto_stops_before_acceptance

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6c5a643a1d49427c81456d2a0f800e95' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6c5a643a1d49427c81456d2a0f800e95' is denied.

### 210. Cli_note_confirms_and_keeps_status_across_task_states

Signature: `Assert.Contains() Failure: Filter not matched in collection`. Pattern: **single-goal-unresolved**. Passes: 105; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=20912699; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 20912699-0-20260803170424836 | 2026-08-03 | goal 20912699 | Assert.Contains() Failure: Filter not matched in collection

### 211. Cli_note_preserves_task_and_allows_subscription_dispatch_and_retry

Signature: `Assert.Contains() Failure: Filter not matched in collection`. Pattern: **single-goal-unresolved**. Passes: 105; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=20912699; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 20912699-0-20260803170424836 | 2026-08-03 | goal 20912699 | Assert.Contains() Failure: Filter not matched in collection

### 212. Cli_profile_dispatch_auto_creates_workspace_when_missing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\34e987bbfb0d4743a73e2f3433af5c36' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\34e987bbfb0d4743a73e2f3433af5c36' is denied.

### 213. Cli_profile_dispatch_auto_creates_workspace_when_missing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a38b852540054082b4f4348a40ec0c1e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a38b852540054082b4f4348a40ec0c1e' is denied.

### 214. Cli_project_create_seeds_independent_runnable_configuration

Signature: `System.InvalidOperationException : Goal 'ca5ac37b' task 1: Subscription preflight failed: profile: codex-spark; dispatch-lane: codex-spark; model-selection: cheap-lane: Developer small-task uses codex-spark/gpt-5.3-codex-spark; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.3-codex-spark; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'codex-spark' is a real launcher; ok: worker profile 'codex-spark' pins selected model; reasoning-effort: low (base); ok: worker profile 'codex-spark' pins selected reasoning when required; capability: workspace-write - Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and stdin prompt delivery are configured.; blocked: missing required local skill(s): dotnet-windows-build-hygiene at .agents\skills\dotnet-windows-build-hygiene\SKILL.md, orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md, verification-before-completion at .agents\skills\verification-before-completion\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: goal lease not yet created artifacts=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-93cbca1a6446432da188ab0030e6d18b\goals\ca5ac37b\artifacts; warning: worktree cleanliness unavailable before dispatch (fatal: not a git repository: C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/mcg-orchestrator-tests/d93c4b05f45e4854bd58b79f1820fd0f/.orchestrator-worktrees/ca5ac37b/..); verify git status from the goal workspace; warn: git metadata index_lock=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-orchestrator-tests\d93c4b05f45e4854bd58b79f1820fd0f\.orchestrator-worktrees\ca5ac37b\.git\index.lock; current_identity=miles; current_process_can_write=False; worker_git_write=unavailable; commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf; git metadata warning: git rev-parse --git-path index.lock failed; reviewer-scope: changed-file contract not required for non-Reviewer role; reviewer-merge-tree: merge-tree contract not required for non-Reviewer role`. Pattern: **single-goal-unresolved**. Passes: 81; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | System.InvalidOperationException : Goal 'ca5ac37b' task 1: Subscription preflight failed: profile: codex-spark; dispatch-lane: codex-spark; model-selection: cheap-lane: Developer small-task uses codex-spark/gpt-5.3-codex-spark; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.3-codex-spark; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'codex-spark' is a real launcher; ok: worker profile 'codex-spark' pins selected model; reasoning-effort: low (base); ok: worker profile 'codex-spark' pins selected reasoning when required; capability: workspace-write - Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and stdin prompt delivery are configured.; blocked: missing required local skill(s): dotnet-windows-build-hygiene at .agents\skills\dotnet-windows-build-hygiene\SKILL.md, orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md, verification-before-completion at .agents\skills\verification-before-completion\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: goal lease not yet created artifacts=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-93cbca1a6446432da188ab0030e6d18b\goals\ca5ac37b\artifacts; warning: worktree cleanliness unavailable before dispatch (fatal: not a git repository: C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/mcg-orchestrator-tests/d93c4b05f45e4854bd58b79f1820fd0f/.orchestrator-worktrees/ca5ac37b/..); verify git status from the goal workspace; warn: git metadata index_lock=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-orchestrator-tests\d93c4b05f45e4854bd58b79f1820fd0f\.orchestrator-worktrees\ca5ac37b\.git\index.lock; current_identity=miles; current_process_can_write=False; worker_git_write=unavailable; commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf; git metadata warning: git rev-parse --git-path index.lock failed; reviewer-scope: changed-file contract not required for non-Reviewer role; reviewer-merge-tree: merge-tree contract not required for non-Reviewer role

### 215. Cli_recover_reconciles_dead_running_task_with_exit_file

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fcebe8c4e6d14fb0a7a69c07fc3fbb29' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fcebe8c4e6d14fb0a7a69c07fc3fbb29' is denied.

### 216. Cli_recover_reconciles_dead_running_task_with_exit_file

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ffe350620b7e428f8b00f92a0501de17' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ffe350620b7e428f8b00f92a0501de17' is denied.

### 217. Cli_recover_resets_cancelled_tasks_without_disturbing_completed_or_running_tasks

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a0ac979db5aa492fadd54caa1cccaf4d' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a0ac979db5aa492fadd54caa1cccaf4d' is denied.

### 218. Cli_recover_resets_cancelled_tasks_without_disturbing_completed_or_running_tasks

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d5bb8b14f25f4169a75970569563e951' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d5bb8b14f25f4169a75970569563e951' is denied.

### 219. Cli_recover_resets_failed_task_to_dispatchable

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\453ac4e3dbf6443c827a043febeb5ea5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\453ac4e3dbf6443c827a043febeb5ea5' is denied.

### 220. Cli_recover_resets_failed_task_to_dispatchable

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\10bdf14caa1f4204907e588b1898da35' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\10bdf14caa1f4204907e588b1898da35' is denied.

### 221. Cli_repo_process_commands_skip_persistent_state_loading

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\662944907bf24b2d8adbf263801aeeb4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\662944907bf24b2d8adbf263801aeeb4' is denied.

### 222. Cli_repo_process_commands_skip_persistent_state_loading

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a49ce9db8af64516919ec953679305d7' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a49ce9db8af64516919ec953679305d7' is denied.

### 223. Cli_startup_help_and_backlog_commands_skip_orphan_worktree_cleanup

Signature: `Assert.DoesNotContain() Failure: Sub-string found`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d5b985e8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d5b985e8-0-20260731013940563 | 2026-07-31 | goal d5b985e8 | Assert.DoesNotContain() Failure: Sub-string found

### 224. Cli_text_file_task_commands_record_file_content

Signature: `Assert.Contains() Failure: Filter not matched in collection`. Pattern: **single-goal-unresolved**. Passes: 105; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=20912699; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 20912699-0-20260803170424836 | 2026-08-03 | goal 20912699 | Assert.Contains() Failure: Filter not matched in collection

### 225. Cli_workspace_command_creates_and_removes_goal_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b360f97fff644f5f989b2a88106f6bea' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b360f97fff644f5f989b2a88106f6bea' is denied.

### 226. Cli_workspace_command_creates_and_removes_goal_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\26a2059abd914968821128551a115135' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\26a2059abd914968821128551a115135' is denied.

### 227. Cli_workspace_help_does_not_create_goal_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6ccedf0f7f7e460bae5794b41442dbf4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6ccedf0f7f7e460bae5794b41442dbf4' is denied.

### 228. Cli_workspace_help_does_not_create_goal_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e5b2ec1d6fbb498abb3f6a8dc80c64c6' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e5b2ec1d6fbb498abb3f6a8dc80c64c6' is denied.

### 229. Cli_workspace_rebase_updates_clean_stale_goal_branch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\452dca1269274e7ebb68870bce6cce8a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\452dca1269274e7ebb68870bce6cce8a' is denied.

### 230. Cli_workspace_rebase_updates_clean_stale_goal_branch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f66dbd67402147d3a69210b04f29a4b0' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\f66dbd67402147d3a69210b04f29a4b0' is denied.

### 231. Cli_workspace_remove_can_target_non_current_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d02103aaeff74f5c9f07f1c6f3975046' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d02103aaeff74f5c9f07f1c6f3975046' is denied.

### 232. Cli_workspace_remove_can_target_non_current_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5c5465079ddc4bc78212f349efe1648c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5c5465079ddc4bc78212f349efe1648c' is denied.

### 233. Cli_workspace_remove_does_not_complete_verified_goal_without_landing_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a8b453955dc84e48aec39c6b0b0a99af' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a8b453955dc84e48aec39c6b0b0a99af' is denied.

### 234. Cli_workspace_remove_does_not_complete_verified_goal_without_landing_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\de4a0a33daa3436d99884f8d49dd6663' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\de4a0a33daa3436d99884f8d49dd6663' is denied.

### 235. Cli_workspace_remove_force_terminal_cleanup_bypasses_escalated_backoff

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5d02f23c73de4f9e98150e732197ae15' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5d02f23c73de4f9e98150e732197ae15' is denied.

### 236. Cli_workspace_remove_force_terminal_cleanup_bypasses_escalated_backoff

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1fa273d77d15419b9c97e71bf79c38cb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1fa273d77d15419b9c97e71bf79c38cb' is denied.

### 237. Cli_workspace_remove_keeps_stale_acceptance_failure_without_landing_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\54dd48b9418c4563a0196c1426f86cf3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\54dd48b9418c4563a0196c1426f86cf3' is denied.

### 238. Cli_workspace_remove_keeps_stale_acceptance_failure_without_landing_evidence

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8b23f7f198384bfd8856f9d3d1d5ae50' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8b23f7f198384bfd8856f9d3d1d5ae50' is denied.

### 239. Cli_workspace_remove_persists_provider_session_retirement

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6ee9c2533e3449be8552b74c8ff93023' is denied.`. Pattern: **single-goal-unresolved**. Passes: 95; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\6ee9c2533e3449be8552b74c8ff93023' is denied.

### 240. Cli_workspace_remove_persists_provider_session_retirement

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e66502fc43bf4017930cb27e2c604506' is denied.`. Pattern: **single-goal-unresolved**. Passes: 95; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e66502fc43bf4017930cb27e2c604506' is denied.

### 241. Cli_workspace_remove_prints_cleanup_backoff_skip_until

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fada0bda04f344fe9dbdf6015ecec13f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fada0bda04f344fe9dbdf6015ecec13f' is denied.

### 242. Cli_workspace_remove_prints_cleanup_backoff_skip_until

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c89327fd643b4f48901c6710e7df9aa5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c89327fd643b4f48901c6710e7df9aa5' is denied.

### 243. Cli_workspace_remove_repairs_landed_cleaned_stale_acceptance_failure

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\083478e58ebf44d786d4b1a692a73365' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\083478e58ebf44d786d4b1a692a73365' is denied.

### 244. Cli_workspace_remove_repairs_landed_cleaned_stale_acceptance_failure

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\4a814b10156144aa8f140d5e52f6c7e2' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\4a814b10156144aa8f140d5e52f6c7e2' is denied.

### 245. Cli_workspace_remove_safe_auto_blocks_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e5f5d83a2b2743e5a62735d8b25c6329' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e5f5d83a2b2743e5a62735d8b25c6329' is denied.

### 246. Cli_workspace_remove_safe_auto_blocks_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\beae10acbb65450d817f0eb108fdaa61' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\beae10acbb65450d817f0eb108fdaa61' is denied.

### 247. CliCommandTestsGoalRevision.CliReviseAcceptsBriefFromStandardInput

Signature: `System.ArgumentException : Unknown option '-'.`. Pattern: **single-goal-unresolved**. Passes: 54; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=85af9b2f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 85af9b2f-0-20260808021001537 | 2026-08-08 | goal 85af9b2f | System.ArgumentException : Unknown option '-'.

### 248. CliCommandTestsPersistentRunnerCommands.ConductLoop_EvictedGoal_LaterIntentKeepsReason

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 249. CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Cancelled, enqueueIntent: False)

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 250. CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Cancelled, enqueueIntent: True)

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 251. CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Failed, enqueueIntent: False)

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 252. CliCommandTestsPersistentRunnerCommands.ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(storedStatus: Superseded, enqueueIntent: False)

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 253. CliCommandTestsPersistentRunnerCommands.ConductLoop_LiveGoal_ReloadsAndWalks

Signature: `System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3543fa6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3543fa6-0-20260806233723915 | 2026-08-06 | goal d3543fa6 | System.IO.IOException : The process cannot access the file 'state.db' because it is being used by another process.

### 254. CliPersistentStateRunner_goal_create_rejects_competing_backlog_link_atomically

Signature: `Assert.Null() Failure: Value is not null`. Pattern: **single-goal-unresolved**. Passes: 43; goal failures: 1; operator failures: 0; pre-review failures: 7.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9f64cd98; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9f64cd98-0-20260820104858058 | 2026-08-20 | goal 9f64cd98 | Assert.Null() Failure: Value is not null

### 255. CodexEgressProxy_tunnels_bytes_end_to_end

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 70; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=24d04cc8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 24d04cc8-0-20260728130105886 | 2026-07-28 | goal 24d04cc8 | Assert.Equal() Failure: Values differ

### 256. Conductor default subscription start does not spawn scoped real worker process

Signature: `Assert.IsType() Failure: Value is not the exact type`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Assert.IsType() Failure: Value is not the exact type

### 257. Conductor_cleanup_records_cleanup_needed_without_deleting_on_critical_path

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e5595fe0a9764dcc81a69b03dca8bee2' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e5595fe0a9764dcc81a69b03dca8bee2' is denied.

### 258. Conductor_cleanup_records_cleanup_needed_without_deleting_on_critical_path

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a04215f87309446f8ebd319c38944c4a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a04215f87309446f8ebd319c38944c4a' is denied.

### 259. ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_loads_valid_file

Signature: `System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\0f96358f-d36a-4552-9d28-b5214731b014'.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\0f96358f-d36a-4552-9d28-b5214731b014'.

### 260. ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_loads_valid_file

Signature: `System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\442f73a0-a4be-46f8-a3f1-34c2021c8a56'.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\442f73a0-a4be-46f8-a3f1-34c2021c8a56'.

### 261. ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_returns_Conservative_when_no_file

Signature: `System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\c59d623c-d331-4f08-9459-2f817ec57de3'.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\c59d623c-d331-4f08-9459-2f817ec57de3'.

### 262. ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_returns_Conservative_when_no_file

Signature: `System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\e0974be0-1949-4be7-9839-3b41ff2aa4fb'.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.IO.DirectoryNotFoundException : Could not find a part of the path 'C:\Users\miles\AppData\Local\Temp\e0974be0-1949-4be7-9839-3b41ff2aa4fb'.

### 263. ConductorBatchLoopTests.TransientSqliteCheckpoint_HeldGoalRecoversWithoutRepeatingSideEffect(sqliteErrorCode: 5)

Signature: `Assert.Single() Failure: The collection did not contain any matching items`. Pattern: **single-goal-unresolved**. Passes: 10; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a840f9b6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a840f9b6-0-20260811154216575 | 2026-08-11 | goal a840f9b6 | Assert.Single() Failure: The collection did not contain any matching items

### 264. ConductorBatchLoopTests.TransientSqliteCheckpoint_HeldGoalRecoversWithoutRepeatingSideEffect(sqliteErrorCode: 6)

Signature: `Assert.Single() Failure: The collection did not contain any matching items`. Pattern: **single-goal-unresolved**. Passes: 10; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a840f9b6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a840f9b6-0-20260811154216575 | 2026-08-11 | goal a840f9b6 | Assert.Single() Failure: The collection did not contain any matching items

### 265. ConductorDriver_unparseable_typed_evidence_finding_reports_typed_refusal

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 59; goal failures: 1; operator failures: 0; pre-review failures: 3.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c8d51c40; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c8d51c40-0-20260808003356973 | 2026-08-08 | goal c8d51c40 | Assert.Equal() Failure: Values differ

### 266. ConductorDriverTests.TesterStructuredRequestReceivesFindingBoundReceiptInRetryContext

Signature: `Assert.DoesNotContain() Failure: Sub-string found`. Pattern: **single-goal-unresolved**. Passes: 59; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c8d51c40; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c8d51c40-0-20260808003356973 | 2026-08-08 | goal c8d51c40 | Assert.DoesNotContain() Failure: Sub-string found

### 267. ConductorLoopHandoff_successor_survives_parent_job_exit_and_emits_loop_start

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 100; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6251e612; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6251e612-0-20260730095420411 | 2026-07-30 | goal 6251e612 | Assert.Equal() Failure: Values differ

### 268. ConductorSelfRelaunch_real_binary_build_self_check_and_handoff

Signature: `build merged conductor failed exit=1 timedOut=False: C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.Core\obj' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj]    Build FAILED.      0 Warning(s)      1 Error(s)    Time Elapsed 00:00:01.15`. Pattern: **single-goal-unresolved**. Passes: 91; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | build merged conductor failed exit=1 timedOut=False: C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.Core\obj' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj]    Build FAILED.      0 Warning(s)      1 Error(s)    Time Elapsed 00:00:01.15

### 269. ConductorSelfRelaunch_real_binary_build_self_check_and_handoff

Signature: `build merged conductor failed exit=1 timedOut=False: C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.Core\obj\38010a3a-ed2b-47f0-a0a4-532c4a5efcd8.tmp' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj]    Build FAILED.      0 Warning(s)      1 Error(s)    Time Elapsed 00:00:01.22`. Pattern: **single-goal-unresolved**. Passes: 91; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | build merged conductor failed exit=1 timedOut=False: C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.Core\obj\38010a3a-ed2b-47f0-a0a4-532c4a5efcd8.tmp' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj]    Build FAILED.      0 Warning(s)      1 Error(s)    Time Elapsed 00:00:01.22

### 270. Dashboard_api_subscription_dispatch_acknowledges_limit_review

Signature: `Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: profile: codex-spark; dispatch-lane: codex-spark; model-selection: cheap-lane: Developer small-task uses codex-spark/gpt-5.3-codex-spark; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.3-codex-spark; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'codex-spark' is a real launcher; ok: worker profile 'codex-spark' pins selected model; reasoning-effort: low (base); ok: worker profile 'codex-spark' pins selected reasoning when required; capability: workspace-write - Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and stdin prompt delivery are configured.; blocked: missing required local skill(s): dotnet-windows-build-hygiene at .agents\skills\dotnet-windows-build-hygiene\SKILL.md, orchestrator-dogfood at .agents\skills\orchestrator-dogfood\SKILL.md, orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md, verification-before-completion at .agents\skills\verification-before-completion\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: goal lease not yet created artifacts=C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\e782a7b8\artifacts; warning: worktree cleanliness unavailable before dispatch (fatal: not a git repository: C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/mcg-orchestrator-tests/418d177949a3496fa2af3d1456c25490/.orchestrator-worktrees/e782a7b8/..); verify git status from the goal workspace; warn: git metadata index_lock=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-orchestrator-tests\418d177949a3496fa2af3d1456c25490\.orchestrator-worktrees\e782a7b8\.git\index.lock; current_identity=miles; current_process_can_write=False; worker_git_write=unavailable; commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf; git metadata warning: git rev-parse --git-path index.lock failed; reviewer-scope: changed-file contract not required for non-Reviewer role; reviewer-merge-tree: merge-tree contract not required for non-Reviewer role`. Pattern: **single-goal-unresolved**. Passes: 105; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: profile: codex-spark; dispatch-lane: codex-spark; model-selection: cheap-lane: Developer small-task uses codex-spark/gpt-5.3-codex-spark; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.3-codex-spark; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'codex-spark' is a real launcher; ok: worker profile 'codex-spark' pins selected model; reasoning-effort: low (base); ok: worker profile 'codex-spark' pins selected reasoning when required; capability: workspace-write - Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and stdin prompt delivery are configured.; blocked: missing required local skill(s): dotnet-windows-build-hygiene at .agents\skills\dotnet-windows-build-hygiene\SKILL.md, orchestrator-dogfood at .agents\skills\orchestrator-dogfood\SKILL.md, orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md, verification-before-completion at .agents\skills\verification-before-completion\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: goal lease not yet created artifacts=C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\e782a7b8\artifacts; warning: worktree cleanliness unavailable before dispatch (fatal: not a git repository: C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/mcg-orchestrator-tests/418d177949a3496fa2af3d1456c25490/.orchestrator-worktrees/e782a7b8/..); verify git status from the goal workspace; warn: git metadata index_lock=C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-orchestrator-tests\418d177949a3496fa2af3d1456c25490\.orchestrator-worktrees\e782a7b8\.git\index.lock; current_identity=miles; current_process_can_write=False; worker_git_write=unavailable; commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf; git metadata warning: git rev-parse --git-path index.lock failed; reviewer-scope: changed-file contract not required for non-Reviewer role; reviewer-merge-tree: merge-tree contract not required for non-Reviewer role

### 271. Dashboard_retry_records_capability_warning_for_gh_cli_instruction

Signature: `Assert.Contains() Failure: Filter not matched in collection`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=81f85740; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 81f85740-0-20260727092940685 | 2026-07-27 | goal 81f85740 | Assert.Contains() Failure: Filter not matched in collection

### 272. Developer_context_receives_complete_ingested_Planner_plan_without_paid_start

Signature: `System.InvalidOperationException : git commit -m Track repository ignore rules failed:`. Pattern: **single-goal-unresolved**. Passes: 83; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=10075221; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 10075221-0-20260805175523615 | 2026-08-05 | goal 10075221 | System.InvalidOperationException : git commit -m Track repository ignore rules failed:

### 273. DispatchProcessHost_ApplyWorkerSandbox_creates_bin_shims_and_prepends_child_path_only

Signature: `System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-apply-sandbox-test\933bee3d1e8f436da29d0e90bcad8f74'.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c93241d0; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c93241d0-0-20260730082751919 | 2026-07-30 | goal c93241d0 | System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-apply-sandbox-test\933bee3d1e8f436da29d0e90bcad8f74'.

### 274. DispatchProcessHost_ApplyWorkerSandbox_leaves_unknown_provider_without_provider_home

Signature: `System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-unknown-provider-sandbox-test\7a8759fd59ad477ea77a441d062f3e34'.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c93241d0; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c93241d0-0-20260730082751919 | 2026-07-30 | goal c93241d0 | System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-unknown-provider-sandbox-test\7a8759fd59ad477ea77a441d062f3e34'.

### 275. DispatchProcessHost_ApplyWorkerSandbox_scopes_codex_home_to_codex_provider

Signature: `System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-provider-sandbox-test\233e3b46d3de4036876b95116ed53994'.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c93241d0; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c93241d0-0-20260730082751919 | 2026-07-30 | goal c93241d0 | System.InvalidOperationException : Failed to protect workspace boundary 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-provider-sandbox-test\233e3b46d3de4036876b95116ed53994'.

### 276. DispatchProcessHost_heartbeat_cpu_and_pids_reflect_wrapped_grandchild

Signature: `System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-dispatch-host-grandchild-tests\360dd58ad7154a0a813866a334c64a04\grandchild-reap-probe.log' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 90; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=38d930d3; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 38d930d3-0-20260808040243645 | 2026-08-08 | goal 38d930d3 | System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-dispatch-host-grandchild-tests\360dd58ad7154a0a813866a334c64a04\grandchild-reap-probe.log' because it is being used by another process.

### 277. DispatchProcessHost_read_only_Claude_after_writable_round_denies_mutation_and_emits_worker_result

Signature: `Writable Claude sandbox dispatch exited 1: {"event":"sandbox-prep","phase":"start","timestamp":"2026-08-10T21:12:32.5602221\u002B00:00","startedAt":"2026-08-10T21:12:32.5598122\u002B00:00","workingDirectory":"C:\\Users\\miles\\AppData\\Local\\Temp\\Low\\mcg-tests\\mcg-orchestrator-tests\\b10aff22fd0842aaa3b0695cbae90fb0\\worktree"}`. Pattern: **single-goal-unresolved**. Passes: 0; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | Writable Claude sandbox dispatch exited 1: {"event":"sandbox-prep","phase":"start","timestamp":"2026-08-10T21:12:32.5602221\u002B00:00","startedAt":"2026-08-10T21:12:32.5598122\u002B00:00","workingDirectory":"C:\\Users\\miles\\AppData\\Local\\Temp\\Low\\mcg-tests\\mcg-orchestrator-tests\\b10aff22fd0842aaa3b0695cbae90fb0\\worktree"}

### 278. DotnetBuildEnvironmentManager_no_holder_artifact_prep_lock_retries_and_acquires

Signature: `Assert.IsType() Failure: Value is not the exact type`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=03f8a767; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 03f8a767-0-20260721054729133 | 2026-07-21 | goal 03f8a767 | Assert.IsType() Failure: Value is not the exact type

### 279. DotnetBuildEnvironmentManagerTests.FocusedRunner_AllSlotsHeld_ReportsNoSlotWithoutStartingDotnet

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\50055382\isolated\build-slots' is denied.`. Pattern: **single-goal-unresolved**. Passes: 69; goal failures: 1; operator failures: 0; pre-review failures: 4.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3829298; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3829298-0-20260804134951462 | 2026-08-04 | goal d3829298 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\50055382\isolated\build-slots' is denied.

### 280. DotnetBuildEnvironmentManagerTests.FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\394455b7\isolated\goals\budget-test\focused-artifacts\build-0\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug' is denied.`. Pattern: **single-goal-unresolved**. Passes: 63; goal failures: 1; operator failures: 0; pre-review failures: 12.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3829298; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3829298-0-20260804134951462 | 2026-08-04 | goal d3829298 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\394455b7\isolated\goals\budget-test\focused-artifacts\build-0\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug' is denied.

### 281. DotnetBuildEnvironmentManagerTests.FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\ca52edc9\LocalLow\mcg-dotnet-isolated\goals\aaaaaaaa\focused-artifacts\build-0\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug' is denied.`. Pattern: **single-goal-unresolved**. Passes: 69; goal failures: 1; operator failures: 0; pre-review failures: 4.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d3829298; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d3829298-0-20260804134951462 | 2026-08-04 | goal d3829298 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\f\ca52edc9\LocalLow\mcg-dotnet-isolated\goals\aaaaaaaa\focused-artifacts\build-0\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug' is denied.

### 282. Equal SHA identity violations salvage the prior ledger and converge normally

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 32; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9c829e5e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9c829e5e-0-20260813161109426 | 2026-08-13 | goal 9c829e5e | Assert.Equal() Failure: Values differ

### 283. ExcessWorkerRoundAnalysisScriptTests.TaskOutcomeReason_WithoutReadyBlockedProducer_IsNotProviderEvidence

Signature: `System.ComponentModel.Win32Exception : An error occurred trying to start process 'pwsh' with working directory 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8304\mcg-excess-round-tests\015f3a05253c4d6e9cf9cfcb07402261'. Access is denied.`. Pattern: **single-goal-unresolved**. Passes: 37; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=f9e4f0f0; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- f9e4f0f0-0-20260818002131646 | 2026-08-18 | goal f9e4f0f0 | System.ComponentModel.Win32Exception : An error occurred trying to start process 'pwsh' with working directory 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8304\mcg-excess-round-tests\015f3a05253c4d6e9cf9cfcb07402261'. Access is denied.

### 284. ExecuteAssignedTask_rejects_subscription_dispatch_evidence_without_calling_provider

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 121; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6a960b3f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6a960b3f-0-20260808170616823 | 2026-08-08 | goal 6a960b3f | Assert.True() Failure

### 285. FailureTriagePlanner_uses_typed_preflight_outcome_for_permission_repair

Signature: `System.InvalidOperationException : git commit -m Seed failed:`. Pattern: **single-goal-unresolved**. Passes: 68; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=10075221; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 10075221-0-20260805175523615 | 2026-08-05 | goal 10075221 | System.InvalidOperationException : git commit -m Seed failed:

### 286. Gated_progressive_glance_preserves_worktree_and_allows_acceptance_completion

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\27563d1048b147dbb10f201db4664297' is denied.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\27563d1048b147dbb10f201db4664297' is denied.

### 287. Gated_progressive_glance_preserves_worktree_and_allows_acceptance_completion

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1197e1639ad046aaa68c11e13f2c2102' is denied.`. Pattern: **single-goal-unresolved**. Passes: 71; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1197e1639ad046aaa68c11e13f2c2102' is denied.

### 288. GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data

Signature: `Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p73e0\snapshot-status-3d2a6a53aa2d4ae99520e678346c8697\status-child.pid`. Pattern: **load-dependent**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
- 19d16954-0-20260821162836436 | 2026-08-21 | goal 19d16954 | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p73e0\snapshot-status-3d2a6a53aa2d4ae99520e678346c8697\status-child.pid

### 289. GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data

Signature: `Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p7450\snapshot-status-b077f240aa8749429c242b5d1f132397\status-child.pid`. Pattern: **load-dependent**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
- 58ae2e59-0-20260821164735299 | 2026-08-21 | goal 58ae2e59 | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p7450\snapshot-status-b077f240aa8749429c242b5d1f132397\status-child.pid

### 290. GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data

Signature: `Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p74bc\snapshot-status-6d5abd64cff1459aa1ffb581ee52adfc\status-child.pid`. Pattern: **load-dependent**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
- 9d804744-0-20260821153015574 | 2026-08-21 | goal 9d804744 | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p74bc\snapshot-status-6d5abd64cff1459aa1ffb581ee52adfc\status-child.pid

### 291. GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data

Signature: `Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8e1c\snapshot-status-8fa08667c5c44c91b1d9f8752808b488\status-child.pid`. Pattern: **load-dependent**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
- 9d804744-0-20260821164347112 | 2026-08-21 | goal 9d804744 | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8e1c\snapshot-status-8fa08667c5c44c91b1d9f8752808b488\status-child.pid

### 292. GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data

Signature: `Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\pbe4\snapshot-status-2409b775a46d47cf99a8aa2193dad8c2\status-child.pid`. Pattern: **load-dependent**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
- 9d804744-0-20260821142754277 | 2026-08-21 | goal 9d804744 | Expected file to exist: C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\pbe4\snapshot-status-2409b775a46d47cf99a8aa2193dad8c2\status-child.pid

### 293. Goal_scope_collision_advisory_keeps_goal_creation_nonblocking_without_dispatch

Signature: `System.InvalidOperationException : GOAL_CREATE_DELIVERY_INCOMPLETE goal=94064117a79042da9e771451d5ebed06 retry="goal-delivery-retry 94064117a79042da9e771451d5ebed06" detail=Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 29; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fe37d616; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | System.InvalidOperationException : GOAL_CREATE_DELIVERY_INCOMPLETE goal=94064117a79042da9e771451d5ebed06 retry="goal-delivery-retry 94064117a79042da9e771451d5ebed06" detail=Assert.Equal() Failure: Values differ

### 294. GoalAbandon_removes_terminal_worktree_in_one_cleanup_cycle

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d9937b8a8d204279b0fef3b5cf74aff8' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d9937b8a8d204279b0fef3b5cf74aff8' is denied.

### 295. GoalAbandon_removes_terminal_worktree_in_one_cleanup_cycle

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8c03b5d523a748db9763d2754b62d3f4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8c03b5d523a748db9763d2754b62d3f4' is denied.

### 296. GoalAcceptanceVerifier_full_shards_override_runs_all_policy_shards

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 297. GoalAcceptanceVerifier_partition_verdict_cache_aggregate_ignores_unrelated_check_failures

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 103; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 298. GoalAcceptanceVerifier_partition_verdict_cache_backstop_forces_full_rerun

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 299. GoalAcceptanceVerifier_partition_verdict_cache_backstop_forces_full_rerun

Signature: `Assert.False() Failure`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=1a270cd6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 1a270cd6-0-20260815151637204 | 2026-08-15 | goal 1a270cd6 | Assert.False() Failure

### 300. GoalAcceptanceVerifier_partition_verdict_cache_filter_hash_change_runs_only_changed_partition

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 103; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 301. GoalAcceptanceVerifier_partition_verdict_cache_invalidates_on_candidate_or_main_sha_change

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 103; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 302. GoalAcceptanceVerifier_partition_verdict_cache_records_later_partitions_after_early_failure

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 303. GoalAcceptanceVerifier_partition_verdict_cache_records_later_partitions_after_early_failure

Signature: `Assert.False() Failure`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=1a270cd6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 1a270cd6-0-20260815151637204 | 2026-08-15 | goal 1a270cd6 | Assert.False() Failure

### 304. GoalAcceptanceVerifier_partition_verdict_cache_reuses_green_partitions_on_reroll

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 103; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 305. GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 86; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c6cdba9b; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c6cdba9b-0-20260815151808188 | 2026-08-15 | goal c6cdba9b | Assert.Equal() Failure: Values differ

### 306. GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease

Signature: `Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-a66722f6477e40e6b541e1beb210d24f\runs\p28736-build-1\artifacts\bin\Mcg.AgentOrchestrator.Core\debug\Mcg.AgentOrchestrator.Core.dll; holder=unknown.`. Pattern: **single-goal-unresolved**. Passes: 86; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fcf8669a; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fcf8669a-0-20260802042053873 | 2026-08-02 | goal fcf8669a | Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-a66722f6477e40e6b541e1beb210d24f\runs\p28736-build-1\artifacts\bin\Mcg.AgentOrchestrator.Core\debug\Mcg.AgentOrchestrator.Core.dll; holder=unknown.

### 307. GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease

Signature: `Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-c7c6751b99c749229a9251b0bbfc4eb6\runs\p33484-build-0\artifacts\bin\Mcg.AgentOrchestrator.Core\debug\Mcg.AgentOrchestrator.Core.dll; holder=unknown.`. Pattern: **single-goal-unresolved**. Passes: 86; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d4897de1; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d4897de1-0-20260801213910727 | 2026-08-01 | goal d4897de1 | Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated-slot-run-c7c6751b99c749229a9251b0bbfc4eb6\runs\p33484-build-0\artifacts\bin\Mcg.AgentOrchestrator.Core\debug\Mcg.AgentOrchestrator.Core.dll; holder=unknown.

### 308. GoalAcceptanceVerifier_runs_core_and_dependent_infrastructure_shards_for_core_scope

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 3.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 309. GoalAcceptanceVerifier_safety_valves_force_full_policy_shards

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 310. GoalAcceptanceVerifier_selects_infrastructure_tests_from_default_plan

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 311. GoalAcceptanceVerifier_slot_gate_prefers_completed_red_test_verdict_after_transient_build_lock

Signature: `Assert.Matches() Failure: Pattern not found in value`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=03f8a767; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 03f8a767-0-20260721054729133 | 2026-07-21 | goal 03f8a767 | Assert.Matches() Failure: Pattern not found in value

### 312. GoalAcceptanceVerifier_substitutes_solution_check_with_partitioned_infrastructure_checks_for_infra_scope

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 313. GoalAcceptanceVerifier_substitutes_solution_check_with_union_for_core_and_infra_scope

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 314. GoalAcceptanceVerifier_test_tamper_guard_absent_when_no_test_files_in_diff

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4961f614; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4961f614-0-20260723183807776 | 2026-07-23 | goal 4961f614 | Assert.Equal() Failure: Values differ

### 315. GoalAcceptanceVerifier_within_attempt_rerun_tolerates_flaky_partition

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 99; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=1a270cd6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 1a270cd6-0-20260815151637204 | 2026-08-15 | goal 1a270cd6 | Assert.Equal() Failure: Values differ

### 316. GoalAcceptanceVerifierDotnetBuildSlotTests.FocusedEvidence_DualArmRealChecksClassifyVacuousValidAndInconclusive

Signature: `Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\NuGet\v3-cache\670c1461c29885f9aa22c281d8b7da90845b38e4$ps_api.nuget.org_v3_index.json\vuln_index.dat-new; holder=unknown.`. Pattern: **single-goal-unresolved**. Passes: 0; goal failures: 1; operator failures: 0; pre-review failures: 3.
Excluded from budget conversion: single-goal-unresolved; failing-goal=38d930d3; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 38d930d3-0-20260808040243645 | 2026-08-08 | goal 38d930d3 | Mcg.AgentOrchestrator.Infrastructure.BuildLockBlockedException : Build artifact lock blocked progress at C:\Users\miles\AppData\Local\NuGet\v3-cache\670c1461c29885f9aa22c281d8b7da90845b38e4$ps_api.nuget.org_v3_index.json\vuln_index.dat-new; holder=unknown.

### 317. GoalHealthEvaluator_prioritizes_dirty_worktree_before_next_action

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cd2a16598f1f4b32affe2a4e952e9328' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\cd2a16598f1f4b32affe2a4e952e9328' is denied.

### 318. GoalHealthEvaluator_prioritizes_dirty_worktree_before_next_action

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9cfdb0b2f92b434e97d809535b3d449f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9cfdb0b2f92b434e97d809535b3d449f' is denied.

### 319. GoalHealthEvaluator_scores_ready_failed_stalled_provider_limited_and_healthy_states

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4aa122fbcbcf4695b7d41a77b0e4781a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4aa122fbcbcf4695b7d41a77b0e4781a' is denied.

### 320. GoalHealthEvaluator_scores_ready_failed_stalled_provider_limited_and_healthy_states

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8df82c820f0c456687a0259b404f1101' is denied.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8df82c820f0c456687a0259b404f1101' is denied.

### 321. GoalRefinementTests.TryLaunchFirstPending_repairs_missing_outbox_then_launches

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 16; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fe37d616; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Equal() Failure: Values differ

### 322. Goals_subscribe_once_waits_through_unrelated_events_and_emits_one_matching_human_event

Signature: `System.TimeoutException : The operation has timed out.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=74e0ec9c; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 74e0ec9c-0-20260719053855675 | 2026-07-19 | goal 74e0ec9c | System.TimeoutException : The operation has timed out.

### 323. GoalWorktree_state_reads_construct_repository_while_write_lock_is_held

Signature: `Microsoft.Data.Sqlite.SqliteException : SQLite Error 5: 'database is locked'.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4c76250b; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4c76250b-0-20260720005555858 | 2026-07-20 | goal 4c76250b | Microsoft.Data.Sqlite.SqliteException : SQLite Error 5: 'database is locked'.

### 324. GoalWorktree_state_reads_construct_repository_while_write_lock_is_held

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d6afca0a6db94b69b995f8c3bcf8b691' is denied.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\d6afca0a6db94b69b995f8c3bcf8b691' is denied.

### 325. GoalWorktree_state_reads_construct_repository_while_write_lock_is_held

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e5e1ac89f9ca4398a0f17ceb21a581eb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e5e1ac89f9ca4398a0f17ceb21a581eb' is denied.

### 326. GoalWorktreeOrphanSweepScheduler_sweep_now_deletes_orphaned_worktree_directory

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b766228bff4a4c2680057fd0ee301d62' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b766228bff4a4c2680057fd0ee301d62' is denied.

### 327. GoalWorktreeOrphanSweepScheduler_sweep_now_deletes_orphaned_worktree_directory

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\818653b0b8ce4d9083339f53184ca0a7' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\818653b0b8ce4d9083339f53184ca0a7' is denied.

### 328. GoalWorktrees rechecks mutation blocker immediately before fast-forward

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fcf6cca9ff63445e91990335d93d48f3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fcf6cca9ff63445e91990335d93d48f3' is denied.

### 329. GoalWorktrees rechecks mutation blocker immediately before fast-forward

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d60d31697b4b4d06baebc2929b052068' is denied.`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d60d31697b4b4d06baebc2929b052068' is denied.

### 330. GoalWorktrees_acceptance_failed_retry_clears_completed_task_evidence_before_redispatch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\65841d503971444e9fc61d443fd76967' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\65841d503971444e9fc61d443fd76967' is denied.

### 331. GoalWorktrees_acceptance_failed_retry_clears_completed_task_evidence_before_redispatch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7ebe3fc9a9dc4eb8913b2adfb1f522de' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7ebe3fc9a9dc4eb8913b2adfb1f522de' is denied.

### 332. GoalWorktrees_cleanup_failure_logs_warning_and_reports_resumable_leftover

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4f7f966410354c14b34947873412cae0' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4f7f966410354c14b34947873412cae0' is denied.

### 333. GoalWorktrees_cleanup_failure_logs_warning_and_reports_resumable_leftover

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e4c07b19d1864328b9cff710a1f54e48' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e4c07b19d1864328b9cff710a1f54e48' is denied.

### 334. GoalWorktrees_commit_on_behalf_after_worker_commit_leaves_worktree_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3ab8971b28004187af44423982982097' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3ab8971b28004187af44423982982097' is denied.

### 335. GoalWorktrees_commit_on_behalf_after_worker_commit_leaves_worktree_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e8055b2ebff74b3a808dcc79fc584039' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e8055b2ebff74b3a808dcc79fc584039' is denied.

### 336. GoalWorktrees_creates_and_resolves_worktree_per_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\117ffd553db0482197efc22f55936205' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\117ffd553db0482197efc22f55936205' is denied.

### 337. GoalWorktrees_creates_and_resolves_worktree_per_goal

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48d6861e757644f3bf9df927cffebe3d' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48d6861e757644f3bf9df927cffebe3d' is denied.

### 338. GoalWorktrees_ensure_clears_existing_orphan_and_retries_once

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ffd8c382807a4e98b97a0f65d2d51e67' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ffd8c382807a4e98b97a0f65d2d51e67' is denied.

### 339. GoalWorktrees_ensure_clears_existing_orphan_and_retries_once

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\45d23c01ea96464581d232002b29832f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\45d23c01ea96464581d232002b29832f' is denied.

### 340. GoalWorktrees_ensure_fast_forwards_undriven_stale_worktree_to_base

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f7740c3cbdc843afad6643dd671cfb95' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f7740c3cbdc843afad6643dd671cfb95' is denied.

### 341. GoalWorktrees_ensure_fast_forwards_undriven_stale_worktree_to_base

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\772cb0b2067f425cb3a39a3925fb30e4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\772cb0b2067f425cb3a39a3925fb30e4' is denied.

### 342. GoalWorktrees_ensure_leaves_driven_divergent_worktree_untouched

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7ffeefa76db54549bdb96ddc4df78ce3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\7ffeefa76db54549bdb96ddc4df78ce3' is denied.

### 343. GoalWorktrees_ensure_leaves_driven_divergent_worktree_untouched

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3b716571ad2041368d1d3e8b5b1eedad' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3b716571ad2041368d1d3e8b5b1eedad' is denied.

### 344. GoalWorktrees_fast_forwards_goal_branch_on_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\07c668af8cd9426d81a20965dad25068' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\07c668af8cd9426d81a20965dad25068' is denied.

### 345. GoalWorktrees_fast_forwards_goal_branch_on_merge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9c84900e9ecd4b7f93c72cffc00dcf22' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9c84900e9ecd4b7f93c72cffc00dcf22' is denied.

### 346. GoalWorktrees_git_metadata_access_marks_low_integrity_worker_non_committing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ea665fbaa58a44949346d41a1ea19b90' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ea665fbaa58a44949346d41a1ea19b90' is denied.

### 347. GoalWorktrees_git_metadata_access_marks_low_integrity_worker_non_committing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2862f288f7dd490ea456793dc942a915' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\2862f288f7dd490ea456793dc942a915' is denied.

### 348. GoalWorktrees_git_metadata_access_resolves_linked_index_lock_path

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3427a35c704a4ac295d73e17a30aab25' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3427a35c704a4ac295d73e17a30aab25' is denied.

### 349. GoalWorktrees_git_metadata_access_resolves_linked_index_lock_path

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1f3a87411c8e4f6d87ba65ab96f1b450' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\1f3a87411c8e4f6d87ba65ab96f1b450' is denied.

### 350. GoalWorktrees_merge_returns_null_without_goal_branch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f5f3d2bfcda440df8e30a4013e028829' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f5f3d2bfcda440df8e30a4013e028829' is denied.

### 351. GoalWorktrees_merge_returns_null_without_goal_branch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d069340eec764f099c9cb5c0e4e3907e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d069340eec764f099c9cb5c0e4e3907e' is denied.

### 352. GoalWorktrees_owned_ephemeral_sweep_persists_cleanup_needed_backoff

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e32886f4c315424199ffa6747ac3e769' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e32886f4c315424199ffa6747ac3e769' is denied.

### 353. GoalWorktrees_owned_ephemeral_sweep_persists_cleanup_needed_backoff

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\746ce1980d07415db92f62b946e9c36f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\746ce1980d07415db92f62b946e9c36f' is denied.

### 354. GoalWorktrees_owned_ephemeral_sweep_removes_goal_context_temp_and_scratch_only

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\61cb20fa2d1b4ce798f96c2c7a070908' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\61cb20fa2d1b4ce798f96c2c7a070908' is denied.

### 355. GoalWorktrees_owned_ephemeral_sweep_removes_goal_context_temp_and_scratch_only

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c6355e4fb3bb4c219bf01bcb0c52aea3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c6355e4fb3bb4c219bf01bcb0c52aea3' is denied.

### 356. GoalWorktrees_rebases_stale_branch_onto_main_when_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\484077db74654aa0a38b8075c7b4f0b5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\484077db74654aa0a38b8075c7b4f0b5' is denied.

### 357. GoalWorktrees_rebases_stale_branch_onto_main_when_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9d3e012c0a5e47d6bee8e4b0b9eafab0' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\9d3e012c0a5e47d6bee8e4b0b9eafab0' is denied.

### 358. GoalWorktrees_refuses_rebase_when_worktree_dirty

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\291ced4dc7f3437688507ac714bd2f6f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\291ced4dc7f3437688507ac714bd2f6f' is denied.

### 359. GoalWorktrees_refuses_rebase_when_worktree_dirty

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\92256f5bd1a84fb095cafae90d20701f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\92256f5bd1a84fb095cafae90d20701f' is denied.

### 360. GoalWorktrees_remove_aborts_branch_delete_when_branch_unmerged_at_deletion_time

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f30b55f2d6e74158b319d592eef6492b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f30b55f2d6e74158b319d592eef6492b' is denied.

### 361. GoalWorktrees_remove_aborts_branch_delete_when_branch_unmerged_at_deletion_time

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\573a1032300b487ca504ff2039b68466' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\573a1032300b487ca504ff2039b68466' is denied.

### 362. GoalWorktrees_remove_defers_when_acl_reset_is_access_denied

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\64251fb39b204367b04728921685d4bd' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\64251fb39b204367b04728921685d4bd' is denied.

### 363. GoalWorktrees_remove_defers_when_acl_reset_is_access_denied

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ce8d2d90c36a46e58b506bf0e51eb3a3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ce8d2d90c36a46e58b506bf0e51eb3a3' is denied.

### 364. GoalWorktrees_remove_defers_when_build_server_cleanup_exhausts_budget

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\bc266a7e30194b47a77048b31807770e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\bc266a7e30194b47a77048b31807770e' is denied.

### 365. GoalWorktrees_remove_defers_when_build_server_cleanup_exhausts_budget

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\4aaeca06d4b64820be0b5fefe8b24329' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\4aaeca06d4b64820be0b5fefe8b24329' is denied.

### 366. GoalWorktrees_remove_deletes_receipt_only_dirty_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e01b629324954cedb96f68624d6e1bb1' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e01b629324954cedb96f68624d6e1bb1' is denied.

### 367. GoalWorktrees_remove_deletes_receipt_only_dirty_worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7402b9bf8fc44879b1285fc96303b14c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7402b9bf8fc44879b1285fc96303b14c' is denied.

### 368. GoalWorktrees_remove_failure_records_cleanup_needed_without_throwing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1f5564e9c2a74ce893bec113f23e269b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1f5564e9c2a74ce893bec113f23e269b' is denied.

### 369. GoalWorktrees_remove_failure_records_cleanup_needed_without_throwing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ba6508cb30254485ab5496b857ac8ebc' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ba6508cb30254485ab5496b857ac8ebc' is denied.

### 370. GoalWorktrees_remove_honors_cleanup_needed_backoff_when_no_lock_holder_was_recorded

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\60112fa2a02748dbb5f157693bc6b568' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\60112fa2a02748dbb5f157693bc6b568' is denied.

### 371. GoalWorktrees_remove_honors_cleanup_needed_backoff_when_no_lock_holder_was_recorded

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8c9a8bda39d84aeb8222db9614507ecb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8c9a8bda39d84aeb8222db9614507ecb' is denied.

### 372. GoalWorktrees_remove_invokes_build_server_shutdown_before_directory_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5bdbcb58d5b146cdb8a19d1af459fb30' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5bdbcb58d5b146cdb8a19d1af459fb30' is denied.

### 373. GoalWorktrees_remove_invokes_build_server_shutdown_before_directory_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\75535b3f00cf487b8b2e8dbe5f5b3860' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\75535b3f00cf487b8b2e8dbe5f5b3860' is denied.

### 374. GoalWorktrees_remove_is_idempotent_when_already_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\350edbb11ee140919f216911f5e4628b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\350edbb11ee140919f216911f5e4628b' is denied.

### 375. GoalWorktrees_remove_is_idempotent_when_already_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6e4a22a244854993b67c03a4db53a18e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6e4a22a244854993b67c03a4db53a18e' is denied.

### 376. GoalWorktrees_remove_kills_unprotected_recorded_worker_process

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\dcb0578e417d409886d79809339d116c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\dcb0578e417d409886d79809339d116c' is denied.

### 377. GoalWorktrees_remove_kills_unprotected_recorded_worker_process

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\164e06d21ff346c580dbbd3fe5862b88' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\164e06d21ff346c580dbbd3fe5862b88' is denied.

### 378. GoalWorktrees_remove_partial_result_identifies_branch_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\227fc840bb24460aa68cb5eb3a3df65e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\227fc840bb24460aa68cb5eb3a3df65e' is denied.

### 379. GoalWorktrees_remove_partial_result_identifies_branch_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\fcdf61e643a34646b3845be91b8c5493' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\fcdf61e643a34646b3845be91b8c5493' is denied.

### 380. GoalWorktrees_remove_persists_cleanup_needed_when_goal_artifacts_delete_fails

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8522561c2b4d45ccb3002c094f71c036' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8522561c2b4d45ccb3002c094f71c036' is denied.

### 381. GoalWorktrees_remove_persists_cleanup_needed_when_goal_artifacts_delete_fails

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\75a6434fde174daba6e66516cd0f6d11' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\75a6434fde174daba6e66516cd0f6d11' is denied.

### 382. GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8f39cc9bb4c64f5994d09af201f479b2' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8f39cc9bb4c64f5994d09af201f479b2' is denied.

### 383. GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\69b58d4375c84bfdbafc4272a5dcbeac' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\69b58d4375c84bfdbafc4272a5dcbeac' is denied.

### 384. GoalWorktrees_remove_rechecks_branch_ancestry_before_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b534c46760054ddd87f2969d224c1f07' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b534c46760054ddd87f2969d224c1f07' is denied.

### 385. GoalWorktrees_remove_rechecks_branch_ancestry_before_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3d2a8d9585cb43daaec1cc6d3f87ab27' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\3d2a8d9585cb43daaec1cc6d3f87ab27' is denied.

### 386. GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b8cc5b7338ed491494469cdbb36b108c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\b8cc5b7338ed491494469cdbb36b108c' is denied.

### 387. GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\88c54e95050a49e58fad0dc5f6833bfd' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\88c54e95050a49e58fad0dc5f6833bfd' is denied.

### 388. GoalWorktrees_remove_reports_owned_ephemeral_cleanup_leftover

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\10c5d22fc7004396961423f4704b2f34' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\10c5d22fc7004396961423f4704b2f34' is denied.

### 389. GoalWorktrees_remove_reports_owned_ephemeral_cleanup_leftover

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\99a58993e67d472d81ff8cb98a44bcdd' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\99a58993e67d472d81ff8cb98a44bcdd' is denied.

### 390. GoalWorktrees_remove_reports_unregistered_leftover_directory_as_incomplete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c9fa33aea919496fa284afe16a90c08e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c9fa33aea919496fa284afe16a90c08e' is denied.

### 391. GoalWorktrees_remove_reports_unregistered_leftover_directory_as_incomplete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\adbe5d41b3eb4166bae0ed5732957aa4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\adbe5d41b3eb4166bae0ed5732957aa4' is denied.

### 392. GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5360787df2e5483a91c3c3ea27dcb5a4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\5360787df2e5483a91c3c3ea27dcb5a4' is denied.

### 393. GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7d987b4497db40dc921a0e472cd6d188' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7d987b4497db40dc921a0e472cd6d188' is denied.

### 394. GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c82a8da6c4504c41b20a26e6100264c7' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\c82a8da6c4504c41b20a26e6100264c7' is denied.

### 395. GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c5f0985e96184e07885fa25aeaa40db7' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c5f0985e96184e07885fa25aeaa40db7' is denied.

### 396. GoalWorktrees_remove_retries_and_succeeds_when_transient_lock_releases

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\744cad95166348739c39f3bd1232aab5' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\744cad95166348739c39f3bd1232aab5' is denied.

### 397. GoalWorktrees_remove_retries_and_succeeds_when_transient_lock_releases

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\bf836c56a59f4c91a0a65c350a852c6b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\bf836c56a59f4c91a0a65c350a852c6b' is denied.

### 398. GoalWorktrees_remove_retries_cleanup_needed_immediately_after_lock_release

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8d5762d0de094510977bbb2c722961f8' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\8d5762d0de094510977bbb2c722961f8' is denied.

### 399. GoalWorktrees_remove_retries_cleanup_needed_immediately_after_lock_release

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\58c2cc0cba2e4ef59c80f87a903c9b06' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\58c2cc0cba2e4ef59c80f87a903c9b06' is denied.

### 400. GoalWorktrees_remove_skips_protected_recorded_worker_process

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\765d8f6e55904de78e8b58f3b8da39a2' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\765d8f6e55904de78e8b58f3b8da39a2' is denied.

### 401. GoalWorktrees_remove_skips_protected_recorded_worker_process

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c615e041b4334a7f89ef110a885e50e9' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c615e041b4334a7f89ef110a885e50e9' is denied.

### 402. GoalWorktrees_remove_threads_remaining_budget_into_nested_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\185a2cf13fa344bb85c4a4be2d489afe' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\185a2cf13fa344bb85c4a4be2d489afe' is denied.

### 403. GoalWorktrees_remove_threads_remaining_budget_into_nested_cleanup

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e985c65b9f2946cf83b390aed3885f59' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\e985c65b9f2946cf83b390aed3885f59' is denied.

### 404. GoalWorktrees_reports_conflict_files_and_aborts_rebase

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\df1f288fcb4a4631822b81587af3e2ea' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\df1f288fcb4a4631822b81587af3e2ea' is denied.

### 405. GoalWorktrees_reports_conflict_files_and_aborts_rebase

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6a4ff7e4fd954816b889eb725df3b5ca' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6a4ff7e4fd954816b889eb725df3b5ca' is denied.

### 406. GoalWorktrees_resolve_all_matches_per_goal_try_resolve

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e89838358959402fb2e56fdd7adfa4b2' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e89838358959402fb2e56fdd7adfa4b2' is denied.

### 407. GoalWorktrees_resolve_all_matches_per_goal_try_resolve

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\410c572a5513496ca0ca232375b43b46' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\410c572a5513496ca0ca232375b43b46' is denied.

### 408. GoalWorktrees_suggests_manual_merge_when_branches_diverge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\87589b5df3424bde8e914cac9b2cf196' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\87589b5df3424bde8e914cac9b2cf196' is denied.

### 409. GoalWorktrees_suggests_manual_merge_when_branches_diverge

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5f75b3b9870e4b05aac121dedbc201c3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\5f75b3b9870e4b05aac121dedbc201c3' is denied.

### 410. GoalWorktrees_sweep_deletes_orphaned_worktree_directory

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ca4fd38ac14d4b728535c3e18c5cfb30' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ca4fd38ac14d4b728535c3e18c5cfb30' is denied.

### 411. GoalWorktrees_sweep_deletes_orphaned_worktree_directory

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a34126db9c204f228d053658b0a293ee' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a34126db9c204f228d053658b0a293ee' is denied.

### 412. GoalWorktrees_sweep_escalates_consecutive_failures_and_auto_resolves_after_slow_retry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e406d0905ef049a6b5e82d966d83057f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\e406d0905ef049a6b5e82d966d83057f' is denied.

### 413. GoalWorktrees_sweep_escalates_consecutive_failures_and_auto_resolves_after_slow_retry

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6c341105f18843588dd307dab105bf39' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\6c341105f18843588dd307dab105bf39' is denied.

### 414. GoalWorktrees_sweep_quietly_journals_in_budget_backoff_skip

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f4013589db8941c1804d0c030d2cb019' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f4013589db8941c1804d0c030d2cb019' is denied.

### 415. GoalWorktrees_sweep_quietly_journals_in_budget_backoff_skip

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8597b57900ac4f36a5615b19b8cb9f4c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\8597b57900ac4f36a5615b19b8cb9f4c' is denied.

### 416. GoalWorktrees_sweep_records_backoff_for_transient_orphan_delete_failure

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\191c73c4d9da4b67ad4b87dcd5b1a330' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\191c73c4d9da4b67ad4b87dcd5b1a330' is denied.

### 417. GoalWorktrees_sweep_records_backoff_for_transient_orphan_delete_failure

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ad54e59e33e540b6b5f503903971036b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ad54e59e33e540b6b5f503903971036b' is denied.

### 418. GoalWorktrees_sweep_records_backoff_when_second_delete_fails_after_acl_reset

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\092528c62ab04273b1936ad398e71cb2' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\092528c62ab04273b1936ad398e71cb2' is denied.

### 419. GoalWorktrees_sweep_records_backoff_when_second_delete_fails_after_acl_reset

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b125fef4cbb64f48af5fb4d940dd0078' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\b125fef4cbb64f48af5fb4d940dd0078' is denied.

### 420. GoalWorktrees_sweep_records_timeout_backoff_and_skips_repeat_acl_reset

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1007dccc6e8a4bd28a05f80ac420ad4d' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\1007dccc6e8a4bd28a05f80ac420ad4d' is denied.

### 421. GoalWorktrees_sweep_records_timeout_backoff_and_skips_repeat_acl_reset

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\06c45f71baad4c95b33af7d021bbc8c4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\06c45f71baad4c95b33af7d021bbc8c4' is denied.

### 422. GoalWorktrees_sweep_resets_acl_only_after_access_denied_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0d648a2e03934fe1ba5cf87bbe3a4fbb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\0d648a2e03934fe1ba5cf87bbe3a4fbb' is denied.

### 423. GoalWorktrees_sweep_resets_acl_only_after_access_denied_delete

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c92b3b997dc04c74b3d5cc631f986a35' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\c92b3b997dc04c74b3d5cc631f986a35' is denied.

### 424. GoalWorktrees_terminal_remove_deletes_long_path_and_prunes_registration

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-long-wt\67774ea65a7b49239cbe1dbb399cbbda\repo' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-long-wt\67774ea65a7b49239cbe1dbb399cbbda\repo' is denied.

### 425. GoalWorktrees_terminal_remove_deletes_long_path_and_prunes_registration

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-long-wt\d58ddd0a845d47d590b9fc34c9a18356\repo' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-long-wt\d58ddd0a845d47d590b9fc34c9a18356\repo' is denied.

### 426. GoalWorktrees_terminal_remove_reports_unsafe_prefix_collision_without_throwing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\51aafb56d18148b0bc39ef0e280e17c4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\51aafb56d18148b0bc39ef0e280e17c4' is denied.

### 427. GoalWorktrees_terminal_remove_reports_unsafe_prefix_collision_without_throwing

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d29ced9b7e244441944289cfc53b46ad' is denied.`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\d29ced9b7e244441944289cfc53b46ad' is denied.

### 428. GoalWorktreeTestsRemoveCleanup.Keyed_goal_replay_keeps_one_workspace_and_clean_repository

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 25; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6d7b3450; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6d7b3450-0-20260815044611717 | 2026-08-15 | goal 6d7b3450 | Assert.True() Failure

### 429. GoalWorktreeTestsRemoveCleanup.RemoveSupersededTerminal_ChangedTip_KeepsBranch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2c8c0b61218d4fb7a929e38a44a339ed' is denied.`. Pattern: **single-goal-unresolved**. Passes: 49; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\2c8c0b61218d4fb7a929e38a44a339ed' is denied.

### 430. GoalWorktreeTestsRemoveCleanup.RemoveSupersededTerminal_ChangedTip_KeepsBranch

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0ff4b021fb5249e48faf337a84cf303a' is denied.`. Pattern: **single-goal-unresolved**. Passes: 49; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\0ff4b021fb5249e48faf337a84cf303a' is denied.

### 431. Harness_docs_counterpart_contracts_stay_in_sync

Signature: `System.InvalidOperationException : AGENTS.md shared-anchor list changed. Expected [output-discipline, retry-and-loop-control, repository-rules, architecture-and-design-discipline, specification-discipline, diagnosis-discipline, dashboard-dogfood-boundary, operating-the-goal-loop, safety, evidence], got [output-discipline, retry-and-loop-control, repository-rules, architecture-and-design-discipline, specification-discipline, diagnosis-discipline, dashboard-dogfood-boundary, operating-the-goal-loop, safety, evidence, dispositive-decision-discipline].`. Pattern: **single-goal-unresolved**. Passes: 121; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c4d02669; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c4d02669-0-20260803234824443 | 2026-08-03 | goal c4d02669 | System.InvalidOperationException : AGENTS.md shared-anchor list changed. Expected [output-discipline, retry-and-loop-control, repository-rules, architecture-and-design-discipline, specification-discipline, diagnosis-discipline, dashboard-dogfood-boundary, operating-the-goal-loop, safety, evidence], got [output-discipline, retry-and-loop-control, repository-rules, architecture-and-design-discipline, specification-discipline, diagnosis-discipline, dashboard-dogfood-boundary, operating-the-goal-loop, safety, evidence, dispositive-decision-discipline].

### 432. Harness_docs_drift_check_rejects_unpaired_contract_or_anchor_edits

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 121; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c4d02669; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c4d02669-0-20260803234824443 | 2026-08-03 | goal c4d02669 | Assert.True() Failure

### 433. InquiryDispatcher_receipt_is_post_hoc_and_does_not_mutate_task_state

Signature: `System.InvalidOperationException : git commit -m init failed:`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=53920957; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 53920957-0-20260722032215268 | 2026-07-22 | goal 53920957 | System.InvalidOperationException : git commit -m init failed:

### 434. InquiryDispatcher_seals_codex_resume_session_after_nonforked_inquiry

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6191dc01; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6191dc01-0-20260723124202737 | 2026-07-23 | goal 6191dc01 | Assert.Equal() Failure: Values differ

### 435. InvokeGit_through_repo_script_preserves_hyphenated_git_arguments

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fd1f533907044c32b72e7ac0f9dddf11' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\fd1f533907044c32b72e7ac0f9dddf11' is denied.

### 436. InvokeGit_through_repo_script_preserves_hyphenated_git_arguments

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48bad7134ec4474ea6d5974b34487992' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\48bad7134ec4474ea6d5974b34487992' is denied.

### 437. InvokeIsolatedDotnet_from_goal_worktree_leaves_repository_status_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\857d38d361fc41b993b7da298581af7e' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\857d38d361fc41b993b7da298581af7e' is denied.

### 438. InvokeIsolatedDotnet_from_goal_worktree_leaves_repository_status_clean

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\47172e95081843499840a27c00609e84' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\47172e95081843499840a27c00609e84' is denied.

### 439. InvokeRepoScript_empty_argument_splat_forwards_zero_arguments

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\a24c1b56cb3442b98a17483ed9c6bfe3' is denied.`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\a24c1b56cb3442b98a17483ed9c6bfe3' is denied.

### 440. InvokeRepoScript_empty_argument_splat_forwards_zero_arguments

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\79a3882ea75a485eb65317e8b9d1d9c2' is denied.`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\79a3882ea75a485eb65317e8b9d1d9c2' is denied.

### 441. InvokeRepoScript_no_trailing_arguments_forwards_zero_arguments

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\6d01c740dd8a496b87dcc8d656d4d1c9' is denied.`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\6d01c740dd8a496b87dcc8d656d4d1c9' is denied.

### 442. InvokeRepoScript_no_trailing_arguments_forwards_zero_arguments

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\ecb352b6c3714626856e8ecef3c4b0bb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\ecb352b6c3714626856e8ecef3c4b0bb' is denied.

### 443. InvokeRepoScript_non_empty_arguments_are_forwarded_in_order

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\781ceeb378c241bdb0b68411ef229f3f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\invoke-repo-script-tests\781ceeb378c241bdb0b68411ef229f3f' is denied.

### 444. InvokeRepoScript_non_empty_arguments_are_forwarded_in_order

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\331715e703404bd58ed761bac7a8bc05' is denied.`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\invoke-repo-script-tests\331715e703404bd58ed761bac7a8bc05' is denied.

### 445. InvokeRepoScript_orchestrator_sqlite_tool_list_goals_smoke

Signature: `exit=1; stdout=C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.Core\obj' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\scripts\OrchestratorSqliteTools\OrchestratorSqliteTools.csproj]`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | exit=1; stdout=C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\src\Mcg.AgentOrchestrator.Core\obj' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\scripts\OrchestratorSqliteTools\OrchestratorSqliteTools.csproj]

### 446. InvokeRepoScript_orchestrator_sqlite_tool_list_goals_smoke

Signature: `exit=1; stdout=C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.Core\obj\c0df1963-907a-44e6-9f89-32476bfdf12c.tmp' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\scripts\OrchestratorSqliteTools\OrchestratorSqliteTools.csproj]`. Pattern: **single-goal-unresolved**. Passes: 109; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | exit=1; stdout=C:\Program Files\dotnet\sdk\10.0.103\NuGet.targets(196,5): error : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\src\Mcg.AgentOrchestrator.Core\obj\c0df1963-907a-44e6-9f89-32476bfdf12c.tmp' is denied. [C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\scripts\OrchestratorSqliteTools\OrchestratorSqliteTools.csproj]

### 447. IsSandboxCommitBlockedFailure_false_for_low_integrity_1312_logon_session_evidence

Signature: `Assert.False() Failure`. Pattern: **single-goal-unresolved**. Passes: 56; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=60ec9bc9; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 60ec9bc9-0-20260809103339251 | 2026-08-09 | goal 60ec9bc9 | Assert.False() Failure

### 448. IsSandboxCommitBlockedFailure_false_for_low_integrity_git_1312_evidence

Signature: `Assert.False() Failure`. Pattern: **single-goal-unresolved**. Passes: 56; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=60ec9bc9; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 60ec9bc9-0-20260809103339251 | 2026-08-09 | goal 60ec9bc9 | Assert.False() Failure

### 449. IsSandboxCommitBlockedFailure_reads_log_paths_for_evidence_outside_retained_excerpt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-sandbox-blocked-8cc9514ede7f4ae88de23e2552adbdf9' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-sandbox-blocked-8cc9514ede7f4ae88de23e2552adbdf9' is denied.

### 450. IsSandboxCommitBlockedFailure_reads_log_paths_for_evidence_outside_retained_excerpt

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-sandbox-blocked-e5cc41ecda25461ab479de5e1132d3e6' is denied.`. Pattern: **single-goal-unresolved**. Passes: 111; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-sandbox-blocked-e5cc41ecda25461ab479de5e1132d3e6' is denied.

### 451. IsTransientEmptyOutputDispatchFlake_false_for_exit0_with_populated_outlog_and_empty_inmemory_stdout

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-emptyflake-661dce1378cc43268733b06cfdfae718.out.log' is denied.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=298da244; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 298da244-0-20260730070447253 | 2026-07-30 | goal 298da244 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-emptyflake-661dce1378cc43268733b06cfdfae718.out.log' is denied.

### 452. IsTransientEmptyOutputDispatchFlake_false_for_exit0_with_populated_outlog_and_empty_inmemory_stdout

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-emptyflake-a1c412dd24544d1ea04453c0c44d7bde.out.log' is denied.`. Pattern: **single-goal-unresolved**. Passes: 120; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=21afd072; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 21afd072-0-20260730070458524 | 2026-07-30 | goal 21afd072 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\AppData\Local\Temp\mcg-emptyflake-a1c412dd24544d1ea04453c0c44d7bde.out.log' is denied.

### 453. Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **single-goal-unresolved**. Passes: 58; goal failures: 1; operator failures: 0; pre-review failures: 7.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fe37d616; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | Assert.Equal() Failure: Strings differ

### 454. Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\post-landing-canary-dirty-9d96111b47a54fb882cf5fc8fb4d3c02.sentinel' is denied.`. Pattern: **single-goal-unresolved**. Passes: 58; goal failures: 1; operator failures: 0; pre-review failures: 7.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\post-landing-canary-dirty-9d96111b47a54fb882cf5fc8fb4d3c02.sentinel' is denied.

### 455. Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\post-landing-canary-dirty-7583e1709cbc4f9c91be71e993510744.sentinel' is denied.`. Pattern: **single-goal-unresolved**. Passes: 58; goal failures: 1; operator failures: 0; pre-review failures: 7.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\post-landing-canary-dirty-7583e1709cbc4f9c91be71e993510744.sentinel' is denied.

### 456. Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main

Signature: `Assert.False() Failure`. Pattern: **single-goal-unresolved**. Passes: 15; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fb13475c; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fb13475c-0-20260803084824509 | 2026-08-03 | goal fb13475c | Assert.False() Failure

### 457. Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main

Signature: `BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-hvp\AppData\LocalLow\mcg-dotnet-isolated\runs\33816-acceptance-focused-changed-core-tests-build-04972e2eb6a74c429d4371dddf0935fe\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown.`. Pattern: **single-goal-unresolved**. Passes: 15; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=1332b2ea; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 1332b2ea-0-20260804013805047 | 2026-08-04 | goal 1332b2ea | BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-hvp\AppData\LocalLow\mcg-dotnet-isolated\runs\33816-acceptance-focused-changed-core-tests-build-04972e2eb6a74c429d4371dddf0935fe\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown.

### 458. Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main

Signature: `BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated\runs\18080-acceptance-focused-changed-core-tests-build-f54a5b58f022489fa6afdcfa4c028ee5\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown.`. Pattern: **single-goal-unresolved**. Passes: 15; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fcf8669a; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fcf8669a-0-20260802042053873 | 2026-08-02 | goal fcf8669a | BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated\runs\18080-acceptance-focused-changed-core-tests-build-f54a5b58f022489fa6afdcfa4c028ee5\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown.

### 459. Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main

Signature: `BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated\runs\29128-acceptance-focused-changed-core-tests-build-c7e1c993a7a84d2596392b69e00ab9e4\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown.`. Pattern: **single-goal-unresolved**. Passes: 15; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d4897de1; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d4897de1-0-20260801213910727 | 2026-08-01 | goal d4897de1 | BuildLockBlockedException: Build artifact lock blocked progress at C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\mcg-dotnet-isolated\runs\29128-acceptance-focused-changed-core-tests-build-c7e1c993a7a84d2596392b69e00ab9e4\artifacts\obj\Mcg.AgentOrchestrator.Core.Tests\debug\apphost.exe; holder=unknown.

### 460. Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main

Signature: `System.InvalidOperationException : Post-landing canary expected main HEAD 0d05f061931328df9712275f788129201161aa47, but resolved ''.`. Pattern: **single-goal-unresolved**. Passes: 15; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=8e8afe96; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 8e8afe96-0-20260803205834519 | 2026-08-03 | goal 8e8afe96 | System.InvalidOperationException : Post-landing canary expected main HEAD 0d05f061931328df9712275f788129201161aa47, but resolved ''.

### 461. Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main

Signature: `System.InvalidOperationException : Post-landing canary repository precondition failed; main was dirty before the run. Baseline SHA: 335e6c14ddafdddd933a3fe8eaac8e455a525e0a. Current SHA: 335e6c14ddafdddd933a3fe8eaac8e455a525e0a. Current is at or ahead of baseline: True. Baseline dirty paths: ?? Microsoft/Windows/PowerShell/ModuleAnalysisCache. Current dirty paths: ?? Microsoft/Windows/PowerShell/ModuleAnalysisCache.`. Pattern: **single-goal-unresolved**. Passes: 15; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6b2085ed; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6b2085ed-0-20260806215204652 | 2026-08-06 | goal 6b2085ed | System.InvalidOperationException : Post-landing canary repository precondition failed; main was dirty before the run. Baseline SHA: 335e6c14ddafdddd933a3fe8eaac8e455a525e0a. Current SHA: 335e6c14ddafdddd933a3fe8eaac8e455a525e0a. Current is at or ahead of baseline: True. Baseline dirty paths: ?? Microsoft/Windows/PowerShell/ModuleAnalysisCache. Current dirty paths: ?? Microsoft/Windows/PowerShell/ModuleAnalysisCache.

### 462. LaneTimingMeasurementScriptTests.MissingManifestLane_FailsAndNamesLane

Signature: `System.ComponentModel.Win32Exception : An error occurred trying to start process 'pwsh' with working directory 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8818\'. Access is denied.`. Pattern: **single-goal-unresolved**. Passes: 21; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=8ab0a29d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 8ab0a29d-0-20260817233257894 | 2026-08-17 | goal 8ab0a29d | System.ComponentModel.Win32Exception : An error occurred trying to start process 'pwsh' with working directory 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\p8818\'. Access is denied.

### 463. LauncherScriptTests.AutoResumeInstallerValidatesBeforeTaskMutationAndRestoresLastKnownGood

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 39; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=40b285d2; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 40b285d2-0-20260813002934691 | 2026-08-13 | goal 40b285d2 | Assert.Equal() Failure: Values differ

### 464. LauncherScriptTests.ConductLoop_ConfiguredRoot_UsesChildStopPath

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 0; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5c7d66b5; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5c7d66b5-0-20260810020723924 | 2026-08-10 | goal 5c7d66b5 | Assert.Contains() Failure: Sub-string not found

### 465. LauncherScriptTests.ConductLoop_StopFilePresent_RefusesBeforeSideEffects

Signature: `Assert.Single() Failure: The collection contained 2 items`. Pattern: **single-goal-unresolved**. Passes: 0; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5c7d66b5; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5c7d66b5-0-20260810020723924 | 2026-08-10 | goal 5c7d66b5 | Assert.Single() Failure: The collection contained 2 items

### 466. LauncherScriptTests.ConductLoop_StopPathDirectory_RefusesBeforeSideEffects

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 0; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5c7d66b5; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5c7d66b5-0-20260810020723924 | 2026-08-10 | goal 5c7d66b5 | Assert.Contains() Failure: Sub-string not found

### 467. Leniency_MarkdownHeadingOpener_passes

Signature: `System.InvalidOperationException : git commit -m Add src/Feature.cs failed: fatal: write failure on 'stdout': Bad file descriptor`. Pattern: **single-goal-unresolved**. Passes: 107; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=ccd8933a; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- ccd8933a-1-20260718233103771 | 2026-07-18 | goal ccd8933a | System.InvalidOperationException : git commit -m Add src/Feature.cs failed: fatal: write failure on 'stdout': Bad file descriptor

### 468. MTP_no_build_missing_apphost_reports_path_and_build_command

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Assert.Equal() Failure: Values differ

### 469. MTP_partition_build_failure_is_loud_and_never_launches_stale_apphost

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Assert.Equal() Failure: Values differ

### 470. MTP_partition_build_stderr_does_not_override_a_successful_exit_code

Signature: `Infrastructure partition: GoalWorktree`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Infrastructure partition: GoalWorktree

### 471. MTP_partition_runner_streams_output_and_passes_manifest_filter_arguments

Signature: `Infrastructure partition: GoalWorktree`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Infrastructure partition: GoalWorktree

### 472. MTP_runner_preserves_child_and_caller_temp_roots_after_clean_run

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Assert.Contains() Failure: Sub-string not found

### 473. MTP_summary_partition_defaults_to_the_infrastructure_test_project

Signature: `RESULTS DIRECTORY FAILURE - Results directory 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\script-tests\81e9bf6098a540d29cd48179b958ed27' is not under the Low-integrity-writable root 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests'. Use -ResultsRoot beneath that root; an ordinary Medium-integrity directory is not writable by the MTP apphost.`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | RESULTS DIRECTORY FAILURE - Results directory 'C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\script-tests\81e9bf6098a540d29cd48179b958ed27' is not under the Low-integrity-writable root 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests'. Use -ResultsRoot beneath that root; an ordinary Medium-integrity directory is not writable by the MTP apphost.

### 474. MtpTestRunnerScriptTests.MtpCmdRunnerPreservesMetacharactersAndPercentExpansionsInPathsAsArgumentData

Signature: `Infrastructure partition: GoalWorktree`. Pattern: **single-goal-unresolved**. Passes: 65; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=40b285d2; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 40b285d2-0-20260813002934691 | 2026-08-13 | goal 40b285d2 | Infrastructure partition: GoalWorktree

### 475. MtpTestRunnerScriptTests.MtpPartitionRunnerSelectsDistinctFailureDiagnosis(behavior: "failed", expectedExitCode: 3, expectedDiagnosis: "TEST FAILURES", expectedDetail: "TRX:")

Signature: `Infrastructure partition: GoalWorktree`. Pattern: **single-goal-unresolved**. Passes: 13; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Infrastructure partition: GoalWorktree

### 476. MtpTestRunnerScriptTests.MtpPartitionRunnerSelectsDistinctFailureDiagnosis(behavior: "no-trx", expectedExitCode: 7, expectedDiagnosis: "RUNNER/TOOLING FAILURE", expectedDetail: "exited 7 without producing TRX")

Signature: `Infrastructure partition: GoalWorktree`. Pattern: **single-goal-unresolved**. Passes: 13; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Infrastructure partition: GoalWorktree

### 477. MtpTestRunnerScriptTests.MtpPartitionRunnerSelectsDistinctFailureDiagnosis(behavior: "zero", expectedExitCode: 27, expectedDiagnosis: "ZERO TESTS", expectedDetail: "filter matched no tests")

Signature: `Infrastructure partition: GoalWorktree`. Pattern: **single-goal-unresolved**. Passes: 13; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=5146fab4; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 5146fab4-0-20260802051458490 | 2026-08-02 | goal 5146fab4 | Infrastructure partition: GoalWorktree

### 478. Native_MTP_dotnet_test_runs_every_repository_test_project_in_one_step(project: "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.Ag"···, testClass: "DashboardValidationHarnessTests")

Signature: `System.TimeoutException : dotnet did not exit within 300 seconds.`. Pattern: **load-dependent**. Passes: 22; goal failures: 1; operator failures: 2; pre-review failures: 0.
- ae9dccd4-0-20260820200404620 | 2026-08-20 | goal ae9dccd4 | System.TimeoutException : dotnet did not exit within 300 seconds.

### 479. OrchestratorSqliteTool_list_goals_from_linked_worktree_reads_primary_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\781f5eeff58146858dd1115c159fe844' is denied.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\781f5eeff58146858dd1115c159fe844' is denied.

### 480. OrchestratorSqliteTool_list_goals_from_linked_worktree_reads_primary_state

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\962528fcdea0497e91b43b134d3241f4' is denied.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\962528fcdea0497e91b43b134d3241f4' is denied.

### 481. OrchestratorSqliteTool_list_goals_omits_label_without_source_backlog_title

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f37e9ae573c94d399c0f23a7a4fae4be' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\f37e9ae573c94d399c0f23a7a4fae4be' is denied.

### 482. OrchestratorSqliteTool_list_goals_omits_label_without_source_backlog_title

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7fb806afa8214b2c82f67aae9039df3c' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\7fb806afa8214b2c82f67aae9039df3c' is denied.

### 483. OrchestratorSqliteTool_list_goals_prints_source_backlog_title_label

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\11c33237dc1f4433b59db6b12d672aef' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\11c33237dc1f4433b59db6b12d672aef' is denied.

### 484. OrchestratorSqliteTool_list_goals_prints_source_backlog_title_label

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ec51bfb44f354cbc94d70dc447fd6f7f' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\ec51bfb44f354cbc94d70dc447fd6f7f' is denied.

### 485. OrchestratorSqliteTool_list_goals_reads_repo_state_while_write_lock_is_held

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\eef8e1368cb24cad997ec12fbb6c3264' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\eef8e1368cb24cad997ec12fbb6c3264' is denied.

### 486. OrchestratorSqliteTool_list_goals_reads_repo_state_while_write_lock_is_held

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\cea8250a08a248319f82fa60a51f8777' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\cea8250a08a248319f82fa60a51f8777' is denied.

### 487. PlannerOutputContractTests.PlannerContract_ExactLive485363d4EmDashNewFileMarkers_Pass

Signature: `Planner output contract failed. Stdout plan reason: target citation 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliArgumentNormalizationTests.cs' does not exist and is not marked as a new file; source span [4896..4977).`. Pattern: **single-goal-unresolved**. Passes: 36; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b526bf42; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b526bf42-0-20260813052851719 | 2026-08-13 | goal b526bf42 | Planner output contract failed. Stdout plan reason: target citation 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliArgumentNormalizationTests.cs' does not exist and is not marked as a new file; source span [4896..4977).

### 488. PlannerOutputContractTests.PlannerContract_ExactRejectedE5c18520NewStoreMarker_Passes

Signature: `Assert.Equal() Failure: Strings differ`. Pattern: **single-goal-unresolved**. Passes: 23; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=903cee50; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 903cee50-0-20260808161431774 | 2026-08-08 | goal 903cee50 | Assert.Equal() Failure: Strings differ

### 489. Post-landing canary sink failure cannot skip successful-landing callbacks(breakSqliteStore: False)

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 9; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=809634e8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 809634e8-0-20260803192140165 | 2026-08-03 | goal 809634e8 | Assert.Equal() Failure: Values differ

### 490. Post-landing canary sink failure cannot skip successful-landing callbacks(breakSqliteStore: True)

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 9; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=809634e8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 809634e8-0-20260803192140165 | 2026-08-03 | goal 809634e8 | Assert.Equal() Failure: Values differ

### 491. ProcessTreeGuiSuppression_sets_inherited_error_mode_and_hidden_console_for_descendants

Signature: `System.InvalidOperationException : StandardInputEncoding is only supported when standard input is redirected.`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=30517207; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 30517207-0-20260720042128003 | 2026-07-20 | goal 30517207 | System.InvalidOperationException : StandardInputEncoding is only supported when standard input is redirected.

### 492. ProgramStartupLifecycle_handoff_configures_registry_without_sweeping_incumbent_processes

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 90; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9de9649d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9de9649d-0-20260804184817202 | 2026-08-04 | goal 9de9649d | Assert.True() Failure

### 493. ProgramStartupLifecycle_non_cleanup_command_configures_registry_and_retains_live_worker

Signature: `Assert.Single() Failure: The collection was empty`. Pattern: **single-goal-unresolved**. Passes: 67; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9de9649d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9de9649d-0-20260804184817202 | 2026-08-04 | goal 9de9649d | Assert.Single() Failure: The collection was empty

### 494. ProgressiveReviewGlance_dispatch_uses_read_only_profile_selection_and_bounded_inputs

Signature: `Assert.DoesNotContain() Failure: Sub-string found`. Pattern: **single-goal-unresolved**. Passes: 103; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=20912699; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 20912699-0-20260803170424836 | 2026-08-03 | goal 20912699 | Assert.DoesNotContain() Failure: Sub-string found

### 495. ProgressiveReviewSteering_cancels_confirms_dead_then_warm_resumes_with_guidance

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 7.
Excluded from budget conversion: single-goal-unresolved; failing-goal=63d8f8e0; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 63d8f8e0-0-20260803023926780 | 2026-08-03 | goal 63d8f8e0 | Assert.Equal() Failure: Values differ

### 496. ProgressiveReviewSteering_records_receipt_and_attention_when_steer_start_throws

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=16d26adc; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 16d26adc-0-20260723145012901 | 2026-07-23 | goal 16d26adc | Assert.Contains() Failure: Sub-string not found

### 497. ProgressiveReviewSteering_requeues_and_preserves_goal_worktree_when_restart_preparation_throws

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 72; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fb13475c; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fb13475c-0-20260803084824509 | 2026-08-03 | goal fb13475c | Assert.True() Failure

### 498. PrototypeWorkspaceSeeder_reuses_persistent_workspace_without_overwriting_state

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=24d04cc8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 24d04cc8-0-20260728130105886 | 2026-07-28 | goal 24d04cc8 | Assert.Contains() Failure: Sub-string not found

### 499. Reconcile_sweep_retries_acceptance_after_untracked_rebase_blocker_is_removed

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3b3fd1a661ab4d4f9f50f847631231cc' is denied.`. Pattern: **single-goal-unresolved**. Passes: 60; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\3b3fd1a661ab4d4f9f50f847631231cc' is denied.

### 500. Reconcile_sweep_retries_acceptance_after_untracked_rebase_blocker_is_removed

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\649fe4e7960c4b399a73fd6aee9229ed' is denied.`. Pattern: **single-goal-unresolved**. Passes: 60; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\649fe4e7960c4b399a73fd6aee9229ed' is denied.

### 501. RecordDispatchExecutionResult_counts_sandbox_preflight_failure_on_shared_empty_output_retry_budget

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 113; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=12bfb676; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 12bfb676-0-20260723002422843 | 2026-07-23 | goal 12bfb676 | Assert.Equal() Failure: Values differ

### 502. RecordDispatchExecutionResult_does_not_reopen_task_on_quoted_usage_limit_fixture

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 121; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a19b9eb7; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a19b9eb7-0-20260719154522651 | 2026-07-19 | goal a19b9eb7 | Assert.Equal() Failure: Values differ

### 503. RecordDispatchExecutionResult_fails_task_on_typed_provider_rate_limit_without_output_text

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 117; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6a960b3f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6a960b3f-0-20260808170616823 | 2026-08-08 | goal 6a960b3f | Assert.Equal() Failure: Values differ

### 504. RecordDispatchExecutionResult_reopens_task_on_typed_provider_rate_limit_without_output_text

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 3; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a19b9eb7; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a19b9eb7-0-20260719154522651 | 2026-07-19 | goal a19b9eb7 | Assert.Equal() Failure: Values differ

### 505. Regression_89a2c42_deferred_verification_blocker_is_advisory_for_dirty_changed_work

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 107; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=0e4d357c; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 0e4d357c-0-20260810014943678 | 2026-08-10 | goal 0e4d357c | Assert.Equal() Failure: Values differ

### 506. Research_first_pipeline_blocks_Planner_then_injects_full_artifacts_without_survey_or_retry_trimming

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 78; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=ae2dbb9e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- ae2dbb9e-0-20260811192851864 | 2026-08-11 | goal ae2dbb9e | Assert.Contains() Failure: Sub-string not found

### 507. Research_first_pipeline_blocks_Planner_then_injects_full_artifacts_without_survey_or_retry_trimming

Signature: `System.InvalidOperationException : git commit -m Seed failed:`. Pattern: **single-goal-unresolved**. Passes: 78; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=64d6959e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 64d6959e-0-20260803014046074 | 2026-08-03 | goal 64d6959e | System.InvalidOperationException : git commit -m Seed failed:

### 508. RunGoalService_auto_failover_does_not_loop_back_to_failed_agent

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Assert.Equal() Failure: Values differ

### 509. RunGoalService_auto_failover_heartbeat_stall_redelegates

Signature: `stopReason=Subscription preflight failed: profile: alternate; dispatch-lane: alternate; model-selection: full-profile: role has custom subscription worker profile; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.5; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'alternate' is a real`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 3.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | stopReason=Subscription preflight failed: profile: alternate; dispatch-lane: alternate; model-selection: full-profile: role has custom subscription worker profile; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.5; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'alternate' is a real

### 510. RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues

Signature: `stopReason=Assembled worker prompt for task 'a947c77de34f432db740442a5639c96e' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'a947c77de34f432db740442a5639c96e' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fe37d616; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | stopReason=Assembled worker prompt for task 'a947c77de34f432db740442a5639c96e' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'a947c77de34f432db740442a5639c96e' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.

### 511. RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues

Signature: `stopReason=Assembled worker prompt for task 'b01a9ff1cc7a484d9c3c464ccd0f15f6' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'b01a9ff1cc7a484d9c3c464ccd0f15f6' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4fa6af44; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | stopReason=Assembled worker prompt for task 'b01a9ff1cc7a484d9c3c464ccd0f15f6' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'b01a9ff1cc7a484d9c3c464ccd0f15f6' is 1,671 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.

### 512. RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues

Signature: `stopReason=Assembled worker prompt for task 'c7c4fa247b9d46c0bd71a3a24701fc6c' is 1,574 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'c7c4fa247b9d46c0bd71a3a24701fc6c' is 1,574 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6241ed3f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | stopReason=Assembled worker prompt for task 'c7c4fa247b9d46c0bd71a3a24701fc6c' is 1,574 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'c7c4fa247b9d46c0bd71a3a24701fc6c' is 1,574 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.

### 513. RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues

Signature: `stopReason=Assembled worker prompt for task 'ed57665f8bc74493b00c8c1d35057e6b' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'ed57665f8bc74493b00c8c1d35057e6b' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=13b3be0d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | stopReason=Assembled worker prompt for task 'ed57665f8bc74493b00c8c1d35057e6b' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'ed57665f8bc74493b00c8c1d35057e6b' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Error: websocket transport failed with os error 10013 before useful work.

### 514. RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues

Signature: `stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi

### 515. RunGoalService_auto_failover_provider_model_rejection_redelegates

Signature: `stopReason=Assembled worker prompt for task '1f46972a8e8f4adb9b265e4241c053c9' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '1f46972a8e8f4adb9b265e4241c053c9' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fe37d616; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | stopReason=Assembled worker prompt for task '1f46972a8e8f4adb9b265e4241c053c9' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '1f46972a8e8f4adb9b265e4241c053c9' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.

### 516. RunGoalService_auto_failover_provider_model_rejection_redelegates

Signature: `stopReason=Assembled worker prompt for task '215b4911013843508e73807225a0bc2d' is 1,665 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '215b4911013843508e73807225a0bc2d' is 1,665 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=13b3be0d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | stopReason=Assembled worker prompt for task '215b4911013843508e73807225a0bc2d' is 1,665 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '215b4911013843508e73807225a0bc2d' is 1,665 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.

### 517. RunGoalService_auto_failover_provider_model_rejection_redelegates

Signature: `stopReason=Assembled worker prompt for task 'd50cb339cbbc4e738b58df0dfa37af6a' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'd50cb339cbbc4e738b58df0dfa37af6a' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4fa6af44; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | stopReason=Assembled worker prompt for task 'd50cb339cbbc4e738b58df0dfa37af6a' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'd50cb339cbbc4e738b58df0dfa37af6a' is 1,666 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.

### 518. RunGoalService_auto_failover_provider_model_rejection_redelegates

Signature: `stopReason=Assembled worker prompt for task 'd5c5c2a249d54044853d8baa7d2b0034' is 1,570 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'd5c5c2a249d54044853d8baa7d2b0034' is 1,570 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6241ed3f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | stopReason=Assembled worker prompt for task 'd5c5c2a249d54044853d8baa7d2b0034' is 1,570 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'd5c5c2a249d54044853d8baa7d2b0034' is 1,570 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=qwen-planner; dispatch=qwen-code-cli; exit=1; output=Planner output contract failed: missing required evidence. Retry Planner for contract repair.

### 519. RunGoalService_auto_failover_provider_model_rejection_redelegates

Signature: `stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi

### 520. RunGoalService_auto_failover_usage_limit_redelegates_and_continues

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Assert.True() Failure

### 521. RunGoalService_auto_failover_uses_added_same_role_catalog_alternate

Signature: `stopReason=Assembled worker prompt for task '55fbae6c55e448efb42606b798ef3556' is 1,573 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '55fbae6c55e448efb42606b798ef3556' is 1,573 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6241ed3f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | stopReason=Assembled worker prompt for task '55fbae6c55e448efb42606b798ef3556' is 1,573 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task '55fbae6c55e448efb42606b798ef3556' is 1,573 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.

### 522. RunGoalService_auto_failover_uses_added_same_role_catalog_alternate

Signature: `stopReason=Assembled worker prompt for task 'c073088625f040819b34f3455f74da32' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'c073088625f040819b34f3455f74da32' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=fe37d616; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- fe37d616-0-20260820045329684 | 2026-08-20 | goal fe37d616 | stopReason=Assembled worker prompt for task 'c073088625f040819b34f3455f74da32' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'c073088625f040819b34f3455f74da32' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.

### 523. RunGoalService_auto_failover_uses_added_same_role_catalog_alternate

Signature: `stopReason=Assembled worker prompt for task 'ccbf3243d9364bd4b2777b6b18fbbb2f' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'ccbf3243d9364bd4b2777b6b18fbbb2f' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4fa6af44; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | stopReason=Assembled worker prompt for task 'ccbf3243d9364bd4b2777b6b18fbbb2f' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'ccbf3243d9364bd4b2777b6b18fbbb2f' is 1,669 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.

### 524. RunGoalService_auto_failover_uses_added_same_role_catalog_alternate

Signature: `stopReason=Assembled worker prompt for task 'f7658d28e9a84fd18f73b82b4bd5717d' is 1,668 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'f7658d28e9a84fd18f73b82b4bd5717d' is 1,668 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=13b3be0d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 13b3be0d-0-20260820052447871 | 2026-08-20 | goal 13b3be0d | stopReason=Assembled worker prompt for task 'f7658d28e9a84fd18f73b82b4bd5717d' is 1,668 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; stopEvidence=Assembled worker prompt for task 'f7658d28e9a84fd18f73b82b4bd5717d' is 1,668 tokens, exceeding the 1-token input budget for Ollama/qwen3:8b; required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.; status=Running; assigned=ollama-planner-qwen-fallback; dispatch=qwen-code-cli; exit=1; output=ERROR: You've hit your usage limit. Visit settings to purchase more credits.

### 525. RunGoalService_auto_failover_uses_added_same_role_catalog_alternate

Signature: `stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi`. Pattern: **single-goal-unresolved**. Passes: 104; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | stopReason=Subscription preflight failed: profile: qwen-code-cli; dispatch-lane: qwen-code-cli; model-selection: full-profile: light-role profile unavailable (claude-cli not configured); auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: Ollama/qwen3:8b; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profi

### 526. RunGoalService_completes_all_tasks_sequentially_and_stops_with_no_actions

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=ae2dbb9e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- ae2dbb9e-0-20260811192851864 | 2026-08-11 | goal ae2dbb9e | Assert.Equal() Failure: Values differ

### 527. StopRepoProcess_refuses_exact_pid_when_command_guard_mismatches

Signature: `The process cannot access the file because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 101; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=7aec1f72; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 7aec1f72-1-20260721010033130 | 2026-07-21 | goal 7aec1f72 | The process cannot access the file because it is being used by another process.

### 528. StorageRetention_persisted_terminal_goals_are_loaded_outside_conductor_working_set

Signature: `Microsoft.Data.Sqlite.SqliteException : SQLite Error 1: 'no such table: goals'.`. Pattern: **single-goal-unresolved**. Passes: 10; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=7f2a3b85; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 7f2a3b85-0-20260820134147648 | 2026-08-20 | goal 7f2a3b85 | Microsoft.Data.Sqlite.SqliteException : SQLite Error 1: 'no such table: goals'.

### 529. Subscription_dispatch_paths_use_workspace_configured_review_stop_round

Signature: `Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: profile: claude-cli; dispatch-lane: claude-cli; model-selection: light-role: Reviewer uses claude-cli/claude-haiku-4-5; auth: Claude CLI Low-IL auth preflight not required because worker sandbox is disabled; model: Anthropic/claude-haiku-4-5; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'claude-cli' is a real launcher; ok: worker profile 'claude-cli' pins selected model; reasoning-effort: none (base); ok: worker profile 'claude-cli' pins selected reasoning when required; capability: read-only - Reviewer tasks run with read-only/plan sandbox settings.; blocked: missing required local skill(s): orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: not required for read-only role; worktree: clean check not required for read-only role; git metadata: write check not required for read-only role; reviewer-scope: git diff --name-only main...HEAD found 0 changed file(s) from merge-base 0b951451fa32; reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD is clean against current main`. Pattern: **single-goal-unresolved**. Passes: 50; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=b4ae70ef; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- b4ae70ef-0-20260810175300086 | 2026-08-10 | goal b4ae70ef | Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: profile: claude-cli; dispatch-lane: claude-cli; model-selection: light-role: Reviewer uses claude-cli/claude-haiku-4-5; auth: Claude CLI Low-IL auth preflight not required because worker sandbox is disabled; model: Anthropic/claude-haiku-4-5; complexity: Simple; artifact-dependency: research-first durable handoff not required for this persisted task graph position; ok: worker profile 'claude-cli' is a real launcher; ok: worker profile 'claude-cli' pins selected model; reasoning-effort: none (base); ok: worker profile 'claude-cli' pins selected reasoning when required; capability: read-only - Reviewer tasks run with read-only/plan sandbox settings.; blocked: missing required local skill(s): orchestrator-worker-verification at .agents\skills\orchestrator-worker-verification\SKILL.md; add the SKILL.md file(s) or adjust the task so the router no longer selects them; build environment: not required for read-only role; worktree: clean check not required for read-only role; git metadata: write check not required for read-only role; reviewer-scope: git diff --name-only main...HEAD found 0 changed file(s) from merge-base 0b951451fa32; reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD is clean against current main

### 530. SubscriptionDispatch_override_profile_replaces_agent_default

Signature: `Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: ERR_REVIEWER_MERGE_BASE_UNAVAILABLE: profile: alt-profile; dispatch-lane: alt-profile; model-selection: override: explicit dispatch profile/model selection; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.5; complexity: Simple; ok: worker profile 'alt-profile' is a real launcher; ok: worker profile 'alt-profile' pins selected model; reasoning-effort: low (base); ok: worker profile 'alt-profile' pins selected reasoning when required; capability: read-only - Reviewer tasks run with read-only/plan sandbox settings.; skills: local skill catalog not present; selected skills will be listed as missing in context artifacts; build environment: not required for read-only role; worktree: clean check not required for read-only role; git metadata: write check not required for read-only role; blocked: ERR_REVIEWER_MERGE_BASE_UNAVAILABLE: Reviewer changed-file scope unavailable because git merge-base main HEAD could not be computed.; reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD is clean against current main`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=57a4b3be; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 57a4b3be-0-20260719015104202 | 2026-07-19 | goal 57a4b3be | Mcg.AgentOrchestrator.Infrastructure.WorkerSubscriptionPreflightException : Subscription preflight failed: ERR_REVIEWER_MERGE_BASE_UNAVAILABLE: profile: alt-profile; dispatch-lane: alt-profile; model-selection: override: explicit dispatch profile/model selection; auth: Claude CLI Low-IL auth preflight not applicable for this worker profile; model: OpenAI/gpt-5.5; complexity: Simple; ok: worker profile 'alt-profile' is a real launcher; ok: worker profile 'alt-profile' pins selected model; reasoning-effort: low (base); ok: worker profile 'alt-profile' pins selected reasoning when required; capability: read-only - Reviewer tasks run with read-only/plan sandbox settings.; skills: local skill catalog not present; selected skills will be listed as missing in context artifacts; build environment: not required for read-only role; worktree: clean check not required for read-only role; git metadata: write check not required for read-only role; blocked: ERR_REVIEWER_MERGE_BASE_UNAVAILABLE: Reviewer changed-file scope unavailable because git merge-base main HEAD could not be computed.; reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD is clean against current main

### 531. TerminalGoalSweep_terminal_goal_cleans_owned_ephemeral_dirs_without_worker_start

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a9a0f99d58b34fae94fb0251ab87de90' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\a9a0f99d58b34fae94fb0251ab87de90' is denied.

### 532. TerminalGoalSweep_terminal_goal_cleans_owned_ephemeral_dirs_without_worker_start

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a4d9f8fb519b4a659c552241d3c234a7' is denied.`. Pattern: **single-goal-unresolved**. Passes: 102; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\a4d9f8fb519b4a659c552241d3c234a7' is denied.

### 533. Test process starts opt out of visible console windows

Signature: `Test-side process starts must set CreateNoWindow=true or request a hidden/no-new PowerShell window. Offenders: WorkerProcessJobsTests.cs:954`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=7eb3f3da; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 7eb3f3da-0-20260805183355628 | 2026-08-05 | goal 7eb3f3da | Test-side process starts must set CreateNoWindow=true or request a hidden/no-new PowerShell window. Offenders: WorkerProcessJobsTests.cs:954

### 534. Test process starts opt out of visible console windows

Signature: `Test-side ProcessStartInfo usage must set CreateNoWindow=true. Offenders: CodexEgressProxyTests.cs:133, CodexEgressProxyTests.cs:143, CodexEgressProxyTests.cs:156`. Pattern: **single-goal-unresolved**. Passes: 108; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=53920957; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 53920957-0-20260722032215268 | 2026-07-22 | goal 53920957 | Test-side ProcessStartInfo usage must set CreateNoWindow=true. Offenders: CodexEgressProxyTests.cs:133, CodexEgressProxyTests.cs:143, CodexEgressProxyTests.cs:156

### 535. VerificationAndInputWorklistTests.Parked_completion_is_not_treated_as_an_answered_duplicate

Signature: `Assert.DoesNotContain() Failure: Sub-string found`. Pattern: **single-goal-unresolved**. Passes: 88; goal failures: 1; operator failures: 0; pre-review failures: 1.
Excluded from budget conversion: single-goal-unresolved; failing-goal=f00622a9; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- f00622a9-0-20260803040528822 | 2026-08-03 | goal f00622a9 | Assert.DoesNotContain() Failure: Sub-string found

### 536. Windows post-landing capture passes exact argv without a shell

Signature: `System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg canary argv & (tests)\0e41baaab2c14ebdb46520c5e3258849\native stdout & (capture).log' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 50; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d8e604d8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d8e604d8-0-20260810033404772 | 2026-08-10 | goal d8e604d8 | System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg canary argv & (tests)\0e41baaab2c14ebdb46520c5e3258849\native stdout & (capture).log' because it is being used by another process.

### 537. WorkerContextPackageTests.Utf8DiscoveryCapturePreservesUnicodeBytesThroughOwnedNamedPipes

Signature: `System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\4fa6af44\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\4fa6af44\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe`. Pattern: **single-goal-unresolved**. Passes: 19; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=4fa6af44; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 4fa6af44-0-20260820054718722 | 2026-08-20 | goal 4fa6af44 | System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\4fa6af44\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\4fa6af44\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe

### 538. WorkerContextPackageTests.Utf8DiscoveryCapturePreservesUnicodeBytesThroughOwnedNamedPipes

Signature: `System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\6241ed3f\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\6241ed3f\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe`. Pattern: **single-goal-unresolved**. Passes: 19; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=6241ed3f; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 6241ed3f-0-20260820023156373 | 2026-08-20 | goal 6241ed3f | System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\6241ed3f\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\6241ed3f\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe

### 539. WorkerContextPackageTests.Utf8DiscoveryCapturePreservesUnicodeBytesThroughOwnedNamedPipes

Signature: `System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\c30eb2fe\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\c30eb2fe\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe`. Pattern: **single-goal-unresolved**. Passes: 19; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=c30eb2fe; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- c30eb2fe-0-20260820070008523 | 2026-08-20 | goal c30eb2fe | System.InvalidOperationException : Ambiguous prebuilt MTP probe apphosts: C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\c30eb2fe\artifacts\bin\Mcg.AgentOrchestrator.RealProcessShardProbe\debug\Mcg.AgentOrchestrator.RealProcessShardProbe.exe, C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\c30eb2fe\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Fixtures\RealProcessShardProbe\bin\debug\net10.0\Mcg.AgentOrchestrator.RealProcessShardProbe.exe

### 540. WorkerDispatchTestsSubscriptionPreflight.WorkerProfileDispatcherPreflightBlocksMissingUpstreamRoleSkill(role: Researcher, expectedSkill: "research-evidence")

Signature: `Assert.Contains() Failure: Filter not matched in collection`. Pattern: **single-goal-unresolved**. Passes: 38; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=ae2dbb9e; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- ae2dbb9e-0-20260811192851864 | 2026-08-11 | goal ae2dbb9e | Assert.Contains() Failure: Filter not matched in collection

### 541. WorkerProcessJobs_concurrent_startup_sweeps_claim_exact_worker_once

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 67; goal failures: 1; operator failures: 0; pre-review failures: 3.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9de9649d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9de9649d-0-20260804184817202 | 2026-08-04 | goal 9de9649d | Assert.True() Failure

### 542. WorkerProcessJobs_fallback_taskkill_tree_kills_unregistered_wrapper_and_grandchild

Signature: `System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-worker-job-tests\20ee418502e64c77a2704043a40a6013.pid' because it is being used by another process.`. Pattern: **single-goal-unresolved**. Passes: 105; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=ca2a66b6; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- ca2a66b6-0-20260725080822057 | 2026-07-25 | goal ca2a66b6 | System.IO.IOException : The process cannot access the file 'C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\mcg-worker-job-tests\20ee418502e64c77a2704043a40a6013.pid' because it is being used by another process.

### 543. WorkerProcessJobs_register_writes_durable_pid_identity

Signature: `Assert.Single() Failure: The collection was empty`. Pattern: **single-goal-unresolved**. Passes: 105; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9de9649d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9de9649d-0-20260804184817202 | 2026-08-04 | goal 9de9649d | Assert.Single() Failure: The collection was empty

### 544. WorkerProcessJobs_startup_sweep_reaps_only_registry_owned_pid

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 37; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=ef4fd330; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- ef4fd330-0-20260719205327540 | 2026-07-19 | goal ef4fd330 | Assert.Equal() Failure: Values differ

### 545. WorkerProcessJobs_startup_sweep_reaps_worker_only_after_owner_is_dead

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 67; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9de9649d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9de9649d-0-20260804184817202 | 2026-08-04 | goal 9de9649d | Assert.True() Failure

### 546. WorkerProcessJobs_startup_sweep_retains_worker_when_owner_image_mismatches

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 67; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9de9649d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9de9649d-0-20260804184817202 | 2026-08-04 | goal 9de9649d | Assert.True() Failure

### 547. WorkerProcessJobs_startup_sweep_retains_worker_with_unknown_legacy_owner

Signature: `Assert.True() Failure`. Pattern: **single-goal-unresolved**. Passes: 67; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9de9649d; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9de9649d-0-20260804184817202 | 2026-08-04 | goal 9de9649d | Assert.True() Failure

### 548. WorkerProfileDispatcher_afc62d88_Tester_round_derives_touch_proof_and_accepts_moved_finding_without_worker_start

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 51; goal failures: 1; operator failures: 0; pre-review failures: 3.
Excluded from budget conversion: single-goal-unresolved; failing-goal=049af2bc; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 049af2bc-0-20260808181810362 | 2026-08-08 | goal 049af2bc | Assert.Equal() Failure: Values differ

### 549. WorkerProfileDispatcher_includes_current_branch_and_head_in_dispatched_prompt

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=57a4b3be; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 57a4b3be-0-20260719015104202 | 2026-07-19 | goal 57a4b3be | Assert.Contains() Failure: Sub-string not found

### 550. WorkerProfileDispatcher_pins_anthropic_subscription_model

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=24d04cc8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 24d04cc8-0-20260728130105886 | 2026-07-28 | goal 24d04cc8 | Assert.Contains() Failure: Sub-string not found

### 551. WorkerProfileDispatcher_preflight_blocks_repo_scoped_skill_targets

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=9e3112b2; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 9e3112b2-0-20260811133706108 | 2026-08-11 | goal 9e3112b2 | Assert.Contains() Failure: Sub-string not found

### 552. WorkerProfileDispatcher_prepares_subscription_tasks_by_assigned_provider

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=24d04cc8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 24d04cc8-0-20260728130105886 | 2026-07-28 | goal 24d04cc8 | Assert.Contains() Failure: Sub-string not found

### 553. WorkerProfileDispatcher_ready_batch_records_provider_from_claude_launcher

Signature: `Assert.Contains() Failure: Sub-string not found`. Pattern: **single-goal-unresolved**. Passes: 106; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=24d04cc8; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 24d04cc8-0-20260728130105886 | 2026-07-28 | goal 24d04cc8 | Assert.Contains() Failure: Sub-string not found

### 554. WorkerProfileDispatcher_rejects_moved_finding_when_identical_commits_provide_no_touch_proof

Signature: `Assert.Equal() Failure: Values differ`. Pattern: **single-goal-unresolved**. Passes: 3; goal failures: 1; operator failures: 0; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=049af2bc; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- 049af2bc-0-20260808181810362 | 2026-08-08 | goal 049af2bc | Assert.Equal() Failure: Values differ

### 555. Workspace merge carries authoritative changed paths without a goal worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4deac85c31234ca2b6d4212a427e618b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\4deac85c31234ca2b6d4212a427e618b' is denied.

### 556. Workspace merge carries authoritative changed paths without a goal worktree

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\23440d3f10f948e2afff802aafde407b' is denied.`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\23440d3f10f948e2afff802aafde407b' is denied.

### 557. Workspace merge fails closed when branch changed paths cannot be determined

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ee3d30d82ff540828d04b3bfd43431bb' is denied.`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=a43bb588; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- a43bb588-0-20260810204011113 | 2026-08-10 | goal a43bb588 | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\a43bb588\.scratch\mcg-wt\ee3d30d82ff540828d04b3bfd43431bb' is denied.

### 558. Workspace merge fails closed when branch changed paths cannot be determined

Signature: `System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\32d594d1bea44791a60e12395241ac45' is denied.`. Pattern: **single-goal-unresolved**. Passes: 79; goal failures: 1; operator failures: 5; pre-review failures: 0.
Excluded from budget conversion: single-goal-unresolved; failing-goal=d16a22ba; introducing-goal=undetermined (single-goal metadata is insufficient for a regression claim).
- d16a22ba-0-20260810210037084 | 2026-08-10 | goal d16a22ba | System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\d16a22ba\.scratch\mcg-wt\32d594d1bea44791a60e12395241ac45' is denied.
