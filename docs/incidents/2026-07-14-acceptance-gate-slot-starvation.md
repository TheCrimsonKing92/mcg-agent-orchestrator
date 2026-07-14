# 2026-07-14 Acceptance Gate Slot Starvation

## Symptom

Verified goals sat unlanded for more than two hours while dev-lane worker self-verification kept using the same stable slot grid that acceptance gates need. Backlog item `c31f7ca53c314433a5f3e1292bb16bb1` records `483594c1` as Verified before 13:37Z and still held at 16:00Z, with `d67a086f`, `4f970f1d`, and `2f03c3a7` also waiting. The only autonomous landing in that window was docs-only goal `5f9789c4`, which did not need a slot rebuild.

Source: backlog item `c31f7ca53c314433a5f3e1292bb16bb1`.

## Root Cause

Backlog item `c31f7ca53c314433a5f3e1292bb16bb1` diagnoses shared stable-slot contention:

- Dev-lane workers continuously ran slot-routed self-verify tests.
- Acceptance gate rebuilds shared the same four-slot grid.
- A gate rebuild could hit a real file-in-use exception on a slot artifact DLL while a worker `testhost.exe` had an image mapping from that slot.
- Lock attribution then timed out with `holderName=unknown-probe-timeout` and `source=handle64-timeout`, a poor attribution path for this lock class.

The receipt was conduct-loop batch45 ticks 109-110. The backlog body records `PHASE_TIMING tick=109 per-goal-walk elapsed_ms=535407` with `4f970f1d:423155ms:Held`, showing one tick burning about nine minutes in repeated probe and gate work.

## Falsified Alternatives

The record separates this incident from a single hung worker or one bad goal: several Verified goals waited, while the docs-only goal landed. It also separates starvation from plain lack of free CPU; the named failure mode was shared slot artifact locking and repeated lock attribution work.

## Fix

No completed fix is recorded here. The backlog item lists design options: gate-priority leasing or a reserved gate slot, transient-slot-contention classification for slot artifact DLL image mappings, and bounded per-goal lock wait budgets.

## Residuals

- Backlog `c31f7ca53c314433a5f3e1292bb16bb1`: acceptance gates starve behind worker self-verify slot locks.
- Backlog `88e2fd9e28c8470491bbd04b86e7f1a4`: related acceptance/landing residual where per-commit rebase failed even though a merge candidate was clean.
- Goal `a5340f2b`: active fix goal for the handle64 probe hang under managed spawn contexts; this is residual, not a resolved fix.

## Lessons

Acceptance gates and worker self-verification cannot compete blindly for the same scarce slots. When lock attribution cannot identify image-mapped slot DLL holders, repeated probes become throughput damage; the loop needs a retry-later classification or gate-priority path.

