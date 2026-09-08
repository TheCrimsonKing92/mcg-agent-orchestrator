# Baseline-attribution replay

Operator-owned, post-landing procedure for goal `5d57fe95`.

1. Build the delivered candidate with the sanctioned managed test workflow and record the candidate branch SHA, main SHA, App DLL path, DLL SHA-256, and producing runtime identity.
2. Adapt and run the retained reflective harness against that App DLL. Exercise `CleanTestBaseline.Resolve` and `CleanTestBaseline.Attribute` with the historical two-goal shared-label input, passing the additive `BranchHeadSha` field when constructing `CleanTestBaselineEvidence`.
3. Record the result as a new run, not a revision of history. The expected new verdict is `ObservedRedCorrelation` with `Unattributed` origin; neither a shared check label nor a candidate pass may produce `AttestedRed`, `AttestedGreen`, `Inherited`, or `Introduced`.
4. Rebuild the representative failed acceptance context for `f4c5073c` and invoke `AgentOrchestratorKernel.BuildAcceptanceFailureBriefBlock` through its normal task-brief entry point. Confirm the brief requests evidence and does not issue the not-attributable instruction from name-only correlation.
5. Separately replay the supported producer already used by acceptance: attach a representative `AcceptanceCheckResult.FailingTestAttributions` record from `GoalAcceptanceVerifier.AttachTestFailureAttributionsBatchAsync`, produced by the merge-base focused arm. Record its exact failing identity, merge-base SHA, focused selection, policy/runtime, and candidate identity. Confirm that only uniform exact identities map to a check-level origin; mixed or omitted identities remain `Unattributed`.
6. Preserve the historical snapshot as observed state only: its producing runtime was not independently verified. Record remaining live gates as open; candidate journals alone still do not supply an executed baseline verdict.

The accepted source path preserves typed failure causes independently of origin. Verify a typed `EnvironmentalApparatus` receipt still reaches the existing apparatus handling path while its origin remains `Unattributed` unless an authoritative baseline producer supplies proof.
