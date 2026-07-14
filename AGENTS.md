# AGENTS

<!-- HARNESS-COUNTERPART-CONTRACT:BEGIN -->
Owns: shared repository discipline plus Codex harness operating guidance.
Counterpart: CLAUDE.md owns Claude Code harness operating guidance and links back to AGENTS.md for shared discipline.
Rule: Agents editing shared-discipline content must update BOTH AGENTS.md and CLAUDE.md counterpart-contract blocks and shared-anchor lists, or move the content to docs/operator-runbook.md or another shared home.
Shared anchors:
- output-discipline
- retry-and-loop-control
- repository-rules
- architecture-and-design-discipline
- specification-discipline
- diagnosis-discipline
- dashboard-dogfood-boundary
- operating-the-goal-loop
- safety
- evidence
<!-- HARNESS-COUNTERPART-CONTRACT:END -->

Attention is scarce. Preserve it.

Prefer short, decision-changing output over comprehensive dumps. Show what changed, what is blocked, what was verified, and what needs a decision. Suppress everything else.

Codex auto-reads this file. Claude Code auto-reads [`CLAUDE.md`](CLAUDE.md); keep Claude-harness idioms there and keep shared discipline here.

> **Operating the orchestrator — driving, observing, or recovering goals? Start with [`docs/operator-runbook.md`](docs/operator-runbook.md).** It is the canonical conductor-first guide, including the stuck-goal playbook (symptom → command) and the state/store map. This file covers output/diagnosis/spec discipline and architecture invariants — read it alongside the runbook, not instead of it.

<!-- shared-discipline:output-discipline -->
## Output Discipline

All commands must minimize output by default.

- Narrow by path, pattern, and glob before running commands. Do not rely on PowerShell pipeline caps such as `| Select-Object -First N`; they create extra Codex permission prompts.
- Prefer bounded `rg`/`rg --files` for discovery. Use exact paths or symbols once known.
- Exclude generated/noisy trees when surveying source using `-g "!**/bin/**"` style globs for `bin`, `obj`, `.scratch`, `.orchestrator-prototype`, `TestResults`, and `playwright-report`.
- Do not run broad repo-root `rg` unless the path and pattern are tight.

Codex harness command guidance:

- For checked-in PowerShell scripts, prefer `.\scripts\Invoke-RepoScript.ps1 <repo-relative-script.ps1> ...`; it is repo-bounded and avoids repeated permission prompts from ad-hoc shell one-liners.
- For foreground orchestrator CLI commands, prefer `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 <orchestrator-args...>` over direct `dotnet` or launcher calls; keep free-form arguments shell-plain (avoid `;`, `|`, `&`) so PowerShell does not split the answer into extra command segments.
- For operator monitoring, prefer `.\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal1> <goal2>`; it combines active goals, conduct/dispatch processes, build locks, and selected statuses without ad-hoc CIM/SQLite/log snippets.
- For direct SQLite utility access, use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 <sqlite-tool-args...>` instead of `dotnet run --project ...`.
- If direct `git` commands prompt, use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-Git.ps1 <git-args...>`; it runs git from the repository root through the same repo-bounded script prefix.
- For exact source windows, use `.\scripts\Invoke-RepoScript.ps1 scripts\Show-RepoFileSlice.ps1 <path> <start> <count>` instead of `Get-Content | Select-Object`.
- Avoid `rg -C` until match count is known. Prefer `rg -n --count PATTERN path`, then inspect exact files/symbols.
- Do not dump full files unless known small. Prefer targeted search or narrow line windows.
- For `git diff`, use `git diff --stat` first, then inspect one file at a time.
- For build/test commands, use minimal verbosity and expand only failing output.
- For dashboard/API checks, use focused endpoints or helpers that return exact fields, not broad HTML/JSON/log payloads.
- If a command returns more than about 100 lines, stop, summarize the signal, and narrow the next command.

Good shape: `rg -n --count "DashboardHost" src/Mcg.AgentOrchestrator.App tests/Mcg.AgentOrchestrator.Infrastructure.Tests -g "!**/bin/**" -g "!**/obj/**"`

Bad shape: `rg -n "dashboard|goal|task|hosted|source-survey" .. -C 4`

<!-- shared-discipline:retry-and-loop-control -->
## Retry and Loop Control

Before repeating a command, state what changed or what is being narrowed.

Do not repeatedly run broad searches, diffs, dashboard reads, browser scripts, build/test cycles, or dogfood actions hoping for a different result. If two attempts do not produce useful signal, switch strategy or ask for direction.

This repository implements an AI agent orchestrator. Avoid recursive or high-fanout behavior.

- Do not start Codex/orchestrator/subscription handoffs unless explicitly required.
- Do not run multiple agent tasks for work that can be inspected locally.
- Do not spawn workers to verify work until local evidence indicates the change is ready.
- Prefer one narrow verification command per change.

**Judging a dispatched worker's progress.** A worker showing zero stdout AND an empty worktree is NOT stalled — it is reading the brief + project context and planning. claude-cli/codex write nothing (stdout *or* files) during this phase, which is 4–6+ minutes for a complex brief. Judge a dispatch hung ONLY by a long-window absence of progress — default 15–30 min for Complex work — and confirm against the worker PROCESS (alive + CPU accumulating = thinking) before concluding, never a short empty-worktree snapshot. A short "backstop timer → cancel → re-dispatch" loop manufactures the very hang it's looking for and is the worst form of repeating-hoping-for-a-different-result; it also invites hallucinated root causes (rate-limiting, etc.) to explain the self-inflicted symptom. Inspect, don't theorize.

<!-- shared-discipline:repository-rules -->
## Repository Rules

- Treat version-controlled files as source. Build outputs, browser profiles, prototype workspace files, logs, scratch scripts, and previous run artifacts are not source structure.
- **Durable state lives in the stores, never in `.scratch`.** Canonical homes: SQLite (backlog, kernel state, collaboration-items) and tracked files (AGENTS.md, docs/, config). `--text-file` is the throwaway vehicle for long retry/note/progress/verify-manual/recover/answer/add-task text; `goal --brief-file` and `backlog-add --body-file` are the command-specific long-text forms. Once the command runs, the durable copy is the goal objective, task note, verification receipt, answer, or backlog item, so DELETE the scratch input. Clean `.scratch` as you go; never let it become a parallel faux-durable store. We keep migrating the codebase off scattered state files — do not reintroduce the same sprawl in operating habits.
- Core and Infrastructure intentionally keep flat public namespaces: `Mcg.AgentOrchestrator.Core` and `Mcg.AgentOrchestrator.Infrastructure`.
- Do not split those namespaces unless there is a strong API reason and a migration plan for consumers.

<!-- shared-discipline:architecture-and-design-discipline -->
## Architecture & Design Discipline

Before adding a member, field, case, or flag, think about the system ontology — what KIND of thing each type represents and what invariant it encodes — not just "where does my new thing compile." A change that types cleanly but violates the model is debt, not progress.

- **Every type carries an unwritten invariant. Name it before you extend it.** `AgentRole` means "an SDLC worker that executes a tracked goal Task via dispatch" — so a judge (a model the orchestrator invokes for its OWN function, never assigned a task) is NOT a role. It was first hacked in as `AgentRole.Judge` and immediately required an `AgentRoles.Worker` exclusion set everywhere roles are enumerated; that churn was the tell. It now lives as a `ModelFunctionBinding{Purpose, Lane, Model}` in a separate registry. Worker agents vs orchestrator-internal model functions are different kinds of thing; keep them in different homes.
- **The exclusion-set smell.** If adding a value to an enum/type forces you to special-case or exclude it almost everywhere that type is consumed, it does not belong in that type. Model it as its own concept instead of pounding a square peg into a round hole.
- **Open domains take open extension points, not enum churn.** When the set of things is expected to grow (orchestrator-internal model functions: judges, samplers, summarizers, oracles), extend via a composable point — an open `Purpose` string + a registry — so the next one needs zero taxonomy change. Reserve enums for genuinely closed, exhaustive sets (the 6 SDLC roles).
- **Constraints are load-bearing; do not trade them for convenience.** The deterministic gates, subscription-budget-first posture, and evidence-over-narrative spine are invariants of this system. New work composes WITH them (e.g. an LLM judgment lands ADVISORY, never inside a deterministic gate; fan-out goes on free/cheap lanes, never the paid CLI). If a feature seems to require breaking one, that is a design signal to rethink the placement, not a license.
- **Put behavior where its data and invariant already live.** Prefer extending an existing seam that owns the concept over threading a parallel path; reuse the deterministic signal that already exists (e.g. select among parallel model outputs by an existing validator, not a model self-rating). Match the surrounding idiom.

<!-- shared-discipline:specification-discipline -->
## Specification Discipline — the brief is the unverified root of trust

The gates verify "did the output match the spec," never "was the spec right" — so a sloppy brief lands a plausible-but-wrong implementation on green tests (the Discord listener that deleted its own forum post compiled and passed fake-API tests; the gateway built but never hosted; `postResult` left optional so the operator saw nothing). A capable worker does exactly what the brief says — quality is set or lost in the brief. Before dispatch, run the objective through this rubric; each item is a scar:

- **External-interaction contracts — happy AND unhappy path.** For every external system/API/UI touched, state the exact contract incl. failure/edge behavior. (Discord interaction-ack semantics were unspecified → improvised wrongly.)
- **Observable success.** Say what the user SEES/experiences on success, not just "it works."
- **Ownership / lifecycle / hosting — decide it.** Where it lives, its lifecycle, its dependencies. Never offer "host here OR there"; collapse options into a decision or escalate the genuine fork to the human. Don't pass under-determination downstream.
- **Own the seams.** If work spans goals/files, name the integration contract and make integration verification a first-class, OWNED step — the bugs live in the unowned seams between locally-green pieces. ("Built but not wired": a "make X work end-to-end" goal that touches only Infrastructure + tests and no App/host/CLI is almost certainly not reachable.)
- **Verification class.** Tag it: TEST-VERIFIABLE (pure logic → automated gate suffices) vs REAL-WORLD-DEPENDENT (external/UX/integration → a green test is NOT "done"; ship a human/real-world checklist as the gate). Fake-API unit tests cannot prove an integration works; that takes a real round-trip.

Separate the axes when scoping: WHERE it applies (which goals) vs HOW it's implemented (stage/role/function) vs WHAT the increment limits (depth vs coverage) — conflating them produces contradictory specs.

<!-- shared-discipline:diagnosis-discipline -->
## Diagnosis Discipline — empirics over theorizing

Reproduce before you theorize. When something fails — especially an external CLI, model, or tool — run the smallest command that reproduces the behavior before concluding a cause or declaring it unfixable. Do not build a verdict on documentation, web reports, or aggregate stats alone.

- **The reproducing command beats the plausible story.** A web search said `gpt-5.3-codex-spark` was "exec-restricted on ChatGPT accounts" and the outcome scorecard rated it Avoid (0 completed / 2 failed); both were misleading. A single ~17-second `codex exec --model gpt-5.3-codex-spark ... </dev/null` proved spark works fine and isolated the real bug: the judge runner never closed the child's stdin, so codex blocked forever on "Reading additional input from stdin…" → 180s timeout. One empirical test turned "unsalvageable" into a two-line fix (`RedirectStandardInput=true` + `StandardInput.Close()`).
- **If you catch yourself stacking hypotheses** (cross-block, version drift, platform restriction) without having run the thing, stop and run it. Layered speculation is a smell.
- **Aggregate stats flag WHERE to look, not the diagnosis.** Scorecards and pass-rates point at a suspect; confirm the mechanism with a direct test before acting on it. A model rated "Avoid" may just be mis-invoked.

<!-- shared-discipline:dashboard-dogfood-boundary -->
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

<!-- shared-discipline:operating-the-goal-loop -->
## Operating the goal loop

**Default: drive goals with the autonomous conductor (`conduct --loop`), not the manual verbs.** The full operate / observe / recover guide — golden path, the `conduct` flag matrix, the three policies, the state model, and the **stuck-goal playbook** (symptom → first command) — lives in [`docs/operator-runbook.md`](docs/operator-runbook.md); read it first. A few notes that complement it:

- **Build locks:** `Directory.Build.props` (`UseSharedCompilation=false`) + `Directory.Build.rsp` (`-nodeReuse:false`) disable the Roslyn/MSBuild build servers REPO-WIDE, so raw `dotnet test` is safe in a worktree and no longer leaves lock-holding daemons (the old CS2012 root cause, fixed at source). If a build still hits a transient lock, `dotnet build-server shutdown` + retry clears it.
- At a landing, the conductor records the dogfood entry in `.orchestrator/dogfood-log.db`; you still close the finished backlog item (`backlog-close`) and add newly discovered ones (`backlog-add`).
- Monitor the stable structured event stream at `.orchestrator/logs/conduct-events.log` first; it is JSON lines with `eventKind` and survives rotation with `tail -F`. Per-batch stdout/stderr log tails are fallback evidence.
- Bounded loops self-renew on `--max-duration` when active work remains. A successful successor emits `LOOP_HANDOFF`; relaunch manually only after `LOOP_HANDOFF_FAILED`, deliberate stop/Ctrl-C, code/config changes, or an all-done stop followed by new goals.
- Daemon mode is for a small, curated active-goal set. Do not point it at a stale/open backlog wholesale; use `backlog-list` and filtered `backlog-intake "<heading>" --create-simple-goal` / `--create-goal`, then keep the first daemon runs bounded with `--max-duration`.

**Manual lower-level verbs (fallback / granular control only — prefer `conduct --loop`):** `subscription-dispatch <n>` → `start-dispatch <n> --confirm-dispatch-start` (the cost guard blocks ONLY on an *anomalous* prompt — disproportionate to task complexity, ≥2× the per-complexity ceiling, or batch total ≥2× the batch ceiling; routine/legitimately-large Complex briefs proceed silently, so `--confirm-large-paid-subscription-start` is needed only when a genuinely bloated prompt trips it) → wait on the printed pid → `refresh-dispatch <n>` → operator gate → `accept`. `acceptance`/`accept` fast-forwards only when main has not advanced mid-goal; otherwise run the printed merge on a scratch verification branch first, then land on `main` only once the evidence is acceptable. ApiOnly tasks (e.g. the local Reviewer) run via `run <n>` with no file access — output reflects prompt text, not branch state; close HUMAN_INPUT with `answer <request-id> --text-file <path>`, then `verify-manual <n> passed --text-file <path>`. To put an operator note into an undispatched task's brief, use `progress <n> running --text-file <path>` → `progress <n> failed --text-file <path>` → `retry <n> --text-file <path>`.

When the task involves subscription/dogfood execution:

- Verify worker profiles before starting subscription tasks.
- Treat `Write-Output {promptPath}` profiles as echo-only, not real execution.
- After dispatch, confirm evidence, process logs, exit code, and verification records; never trust task status alone.
- When credits are constrained, inspect existing continuations/evidence/logs and cancel stale work before starting new agents.
- Prefer focused checks such as `/api/goals/{goalPrefix}/work-summary` and `Invoke-DashboardApi.ps1 -Path api/goals/<prefix>/work-summary` before broad dashboard JSON or HTML reads.
- Prefer checked-in helpers (`Invoke-DashboardApi.ps1`, `Run-DashboardBrowserScript.ps1`, `Invoke-DashboardDogfoodAction.ps1`) over one-off browser/API scripts.

<!-- shared-discipline:safety -->
## Safety

Use dashboard cancel/refresh controls or exact known process ids for stuck workers. Never run broad cleanup such as `Get-Process codex | Stop-Process`; it can kill the active Codex session.

On Windows, a running dashboard can lock app binaries. Prefer `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/`.

Windows Firewall prompts once per executable path that binds a non-loopback address. Spawn the app as `dotnet <App.dll>` (covered by the standing ".NET Host" allow rule), never `dotnet run`/direct apphost exe, for anything that binds `0.0.0.0` from a worktree or fresh build output; hosted-dashboard launches from new exe paths will otherwise prompt (root cause and one-time setup: DOGFOOD_LOG 2026-06-11 firewall entry).

`mcg-orchestrator.cmd` with no arguments starts an interactive REPL that stays alive and holds build outputs. Always pass a command. Build/compiler servers are disabled repo-wide (`Directory.Build.props`/`Directory.Build.rsp`), so stale-node CS2012 locks should not recur; if builds still fail with "file in use", check for lingering `Mcg.AgentOrchestrator.App`/`dotnet run` processes and run `dotnet build-server shutdown` before blaming antivirus.

Tests that spawn the real app inherit the machine environment; pin provider env vars (see `StartPrototypeDashboardProcess`) so assertions do not depend on which providers are live on the dev machine.

Quote PowerShell test filters containing `|`, for example `--filter 'AgentCatalog|PrototypeWorkspaceSeeder|WorkerProfile|WorkerDispatch'`.

<!-- shared-discipline:evidence -->
## Evidence

Record dogfood goal-boundary evidence with `dogfood-log add <goal-prefix>` or read it with `dogfood-log list --limit <n>`. Durable entries live in `.orchestrator/dogfood-log.db`, not in `DOGFOOD_LOG.md`. Keep entries short: goal id, objective, command/action, exit code, focused result, blocker/friction, verification, next follow-up.

For subscription/API-authored work, include a `Model fit:` note with the selected model or launcher, the task shape, and whether it was adequate, overkill, or underpowered. Use this evidence to tune future model selection.

Do not paste full dashboard responses, full prompts, full logs, or long API payloads.

Keep `DOGFOOD_LOG.md` as a pointer to the SQLite-backed command surface. Do not append durable entries there. Do not load historical archives into context for routine work.

Check the backlog before proposing follow-up work: `backlog-list` reads the canonical store (`.orchestrator/backlog.db`; the `BACKLOG.md` file is legacy and being retired). Update it at goal boundaries — `backlog-close` finished items and `backlog-add` newly discovered follow-ups as self-contained entries that need no conversation history.
