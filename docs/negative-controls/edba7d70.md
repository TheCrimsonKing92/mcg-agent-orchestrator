# Negative-control receipt: exhaustive reviewer findings

Status: complete. The behavioral RED and restored GREEN were executed on 2026-08-11 in a
disposable `--no-local` clone detached at candidate
`ddd3a990d28c063ef1df5ffe95e3b7d2815bfe9e`. Its verified base was
`b8c32ee25f05b25082b452401fbcad00da3d4629`. The probe deleted its isolated clone only after
restoring the exact candidate and confirming a clean worktree.

## Behavioral RED

The probe restored the base versions of:

- `src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs`
- `src/Mcg.AgentOrchestrator.Core/Application/SdlcRolePromptRequirements.cs`

It retained the candidate `TryFindBlockers` API so the tests still compiled, but replaced its
implementation with the pre-change single-blocker behavior (`blockers = [blocker]`). These were
the only three declared and observed production mutations.

The focused filter was
`FullyQualifiedName~AgentOutputDirectivesTests|FullyQualifiedName~TaskBriefTests|FullyQualifiedName~WorkerResultBlockersTests`.
The isolated runner built successfully, executed 148 tests, and returned FAIL with 144 passed,
4 failed, 0 skipped, and test-process exit 2:

- `WorkerResultTemplate_requires_structured_tests_and_blockers_tokens`
- `Reviewer_requirements_order_spec_before_quality_within_budget`
- `WorkerResultBlockersTests.TryFindBlockers_MultipleFindings_ReturnsEveryTokenInOrder`
- `BuildTaskBrief_adds_exhaustive_reviewer_contract_for_all_intake_labels`

The RED receipt reported `reason=test-failures`, an unchanged mutation digest across execution,
and `leaseReleased=true`. Receipt SHA-256:
`31ED68586500D8733E1900A9501968C3B75E174A8A9B5B01181336A06DB811C6`. TRX SHA-256:
`E0D5055185522FD5F44263B1410B9D66C03ECEC74F5810ED6E800CEE183F14EB`.

## Restored GREEN

The probe restored all three production files from the exact candidate, required an empty
`git status --short`, and reran the identical filter. It returned PASS with 148 passed, 0 failed,
0 skipped, test-process exit 0, and `leaseReleased=true`. Receipt SHA-256:
`26BBF1C9FF7BAF1098E213074E2CE33B67E1BC0AD3DD8966997BED71A73465CF`. TRX SHA-256:
`CB392FCDE6D968E66CD1449C30B9AFD7519E602178F06CEE33BEA38023BC08C1`.

## Full gate

The unchanged code candidate also passed normal acceptance attempt
`edba7d70-0-20260811184009066-0264aa6a8abe4851b5148a3ebde2354e`; its result JSON SHA-256 is
`08601F46A6DC07ED05133A8DEFA310CC1B76D068229D50945EDE5D1FB47D39C4`. That attempt is evidence
for the code candidate only. This receipt-only documentation commit changes the candidate SHA,
so the conductor must run and bind a fresh final acceptance gate before landing.
