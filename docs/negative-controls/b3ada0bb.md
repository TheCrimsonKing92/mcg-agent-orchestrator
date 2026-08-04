# Negative-Control Record: b3ada0bb

This subscription Developer may run only `Invoke-WorkerBuildCheck.ps1`, so behavioral RED receipts remain an acceptance-lane obligation. The successful compile is not a substitute.

Run each mutation independently, retain the named focused failure, restore the source, and rerun GREEN:

- In `BackgroundDispatchRunner.ReadExitCode`, map invalid content back to `Valid(1)`. `BackgroundDispatchRunnerInvalidExitArtifactHoldsWithoutManufacturingExitOne` must go RED; make the unreadable catches return `Valid(1)` and `BackgroundDispatchRunnerUnreadableExitArtifactHoldsWithTypedEvidence` must also go RED.
- In `DispatchRecoveryPolicy.Evaluate`, remove the non-missing unavailable-heartbeat `Hold` arm. `DispatchRecoveryPolicyInvalidHeartbeatIsApparatusHoldNotStaleBudget` must go RED.
- In `BackgroundDispatchRunner`, remove the unavailable-worktree recovery status and the completed-inspection diagnostic/failure arm. `BackgroundDispatchRunnerUnavailableWorktreeInspectionHoldsUnknownState` and `BackgroundDispatchRunnerCompletedGitInspectionFailureFailsWithReceipt` must go RED.
- In `DispatchStateSurface.Evaluate`, pass every requested worktree inspection to recovery regardless of role, liveness, or exit receipt. `DispatchStateSurfaceReadOnlyMissingWorktreeMatchesRefreshStaleDisposition` must go RED.
- In `PostLandingCanaryCoordinator` and `SemanticAcceptanceEvaluator`, classify internal `OperationCanceledException` as timeout again. `RunnerInternalCancellationIsNotRecordedAsTimeout` and `EvaluatorRecordsInternalJudgeCancellationWithoutClaimingTimeout` must go RED.
- In `FailureTriagePlanner`, classify the concatenated worker streams by permission substrings before the typed dispatch outcome. `FailureTriagePlannerDoesNotTreatQuotedPermissionTestTextAsApparatusFailure` and `DashboardActionRecommendationKeepsPermissionTestFailureOnCodeRetryPath` must go RED.
- In `PostLandingCanaryState.TryParseKind`, map unrecognized status to `Failed`. `UnrecognizedCanaryReceiptStatusSurfacesUnavailableStateWithoutTrippingUnhealthy` must go RED.

Status: RED execution deferred to the test-capable acceptance lane; GREEN compile verification is recorded in the goal worker result.
