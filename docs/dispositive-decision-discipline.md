# Dispositive-Decision Discipline

<!-- shared-discipline:dispositive-decision-discipline -->

Use this guidance wherever code assigns a **dispositive outcome** — a verdict, a terminal state, a circuit
trip, an escalation, a retry-vs-fail choice — on the basis of a result produced somewhere else.

## The rule

**A dispositive decision must be made in the presence of the evidence that discriminates its alternatives,
and must record that evidence.**

The second clause is the enforceable one. Requiring a written justification forces the discriminating
evidence to survive to the decision point, because you cannot justify what you cannot see. The first clause
then comes for free.

## The check (apply at review time)

At any site that assigns a terminal or dispositive outcome, ask:

> **Could I write the justification for this outcome from what is in scope right here?**

If the answer is no, the evidence was discarded upstream and the decision is a guess. Two fixes are
available, in preference order:

1. **Move the decision to where the evidence is.** Classify at the throw site, not the catch site.
2. **Carry the evidence to the decision.** Widen the result type so it distinguishes the cases that require
   different handling — "did not run" is not "ran and failed", "the apparatus broke" is not "the candidate is
   bad".

Never resolve it by guessing at the consumer.

## Corollaries

- **A failure channel is typed by cause class, not by a boolean.** `false` cannot carry why.
- **Name an outcome for what it MEANS, not what it looks like.** This is the corollary that catches the
  hardest case below. `EmptyReceipt` reads as benign plumbing; `AcceptanceEngineGreenWithZeroTestsExecuted`
  is nearly impossible to misfile as an environment fault.
- **A terminal transition records a typed reason.** A goal, task, or gate reaching a terminal state with no
  recorded cause is undiagnosable by construction, and an operator will end up bracketing it by log
  timestamps.
- **Apparatus and candidate are separate channels, in BOTH directions.** An infrastructure fault must not
  become a verdict about the candidate, and a genuine verdict must not be demoted to an infrastructure fault.
- **Unknown and unavailable are outcomes, not defaults.** Missing, unreadable, invalid, and valid artifacts
  remain distinct until the decision is recorded; none may be silently mapped to clean, absent, failed, or
  any other specific verdict.
- **Typed evidence outranks display text.** A classifier receipt or typed status is consulted before any
  last-resort substring heuristic. Heuristics require positive and negative controls so quoted diagnostics,
  test names, and assertion text cannot manufacture the disposition.
- **Timeout means the deadline fired.** Caller cancellation and a dependency that cancels itself are separate
  reasons; record timeout only when the deadline token or timer supplies the discriminating evidence.

## Corrected operator decisions

Clarification answers are append-only operator decisions. If an answer is wrong, the operator supersedes it:
the replacement becomes the single authoritative answer, while each earlier answer remains in audit history
as retracted with a pointer to its replacement. A retracted operator statement is not a competing directive.

Role context must contain the authoritative answer and omit retracted answer text, including stale copies
quoted by completed upstream artifacts. Completed artifacts retain their original bytes for audit; prompt
assembly applies the retraction when rendering them. If a role encounters a stale value through some other
history view, it follows the authoritative answer instead of stopping to ask which statement governs.

Superseding does not rewrite or invalidate completed work. It resolves matching clarification waits and
their derived blocker records so only tasks stopped on the contradiction become dispatchable again. Re-running
a completed task remains an explicit operator retry decision.

## Why the existing discipline did not catch these

`docs/test-design-discipline.md` governs tests, and the evidence-first principle governs the
record-versus-world relationship in general. All five failures below are **result types at internal
boundaries in ordinary production code**, where a rich outcome is projected into a poorer one on the way to a
consumer. That was the gap.

## Worked examples — five instances found in a single day (2026-08-03)

Each is stated as: the evidence that existed, and where it was lost.

1. **Canary slot-busy trips the acceptance circuit** (backlog `cfe34c0e`, fixed in goal `809634e8`).
   `DotnetBuildSlotsBusyException` meant "I could not get a build slot". By the time the circuit decided, it
   saw only "the canary failed", tripped to `Unhealthy`, and halted every landing board-wide for 29 minutes
   on a landing it never evaluated. *Check: the circuit could not have written "unhealthy because the engine
   produced a false verdict" — it only had an exception.*

2. **Terminal-without-run read as a rejected evidence request** (goal `8e8afe96`, finding
   `PRE-REVIEW-ASYNC-001`). `BlockedBuildSlot` / `BlockedBuildLock` / `ProcessDied` / `CorruptArtifacts` /
   `LaunchFailed` / `Cancelled` / `StaleCandidate` were all synthesized as
   `FocusedEvidenceRunResult(Accepted: false)`, which the consuming `!Accepted` branch treats as "the
   reviewer asked for something invalid" → Tester reopen or escalation. With `TimeSpan.Zero` permit
   acquisition, `BlockedBuildSlot` is routine. *Check: the branch could not have written "the request was
   invalid" — it did not know whether a run had occurred.*

3. **CS2012 baseline build recorded as an acceptance verdict** (backlog `18ccc733`). A transient `csc` file
   lock in the *trusted main baseline* build produced `passed: false` with
   `resultSummary: "trusted main baseline build failed"`. The candidate's code was never evaluated, yet the
   conductor failed the goal and dispatched a paid Developer round to chase a phantom defect. *Check: the
   gate could not have written "acceptance failed because the candidate…" — the failure was in the baseline.*

4. **`EmptyReceipt` demoted to `EnvironmentFault`** (goal `809634e8`, caught by the Reviewer). This is the
   REVERSE direction and the hardest case. `EmptyReceipt` is emitted only when `verification.Passed == true`
   with `executedTestCount == 0` — a completed evaluation proving the acceptance engine is **falsely green**,
   the single most important thing the canary exists to detect. It was mapped to `EnvironmentFault` and
   routed to the deferral path. *Check: a classifier CAN write "EmptyReceipt is an environment fault" and
   believe it — which is why the naming corollary above exists. The type was expressive enough; the member
   name hid the meaning.*

5. **Five-role goal → `Failed` with no recorded reason** (backlog `3a7afb95`). A Planner exited 0 with a
   well-formed `WORKER_RESULT` and `blockers: none`; the goal was `Failed` ten seconds later, with nothing in
   the goal-operations journal naming a cause. Diagnosis required bracketing the transition between two tick
   timestamps. *Check: no justification was recorded at all.*

Note that (1) was fixed correctly and locally, and the same codebase then got (4) wrong two files away —
because the principle was applied as a patch rather than stated as a rule. That is the argument for this
document existing.

## Reviewer checklist

- For each new or changed site that assigns a verdict, terminal state, circuit state, escalation, or
  retry-vs-fail disposition: confirm the justification is derivable from what is in scope, and that a typed
  reason is recorded.
- Treat a `catch` block that returns a bare failure result as a smell — ask what the caught exception knew
  that the returned value cannot express.
- Treat any boolean failure channel crossing a module boundary as a smell.
- Treat an outcome enum member whose name describes appearance rather than meaning as a smell.
