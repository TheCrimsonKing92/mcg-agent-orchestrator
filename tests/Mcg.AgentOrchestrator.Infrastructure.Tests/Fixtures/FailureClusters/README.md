# Failure cluster fixtures

The operator selected these real records from 2026-09-21 through 2026-10-05 and sanitized
user paths, session identifiers and long fields for the public repository. All four JSONL
files are verbatim copies of the authorized `e5eb3946-fixtures` evidence pack.

`goal-events.jsonl` contains green receipts, dirty worktrees, Claude unknown failures,
invalidations, five repeated requeues, a later process start and eight steady holds.
`conduct-events.jsonl` contains two discarded cohorts and one completed control. Each
cohort emits its elapsed time per member; the report charges each cohort once.
`operator-intents.jsonl` contains the associated operator touches.

The operator authorized `goal-events-dedupe-precheck.jsonl` for recurrence after a new
dispatch because no cost-bearing family in this window has that exact sequence. It
contains five identical successful pre-check notes, a process start and another note.
The root-counting test uses the report's dedupe seam; successful notes carry no failure cost.
