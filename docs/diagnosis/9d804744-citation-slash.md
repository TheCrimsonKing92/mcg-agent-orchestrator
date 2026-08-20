# Planner: a backticked span containing a slash is read as a file path and discards the plan

Your previous plan was rejected for citation FORMAT only. The analysis was accepted on every other axis.

## What was rejected

    target citation 'try/finally' does not exist and is not marked as a new file

from your sentence:

    ...tests must save and restore it in `try/finally` exactly as `tests/Mcg.AgentOrchestrator...`

You wrote that correctly. Backticks around `try/finally` mark it as code, which is right. The validator
scans backticked spans, sees the `/`, treats it as a path, finds no such file, and discards the whole plan.

## What to do

Do NOT put a slash inside backticks unless it is a real repository path. Rewrite as plain words:

    "in a try/finally block"        ->  in a try-finally block
    "`read/write`"                  ->  read and write
    "`and/or`"                      ->  and, or both

Real paths in backticks are fine and expected: `src/Mcg.AgentOrchestrator.App/Orchestration/GoalOperationJournal.cs`.

Also still applies: cite a single line, never a range. `File.cs:127` is accepted; `File.cs:127-140` is
rejected for the same reason.

Before emitting, scan your output for backticked spans containing `/` and confirm each one is a real path.

## Keep everything else

This is an orchestrator defect, filed as backlog `9fbce159`; you are not expected to have known it. Your
plan's substance was not questioned. Re-emit it with the prose adjusted - do not re-derive the research and
do not change scope.

Prior work you should build on, already committed on this branch at
`docs/diagnosis/9d804744-journal-openers.md`: the four openers in `GoalOperationJournal.cs` are
`File.ReadLines` at :180 and :698 and `File.AppendAllText` at :202 and :876, none with an explicit
`FileShare`. Criterion 1 still requires a repository-wide audit rather than accepting those four as the
complete set.
