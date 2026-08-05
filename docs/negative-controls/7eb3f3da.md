# Owned acceptance process registration negative control

Date: 2026-08-05

The controlled Windows matrix exercises both sides of the registration lifetime boundary through
the production background route (`ConductorParallelAcceptanceAttemptCoordinator` -> external
`__acceptance-gate-attempt` -> `GoalAcceptanceVerifier`):

- RED control: `ParallelAttempt_ExternalOldProductionSeam_FastExitRed` sets the test-only process
  start mode on the external attempt child. The verifier replays the former start-then-attach
  sequence with a deterministic fast exit. The durable attempt is `Failed` at
  `stage=owned-process-group-attachment`, the command marker is absent, and exactly one stable-slot
  lease release is recorded.
- GREEN behavior: `ParallelAttempt_OwnedStart_ReachesNormalResult` launches the identical external
  attempt and manifest through the default atomic owned start. The durable attempt is `Passed`, the
  command marker contains `started`, the result artifact exists, and exactly one stable-slot lease
  release is recorded.
- Unit comparators retain the direct fast-exit matrix and verify launch diagnostics, non-Windows
  process disposal, and resume rollback without a durable registry residue.

Verification receipt:

`Invoke-TestSummary.ps1 ... -Filter "FullyQualifiedName~WorkerProcessJobsTests"` — 32 passed,
0 failed.

`Invoke-TestSummary.ps1 ... -Filter "FullyQualifiedName~GoalAcceptanceVerifierDotnetBuildSlotTests&DisplayName~OwnedStart"`
— 1 passed, 0 failed; the real verifier wrote the configured marker.

`Invoke-TestSummary.ps1 ... -Filter "FullyQualifiedName~ConductorBatchLoopTests&DisplayName~FastExitRed"`
— 1 passed, 0 failed; external old-seam RED.

`Invoke-TestSummary.ps1 ... -Filter "FullyQualifiedName~ConductorBatchLoopTests&DisplayName~ReachesNormalResult"`
— 1 passed, 0 failed; external atomic GREEN.

This proves the old lifecycle window and the atomic launch behavior. It does not claim that every
historical live failure had the same native error; live failures now retain the original native
code/message and bounded job-membership/limit evidence so that claim can be evaluated from a real
receipt.

The operator-owned live `acceptance-retry 10075221` receipt is intentionally not supplied by this
Developer worker and remains a later acceptance prerequisite after independent review accepts the
candidate.
