# Planner: cite single lines, never line ranges — this rejected the previous plan

Operator note, 2026-08-20. Your previous plan was discarded for citation FORMAT only. The analysis was not
the problem.

## The rule

The output contract accepts:

    File.cs
    File.cs:442
    File.cs#L442
    File.cs::Symbol

It REJECTS a hyphenated range:

    File.cs:442-479

`PlannerOutputContract.cs:1137` matches an optional suffix of `:<digits>`, `#L<digits>`, or `::<symbol>`.
A range matches none of them, so the validator treats the ENTIRE string including `:442-479` as the path,
finds no such file, and discards the whole plan with
`target citation '...' does not exist and is not marked as a new file`.

## What was rejected

Your previous output carried at least these:

    AcceptanceCohorts.cs:132-180
    AcceptanceCohorts.cs:139-142
    CliCommandHandlers.Goals.cs:702-724
    ConductorAcceptanceCohorts.cs:141-209
    ConductorAcceptanceCohorts.cs:150-171

Replace each with its single start line - `AcceptanceCohorts.cs:132`, `ConductorAcceptanceCohorts.cs:141` -
or drop the line suffix entirely. Before emitting, scan your whole output for the pattern
`.cs:<digits>-<digits>`.

## This is an orchestrator defect, not your error

Filed as backlog `9fbce159`. A MORE precise citation is rejected where a vaguer one passes. You are not
expected to have known it, and the guidance that should have told you is not reaching the Planner prompt on
the research-first path.

## Keep the rest of your plan

The Researcher's finding stands and your plan should build on it: the existing acceptance-cohort path is
`advisory=true`, max 4 members, and its pair invariants are load-bearing. Build `MergeTrain*` planner /
workspace / landing BESIDE the pair cohort rather than replacing it. Do not re-derive the source survey.
