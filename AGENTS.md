# AGENTS

Attention is scarce. Preserve it.

Prefer short, decision-changing output over comprehensive dumps. Show what changed, what is blocked, what was verified, and what needs a decision. Suppress everything else.

## Output Discipline

All commands must minimize output by default.

- Narrow by path, pattern, and glob before running commands. Do not rely on PowerShell pipeline caps such as `| Select-Object -First N`; they create extra Codex permission prompts.
- Prefer bounded `rg`/`rg --files` for discovery. Use exact paths or symbols once known.
- Exclude generated/noisy trees when surveying source using `-g "!**/bin/**"` style globs for `bin`, `obj`, `.scratch`, `.orchestrator-prototype`, `TestResults`, and `playwright-report`.
- Do not run broad repo-root `rg` unless the path and pattern are tight.
- Avoid `rg -C` until match count is known. Prefer `rg -n --count PATTERN path`, then inspect exact files/symbols.
- Do not dump full files unless known small. Prefer targeted search or narrow line windows.
- For `git diff`, use `git diff --stat` first, then inspect one file at a time.
- For build/test commands, use minimal verbosity and expand only failing output.
- For dashboard/API checks, use focused endpoints or helpers that return exact fields, not broad HTML/JSON/log payloads.
- If a command returns more than about 100 lines, stop, summarize the signal, and narrow the next command.

Good shape: `rg -n --count "DashboardHost" src/Mcg.AgentOrchestrator.App tests/Mcg.AgentOrchestrator.Infrastructure.Tests -g "!**/bin/**" -g "!**/obj/**"`

Bad shape: `rg -n "dashboard|goal|task|hosted|source-survey" .. -C 4`

## Retry and Loop Control

Before repeating a command, state what changed or what is being narrowed.

Do not repeatedly run broad searches, diffs, dashboard reads, browser scripts, build/test cycles, or dogfood actions hoping for a different result. If two attempts do not produce useful signal, switch strategy or ask for direction.

This repository implements an AI agent orchestrator. Avoid recursive or high-fanout behavior.

- Do not start Codex/orchestrator/subscription handoffs unless explicitly required.
- Do not run multiple agent tasks for work that can be inspected locally.
- Do not spawn workers to verify work until local evidence indicates the change is ready.
- Prefer one narrow verification command per change.

## Repository Rules

- Treat version-controlled files as source. Build outputs, browser profiles, prototype workspace files, logs, scratch scripts, and previous run artifacts are not source structure.
- Core and Infrastructure intentionally keep flat public namespaces: `Mcg.AgentOrchestrator.Core` and `Mcg.AgentOrchestrator.Infrastructure`.
- Do not split those namespaces unless there is a strong API reason and a migration plan for consumers.

## Architecture & Design Discipline

Before adding a member, field, case, or flag, think about the system ontology — what KIND of thing each type represents and what invariant it encodes — not just "where does my new thing compile." A change that types cleanly but violates the model is debt, not progress.

- **Every type carries an unwritten invariant. Name it before you extend it.** `AgentRole` means "an SDLC worker that executes a tracked goal Task via dispatch" — so a judge (a model the orchestrator invokes for its OWN function, never assigned a task) is NOT a role. It was first hacked in as `AgentRole.Judge` and immediately required an `AgentRoles.Worker` exclusion set everywhere roles are enumerated; that churn was the tell. It now lives as a `ModelFunctionBinding{Purpose, Lane, Model}` in a separate registry. Worker agents vs orchestrator-internal model functions are different kinds of thing; keep them in different homes.
- **The exclusion-set smell.** If adding a value to an enum/type forces you to special-case or exclude it almost everywhere that type is consumed, it does not belong in that type. Model it as its own concept instead of pounding a square peg into a round hole.
- **Open domains take open extension points, not enum churn.** When the set of things is expected to grow (orchestrator-internal model functions: judges, samplers, summarizers, oracles), extend via a composable point — an open `Purpose` string + a registry — so the next one needs zero taxonomy change. Reserve enums for genuinely closed, exhaustive sets (the 6 SDLC roles).
- **Constraints are load-bearing; do not trade them for convenience.** The deterministic gates, subscription-budget-first posture, and evidence-over-narrative spine are invariants of this system. New work composes WITH them (e.g. an LLM judgment lands ADVISORY, never inside a deterministic gate; fan-out goes on free/cheap lanes, never the paid CLI). If a feature seems to require breaking one, that is a design signal to rethink the placement, not a license.
- **Put behavior where its data and invariant already live.** Prefer extending an existing seam that owns the concept over threading a parallel path; reuse the deterministic signal that already exists (e.g. select among parallel model outputs by an existing validator, not a model self-rating). Match the surrounding idiom.

## Dashboard / Dogfood Boundary

For ordinary implementation or debugging, inspect and edit source directly.

Use dashboard workflow controls only when the task explicitly involves dogfood validation, dashboard orchestration, subscription handoff behavior, or prototype workflow testing.

For live prototype dashboard work, prefer `.\mcg-orchestrator.cmd prototype-ui http://localhost:5087/ --refresh 5 --no-open`.

Keep prototype state isolated from repository state. Prototype dashboard state lives under `src/Mcg.AgentOrchestrator.App\.orchestrator-prototype\workspace`; launcher-backed task execution should run from the repository root through `MCG_ORCHESTRATOR_REPOSITORY_ROOT`.

API model runs (`run`/`api-run`) are pure text completion with no file access; embed any data the model needs in the task description, and route file-touching work through subscription dispatches. Task descriptions also drive complexity classification: start inspection work with `Summarize `/`Report `/`Inspect ` and avoid risk keywords (auth, migration, rollback) unless the task genuinely carries that risk.

Goals that touch files should get an isolated workspace: `workspace create` adds a git worktree under `.orchestrator-worktrees/<goal-prefix>` on branch `goal/<goal-prefix>`, and dispatches/verifications for that goal then run there instead of the shared repository root. `acceptance` fast-forwards the goal branch automatically when possible and prints the manual merge command otherwise; `workspace remove` cleans up after merge. Worktrees contain committed files only - uncommitted config does not ride along.

Anthropic subscription work goes through the `claude-cli` profile (claude CLI in print mode), validated end-to-end 2026-06-10. Two requirements: the model must be a valid claude CLI name (full ids like `claude-sonnet-4-6` or CLI aliases `sonnet`/`haiku`/`opus`; the old default `claude-sonnet` is rejected with exit 1), and the command must carry a permission mode — without one, print-mode claude denies every file edit, replies BLOCKED, and still exits 0, which the exit-code auto-pass records as a completed task. The default profile carries both, profile repair upgrades stale saved catalogs, and the patch-capability gate refuses Developer dispatch through permission-less claude templates. Claude resolves relative paths from the dispatch working directory correctly (no absolute-path requirement like qwen).

OpenAI subscription work goes through the `codex-cli` profile. On a ChatGPT account, codex accepts only `gpt-5.5`; `gpt-5.3-codex` and `gpt-5.5-codex` are rejected with 400 (validated 2026-06-11). Built-in defaults use gpt-5.5 and stale saved catalogs repair on load. There is no CLI surface for a custom subscription alias — `agent <role> <provider> <model>` reapplies the built-in default; hand-edit the workspace `agents.json` if a custom alias is needed.

Dispatch sandboxes resolve by task role (2026-06-11): the templates carry `{sandboxMode}`/`{permissionMode}` placeholders, and Developer/Tester dispatches expand to codex `workspace-write` / claude `bypassPermissions` while Planner/Researcher/Reviewer expand to `read-only` / `plan`. Non-implementation roles cannot modify the worktree; their prompt requirements state this too. (Enforced in code/tests; first live pipeline validation still pending.)

Local-model file work goes through the `qwen-code-cli` profile (Qwen Code against Ollama). Two requirements, both validated 2026-06-10: thinking must be disabled via the repo's `.qwen/settings.json` (`generationConfig.reasoning: false` per model; Ollama ignores `/no_think`), and task briefs must state absolute target paths because qwen-code's write tool rejects relative paths and small models do not self-correct. With both in place qwen3:8b completes simple file tasks in ~20 s; expect minor content drift that verification must catch.

Do not raise Ollama's context window for qwen3:8b under qwen-code: at 8k/16k the model receives the full qwen-code system prompt and returns an empty stream; the default 4k truncation is what makes it work. qwen3:14b cannot finish a qwen-code turn within the ~483 s request timeout on this GPU (use it for single-shot API runs instead). qwen-code rewrites `.qwen/settings.json` at exit with its startup view - never hand-edit that file while a qwen process is running.

## Operating the goal loop

The validated operator cycle for self-improvement goals (9 goals shipped 2026-06-10/11):

- Goal shape: `simple-goal "<payload>"` for a single Developer task (the whole brief rides in the objective), or `goal "<payload>"` for the five-role pipeline (role tasks are a fixed generic template; the objective carries everything; later briefs/prompts gain a `## Prior Task Evidence` section with up to the 3 most recent completed tasks' verification stdout).
- Per task: `subscription-dispatch <n>` → `start-dispatch <n> --confirm-dispatch-start` (the cost guard now blocks ONLY on an *anomalous* prompt — one disproportionate to its task complexity, ≥2× the per-complexity ceiling, or a batch total ≥2× the batch ceiling; routine-large and legitimately-large Complex briefs proceed silently as advisories, so `--confirm-large-paid-subscription-start` is needed only when a genuinely bloated prompt trips it, not "from task 3 on") → wait on the printed pid → `refresh-dispatch <n>`.
- Operator gate before acceptance: review the goal-branch diff yourself and run `dotnet test` in the worktree independently; never accept on worker-reported results alone. `Directory.Build.props` (`UseSharedCompilation=false`) + `Directory.Build.rsp` (`-nodeReuse:false`) now disable the Roslyn/MSBuild build servers REPO-WIDE, so a raw `dotnet test` in the worktree no longer leaves lock-holding daemons — this was the recurring CS2012 root cause and it is fixed at source. If a build still hits a transient lock, `dotnet build-server shutdown` + retry clears it.
- ApiOnly tasks (e.g. the local Reviewer) run via `run <n>` with no file access — the output reflects prompt text, not branch state. Close with `answer <request-id>` for any HUMAN_INPUT, then `verify-manual <n> passed "<operator evidence incl. Model fit: line>"`.
- `acceptance` fast-forwards only when main has not advanced mid-goal; otherwise run the printed `git merge goal/<prefix>`. `workspace remove` can half-fail if e2e-test-spawned processes still hold logs (worktree unregisters, directory stays); finish with `git branch -d goal/<prefix>` plus directory removal once the holder exits.
- To put an operator note into an undispatched task's brief, use the timeline: `progress <n> running "<note>"` → `progress <n> failed "<reset>"` → `retry <n> "<msg>"` (no status-neutral note command exists yet).
- At merge: remove the finished BACKLOG entry, add newly discovered items, record the DOGFOOD_LOG entry.

When the task involves subscription/dogfood execution:

- Verify worker profiles before starting subscription tasks.
- Treat `Write-Output {promptPath}` profiles as echo-only, not real execution.
- After dispatch, confirm evidence, process logs, exit code, and verification records; never trust task status alone.
- When credits are constrained, inspect existing continuations/evidence/logs and cancel stale work before starting new agents.
- Prefer focused checks such as `/api/goals/{goalPrefix}/work-summary` and `Invoke-DashboardApi.ps1 -Path api/goals/<prefix>/work-summary` before broad dashboard JSON or HTML reads.
- Prefer checked-in helpers (`Invoke-DashboardApi.ps1`, `Run-DashboardBrowserScript.ps1`, `Invoke-DashboardDogfoodAction.ps1`) over one-off browser/API scripts.

## Safety

Use dashboard cancel/refresh controls or exact known process ids for stuck workers. Never run broad cleanup such as `Get-Process codex | Stop-Process`; it can kill the active Codex session.

On Windows, a running dashboard can lock app binaries. Prefer `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/`.

Windows Firewall prompts once per executable path that binds a non-loopback address. Spawn the app as `dotnet <App.dll>` (covered by the standing ".NET Host" allow rule), never `dotnet run`/direct apphost exe, for anything that binds `0.0.0.0` from a worktree or fresh build output; hosted-dashboard launches from new exe paths will otherwise prompt (root cause and one-time setup: DOGFOOD_LOG 2026-06-11 firewall entry).

`mcg-orchestrator.cmd` with no arguments starts an interactive REPL that stays alive and holds build outputs. Always pass a command. Build/compiler servers are disabled repo-wide (`Directory.Build.props`/`Directory.Build.rsp`), so stale-node CS2012 locks should not recur; if builds still fail with "file in use", check for lingering `Mcg.AgentOrchestrator.App`/`dotnet run` processes and run `dotnet build-server shutdown` before blaming antivirus.

Tests that spawn the real app inherit the machine environment; pin provider env vars (see `StartPrototypeDashboardProcess`) so assertions do not depend on which providers are live on the dev machine.

Quote PowerShell test filters containing `|`, for example `--filter 'AgentCatalog|PrototypeWorkspaceSeeder|WorkerProfile|WorkerDispatch'`.

## Evidence

Update `DOGFOOD_LOG.md` only at dogfood goal boundaries or when recording durable product friction. Keep entries short: goal id, objective, command/action, exit code, focused result, blocker/friction, verification, next follow-up.

For subscription/API-authored work, include a `Model fit:` note with the selected model or launcher, the task shape, and whether it was adequate, overkill, or underpowered. Use this evidence to tune future model selection.

Do not paste full dashboard responses, full prompts, full logs, or long API payloads.

Rotate `DOGFOOD_LOG.md` when it grows past roughly 500 lines: move all but the most recent entries to a dated archive under `docs/` (for example `docs/DOGFOOD_LOG-2026-06.md`) and keep the pointer line at the top of the log current. Do not load archives into context for routine work.

Check `BACKLOG.md` before proposing follow-up work; it holds open items with context, file pointers, and done-conditions. Update it at goal boundaries: remove finished entries, add newly discovered follow-ups as self-contained entries that need no conversation history.
