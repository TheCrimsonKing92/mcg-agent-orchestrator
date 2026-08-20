# Planner: NEVER put a slash inside backticks unless it is a complete path to an existing file

Three plans have now been discarded on this rule. The analysis was accepted every time; only the prose was
rejected.

## The rule, stated as a mechanical check

Before emitting, find every backticked span in your output. For each one containing `/`, ask: **is this the
complete relative path of a file that exists in this repository right now?**

- YES -> keep it. `src/Mcg.AgentOrchestrator.App/Orchestration/GoalOperationJournal.cs` is fine.
- NO -> remove the backticks and rewrite in plain words.

There is no third option. A backticked span with a slash that is not an existing file path DISCARDS YOUR
ENTIRE PLAN.

## What has been rejected so far, all correct English

    `try/finally`   ->  write: a try-finally block
    `archive/`      ->  write: the archive subdirectory, or the full path if you mean a specific one

Also rejected: any line RANGE. `File.cs:127` is accepted, `File.cs:127-140` is not.

Directories are the trap. A trailing slash makes something look like a path but a bare directory name is
not an existing file, so it fails. Say "the archive subdirectory under goal-operations" in plain text.

## Your previous finding is worth keeping — restate it

Your last plan contained this, and it is a genuine and important observation:

    ArchiveGoalJournals renames the live journal into archive/, and today an open handle blocks it

That is a real interaction between the journal archiving that landed recently and the share-violation this
goal fixes: the archive rename is another operation that contends for the same handle, so the audit and the
fix must cover it. Keep this in the re-emitted plan, written without backticks around `archive/`.

## Everything else stands

This is an orchestrator defect, backlog `9fbce159`; you are not expected to have known it. Do not re-derive
the research and do not change scope. Also on this branch:
`docs/diagnosis/9d804744-journal-openers.md` records the four verified openers in
`GoalOperationJournal.cs` — `File.ReadLines` at :180 and :698, `File.AppendAllText` at :202 and :876, none
with an explicit `FileShare` — and notes that criterion 1 still requires a repository-wide audit rather than
treating those four as the complete set.
