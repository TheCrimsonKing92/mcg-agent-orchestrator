# Reviewer: `blockers: none` does NOT close the open finding. You must re-emit it.

This goal is cycling Reviewer to Tester to Reviewer without converging. The reason is bookkeeping, not code.
The last Reviewer round was FAILED with:

    Reviewer WORKER_RESULT verdict rejected: merged structured finding state still has open blocking
    stable_id(s): recognition-consults-stderr-completes-evidence-free-tester

## Why silence does not close a finding

`src/Mcg.AgentOrchestrator.Core/Domain/ReviewFindings.cs` line 566, in the merge of the previous round's
findings with the round you submit:

    if (!nextById.Remove(prior.StableId, out var submitted))
    {
        merged.Add(prior);
        continue;
    }

A prior finding you do NOT resubmit is carried into the merged state unchanged, which means it stays Open.
`AgentOrchestratorKernel.Recording.cs` line 583 then fails any Reviewer task whose merged state still has an
open blocking finding. An empty findings list is not "nothing is wrong" — it is "leave it exactly as it was".

## What to do

Re-emit `recognition-consults-stderr-completes-evidence-free-tester` explicitly, spelled exactly that way,
with state resolved if you judge it fixed, or state open with a description of what remains if you do not.
Keep its original location. Both are legitimate outcomes. Omitting it is not.

## The substance, so you can judge it

The finding says the recognition path consults STDERR and can therefore complete a Tester that produced no
real verification evidence. That matters directly for this goal, whose criterion 4 is the negative control:
a Tester that completes clean with NO verification evidence at all must still fail.

Judge it against the current diff. `HasClassifiedVerificationEvidence` and `HasCompletedVerification` in
`src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs` both take standardOutput and
standardError. If evidence found only in stderr can now satisfy recognition, criterion 4 is weakened and the
finding should stay open with that stated. If the change confines the recognition to a channel where an
evidence-free Tester cannot pass, resolve it and say why.

Do not resolve it merely to unblock the goal. A wrongly resolved finding here would defeat the exact
negative control this goal exists to preserve.
