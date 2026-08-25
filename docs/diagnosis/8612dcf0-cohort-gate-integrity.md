# Cohort gate workspace integrity diagnosis

The retained cohort verdict at `.orchestrator/cohort-acceptance.db.artifacts/cohort-v2-c138e08f1166c7b973d281c70a933742572b957c173cafb868a7f6ab6b6f7905/de75647cd2bf354c6fc02ce2416adfa6573d7b9f7ba17981ff095db5ae1559e5/gate-verdict.json` records `Outcome: InfrastructureFailure`, `GateExitCode: 2`, and the five failed checks reported for all eight cohort attempts. Its SHA-referenced `trx-011.trx` contains this failure from `GoalWorktrees_git_metadata_access_marks_low_integrity_worker_non_committing`:

> System.UnauthorizedAccessException : Access to the path 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\c-230dea6c45fb\.scratch\mcg-wt\b9559b96ddb7463fafe2bf75031d7210' is denied.

The mechanism is Windows mandatory-integrity control, not cohort selection or the verifier's null goal id:

1. `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.AcceptanceCohorts.cs` created and merged the detached `c-<token>` worktree but previously returned it without recursively applying an inheritable Low integrity label.
2. `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs` supplies that worktree path as the cohort verifier repository root.
3. `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs` roots its mutable `.scratch/mcg-wt` fixtures from the compile-time source path. A cohort build therefore resolves those fixtures beneath `c-<token>`.
4. The gate test host is Low integrity while the fresh worktree remains Medium, so Windows denies the fixture directory creation. The failing partitions track tests that use this source-root `.scratch` location. `GoalWorktreeAcceptanceContentionTests`, which uses `Path.GetTempPath()` instead, is the negative control and does not show the signature.

Ordinary acceptance behavior is unchanged. It continues to use the existing goal worktree in `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs`; writable dispatch preparation in `src/Mcg.AgentOrchestrator.Infrastructure/Processes/DispatchProcessHost.cs` already recursively labels that worktree Low and restores the worktree boundary, linked `.git` file, and Git common directory to Medium.

The signature is not inherently cohort-only. `docs/acceptance-gate-flake-inventory.md` records the same `.scratch\mcg-wt` access denial for two single-goal worktrees that had not received a writable Low-integrity dispatch. Cohorts were deterministic because their newly materialized worktrees were never prepared.

The fix prepares disposable cohort, attribution, and merge-train worktrees only after all Medium-integrity merges or rebases finish: recursively label the workspace Low, then restore the shared workspace boundary and Git metadata to Medium. If a disposable test repository already lives in an inheritable Low grove, preparation is a true no-op; this avoids asking the Low test host to raise fixture labels, while the production Medium grove still takes the full preparation path. A preparation failure fails closed; cohort creation reports the existing typed `WorkspaceFailure` and removes the disposable worktree. Selection, eligibility, the acceptance manifest, ordinary acceptance, and the gate-verdict schema are untouched. This removes the deterministic MIC infrastructure failure but does not guarantee a green gate in the presence of unrelated known flakes.
