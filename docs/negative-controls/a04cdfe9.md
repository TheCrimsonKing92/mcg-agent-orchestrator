# Acceptance failure census negative controls

Recorded 2026-08-24 UTC with the repository focused-test wrapper against
`AcceptanceFailureCensusScriptTests`. Both arms ran from the goal worktree with
the same class filter and isolated build slot; each receipt reported a released
lease and nine matched tests.

## Complete signature tuple

RED mutation: replace the `(message, top frame)` grouping key with `message`
alone in `scripts/Get-AcceptanceFailureCensus.ps1`.

- Receipt: `focused-20260824T035526283-a352e237.receipt.json`
- Result: 8 passed, 1 failed.
- Failure: `FailureCorpus_AggregatesAndOrdersSignatures` reported
  `Assert.Contains() Failure: Sub-string not found`; expected
  `trx=2 executed~6 failed=4 distinctSignatures=3`.

GREEN restoration: restore the frame to the grouping key.

- Receipt: `focused-20260824T035618377-a6be937f.receipt.json`
- Result: 9 passed, 0 failed.

## Retained-artifact edge behavior

RED mutation: restore all three reviewed legacy behaviors together: infer arm
suffixes as separate attempt ids and exclude their TRX from the base attempt,
silently ignore malformed attempt metadata, and emit the absolute zero-failure
sentence for a partial corpus.

- Receipt: `focused-20260824T035710341-ecadaaed.receipt.json`
- Result: 6 passed, 3 failed, exactly at the changed seams.
- `ArmFiles_AreIncludedWithoutCreatingPhantomAttempts`: expected
  `trx=3 executed~3 failed=2 distinctSignatures=2` was absent.
- `InFlightMetadata_ReportsPartialWhileReadingTrx`: expected `status=partial`
  was absent.
- `PartialCorpus_ContinuesPastIncompleteFiles`: the forbidden absolute sentence
  `No failing test results in this attempt corpus.` was present.

After restoring those behaviors, the tracked source digest returned to
`78cacc6afa77510fe3038c2aca71d036454eb41205eb37082a8d4eb002f40b6e`, the
same digest as the 9/9 GREEN receipt above.
