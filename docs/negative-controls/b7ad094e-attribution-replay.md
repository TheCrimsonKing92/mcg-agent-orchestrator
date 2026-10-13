# Acceptance attribution replay handoff

Developer builds only. Acceptance owns behavioral execution and negative controls; Operator owns the actual-copy replay and final Grok review. The historical cleanup mechanism and its latency share remain undetermined.

The isolated fixture is `AcceptanceAttributionReplayFixture.ReplayCopiedAttempt(inputPath)`, reached through `ApparatusRedGateSourceIndexTests.CompletionRecordsOneFailedDecisionAndNextGoalCanAdvance`. Without an input reference that test exercises a normalized 9532-cleanup/9529-pass/3-not-executed fixture. That run is **not** actual-copy evidence.

Operator copies the retained attempt's `.attempt.json`, `.result.json`, original/rerun TRXs and original/rerun gate heartbeats into a new isolated directory. Do not edit or point the fixture at live artifacts. Preserve the original bytes. Create `input.json` beside the copies, using this schema (paths in the six receipt objects must be relative to that directory):

```json
{
  "sourceRoot": "ABSOLUTE_FINAL_CANDIDATE_WORKTREE",
  "finalCandidateSha": "FINAL_BUILD_COMMIT",
  "attempt": { "file": "copied.attempt.json", "sha256": "ACTUAL_COPY_HASH" },
  "result": { "file": "copied.result.json", "sha256": "ACTUAL_COPY_HASH" },
  "originalTrx": {
    "file": "981d0d72-0-20261013130557813-1dc5ce3344664bec802614ccb1bf0037.infrastructure-tests-remainder.trx",
    "sha256": "F73C97922C03023A50CD127B0A2A377F0F7DD4D3AE68CC184629FAB7CDD30E77"
  },
  "rerunTrx": {
    "file": "981d0d72-0-20261013130557813-1dc5ce3344664bec802614ccb1bf0037.infrastructure-tests-remainder-run-1.trx",
    "sha256": "79B03869D3AAA5247C81E1E22C1F625AF4E8659836AA54EFCD02B4847FFD100E"
  },
  "originalHeartbeat": { "file": "original.gate-heartbeat.json", "sha256": "ACTUAL_COPY_HASH" },
  "rerunHeartbeat": { "file": "rerun.gate-heartbeat.json", "sha256": "ACTUAL_COPY_HASH" }
}
```

Use `Get-FileHash -Algorithm SHA256` on the copies; verify the two known TRX hashes above. Run from the final candidate worktree with a current test build:

```powershell
$env:MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_INPUT = 'ABSOLUTE_ISOLATED_DIRECTORY/input.json'
try {
    .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'Name~CompletionRecordsOneFailedDecisionAndNextGoalCanAdvance'
}
finally {
    Remove-Item Env:MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_INPUT
}
```

This input reference is consumed only by the managed replay test, which starts no descendant processes. The fixture validates receipt hashes, candidate/main/goal/attempt/partition binding, distinct TRX run IDs, heartbeat exit codes and distinct children. Legacy copied checks acquire scoped cleanup proof only after validation; original acceptance remains failed. Input/receipt mismatch fails loudly. Source resolution and census writes use isolated fixtures; recorded stores are never loaded or updated.

Retain the emitted `replayKind=actual-copy` JSON, managed test receipt and final build identity. It reports historical candidate/main separately from the final candidate, attribution and completion durations, read/enumeration counts, exactly one failed completion disposition, and a subsequent unrelated isolated goal advance. Timing is measured, never used as a test threshold. This fixture exercises `CompleteParallelLandingAcceptance` through the real landing completion handler; continued production conductor ticks remain a separate Operator observation. A normalized fixture pass cannot settle that observation.

Acceptance focused classes: `AcceptanceTestSourceResolverTests`, `ApparatusRedGateSourceIndexTests`, `AssemblyCleanupApparatusRoutingTests`, `AssemblyCleanupOnlyCompletionDecisionTests`, `AssemblyCleanupFailureCauseEvidenceTests`, `ApparatusRedGateMessageFingerprintTests`, `ApparatusRedClassifierCandidateRerunTests`, `ApparatusRedClassifierRerunNotExecutedTests`. Also run the configured full gate.

Required RED/GREEN controls: create a fresh source request per identity (expect read-count failure); disable bound cleanup handling (expect synthetic census/count failure); remove or mismatch only receipt binding (expect exemption refusal); add a real failed row/check (expect retained real attribution and apparatus-route refusal). Record nonzero discovery and the predicted assertion failure, then restore production and record GREEN. Compilation failure or zero discovery is not RED evidence.

Grok independently reviews the final source, request lifetime, conservative decisions, and supplied dump/source inference. Do not present this handoff, build receipts, source review, or normalized tests as executed actual-copy/tick evidence.
