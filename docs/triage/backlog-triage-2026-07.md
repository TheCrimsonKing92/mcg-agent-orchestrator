# Backlog Triage - 2026-07

Scope: read-only sweep of the canonical root `.orchestrator/backlog.db` plus read-only goal-state/git/source evidence. The worktree-local backlog DB was empty, so the populated root store was used for the actual backlog. Verdicts are conservative: rows are closed only when source-linked/landed/duplicate evidence is explicit; otherwise they remain LIVE or STALE.

## Summary

- Open rows swept: 204.
- Verdict counts: LIVE=180; SHIPPED=11; SUPERSEDED=8; STALE=5.
- Root CLI evidence: `backlog-list --limit 250` reported 204 open rows; `backlog-triage --limit 80` reported `open=204 done=277 active-linked=4`.
- Worktree CLI evidence: `backlog-list --status Open --limit 250` reported no rows because the worktree-local `.orchestrator/backlog.db` is empty.
- Operator hold: `144b11b5` recurring-goal primitive is OPERATOR-HELD and intentionally excluded from the ranking.

## Active Board And Region Fences

- ca7144e3 Active: goal timing aggregate rollup; likely touches reports/CLI timing only.
- 1c379ae2 Active: this backlog triage artifact; docs/triage only.
- 5f4bb7de Active: acceptance gate races own verification children; overlaps slot/gate lock family.
- f98a8420 Active: bounded/evidence-rich build-artifact lock-holder identification; overlaps lock attribution.
- a5340f2b Active: handle64 probe hang split B; overlaps lock attribution.
- e73887c2 Active: reviewer needs-work auto-retry; overlaps task outcome/retry policy.
- 14cb5f8b Active: sandbox-prep receipt fast path; overlaps dispatch sandbox prep.
- 483594c1 Active: slot-lease/fixture lock-classification split A; overlaps gate slot/fixture classification.

Parked source-linked backlog rows already have goals and should not be re-intaken without explicit operator restart: `73a71f4a` -> goal `26ea3c37`, `dd7ce2e2` -> `3a53b207`, `1e84f1c5` -> `d758f734`, `a8c1c7eb` -> `4d5befdc`.

## Top 15 LIVE Intake Ranking

| Rank | Item | Verdict | Value | Cost | Readiness | Reason |
|---:|---|---|---|---|---|---|
| 1 | `3cdca621` | LIVE | Very high | High | Medium | Goal-scoped transactions / shorten tick write locks; unlocks operator recovery and parallel gate writes. Receipt: 2026-07-12 multi-hour tick locked out cancel-dispatch. Fence: overlaps none of active split goals except broad conductor state; schedule after current lock-attribution goals if they still write ConductorBatchLoop. |
| 2 | `ffa3bfc4` | LIVE | Very high | Medium | Medium | Worker dispatch cannot read cert store, forcing HTTPS fallback and stream disconnects; directly improves long codex survival. Fence: wait for 14cb5f8b or coordinate sandbox/dispatch-context edits. |
| 3 | `e0f1cbdc` | LIVE | High | Low | High | ProviderConnectivity should bounded-auto-retry instead of hard-failing. Small policy mapping, strong autonomy value. Fence: low overlap with e738 unless both edit task outcome retry policy. |
| 4 | `8db17ab6` | LIVE | High | Low | High | Provider classifier false auth from source-code echoes can pause providers incorrectly. Small classifier scoping fix with clear regression. |
| 5 | `789e8599` | LIVE | High | Medium | High | Auto-attach verification evidence to Reviewer context; removes repeated 35-40 min evidence relay rounds. Likely WorkerArtifactWriter/task-brief scope, not active lock/sandbox scope. |
| 6 | `e2a1fc50` | LIVE | High | Medium | Medium | Retry feedback must reach downstream sibling roles; prevents stale tester/reviewer expectations. Fence: coordinate with active e738 reviewer-feedback auto-retry. |
| 7 | `107b7e93` | LIVE | High | Medium | Medium | Structured WORKER_RESULT classification consolidates false-fail family; strong stability/autonomy impact. Fence: may overlap worker result parser/classifier. |
| 8 | `60c9567a` | LIVE | High | Medium | Medium | Rate-limit classifications need positive evidence and stderr-growth liveness; reduces fabricated subscription cap churn. |
| 9 | `5daadb79` | LIVE | Very high | High | Low | Parallel gate throughput with serialized landing barrier; huge throughput payoff but depends on current gate/lock split and candidate-key correctness. |
| 10 | `4a50857f` | LIVE | High | Medium | Medium | Shared base-build layer for gates is independently useful before full parallel gates; coordinate with slot artifact isolation. |
| 11 | `a9d5e81d` | LIVE | High | Medium | Medium | Diff-scoped suite selection reduces every gate cost for narrow diffs; likely independent of active lock attribution. |
| 12 | `5a500efc` | LIVE | Medium | Low | High | Parallel-safety wave 1 is read-only audit and can run concurrently; prepares safe test parallelism and scope fences. |
| 13 | `d89371a6` | LIVE | High | High | Medium | Dependency-deferred intake / stacked-goal collaboration removes operator-held staged briefs; bigger design but directly addresses wave selection and region fences. |
| 14 | `a4a8df24` | LIVE | Medium | Low | High | Shadow-loop smoke for conductor-touching goals gives cheap confidence before/after conductor changes. |
| 15 | `ba6a5ff3` | LIVE | Medium | Low | High | Goal brief amendment verb avoids abandon/recreate on superseded criteria; contained CLI/state slice. |

Held, not ranked: `144b11b5` recurring-goal primitive remains OPERATOR-HELD per brief.

## Proposed Next Two Waves

Wave 1 - reliability and false-failure reducers with mostly disjoint files:

- `e0f1cbdc` ProviderConnectivity bounded auto-retry.
- `8db17ab6` provider classifier must ignore source-code echoes.
- `789e8599` attach verification evidence to Reviewer dispatch context.
- `5a500efc` parallel-safety wave 1 read-only audit.
- `a4a8df24` shadow-loop smoke for conductor-touching goals.

Wave 2 - throughput spine after current lock/sandbox goals land or file scopes are fenced:

- `3cdca621` goal-scoped transactions / no multi-hour tick write lock.
- `4a50857f` shared base-build layer for gates.
- `a9d5e81d` diff-scoped suite selection for gates.
- `5daadb79` parallel gate throughput with serialized landing barrier.
- `d89371a6` dependency-deferred intake / stacked-goal collaboration.

Do not intake `25f00464`, `76f700b7`, `92ee7552`, `9de81088`, or `b75dcba5` as standalone goals: current active goals already own those file regions or split scopes.

## Operator Close Commands

Run only after operator review; backlog mutation is intentionally not done by this task.

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 074b3174 "Triaged 2026-07: shipped/verified on main. Evidence: goal/2f787d8e integrated by 16b5ba8d; reassign-agent and stale-assigned fallback code in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Tasks.cs and src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 464b4b27 "Triaged 2026-07: superseded/absorbed. Evidence: exact Done backlog duplicate 48af52d9; WaitingForHuman status/resume support exists in GoalLifecycle, StatusProjector, and task recovery code."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close baa0fbdf "Triaged 2026-07: shipped/verified on main. Evidence: goal/2b4db724 integrated by 34f16ca2; CleanedUp/Landed projection code in src/Mcg.AgentOrchestrator.Core/Application/GoalLifecycle.cs and CLI/dashboard rendering tests."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 5d6adcf5 "Triaged 2026-07: shipped/verified on main. Evidence: title-matched landed commits 515f0986 and f629b2ee; busy_timeout and locked-DB handling in src/Mcg.AgentOrchestrator.Infrastructure/Persistence/SqliteOrchestratorStateRepository.cs and src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 6a23a33b "Triaged 2026-07: shipped/verified on main. Evidence: goal/8a7933b4 merged by d313a292; cancellation kill/diagnostic path present in src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close da06c940 "Triaged 2026-07: shipped/verified on main. Evidence: goal/705803f0 integrated by ef3ed654; landing records and deferred cleanup path in src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor.cs and GoalOperationJournal.cs."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close b940ad5c "Triaged 2026-07: shipped/verified on main. Evidence: goal/b4d408ed completed this design decision; goal-mark-landed deferred cleanup code and tests exist in CliCommandHandlers.Goals.cs and PersistentRunner/GoalWorktree cleanup tests."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 201b9b3c "Triaged 2026-07: superseded/absorbed. Evidence: partially absorbed by completed goal cee1d2da and now a dependency of parallel-gate design; keep future work under 5daadb79/4a50857/a9d5e81 rather than this stale umbrella."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 92ee7552 "Triaged 2026-07: superseded/absorbed. Evidence: split across completed goal a356e0b1 plus active goals f98a8420 and a5340f2b; the original probe-hang item is no longer the intake unit."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 9de81088 "Triaged 2026-07: superseded/absorbed. Evidence: absorbed by active goal 483594c1 (slot-lease/fixture lock-classification machinery from abandoned 380a02c2)."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 4de9881c "Triaged 2026-07: shipped/verified on main. Evidence: goal/496db27e completed first-class gate/acceptance progress observability; current GoalAcceptanceVerifier heartbeat/progress code is present."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close b75dcba5 "Triaged 2026-07: superseded/absorbed. Evidence: absorbed by active goal 5f4bb7de plus active split goals 483594c1/f98a8420/a5340f2b; the remaining lock family is now decomposed."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 25f00464 "Triaged 2026-07: superseded/absorbed. Evidence: absorbed by active goal 14cb5f8b (sandbox-prep receipt fast path); keep closed to avoid double-intake once operator accepts the in-flight goal."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 07f61c93 "Triaged 2026-07: shipped/verified on main. Evidence: goal/c0624af9 completed this item; task briefs now surface missing executed test evidence in src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs with tests in TaskBriefTests."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 76f700b7 "Triaged 2026-07: superseded/absorbed. Evidence: absorbed by active goal e73887c2 (reviewer needs-work auto-retry); same task-outcome region."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close c31f7ca5 "Triaged 2026-07: shipped/verified on main. Evidence: goal/cee1d2da explicitly names backlog c31f7ca5 and integrated by 73482780; acceptance-slot starvation fix landed with current HEAD."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 61ca9c5d "Triaged 2026-07: shipped/verified on main. Evidence: goal/b9bbe713 explicitly names backlog 61ca9c5d and integrated by c060ec08; handoff slow-boot/retry/output coverage in ConductorLoopHandoff and ConductorBatchLoop tests."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 88e2fd9e "Triaged 2026-07: superseded/absorbed. Evidence: mentioned in completed goal cee1d2da scope; pre-landing rebase/merge fallback should be reopened only if a fresh receipt appears."
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 backlog-close 9cc73659 "Triaged 2026-07: shipped/verified on main. Evidence: goal/2aa54c1b completed this item; scripts/Backup-OrchestratorState.ps1 and docs/operator-runbook.md backup/restore section exist."
```

## Classification Table

| ID | Title stub | Verdict | Evidence pointer |
|---|---|---|---|
| `a-princi` | A principled failure taxonomy that drives auto-recovery and routing | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `autonomo` | Autonomous conductor — FEATURE-COMPLETE; refinements remain for true unattended operation | STALE | umbrella says FEATURE-COMPLETE; use concrete descendant items for intake rather than the umbrella. |
| `best-of-` | Best-of-N on Ideation (parallel-model survey #2) | STALE | survey/ideation item has completed descendants; no current receipt makes it competitive with conductor reliability work. |
| `continuo` | Continuous backlog-drain spine (3 coupled items — unlock the feature-complete conductor to self-... | STALE | umbrella decomposed into many completed concrete goals plus remaining live throughput items; no single intake unit remains. |
| `decision` | Decision record (durable context, not work items) | STALE | explicit durable-context note, repeatedly referenced by completed goals; not an intakeable work item. |
| `deepen-a` | Deepen acceptance verification beyond "tests pass" (semantic acceptance + prompt-injection gate) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `promote-` | Promote deterministic workflow brokers into executable steps | STALE | partly realized in current worker context artifacts/workflow brokers; future work should be filed as executable-step slices. |
| `treat-wo` | Treat worker brief/context engineering as a first-class, measured discipline | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `unify-th` | Unify the Director interface (what auto-lands vs escalates; curation, ideation, pager as one sur... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f7acd277` | Workspace consolidation must migrate config, not just goals | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `149a05c5` | Validate SDK-harness subscription billing before building it | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `dfbdb423` | Dedupe judge + worker CLI process runners into one hardened helper | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c66086e4` | Critically audit and prune the CLI verb surface | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `79803e72` | Footgun: editing the launcher then accepting via it corrupts the running cmd | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c1a796d6` | Verify the brief's own ## Acceptance criteria (advisory->retry->gate) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9bc2a261` | Autonomy/exposure/control program: Director + deterministic collaboration spine (Phases 0-4) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f68df45e` | Phase 5: decorrelate + ground the multi-role pipeline (cross-family roles on RefinedSpec, agent ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `64750e9d` | NORTH STAR: work the human-invoked LLM driver out of the job — orchestrator self-manages from Di... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `69fea490` | Resolve orchestrator repo root from config, not by discovering a hardcoded .sln | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `e7be4889` | Give host-integration tests a dedicated lane instead of excluding from the gate | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `bfa63289` | Make worker write-confinement cross-platform via an IWorkerSandbox abstraction (Windows MIC + Li... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b6e58587` | Harden worker-sandbox tests to assert out-of-worktree writes are DENIED (not just process-exit) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `bc6c505f` | Refine sandbox-blocked recovery to help the worktree finish and advance the pipeline (Tester/Rev... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b1b7b2a7` | Declare goal/backlog dependencies at filing time and enforce ordering in the conductor (extend g... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `e021eee8` | Auto-resolve escalations on out-of-band resolution (goal landing + CLI) so Discord clears them a... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `cf00232e` | Add a 'Request More Information' button to truncated attention messages that replies in-thread w... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `65787b94` | Clarification attention messages duplicate the question (subject+body) and repeat the goal objec... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a03a55f0` | Route small, well-contained Developer tasks to gpt-5.3-codex-spark to add throughput across a se... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `ec331244` | Concurrent goal-lane commands serialize through per-command whole-kernel load/save; expose a thi... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `81db7e68` | Local-inference engine + concurrency: bound the local per-file judge fan-out with a SemaphoreSli... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c5e797dc` | Evaluate a smaller non-thinking judge model (qwen2.5-coder:7b is installed, or a 3-4B instruct) ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `0b5c3e17` | conduct --loop crashes (exit 1) on a goal's acceptance build IO error (App.dll lock) instead of ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `71c7cbc4` | Projects/workspaces: partition goals (backlog, worktrees, state, insights) by project so indepen... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `3f7f95f7` | Per-project evidence-bound attempt/insight ledger (Arbor HTR-lite): learn failure->recovery acro... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `52777a8b` | abandon-goal must be runnable even when a worker is live in the goal's worktree: reap the runnin... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b13ea260` | Dispatch observability: CPU-time worker-tree signal in heartbeat + watchdog (fix the byte-growth... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `16ea5b17` | Architecture redesign roadmap (Opus+codex validated): (A) goal-scoped transactions FIRST, then (... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `e833ab90` | Live warm worker sessions: keep a role's worker process ALIVE and context-hot through its consum... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `074b3174` | Agent-pinning footgun: a role's provider can't be rebalanced mid-flight without orphaning in-fli... | SHIPPED | goal/2f787d8e integrated by 16b5ba8d; reassign-agent and stale-assigned fallback code in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Tasks.cs and src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs. |
| `57c7f1a7` | ANCHOR - build/slot lock lifecycle hardening: consolidate 606910bb (residual obj/Core.dll lock) ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `cb800d76` | stable-slot-dotnet silently fails (exit 1, no trx, no output) when a build-server lock blocks th... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `46c84dfb` | First-class amend-goal-brief and re-refine-in-place (avoid abandon+recreate for spec revision) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b6bec53c` | Closed-loop self-correction + layered resilience - adjusted-strategy retry, fallback chains, cir... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `cf3fc35c` | Agent-as-Judge: executable semantic verification in the worktree + judge-bias guards (make the s... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `e315c062` | Cascade model routing with the acceptance gate as the escalation trigger (start cheap, escalate ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `6a43ee32` | Self-evolving pipeline: experiential ledger feeding the refiner + optimize role prompts/model-as... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `877c430d` | God-class decomposition initiative: behavior-preserving extraction, one class per disjoint lane,... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `d15bb242` | Detect shared-service ripple earlier: reverse-dependency test-impact selection (run dependent in... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `73a71f4a` | Reconcile-wedge gap: exited worker at a role boundary leaves task unreconciled (no ready batch f... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `4cb2d9ff` | dashboard live monitor stale indicator should treat conductor.tick or keepalive as liveness | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `cc58bed1` | Add explicit-cancel/reap regression coverage for ConductorBatchLoop | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c5da80d6` | Scope retry-feedback prompt blocks to relevant upstream tasks | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b87dfad8` | Claude low integrity sandbox cannot see subscription credentials | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `68cb4238` | Sandbox cleanup must handle long paths and noisy ignored trees | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f53fc304` | Developer retry should not accept no change on confirmed blocker | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `1be88234` | Agent-pinning readiness blocks stale AssignedAgentId before dispatcher fallback | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c7414620` | Dashboard run path uses strict stale AssignedAgentId resolver | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `14c895c9` | Promote hidden start blockers into first-class conductor output | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `7ef8c837` | Make acceptance broad dotnet gate use reliable project-level isolated tests | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `3bf52b03` | Fix ProcessSpawnGuard under low-integrity worker sandbox | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `6e6ed23b` | Add focused regression for Start-OrchestratorCommand double-dash preservation | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `464b4b27` | Make WaitingForHuman typed and resumable | SUPERSEDED | exact Done backlog duplicate 48af52d9; WaitingForHuman status/resume support exists in GoalLifecycle, StatusProjector, and task recovery code. |
| `ab59850c` | Acceptance verifier must kill its owned dotnet test process tree on cancellation | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `baa0fbdf` | Goal status commands should surface CleanedUp or Landed terminal state | SHIPPED | goal/2b4db724 integrated by 34f16ca2; CleanedUp/Landed projection code in src/Mcg.AgentOrchestrator.Core/Application/GoalLifecycle.cs and CLI/dashboard rendering tests. |
| `497dc144` | Retire obsolete low-integrity smoke | SHIPPED | Obsolete manual smoke retired in favor of deterministic dispatch-host coverage in `DispatchProcessHostTests.LowIntegritySetupKeepsLinkedWorktreeGitFileMedium`. |
| `9450b849` | Do not mark verification roles complete when worker launch fails before running | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `75e6ec97` | Flaky WorkerDispatchTests nonzero-exit artifact status race can block full Infrastructure accept... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `8d7e91d0` | Spec-refiner clarification gate re-asks resolved questions and blocks dispatch | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `dd7ce2e2` | Split broad Infrastructure test classes to unlock sub-3-minute acceptance | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `599cef9c` | Verification-role WORKER_RESULT blockers must fail the task | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a2d9ba8c` | Enable no-build timing passes in Invoke-IsolatedDotnet | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `19350693` | Acceptance verifier should build once and reuse no-build test execution | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `17f5776e` | Acceptance verifier should shard no-build Infrastructure checks after one build | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9fc70e5e` | Acceptance command can hang after accepted output without merging | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `4d7cec46` | Invoke-IsolatedDotnet can abort VSTest with OpenProcess access denied | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9a2de4cd` | Keep operator runbook and skills aligned with conductor-first workflow | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `6a5f3541` | backlog-intake can hang and leave an app-host process | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `345175c8` | Reduce remaining Infrastructure acceptance/worktree test bottlenecks | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9f1f9c55` | Workspace rebase conflict escalations need a first-class operator repair flow | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `ef458215` | Failed-worker dirty worktrees should not block retry dispatch | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `02d381a5` | Low-IL sandbox prep should avoid recursive ACL cost | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b0b60645` | conduct --loop should exclude parked and waiting goals by default | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `be68e5eb` | Backlog needs update, supersede, and duplicate-link commands | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `451ed42e` | Dashboard and API batch actions should return ready-blocked diagnostics | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `ee4bebc1` | Worktree cleanup must never flip root core.bare | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `8101f6d2` | Daemon conduct should honor or reject --poll-seconds explicitly | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `26917c09` | Exit-0 dirty worker completion without commit must reconcile deterministically | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `ef410e16` | Prevent duplicate same-task dispatches during refresh/conduct races | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `1e84f1c5` | Invoke-IsolatedDotnet temp root must not dirty the repo | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `5d6adcf5` | conduct on completed goal can fail with SQLite busy while no holder is visible | SHIPPED | title-matched landed commits 515f0986 and f629b2ee; busy_timeout and locked-DB handling in src/Mcg.AgentOrchestrator.Infrastructure/Persistence/SqliteOrchestratorStateRepository.cs and src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs. |
| `6a23a33b` | Acceptance runner cancels long Infrastructure verification without diagnostics | SHIPPED | goal/8a7933b4 merged by d313a292; cancellation kill/diagnostic path present in src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs. |
| `a8c1c7eb` | Fix DispatchProcessHost low-integrity linked-worktree git-file setup failure | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a3c188b6` | Dogfood log renderer must not claim acceptance passed after manual or failed acceptance landing | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `41f37a93` | backlog-intake emits stale generic target scopes into goal briefs | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `91353748` | start-subscription-ready should expose or honor high-risk ownership approval | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c3c6e568` | Conductor integration should preserve current main as first parent | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `743cd04e` | Conductor should automatically resume budget-exhausted cleanup | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f589c814` | Verification selector must use branch diff, not dirty tree only | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `990d0e10` | Isolated dotnet no-build runs must preserve prior build artifacts | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `6cbd7881` | Conductor and operator checks must prune or ignore stale nested git worktree registrations | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b91b09dd` | goals subscribe --goal-prefix must emit only the requested goal and honor event filters | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `da06c940` | HIGH: Acceptance must not mark goal Completed before integration and cleanup are durable | SHIPPED | goal/705803f0 integrated by ef3ed654; landing records and deferred cleanup path in src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor.cs and GoalOperationJournal.cs. |
| `3c585191` | HIGH: cancel-dispatch should return promptly and reconcile task state | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `818d9b33` | start-dispatch must preserve subscription reasoning overrides | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `68cc3843` | refresh-dispatch false usage-limit retry from worker context text | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `5f71c0a6` | Ghost retirement write never fires for branchless goals: retire at the tick pre-landing detectio... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `5a500efc` | Parallel-safety wave 1: read-only audit of shared-state hazards across all test projects with wa... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `39a8a58e` | Parallel-safety wave 2.1: console capture consolidation - eliminate direct Console.SetOut/SetErr... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `d936e31d` | State-effect deliverables are structurally unverifiable and unwritable by workers: introduce dif... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b686e4ea` | Re-intake: acceptance/landing must not hold the state.db single-writer lock across the multi-min... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `1b5d0294` | Re-evaluate branch goal/0cba7696 (single commit ea8d650b, dispatch-command refactor territory, 5... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a2d5b186` | Monitor-child tail.exe zombies: ~75 tail.exe processes spanning 2 days hold .orchestrator/logs h... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `59698301` | Tick idempotence: eliminate run-scoped loop state so a fresh run's tick 1 and a long run's tick ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `170853de` | Acceptance-failure task auto-retry did not fire for 1d2223c2 (2026-07-08): gate failed with 3 re... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `cd753fd9` | Event-driven role handoffs: the loop polls, so every handoff costs up to a full tick (measured 2... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c7b1bd4a` | Role-round model tuning: read-only rounds (Planner/Researcher/Reviewer) run 4-8 min dominated by... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `51022b21` | Loop self-stop on all-done-or-escalated strands routed retries: batch-14b stopped at tick 9 with... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b289e86e` | PHASE_TIMING dispatch-prep receipts misattribute task/role - TWO confirmed instances 2026-07-08:... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `c3af9f36` | Per-task resource accounting + memory-aware slot scaling (Miles 2026-07-08; prerequisite for add... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `fd9eb50f` | Quiet-board parallel suite re-measure (follow-up to wave-3 flip 694f8eae): the 3x safety runs we... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `fdb75163` | Gates off the tick critical path - acceptance as a background dispatch (Miles 2026-07-08 'exactl... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9ccfbd4c` | FOURTH live exit-0 misclassification variant post-29edee5c/13902847 (2026-07-08 18:48, goal 54c8... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `614f3e51` | Auto-route Reviewer blockers as retry feedback (sibling of 170853de): every Reviewer-blocker esc... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `bb6f496a` | Long-running branch worker experience (three manual conflict routings on 2026-07-08 as main land... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `1c8b9d90` | Researcher role must check vendor/upstream capabilities before the system builds around a limita... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `88f8cc61` | SDK/thin-harness lane prerequisites (distilled from docs/sdk-harness-feasibility.md, deleted 202... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f22bc596` | EPIC primitive: plan-once, fan-out micro-slices to small implementing models (Miles 2026-07-08 -... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f5d3f7cc` | FIX-FORWARD 3 for prep receipts + loop crash on cleanup (2026-07-09 05:11: loop DIED unhandled a... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b940ad5c` | DESIGN DECISION (Miles 2026-07-09): cleanup must never be on the landing critical path - schedul... | SHIPPED | goal/b4d408ed completed this design decision; goal-mark-landed deferred cleanup code and tests exist in CliCommandHandlers.Goals.cs and PersistentRunner/GoalWorktree cleanup tests. |
| `6afca322` | Start-OrchestratorCommand.ps1 / conduct --loop launch should detect a pre-existing .conduct-stop... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `74ea51fd` | Scope gate at commit-on-behalf: the orchestrator commits the worker's ENTIRE worktree, so any ou... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `8138e543` | Adaptive reasoning escalation for dispatches: every round 2026-07-09 ran gpt-5.5 complexity=Simp... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `283e8716` | Auto-attach acceptance-failure evidence to re-dispatch briefs: when a goal's latest acceptance f... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `07f9bd2b` | Watch-mode disposition snapshot does a per-task process-table scan across ALL goals every tick -... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `8ab65ab8` | Test-fixture child processes survive testhost crashes and poison later gate runs: reaping/worker... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `3ffdb8b1` | Acceptance state guard trips on the gate's own auto-verify write: gate 10 for e93b30f6 (log oper... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9af1d1cc` | Advisory semantic-acceptance judge panel has been 100 percent broken since 2026-06-21 yet still ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b2920a73` | Reviewer briefs must mandate merge-base (three-dot) diffs: after 31ad2da6 landed on main, THREE ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `1a7d200d` | TerminalGoalSweep startup cost: durable dispositions + batched git snapshot (supersedes 33b15b65... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `60d45cad` | Worker CLI non-launch root causes: instrument sandbox prep phases + bound them with typed outcom... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f2045f29` | Gortex (github.com/zzet/gortex) pilot: graph-backed dependent-test advisor - SPIKE-GATED, ADDITI... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a05af4e1` | Superpowers skill transplant (obra/superpowers, MIT; two-expert analysis 2026-07-10, verdict ADO... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `cca13692` | Methodology steals from superpowers analysis (2026-07-10, both experts): two orchestrator-side p... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `4aabf2b4` | Add --feedback-file to the retry command (mirrors --brief-file which solved the identical length... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `18090c97` | Partition and gate test filters key off class-name substrings (FullyQualifiedName~GoalWorktreeTe... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `8835ad6b` | Two flaky Infrastructure tests are taxing every acceptance gate ~15 min per occurrence (receipts... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b8ea37d7` | Conduct loop retries tasks when acceptance PASSES with only an ADVISORY criterion unmet: goal 57... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `683a3065` | Test tamper guard false-positives on test-class splits: AnalyzeTestFileDiff (GoalAcceptanceVerif... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `417e4175` | A retried Developer round that produces NO commit is classified VerifiedSuccess and silently bur... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `4d63661a` | IsSubscriptionStartCandidate (GoalManagementCommandService.Dispatches.cs:350) silently filters a... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b4fc1d66` | CORRECTION to 4d63661a: the 5bb933fd empty-batch stall root cause was the DIRTY-WORKTREE dispatc... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b6685ad0` | Classifier rule succeeded-worker-result-failing-tests false-fails read-only roles on the word 'f... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `107b7e93` | Classifier verdicts must derive from STRUCTURED WORKER_RESULT fields and settled dispatch state,... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `839b043d` | Review-saga circuit breaker: goal 5bb933fd reached THIRTEEN dev/review rounds (2026-07-10/11) wi... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `571c5f5c` | Conductor events need a STABLE cross-batch stream: operator monitors tail per-batch logs (operat... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `201b9b3c` | Acceptance verdicts must be keyed to the integration candidate and every attempt must journal a ... | SUPERSEDED | partially absorbed by completed goal cee1d2da and now a dependency of parallel-gate design; keep future work under 5daadb79/4a50857/a9d5e81 rather than this stale umbrella. |
| `346098a2` | Terminal sweep retire dispositions are journaled but never consulted, producing eternal ghost os... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `94fd3128` | retry on a Verified goal resets tasks to Assigned but leaves goal status Verified, so the termin... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `ef7623dc` | Reconcile re-completes a RETRIED task from stale pre-retry dispatch records, silently swallowing... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `57f6eebe` | Analysis-grade ROUND LEDGER: extend the existing dispatch-outcome store (model-outcomes already ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `7a015307` | Round VALUE accounting on top of the round ledger 57f6eebe (Miles 2026-07-11: 'not all dispatche... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `60c9567a` | Fabricated provider-RateLimit classifications and a suspected stall-watchdog kill of healthy buf... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `38aa99c9` | CORRECTION+CONFIRMATION to 60c9567a after disproof pass: (CONFIRMED at source) DispatchFailureCl... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a7966e90` | Backlog items cannot be annotated after creation: no backlog-note/update verb, so root-cause rec... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `92ee7552` | Acceptance gate lock-holder probe hangs the whole conduct loop: BuildLockBlocked classification ... | SUPERSEDED | split across completed goal a356e0b1 plus active goals f98a8420 and a5340f2b; the original probe-hang item is no longer the intake unit. |
| `9de81088` | Fixture-root lock registration is process-local, so the conduct host misclassifies its own gate'... | SUPERSEDED | absorbed by active goal 483594c1 (slot-lease/fixture lock-classification machinery from abandoned 380a02c2). |
| `d89371a6` | Same-scope goal concurrency via structured cross-goal collaboration (Miles idea 2026-07-12), pha... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9e05e372` | Dependency-state administration suite for staged goals (follow-up to d89371a6 Phase 1; Miles 202... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `4de9881c` | Gate/acceptance runs need first-class progress observability - no process archaeology (Miles 202... | SHIPPED | goal/496db27e completed first-class gate/acceptance progress observability; current GoalAcceptanceVerifier heartbeat/progress code is present. |
| `202468a4` | Auth failures are misclassified as recoverable rate limits, so dead-auth dispatches churn retrie... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `3cdca621` | Conduct tick holds the state.db write lock for the ENTIRE tick - hours during gate grinds - lock... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `2c5e787d` | Conductor dispatched a downstream Reviewer while the upstream Developer retry was still RUNNING,... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `bc6f6e7d` | Cancelled/abandoned goals get resurrected by sweep normalization and escalate forever - a new gh... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `5daadb79` | Parallel gate throughput with preserved safety (Miles 2026-07-12): serial gates cost 20-35 min e... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `b75dcba5` | Three-probe-blind slot-artifact lock: the remaining lock mystery, now sharply characterized by a... | SUPERSEDED | absorbed by active goal 5f4bb7de plus active split goals 483594c1/f98a8420/a5340f2b; the remaining lock family is now decomposed. |
| `789e8599` | Auto-attach verification evidence to Reviewer dispatch context: read-only reviewers cannot run t... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `25f00464` | Sandbox-prep receipt must be a fast path: every worker dispatch pays ~2 minutes of sandbox prep ... | SUPERSEDED | absorbed by active goal 14cb5f8b (sandbox-prep receipt fast path); keep closed to avoid double-intake once operator accepts the in-flight goal. |
| `4a50857f` | Shared base-build layer for gates: N gates against the same main sha should not each rebuild the... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a9d5e81d` | Diff-scoped suite selection for gates: full-suite re-runs on every acceptance are unnecessary fo... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `78005841` | All-tasks-Completed goal never auto-advanced: 687c12d0 sat status=Active with 5/5 tasks Complete... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `9e80b60e` | Triage ghost goal 95c477e8's unmerged branch: it holds 5 REAL worker commits never integrated (t... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `df8311e1` | First live loop self-renewal handoff FAILED: successor died before first output. RECEIPT 2026-07... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `e0f1cbdc` | ProviderConnectivity verdicts must auto-retry with bounded backoff instead of hard-failing the t... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `e2a1fc50` | Retry feedback reaches only the retried role - sibling roles verify against stale expectations. ... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `07f61c93` | Developer+Reviewer pipelines can reach Verified with ZERO executed tests: workers policy-defer d... | SHIPPED | goal/c0624af9 completed this item; task briefs now surface missing executed test evidence in src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs with tests in TaskBriefTests. |
| `c553b10e` | Sweep finalization races operator merge-then-revert: a TEMPORARY merge visible at sweep time get... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `eab19492` | Handoff successor still dies - NEW evidence narrows to two candidates (follow-up to landed 9deb2... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `8db17ab6` | Provider-failure classifier matches signatures inside SOURCE CODE the worker reads - false provi... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `ffa3bfc4` | Worker dispatch contexts cannot read the user certificate store, forcing ALL codex sessions onto... | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `76f700b7` | Auto-retry upstream tasks from reviewer needs-work verdicts with the reviewer findings as feedba... | SUPERSEDED | absorbed by active goal e73887c2 (reviewer needs-work auto-retry); same task-outcome region. |
| `c31f7ca5` | Acceptance gates starve behind worker self-verify slot locks | SHIPPED | goal/cee1d2da explicitly names backlog c31f7ca5 and integrated by 73482780; acceptance-slot starvation fix landed with current HEAD. |
| `a4e6618e` | Worker briefs default to persist-as-you-go committed artifacts | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `f7f1e4fd` | Auto-WIP-commit partial work on provider-failure dispatches | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `61ca9c5d` | Handoff verification: false-fail on slow boot, duplicate spawn, lost successor console output | SHIPPED | goal/b9bbe713 explicitly names backlog 61ca9c5d and integrated by c060ec08; handoff slow-boot/retry/output coverage in ConductorLoopHandoff and ConductorBatchLoop tests. |
| `575fd02e` | add-task needs a one-shot goal-prefix form (REPL-only today) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `0fdef5e9` | Agent settings out of JSON into durable storage with journaled changes | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `88e2fd9e` | Pre-landing rebase fails where merge is clean - squash/merge fallback before escalating | SUPERSEDED | mentioned in completed goal cee1d2da scope; pre-landing rebase/merge fallback should be reopened only if a fresh receipt appears. |
| `9cc73659` | Durable backup for orchestrator state (.orchestrator snapshot + restore runbook) | SHIPPED | goal/2aa54c1b completed this item; scripts/Backup-OrchestratorState.ps1 and docs/operator-runbook.md backup/restore section exist. |
| `e94a8b93` | Scheduled quiet-board suite run with journaled main-health verdict | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a4a8df24` | Shadow-loop smoke for conductor-touching goals | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `a32001ff` | Cost telemetry into dispatch decisions (tokens per round/goal/effort) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `144b11b5` | Recurring-goal primitive (cadenced brief templates intaken by the conductor) | LIVE | LIVE but OPERATOR-HELD by brief; do not rank for intake. |
| `79ab3324` | Workers rebase against pinned dispatch-time main sha, never live repo-root state | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |
| `ba6a5ff3` | Goal brief amendment verb (immutable briefs deadlock pipelines on superseded criteria) | LIVE | Open in canonical backlog; no explicit landed/source-linked/duplicate evidence found in this sweep, so keep live. |

## Verification

- Markdown generated under `docs/triage/backlog-triage-2026-07.md` only.
- No backlog-close, goal-state, dashboard API, or source mutation command was run.
- Data commands used: root `backlog-list`, root `backlog-triage`, SQLite read-only `select` against root backlog/state stores, targeted `git log`, and targeted `rg`.
