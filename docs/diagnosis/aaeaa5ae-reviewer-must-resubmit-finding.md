# Reviewer: re-emit `cohort-inflight-key-includes-revisions`. And read this before deciding — it looks REAL.

Your round was FAILED with:

    Reviewer WORKER_RESULT verdict rejected: merged structured finding state still has open blocking
    stable_id(s): cohort-inflight-key-includes-revisions

## Why silence does not close a finding

`src/Mcg.AgentOrchestrator.Core/Domain/ReviewFindings.cs` line 566: a prior finding you do NOT resubmit is
carried into the merged state unchanged, so it stays Open.
`src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.Recording.cs` line 583 then fails the
Reviewer task. An empty findings list means "leave it exactly as it was", not "nothing is wrong".

So you must re-emit `cohort-inflight-key-includes-revisions`, spelled exactly that way, with state resolved
if you judge it fixed or state open with a description if you do not.

## Operator reading of the substance, offered as evidence, not as a verdict

`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAcceptanceCohorts.cs` line 139:

    PairFingerprint = $"{first.GoalId.Value}:{first.CandidateRevision}:{second.GoalId.Value}:{second.CandidateRevision}:{first.MainRevision}"

The key includes both candidate revisions AND the main revision. The new async code in
`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs` looks up in-flight runs by that key with
`_cohortGateRuns.TryGetValue(pairFingerprint, out var currentRun)` around line 3251.

If any component of that key changes while a cohort gate is running, the next lookup computes a DIFFERENT
fingerprint, misses the running entry, and the loop concludes no cohort is in flight for that pair. The
running gate is then orphaned, and a second one can be started for the same members.

Main advancing mid-gate is not hypothetical on this board. It is the documented behaviour behind backlog
`ef515b07`: three concurrent gates on 2026-08-21 each recorded a `mainHeadSha` and main moved out from under
two of them when the third landed.

So on the evidence available to me, this finding appears correct and NOT yet addressed. I am not the
Reviewer and I may be missing a guard elsewhere in the diff.

## What to do

Judge it yourself against the current diff. If you agree it is unfixed, emit it with state OPEN and a
description of the orphaning path. That is a legitimate outcome and it routes the work to the Developer,
which is where a key-stability fix belongs.

Do NOT resolve it merely to unblock the goal. This goal exists to stop cohort gates freezing the conductor,
and an orphaned cohort run is a new way to lose track of one. Resolving it wrongly would ship the very class
of defect the goal was filed to remove.

The rest of the change is in good shape: focused evidence at this candidate was 497 tests executed, 497
passed, including `ProductionBatch_LongCohortGateDoesNotBlockTicksOrOperatorIntents_AndReconcilesLater`.
