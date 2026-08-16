# Redundancy Audit

Date: 2026-07-22
Scope: production code under `src/` only. Test projects were not ranked with production candidates per the operator clarification; obvious test-only fat was checked opportunistically and none was promoted as a primary trim.

## Sweep Coverage

Production projects swept:

| Project | Files swept | Areas checked |
| --- | ---: | --- |
| `src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj` | 221 | CLI, dashboard API/rendering/hosting, orchestration, subscription planning, cost control, demo/prototype seeding |
| `src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj` | 74 | domain model, kernel/application partials, reports, classifier/reporting utilities |
| `src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj` | 103 | persistence, worker dispatch, processes, workspaces, providers, operator comms, verification |

Deliberately excluded from primary ranking: `tests/`, scripts, docs, generated `bin`/`obj`, `.scratch`, and prototype state. Tests were used as reference evidence but not ranked as production removals.

## Method

Scoring: `Impact 1-5 * Safety 1-5`, tie-break by higher Safety then lower blast radius. Only Safety >= 4 is labeled ready-to-execute.

Documented seams cleared before flagging candidates: AGENTS/CLAUDE counterpart contract; flat `Core`/`Infrastructure` namespace rule; conductor/dogfood state stores; deterministic gates and advisory semantic acceptance; worker process/job registration; subscription dispatch sandbox/permission placeholders; `TestRewriteRealWorkerCommandsVariable`; `TimeProvider`/clock/sleep test seams; `ModuleInitializer`/attribute discovery; DI registration; reflection/string-literal use; JSON/source-generator-style DTO use.

Tooling limitations: this was a grep/static-inspection pass, not a Roslyn call graph. DI/reflection was checked by name/string/registration scans (`AddSingleton`/`AddScoped`/`AddTransient`/`TryAdd`/`Register`, `Activator.CreateInstance`, `Type.GetType`, `Assembly.Load`, attributes), but public APIs may still have external consumers outside this repo.

## Ready-To-Execute Trim Slices

| Rank | Score | Candidate | Category | Est. LOC removable | Evidence / reference count | Seam clearance | Residual risk | Suggested action |
| ---: | ---: | --- | --- | ---: | --- | --- | --- | --- |
| 1 | 16 | `src/Mcg.AgentOrchestrator.Core/Reports/SemanticAcceptanceReceipt.cs:18` (`SemanticAcceptanceReceiptReader`) | dead code | 57 | `rg -n "SemanticAcceptanceReceiptReader"` returns only the class definition; `rg -n "ParseAll\\("` returns only `ParseAll` itself. The receipt records are used by `LoopHealthReport`, but the parser is not. `git blame` shows this block from 2026-06-16, outside the 30-day recent-code downgrade. | Not a documented seam; no DI/reflection/string registration hit for the reader name; not a `TimeProvider`, sleep, process, module initializer, or gate hook. | Public `Core` type could have an out-of-repo consumer; no in-repo evidence of one. | Delete `SemanticAcceptanceReceiptReader` and keep `SemanticAcceptanceReceipt` / `SemanticJudgeReceiptEntry`. |
| 2 | 12 | `src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs:2800` vs `src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs:1596` | duplicate logic | 53 | `ClassifyDispatch` is called once at `BackgroundDispatchRunner.cs:2772`; its `ContainsRateLimitSentinel` is a narrower duplicate of `DispatchFailureClassifier.IsRecoverableSubscriptionLimitText` (`usage limit` + `try again` / `purchase more credits`). The central classifier has broad app/core/infrastructure/test references. | Not a documented seam; local helper is private, not DI/reflection registered; dispatch classification seam is documented as a central positive-evidence classifier. | Diagnostic labels (`success`, `rate-limited`, `genuine-failure`) may be consumed by diagnostic readers, so preserve output strings or add an adapter. | Collapse diagnostic classification onto `DispatchFailureClassifier` or expose a small central provider-limit predicate; keep diagnostic label mapping as a wrapper. |
| 3 | 12 | `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs:267` | god-class repetition / duplicate control flow | 35 | Four branches repeat the same loop body: run check, OR `retried`, add result, stop on `ShouldStopAfterFailedCheck` (`GoalAcceptanceVerifier.cs:274`, `290`, `309`, `348`, `364`). | Not a documented seam; no DI/reflection; preserves acceptance gate behavior if extracted as a private helper in the same file. | Acceptance gate output order is sensitive; helper must preserve ordering and result synthesis around solution/deferred checks. | Add a private `RunChecksUntilStopAsync` helper and replace repeated loop bodies. |
| 4 | 12 | `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals.cs:778` and `:921` | god-class repetition / duplicate CLI setup | 55 | Conduct policy resolution is duplicated in loop and single-goal paths; `new ConductorDriver(...)`, `new BackgroundDispatchRunner()`, `ConductorBatchLoop` construction, tick callback, and refresh lambdas are also repeated across `--loop` and `--watch` paths (`:819`, `:834`, `:894`, `:898`, `:928`, `:947`, `:963`). | Not a documented seam; CLI command handling is not DI/reflection registered; `ConductorDriver` itself remains load-bearing. | CLI text and option error messages must remain byte-stable for command tests. | Extract local helpers for policy resolution, conductor driver creation, and batch-loop dependency construction. |
| 5 | 10 | `src/Mcg.AgentOrchestrator.Infrastructure.Providers/ModelProviders.cs:70`, `:136`, `:250` | duplicate logic | 18 | Three provider classes each define identical `RequireText`; three `JsonOptions()` wrappers return `SharedJson.Options`; `EnsureSuccess` is duplicated with only provider-name variation and already delegates to `ProviderHttpErrorFormatter`. | Not a documented seam; all helpers are private/static in one source file; no DI/reflection. | Low; stack traces or method names could change, but public provider behavior should not. | Replace per-class copies with shared file-local helpers accepting provider name where needed. |
| 6 | 8 | `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs:688` | god-class repetition / duplicate recovery branch | 25 | The preflight-flake branch and empty-output-flake branch both compute max attempts, apply `ComputeEmptyOutputBackoff`, call `_emptyOutputBackoffDelay`, write a retry note, call `_retryTask`, then `ExecuteDispatchAndStart` (`:710-739`, `:742-775`). | Not a documented seam; no DI/reflection; `Thread.Sleep` injection seam remains `_emptyOutputBackoffDelay` and should stay. | Branch-specific messages and attempt counters differ; tests may assert exact text. | Extract a private `RetryDispatchFlake` helper parameterized by outcome kind/message formatter. |

## Needs Owner Decision

| Rank | Score | Candidate | Category | Est. LOC removable | Evidence / reference count | Seam clearance | Residual risk | Suggested action |
| ---: | ---: | --- | --- | ---: | --- | --- | --- | --- |
| 7 | 9 | `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs:1903` | god-class repetition / over-abstraction | 40 | `RunCommandCheckAsync`, `RunDotnetTestCheckAsync`, and `RunManagedDotnetTestCheckAsync` repeatedly thread the same tuple of check/worktree/goal/stable-slot/build-phase/cancellation arguments and build/test phase flow (`:1903-2052`, `:2289+`). | Not a documented seam; no DI/reflection; stable-slot and build-lock seams are documented and must remain explicit. | More than a local loop collapse; wrong extraction can weaken build-lock/heartbeat receipts. | Collapse only after characterization tests around dotnet timeout, stable slot, no-build, telemetry, and build-lock paths. |
| 8 | 6 | `src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor.cs:217` vs `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.GitOps.cs:228` | duplicate logic | 14-20 | `LandingExecutor.IsRegisteredWorktree` parses `git worktree list --porcelain` directly; `GoalWorktrees` has the same normalized registered-worktree helper and shared registered-path parser. References: two local landing calls at `LandingExecutor.cs:48` and `:60`; multiple `GoalWorktrees` cleanup calls use the central helper. | Not a documented seam; no DI/reflection; private helper only. | `GoalWorktrees` helper was modified on 2026-07-21, inside the 30-day recent threshold, so confirm author intent before changing surface. | Expose a narrow `GoalWorktrees.IsRegisteredWorktree` or move the parser to a small shared internal helper, then delete the landing-local parser. |

## Probably Dead But Unproven

These are not ready-to-execute because the static pass could not fully disprove external/manual use.

| Candidate | Category | Est. LOC removable | Evidence / reference count | Seam clearance | Residual risk | Suggested action |
| --- | --- | ---: | --- | --- | --- | --- |
| `src/Mcg.AgentOrchestrator.Infrastructure/OperatorComms/OperatorDecisionLog.cs:5` | probably dead production helper | 72 | Source references are definition-only; `rg` finds usage in `OperatorChannelTests` but no production call site. Current decision/audit durability appears to live in `CollaborationItemStore.Decisions` and SQLite-backed operator comms. | Not a documented seam; no DI/reflection hit seen; not a process/time/module initializer seam. | Could be a manual JSON audit fallback retained intentionally; tests currently preserve behavior. | Ask owner whether file-backed `operator-decisions.json` is still supported. If not, delete class and its dedicated tests in a separate test+source trim. |

## Explicit Hotspot Assessments

- Dispatch failure classification split: candidate 2 above. The local `BackgroundDispatchRunner.ClassifyDispatch` is redundant with the central typed classifier but retains diagnostic string labels, so collapse through a wrapper instead of directly deleting labels.
- `CliCommandHandlers.Goals`: candidate 4 above. Collapsible duplication found in conduct policy/driver/batch-loop setup.
- `GoalAcceptanceVerifier`: candidates 3 and 7 above. Collapsible duplication found in repeated check loops and dotnet execution plumbing.
- `ConductorDriver`: candidate 6 above. Collapsible duplication found in failed-state dispatch-flake retry branches.

## Static Sweep Notes

- Many name-count false positives were intentionally cleared: DTO/record types used through JSON serialization, target-typed `new`, tuple/record return types, and dashboard response DTOs.
- Confirmed load-bearing examples kept out of the candidate list: `PrototypeWorkspaceSeeder` (dashboard host and tests), `WindowsSandboxAclHelper` (OS-specific sandbox seam), `DotnetTesthostFirewallPath` (stable-slot firewall setup), `SourceSurvey` (dashboard API and worker context artifact), `AcceptanceCheckTimeouts` (acceptance/local-process timeout seam), `WorkerProcessJobs.TryRegister` calls, and clock/sleep injection points.
- No source removals were performed in this audit slice.
