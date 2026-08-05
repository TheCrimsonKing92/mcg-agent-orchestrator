# Owned acceptance process registration negative control

Date: 2026-08-05

The controlled Windows matrix in
`WorkerProcessJobsAtomicOwnedStartClosesFastExitRegistrationWindow` exercises both sides of the
registration lifetime boundary:

- RED control: start a fast `cmd.exe /d /c exit 0`, wait for its exit, then use the former
  start-then-attach registration path. Registration fails at
  `stage=owned-process-group-attachment`.
- Negative comparator: the same former path registers a deliberately long-lived process, showing
  that the control is about candidate lifetime rather than an unconditional job-object failure.
- GREEN behavior: start 12 fast-exit children suspended and atomically inside the owned
  kill-on-close job, publish registration, then resume them. Every child is registered before it
  exits and every registration is released.

Verification receipt:

`Invoke-TestSummary.ps1 ... -Filter "FullyQualifiedName~WorkerProcessJobsTests"` — 29 passed,
0 failed, including the matrix and native-diagnostic preservation test.

`Invoke-TestSummary.ps1 ... -Filter "FullyQualifiedName~GoalAcceptanceVerifierDotnetBuildSlotTests&DisplayName~OwnedStart"`
— 1 passed, 0 failed; the real verifier launched child PID 27332 and wrote the configured marker.

This proves the old lifecycle window and the atomic launch behavior. It does not claim that every
historical live failure had the same native error; live failures now retain the original native
code/message and bounded job-membership/limit evidence so that claim can be evaluated from a real
receipt.
