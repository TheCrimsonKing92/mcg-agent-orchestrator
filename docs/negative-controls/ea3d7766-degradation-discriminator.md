# The degradation branch keys on the wrong discriminator

Operator analysis of the round-8 blocking finding at
`src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerVerificationEvidence.cs:48`.

The typed non-authoritative preview is correct and should stay. The condition that selects it is
wrong, and it is wrong in the same way the two previous fixes on this seam were wrong: it covers
one subset of the failing population instead of the population.

## Current behaviour

`ResolveStandardOutputForContext` degrades only when the unavailability reason matches one exact
string:

    if (!string.Equals(
            verification.FullStandardOutputUnavailableReason,
            LegacySnapshotUnavailableReason,      // "legacy-snapshot-authoritative-output-unavailable"
            StringComparison.Ordinal))
    {
        return new ContextOutput(
            RequireAuthoritativeStandardOutput(verification, identity),   // throws
            true,
            null);
    }

Any other reason falls through to the throwing path.

## Why other reasons reach it

`TaskSpec.RestoredAuthorityUnavailableReason` only synthesises the sentinel when there is no
reason at all:

    authoritativeText is null && unavailableReason is null
        ? "legacy-snapshot-authoritative-output-unavailable"
        : unavailableReason;

Otherwise the captured reason passes through unchanged. That value originates at dispatch time
from `BackgroundDispatchRunner`:

    FullStandardOutputUnavailableReason: fullStandardOutput.UnavailableReason

So a record with **no authoritative output and a non-null, non-sentinel reason** — the ordinary
shape for a capture that failed for a stated cause — skips recovery, skips degradation, and
throws. Dispatch of the following task wedges, which is criterion 20's failure and the exact
outcome the degradation path was added to prevent.

## The discriminator to use instead

The question is not *which reason string is recorded*. It is *could this record ever have carried
authoritative bytes?*

- Bytes were never persisted, whatever the recorded reason: deliver the bounded preview marked
  non-authoritative, carrying that reason verbatim. Do not throw. There is nothing to recover and
  refusing to dispatch destroys more context than it protects.
- The record was produced after this change, by code responsible for persisting authoritative
  output, and has none: throw. That is a real invariant break and must stay loud.

Restated as the original decision table, with the last row unchanged:

| Record shape | Behaviour |
| --- | --- |
| `AuthoritativeStandardOutput` present | return it |
| Legacy snapshot recoverable and byte-equal | return it, authoritative |
| No authoritative bytes, any unavailability reason | bounded preview, `authoritative: false`, reason carried through |
| Post-change record missing authoritative output | throw |

Inverting the condition is close to a one-line change: degrade whenever authoritative bytes are
absent and recovery failed, and reserve `RequireAuthoritativeStandardOutput` for the post-change
case. Carry `FullStandardOutputUnavailableReason` into the emitted header rather than hardcoding
`LegacySnapshotUnavailableReason`, so the reader sees why it is not authoritative.

## Third instance of the same shape on this seam

1. Recovery was added but only fired when the snapshot was within the preview bound — that is,
   only when nothing had been lost.
2. The exemption was applied at one of the sites that gate on it.
3. The degradation branch is keyed on one of several possible reason values.

Each fix was correct in intent and narrower than the defect. Before submitting, state explicitly
which populations reach the throwing path and why each is correct — do not describe only the case
you fixed. Filed as backlog `dfa78c8d`.

## Unchanged

The hash-bound pointer design, the semantic package ID, the typed `ContextOutput` shape, and
criterion 3 fail-closed behaviour for unresolvable pointers all stand. Do not weaken any assertion
in the lifecycle tests.
