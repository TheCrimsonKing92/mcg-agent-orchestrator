# Reviewer: reporting `blockers: none` does NOT close the four open findings. You must re-emit them.

This goal has now cycled Developer, Tester, Reviewer three times without converging, and the reason is
bookkeeping, not code. The last Reviewer round reported `blockers=none` and its task was still FAILED with:

    Reviewer WORKER_RESULT verdict rejected: merged structured finding state still has open blocking
    stable_id(s): merge-train-landing-has-no-prepared-recovery,
    merge-train-missing-one-gate-three-landings,
    merge-train-missing-red-newest-drop-solo-attribution,
    merge-train-skips-pair-cohort-after-any-train-attempt

## Why silence does not resolve a finding

In `src/Mcg.AgentOrchestrator.Core/Domain/ReviewFindings.cs`, the merge of the previous round's findings with
the round you submit begins at line 566:

    if (!nextById.Remove(prior.StableId, out var submitted))
    {
        merged.Add(prior);
        continue;
    }

A prior finding that you do NOT resubmit is carried into the merged state exactly as it was, which means it
stays Open. The kernel then checks the MERGED state in
`src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.Recording.cs` at line 583: if the task is
a Reviewer task, a worker result is present, and any open blocking finding remains, the task is failed.

So an empty findings list is not "nothing is wrong". It is "leave all four exactly as they were".

## What to do

Re-emit ALL FOUR stable_ids in your findings output, each with state resolved, keeping the stable_id spelled
exactly as listed above. Do not invent new ids, do not rename them, and do not drop any of them.

Keep each finding's location the same as the prior round's location. A resolved finding retains its original
anchor for regression-reopen protection, so changing the location is unnecessary and only adds risk.

If you genuinely believe one of the four is NOT fixed, then say so by emitting it with state open and a
description of what remains. That is a legitimate outcome and it will route the work onward. What is not
legitimate, and what has failed three times now, is omitting them.

## The substantive state, so you can judge each one

Focused evidence at the current candidate executed the full cohort class and passed:

    AcceptanceCohortWorkflowTests   total 32   executed 32   passed 32   failed 0

That run took 190 seconds and is attached to this round. Earlier in this goal the same class stood at
31 total with 3 failed, so three previously failing tests are now green and one test was added.

- `merge-train-skips-pair-cohort-after-any-train-attempt` — fixed in commit f95c0ea9. The `trainAttempted`
  flag was removed from `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs`, so attempting a
  train no longer suppresses the pair-cohort path. Verify by reading that diff.
- `merge-train-missing-one-gate-three-landings` — a test now exists and passes
  (ProductionMergeTrain_OneGateAttemptLandsThreeMembers).
- `merge-train-missing-red-newest-drop-solo-attribution` — a test now exists and passes, asserting the
  landing paths and changed files for the ejected newest member.
- `merge-train-landing-has-no-prepared-recovery` — judge this one on its merits. Prior rounds noted that
  recovery entry points exist but that FinalizeLanding still synthesizes in-memory coverage. If the
  persistence half is still missing, emit this one as open with that description rather than resolving it.

Do not resolve a finding you do not believe is fixed. Do not leave a fixed one unmentioned.
