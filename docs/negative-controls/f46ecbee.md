# SQLite retention and compaction negative controls

Acceptance owns execution of these RED/GREEN controls. Each mutation is temporary and must be reverted before the candidate is accepted.

| Control | RED mutation | Required failure signal |
| --- | --- | --- |
| Capped continuation | Remove the `MaxDeleteBatchesPerPass` stop | `CappedPassReturnsIncompleteAndContinuation`: expected `Incomplete`, observed `Completed` |
| Protected evidence | Add the protected goal id to the eligible fixture set | `CappedPassReturnsIncompleteAndContinuation`: expected one protected row |
| Growth veto | Remove the total-byte comparison from `SqliteMaintenanceClassifier` | `ByteGrowthCannotBeClassifiedAsCompleted`: expected `Stalled`, observed `Completed` |
| No-progress stop | Remove `MaxNoProgressAttempts` classification | `RepeatedNoProgressStallsWithTypedReason`: expected `NoProgressLimit` |
| Busy checkpoint recurrence | Gate the no-progress counter only on bytes over budget | `BusyCheckpointUnderBudgetEscalatesAcrossCalls`: expected counter `1`, observed `0` |
| Continuation scheduling | Always assign the daily cadence after an incomplete pass | `IncompleteReceiptSchedulesBoundedContinuation`: expected two maintenance calls |
| Active conductor lease | Remove the exclusive-lease refusal | `ActiveConductorLeaseDefersBeforeCompaction`: expected `Deferred` |
| Off-peak policy | Ignore `UtcNow` in the state maintenance plan | `OutsideOffPeakWindowIsDeferred`: expected `DeferredOutsideWindow` |
| Conversion lease | Remove the exclusive-lease refusal in `StateDatabaseOfflineConversion` | `ConvertCopy_ActiveLease_DefersWithoutCandidate`: expected `Deferred/ActiveWork` and no candidate |
| Candidate integrity | Skip candidate reopen and `integrity_check` | `ConvertCopy_CorruptCandidate_PreservesSource`: expected `CandidateIntegrityCheckFailed` and unchanged source |
| Candidate compatibility | Skip user-version/schema fingerprint comparison | `ConvertCopy_CandidateMismatch_PreservesSource`: expected `CandidateSchemaMismatch` and unchanged source |
| Replacement rollback | Remove backup restoration after install failure | `ConvertLive_InstallFailure_RestoresOriginal`: expected original auto-vacuum mode and rows after typed failure |
| Copy amplification | Omit candidate bytes from aggregate receipt accounting | `ConvertCopy_ValidCandidate_ReportsAmplification`: expected aggregate bytes greater than source-only final bytes |
| Byte-pressure branch | Force `bytePressureRequired` false after the pre-pressure checkpoint | `BytePressure_PrunesOldRowsAndPreservesRecentRowsAcrossPasses`: stalled in one pass with `ProtectedEvidenceFloor`, zero deletions, and 647,168 bytes over budget |
| Post-VACUUM checkpoint | Bypass the synchronized real-reader callback after VACUUM measurement | `Vacuum_BusyConvergenceCheckpointCannotReportCompleted`: `Assert.False(result.VacuumCompleted)` failed because the unblocked convergence checkpoint completed |

The real SQLite acceptance slice must also inspect the `wal_checkpoint(TRUNCATE)` receipt and assert that `busy != 0` cannot produce `Completed`. Zero matched tests or a missing test result is inconclusive.
