# Pre-review focused-evidence requests replay forever when the selector cannot resolve

Diagnosed 2026-08-22. Source-verified. Recorded because two separate operator investigations reached wrong
conclusions before this one, and the discriminating evidence is easy to miss.

## Symptom

A goal emits focused-evidence attempts that are rejected in 10 to 35 seconds with `tests_executed=unknown`
and the same message every time:

    focused evidence selection 'FullyQualifiedName~CliCommandTests.PersistentRunnerCommands'
    does not resolve to a class or method in Infrastructure.Tests

Goal `21b284a0` reached ordinal 58 this way, re-dispatching its Tester roughly every 2.5 minutes. No operator
verb clears it.

## The three seams, each sufficient to sustain the loop

**1. Generation.** `RepositoryTestImpactPlanner.BuildChangedTestClassFilter` maps every changed test file
through `Path.GetFileNameWithoutExtension` and wraps the result as `FullyQualifiedName~{name}`. .NET strips
only the final extension, so `CliCommandTests.PersistentRunnerCommands.cs` yields
`CliCommandTests.PersistentRunnerCommands`, while the declared type is
`CliCommandTestsPersistentRunnerCommands` with no dot. Census over test sources excluding obj, bin, scratch,
artifacts, and generated files: 12 dotted files, 17 declared classes, zero containing a dot. The mapping is
wrong in every case. It is also unsound rather than merely dot-fragile -
`CliCommandTests.GoalLifecycleCommands.cs` declares six classes, four of which no string transform recovers.

**2. All-or-nothing rejection.** `GoalAcceptanceVerifier` returns immediately when any `FullyQualifiedName`
token fails to resolve. A single-segment selector such as `CliCommandTests` is accepted directly and matches
all 17 classes by substring, while the dotted selector enters class resolution and produces
`UnresolvableSelection`. Valid union members do not salvage the request, so a mostly-correct filter is
discarded whole.

**3. The typed reason is discarded, then the request is deliberately rerun.** The rejection code survives into
the attempt result record, but `PreReviewEvidenceReceipt` has no rejection-code field. The not-accepted branch
therefore records only `PreReviewEvidenceDisposition.MappingNeedsInput` and retries the Tester. A matching
prior receipt suppresses only `Green` and `NoApplicableTests`; `MappingNeedsInput` falls through and executes
again. Because the diff is unchanged, every Tester completion regenerates the identical malformed request.

## Why the existing guard does not stop it

Commit `83d0ea3a` added `IsPermanentFindingEvidenceRefusal`, which correctly treats an unresolvable selection
as permanent. It lives in `TryBuildFindingEvidenceRequest`, reached only after resolving a worker-reported
finding by stable id, and its regression test explicitly disables pre-review with `NoPreReviewContext`.

It is correct for its seam and does not apply to this one. The commit IS present in the running binary -
`git merge-base --is-ancestor 83d0ea3a 57f4dea6` exits 0 - so staleness is not the explanation. Two earlier
investigations blamed a stale conductor binary and legacy pre-fix records respectively; both were wrong.

## Discriminator

Both the finding-bound and pre-review paths emit the same rejection text. The `EVIDENCE_END` event
distinguishes them: finding-bound runs carry `finding_round_fingerprint` and `request_dispositions`. On the
pre-review path both are null.

Diagnose from that field pair, not from the message.

## Fresh-goal exposure

A goal with no prior records reproduces this. The request is synthesized from the current changed-file list,
not recovered from history: the first pre-review run is rejected, recorded as `MappingNeedsInput`, the Tester
is retried, and the next completion regenerates the same request. Any goal whose diff touches one of the 12
dotted test files is exposed.

## Tracked as

Generation seam: backlog `36e0b85e`, goal `47b181c3`. Replay seam: backlog `95a7ab2f`. File-shape conventions
that create the trap: goal `09bc0214`.
