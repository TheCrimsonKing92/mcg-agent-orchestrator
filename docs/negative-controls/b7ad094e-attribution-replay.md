# Acceptance attribution replay handoff

Developer builds only. Acceptance owns behavioral execution and negative controls; Operator owns the actual-copy replay and final Grok review. The historical cleanup mechanism and its latency share remain undetermined.

The isolated fixture is `AcceptanceAttributionReplayFixture.ReplayCopiedAttempt(mode, requestJson)`, reached through `ApparatusRedGateSourceIndexTests.ActualCopiedCompletionRecordsOneFailedDecisionAndNextGoalCanAdvance`. It requires the explicit helper argument below. The separate `CompletionRecordsOneFailedDecisionAndNextGoalCanAdvance` test exercises a normalized 9532-cleanup/9529-pass/3-not-executed fixture. That run is **not** actual-copy evidence. The actual-copy test skips during ordinary no-input suites; when replay is requested the helper requires one executed replay test and its bound actual-copy artifact, so a skip or normalized pass cannot satisfy the request.

Completion evidence counts the driver's failed disposition sink (`writeEscalation`), separately from the product acceptance-failure record. The recorded summary contains a `shared-gate-apparatus-invalidated` partition and an identityless aggregate without environmental classification. Preserve those inputs: the isolated goal has no completed retry task, so the current handler escalates with `unmet-criteria-no-retry-task`; the normalized cleanup-only summary holds with `environmental-apparatus-hold`. Both remain failed. The receipt exposes the exact typed route and product-record count, and validates candidate/main facts and the recorded disposition reason. Guard failures emit bounded lease, disposition/product counts, request presence, acceptance outcome, candidate binding, and route. Source counts treat an unused request as zero; absence alone is not proof of required I/O. The controlled copied-request theory exercises both full completion routes, including the historical 9532/9529/3 row shape, without claiming historical execution.

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
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~ApparatusRedGateSourceIndexTests&Name~ActualCopiedCompletionRecordsOneFailedDecisionAndNextGoalCanAdvance' -AcceptanceAttributionReplayInput 'ABSOLUTE_ISOLATED_DIRECTORY/input.json'
```

Run from a committed candidate with clean tracked files; `sourceRoot` must equal this worktree and `finalCandidateSha` must equal its HEAD. The helper accepts only the exact filter above, the Infrastructure.Tests managed host, and one invocation. It delivers a fixed replay context only to that host's ProcessStartInfo after hermetic sanitization; build/discovery and the caller receive no replay environment change. The context binds input bytes, final candidate/root, owning source hashes, and launched assembly hash. The fixture revalidates these plus receipt hashes, candidate/main/goal/attempt/partition binding, distinct TRX run IDs, heartbeat exit codes and distinct children. It starts no descendants. Legacy copied checks acquire scoped cleanup proof only after validation; original acceptance remains failed. Missing/mismatched actual input fails closed. Source resolution and census writes use isolated fixtures; recorded stores are never loaded or updated. Do not use the previous ambient `MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_INPUT` command; hermetic sanitization intentionally strips it.

Retain `acceptance-attribution-replay.json` from the helper's printed retained evidence directory, its adjacent TRX and `run-identity.json`, and the final build identity. The run identity binds the structured replay artifact by SHA256; the helper requires its request identity and completion counters before retaining a passing run. Passing-test console output is not the evidence vehicle. The artifact reports historical candidate/main separately from the final candidate, attribution and completion durations, read/enumeration counts, exactly one failed completion disposition, and a subsequent unrelated isolated goal advance. Timing is measured, never used as a test threshold. This fixture exercises `CompleteParallelLandingAcceptance` through the real landing completion handler; continued production conductor ticks remain a separate Operator observation. A normalized fixture pass cannot settle that observation.

Acceptance focused classes: `AcceptanceTestSourceResolverTests`, `ApparatusRedGateSourceIndexTests`, `AcceptanceAttributionReplayRequestTests`, `MtpAcceptanceAttributionReplayTests`, `AssemblyCleanupApparatusRoutingTests`, `AssemblyCleanupOnlyCompletionDecisionTests`, `AssemblyCleanupFailureCauseEvidenceTests`, `ApparatusRedGateMessageFingerprintTests`, `ApparatusRedClassifierCandidateRerunTests`, `ApparatusRedClassifierRerunNotExecutedTests`. Also run the configured full gate.

Exact current-round regression request (ordinary no-input actual-copy test is skipped; copied fixtures in unit tests are controlled fixtures, not the retained historical attempt):

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~AcceptanceAttributionReplayRequestTests|FullyQualifiedName~MtpAcceptanceAttributionReplayTests|FullyQualifiedName~ApparatusRedGateSourceIndexTests'
```

Required RED/GREEN controls: create a fresh source request per identity (expect read-count failure); disable bound cleanup handling (expect synthetic census/count failure); remove or mismatch only receipt binding (expect exemption refusal); add a real failed row/check (expect retained real attribution and apparatus-route refusal). Record nonzero discovery and the predicted assertion failure, then restore production and record GREEN. Compilation failure or zero discovery is not RED evidence.

Grok independently reviews the final source, request lifetime, conservative decisions, and supplied dump/source inference. Do not present this handoff, build receipts, source review, or normalized tests as executed actual-copy/tick evidence.
