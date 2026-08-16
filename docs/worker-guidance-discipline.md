# Worker guidance discipline

Owns: how to write briefs, retry feedback, and clarification answers that a worker can act on.
Counterpart: [`role-capability-matrix`](role-capability-matrix.md) owns *which role* can satisfy a
criterion. This document owns *how to phrase* the requirement once the role is right.

Read this before writing a second correction to the same worker. A first instruction that failed
is usually not underspecified — it is specified in a form the worker cannot check itself against.

## The rule

**A prohibition invites variants. An adjective invites interpretation. A decision procedure or a
worked exemplar produces the thing.**

When a worker misses twice on the same axis, stop restating the requirement. Replace it with
either:

- a **decision procedure** the worker can run against its own output before submitting, or
- a **pointer to an existing artifact in the repository** that already meets the standard.

## Worked cases

All four are from a single session, 2026-08-15. In each the first instruction was accurate and
still failed.

| Situation | Instruction that failed | Instruction that worked |
| --- | --- | --- |
| Planner cited a gitignored runtime store as a plan target | "`.orchestrator/` is never a target" | "a target must be a file `git ls-files` lists today; runtime artifacts are subject matter, cite the tracked source that produces them" |
| Tester deferred negative controls without detail | "record the negative controls" | "meet the standard in `docs/negative-controls/8fc5413a.md`" |
| Developer left refinement ownership in memory, three rounds | "the attachment owner must be durable" | "name the store, and name who re-drives it on conductor restart" |
| Tester blocked wanting to execute controls it may not run | — | "record the exact mutation, the RED signature, the GREEN expectation, and the exact acceptance filter" |

The first case is the clearest. Told `.orchestrator/` was forbidden, the Planner stopped citing
`.orchestrator/` and cited `*.reservation.json` — the same runtime artifacts as a glob. It obeyed
the prohibition and reproduced the error, because the prohibition named an instance and not the
principle. `git ls-files` is a test the Planner can run on every entry in its own target list.

## Prefer an exemplar for structured deliverables

For anything with a required shape — audit records, negative controls, disposition tables —
pointing at a file already in the repository outperformed every prose description of the same
requirement, twice in one session, and both times the result **exceeded** the specification.

Told to match `docs/negative-controls/8fc5413a.md`, a Tester independently added a discovery
guard the exemplar lacked: *"zero matched tests is inconclusive and is not a RED or GREEN
receipt."* That is the vacuous-pass hazard closed without being asked, because the worker was
reasoning about a concrete artifact rather than parsing an abstract requirement.

Maintain the exemplars deliberately. When a role produces an unusually good record, cite it by
path in the next brief that needs the same shape.

## Retry feedback and brief text are not interchangeable

**If a correction fails twice through retry feedback, move it into the brief.** Do not reword it
a third time.

Worked A/B, same requirement, 2026-08-15/16. Goal `15c4a259` was rejected four times with
`acceptance criterion mapping is incomplete: criterion 5 is unmapped`. The Planner had judged the
criterion operator-owned — correctly — and said so in prose each round rather than emitting a
mapping entry. Three operator corrections through retry feedback failed, including one supplying
the literal `disposition=undecidable` syntax with its three required fields.

The goal was cancelled and re-filed as `da7a1747` with one change: the criterion now states its
own mapping form inline in the brief.

    5. OPERATOR-OWNED, NOT WORKER-SATISFIABLE. ...
       **Planner: map this as `disposition=undecidable` with `would-settle`, `required-source`
       and `unavailable-because`, as a numbered entry in the mapping list.** Prose outside the
       list reads to the contract as unmapped.

The re-intaken goal `70eebe35` passed the contract on its Planner's **first attempt**.

The mechanism is worth understanding rather than memorising: a Planner plans against the brief.
Retry feedback arrives as a correction to a plan it has already reasoned out, and a worker that
believes its analysis is right — as this one was — will preserve the analysis and adjust around
the correction. The brief is the input; feedback is commentary on the output. Requirements belong
in the input.

Cost of learning this the slow way: four contract rejections, three operator rounds, an
irreversible mis-waive on a safety constraint while trying to bypass the problem, and a cancelled
goal.

## Corollaries

**State the mechanism, not the property.** "Durable" is an adjective; "persisted to the outbox,
re-driven by the conductor on startup" is a mechanism. The worker that received the second form
produced a durable-outbox coordinator in one round after three rounds of failing against the
first.

**Say what is unchanged.** A worker receiving a correction can reasonably conclude its whole
analysis was rejected and redo sound work. If only the target list or one assertion is wrong, say
so explicitly: *"your premise is unchanged; only the citation form is wrong."*

**Supply evidence as constants, and mark provenance as provenance.** Naming the store a fact came
from invites the worker to cite it as scope. Give the figures inline, state the source in prose,
and say plainly that the path is not a target. Better still, commit the evidence as a tracked
file in the goal's own worktree and point at it — a worker asked for data it cannot reach is
right to refuse, and the fix is to make the data reachable, not to repeat it in a message.

**An accurate blocker beats an optimistic completion.** When a round may not be able to finish,
ask for the specific reason instead of another attempt. Name what must be in the blocker: the
file, the missing seam, the contradiction. After several failed rounds, a precise refusal is more
valuable than a fifth try.

## Why this is worth the effort

Measured across two sessions, worker blockers were correct in every instance where they were
checked — the loss was upstream, in briefs and corrections, not in implementation. Rounds spent
re-explaining a requirement cost the same as rounds spent implementing it, and produce nothing.
