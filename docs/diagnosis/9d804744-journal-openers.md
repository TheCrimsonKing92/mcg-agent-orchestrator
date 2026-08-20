# Confirmed: the four journal openers all use share-unfriendly File.* helpers

A previous Researcher round found this and died before it could emit its artifact. The finding is correct
and has been verified against source — start from here rather than re-deriving it.

## The four openers in `GoalOperationJournal.cs`

    :180   File.ReadLines(path)
    :202   File.AppendAllText(path, ... + Environment.NewLine)
    :698   File.ReadLines(path)
    :876   File.AppendAllText(path, ... + Environment.NewLine)

`File.AppendAllText` and `File.ReadLines` do not let you specify `FileShare`. `AppendAllText` opens for
write with default sharing, and `ReadLines` opens for read. When the conduct loop appends while a
parallel-acceptance holder child reads or appends the same `<goal>.jsonl`, the collision produces:

    The process cannot access the file .orchestrator/goal-operations/<goal>.jsonl
    because it is being used by another process.

which faults a healthy acceptance attempt into a Verified-phase escalation.

## What this does NOT tell you yet

Four openers in this file are not necessarily ALL openers. Criterion 1 asks you to enumerate every opener
of `goal-operations/*.jsonl` across the repository — the loop process, the parallel-acceptance holder, and
any CLI readers — and to state how you found them. A repository-wide search for the directory name and for
the journal path helper is the honest method; four hits in one file is a starting point, not the answer.

Note also that a retention goal recently added journal ARCHIVING for retired goals, which introduced
additional readers. Merge current main before auditing so those are in scope.

## Direction, not a decision

`FileMode.Append` + `FileShare.ReadWrite` via `FileStream` replaces `AppendAllText`; `FileShare.ReadWrite|Delete`
on the read side replaces `ReadLines`. The backlog item also offers a single-writer channel through the loop
process as the stronger alternative. Choose and justify — do not assume the smaller fix is sufficient
without saying why.
