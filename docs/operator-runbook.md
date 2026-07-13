# Operator Runbook

**If you are a new AI instance about to operate this orchestrator, read this first.** It is the canonical guide for *driving, observing, and recovering* goals. It supersedes the older "Core Loop" in `README.md` (manual verbs) and the manual sequence in `.agents/skills/orchestrator-dogfood/SKILL.md` — those are fallbacks, not the default path.

Most commands below are shown through the launcher: `.\mcg-orchestrator.cmd <command> ...` (or `./mcg-orchestrator.cmd` from a POSIX shell). For Codex/operator foreground CLI calls, prefer `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 <orchestrator-args...>`; it runs the built app through the repo-bounded helper prefix and avoids repeated permission prompts from ad-hoc `dotnet` commands. For other checked-in helper scripts, prefer `.\scripts\Invoke-RepoScript.ps1 <repo-relative-script.ps1> ...`; for source windows, use `.\scripts\Invoke-RepoScript.ps1 scripts\Show-RepoFileSlice.ps1 <path> <start> <count>` instead of PowerShell pipelines. For compact monitoring, use `.\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal1> <goal2>`; for SQLite store reads/repairs, use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 ...`. Never run the bare launcher with no command — it opens an interactive REPL that holds build-output locks.

---

## 1. Golden path (conductor-first)

The conductor drives a goal through its **entire** lifecycle. You almost never call the manual verbs.

```
# 1. Create a goal
mcg-orchestrator.cmd goal "<objective>"                 # five-role SDLC pipeline
mcg-orchestrator.cmd simple-goal "<objective>"          # single Developer task
mcg-orchestrator.cmd goal --text-file <path>            # long objective via throwaway file (then delete the file)

# 2. (If the refiner raised clarifications) clear them so the goal can flow
mcg-orchestrator.cmd attention dismiss <goal-prefix>    # proceed with the brief as written

# 3. Drive it (and every other active goal) to landing
mcg-orchestrator.cmd conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 5400

# 4. Observe
mcg-orchestrator.cmd next <goal-prefix> --full          # one-shot full inspection
```

`conduct --loop` runs `ConductorBatchLoop`: each tick advances every eligible goal one policy-gated step through its state machine, creates worktrees, dispatches workers, waits on them, runs the acceptance suite against the worktree, applies the change-risk gate, fast-forward-merges into `main`, records the dogfood entry in SQLite, and removes the worktree. The loop ends on its own when all goals are done or escalated (`LOOP_STOP reason=all-done-or-escalated`).

You do **not** need `workspace create`, `subscription-dispatch`, `start-dispatch`, `refresh-dispatch`, or `accept` by hand. A single non-loop `conduct <goal-prefix>` advances exactly one step (useful for stepping/inspection).

### `conduct` flags
| flag | meaning |
|---|---|
| `--loop` | run continuously, advancing all eligible goals each tick |
| `--watch` | sleep between ticks while goals are held (workers running) |
| `--policy <P>` | autonomy policy: `Conservative` (default) \| `Permissive` \| `Manual` |
| `--poll-seconds <n>` | seconds between ticks (15 is fine) |
| `--max-duration <s>` / `--max-iterations <n>` | bound the run |
| `--daemon` | persistent mode for controlled active-goal intake; stays alive on an empty backlog and picks up goals submitted after the loop starts |
| `--dashboard-url <url>` | attach to a running dashboard |

**Stop a loop** by creating a `.conduct-stop` file in the repo root (graceful) or Ctrl-C. The loop is now interrupt-safe — a stop no longer cancels in-flight worker tasks.

---

## 2. Policies (the gate you choose)

| Policy | Workers | Behavior |
|---|---|---|
| **Conservative** (default) | 4 | Auto-promotes only DocsOnly changes; **escalates every code/build change at the promote gate** for you to review the diff and promote. Does **not** auto-approve high-risk ownership areas (see §5). |
| **Permissive** | 5 | Auto-promotes any change whose acceptance suite is green, including broad changes and high-risk ownership areas. The real build/test suite still runs — promotion is never on worker word alone. Use when you accept automated acceptance as sufficient. |
| **Manual** | 1 | Escalates at every step. |

For autonomous dogfooding, `Permissive` is usually what you want; the acceptance gate is the real safety net.

---

## 3. Conceptual model

A **goal** decomposes into **tasks** (the 6 SDLC roles: Planner, Researcher, Developer, Tester, Reviewer — Ideation optional). Each task is dispatched to a role-matched **worker** (a `claude-cli`/`codex-cli` subscription process, or an API run). The **conductor** advances the goal's lifecycle:

```
Created → WorkspaceReady → Dispatched → Running → AwaitingVerification → Verified → Merged → Recorded → CleanedUp
```

Plus off-path states you will see in escalations: `AwaitingClarification`, `Failed`, `Blocked`, `AwaitingHumanInput`.

Tick outcomes in the loop output: `executed` (advanced a step), `held` (worker running, will reconcile), `escalated` (needs you), `done` (terminal).

**Trust order — believe evidence in this order, never the top one alone:**
acceptance suite output → git diff / commits in the worktree → verification records → worker prose → task `Completed` status. "Completed" is a *claim*, not proof (see `.agents/skills/orchestrator-worker-verification`).

**Judging a dispatched worker:** zero stdout *and* an empty worktree is **not** stalled — it is reading the brief and planning (4–6+ min for a complex brief). Judge "hung" only by a long-window (15–30 min) absence of file changes, and confirm the worker process is alive with CPU before concluding. Do not run a short cancel/re-dispatch loop; it manufactures the hang it looks for.

---

## 4. Observe what's happening

| Command | Shows |
|---|---|
| `next <goal-prefix>` | prioritized recommended next command for where the goal stands |
| `next <goal-prefix> --full` | full inspection: status, readiness, evidence, stage readiness, verification gates, human-input worklist, recovery plan, operator inbox |
| `status <goal-prefix>` | objective + the task list with each task's status (`[Assigned]`/`[Completed]`/`[Failed]`/...) |
| `task <goal-prefix> <n>` | one task's detail: assigned agent, dispatch command, exit code, verification history, failure reason |
| `readiness <goal-prefix>` | **start blockers** — high-risk objective terms and ownership approval (run this first when a goal won't dispatch) |
| `durations` | role/complexity runtime medians plus attempts-per-task; use it to spot slow lanes and retry redundancy before changing worker mix or loop policy |
| `durations --by-model` | the same duration report sliced by model/provider; use it when a role looks slow but provider choice may be the real variable |

Read the loop's own output too — `TICK`, `held`, `escalated`, `LOOP_STOP` lines tell you exactly what each goal did. `PHASE_TIMING` receipts in loop logs are the tick latency profile: `sweep`, `prewalk`, `per-goal-walk`, and `dispatch-prep` show where the conductor spent the tick. Use them when ticks feel slow before blaming a worker.

Judge worker progress by **worktree file changes**, not stdout bytes: `git -C .orchestrator-worktrees/<prefix> status --short` and `git -C .orchestrator-worktrees/<prefix> log --oneline main..HEAD`.

For focused goal inspection, prefer the repo helpers over broad `.orchestrator` reads:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Get-GoalDispatchInventory.ps1 <goal-prefix>
.\scripts\Invoke-RepoScript.ps1 scripts\Get-GoalTaskSummary.ps1 <goal-prefix>
.\scripts\Invoke-RepoScript.ps1 scripts\Get-WorkerResultTail.ps1 <goal-prefix> [task-prefix] [-Chars 800]
```

`Get-GoalDispatchInventory.ps1` is the ground truth for round status: it lists each dispatch generation with exit and heartbeat evidence. Use it when `status` says Running/Dispatched but the round may have died. `Get-GoalTaskSummary.ps1` shows each task's status, last failure, and blockers; use it before repair commands. `Get-WorkerResultTail.ps1` prints the newest `WORKER_RESULT`; add `[task-prefix]` when several tasks have recent results, and `-Chars` when the default tail is too short.

For persistent monitoring from a shell that can keep running:

```bash
scripts/watch-goal-pulse.sh <goal-prefix> [goal-prefix...]
scripts/watch-loop-events.sh <goal-prefix> [goal-prefix...]
```

`watch-goal-pulse.sh` is the lightweight goal pulse while workers run. `watch-loop-events.sh` follows terminal events for the named goals and auto-selects the newest loop log, so use it after launching a conductor loop without hunting for the log path.

For long-running conductor/acceptance commands, keep the operator seat free by launching a bounded background command and polling:

```
.\scripts\Invoke-RepoScript.ps1 scripts\Start-OrchestratorCommand.ps1 -Name <goal>-acceptance acceptance <goal> --autonomy supervised-auto
.\scripts\Invoke-RepoScript.ps1 scripts\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix operator -TaskPrefix <goal>-acceptance -TailLines 20
```

For worker logs, use the same bounded helper instead of ad hoc `.orchestrator` PowerShell reads:

```
.\scripts\Invoke-RepoScript.ps1 scripts\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix <goal> -TaskPrefix <task> -TailLines 20
```

---

## 5. Stuck-goal playbook (symptom → first command)

This is the most important section. Match the **observable symptom** to its cause and first command.

| Symptom (in loop output or `status`) | Cause | First action |
|---|---|---|
| `escalated at WorkspaceReady - No tasks in ready batch` **but `status` shows tasks Assigned** | The goal's file scope touches a **high-risk ownership area** (`scripts/`/`.ps1`, `src/.../Infrastructure/`, build-system, config, dashboard-api, skills) — under a non-Permissive policy these need operator approval. (Or a high-risk *objective term* like "production"/"auth".) | `readiness <goal>` to see the exact blocker. Re-run the loop under `--policy Permissive` (auto-approves ownership). |
| `escalated at Failed - operator action required` | A task is in `Failed` status. | `status <goal>` to find the Failed task → `recover <goal> --text-file <path>` (resets stuck/Failed/Cancelled tasks to dispatchable) → re-run the loop. |
| `escalated at AwaitingClarification` | The spec-refiner asked questions. | `attention dismiss <goal>` (proceed with the brief) or answer them, then re-run. |
| A task shows `[Cancelled]` (e.g. a loop was stopped mid-dispatch on an older build) | Interrupted dispatch. | `recover <goal> --text-file <path>` — it now revives Cancelled tasks too. |
| `escalated at Verified - Acceptance verification failed` | The acceptance build/test suite failed against the worktree (a real defect, a worker-written test bug, or a gate defect). | Inspect the worktree, run the suite there (`scripts/Invoke-TestSummary.ps1 -Target <worktree project>`), fix + commit in the worktree, then `acceptance <goal>`. If a gate defect blocks an otherwise green goal, file and fix the gate bug, then re-run acceptance; do not bypass the gate. |
| `acceptance <goal>` prints **"not accepted"** with `Tasks passed: N/5` | A task isn't verified yet (often a verification-role task). | `status <goal>` → if a Tester/Reviewer is `Failed`, `recover` it and re-run; the goal reconciles `Failed → Active`. |
| `status <goal>` shows goal `Completed` while one or more tasks are still `[Assigned]` after a retry | Lifecycle/task desync from a retry or failed conductor pass. The conductor may refuse to start the assigned task because the persisted goal status is terminal. | First try `recover <goal> --text-file <path>`. If it remains `Completed`, use the repo-bounded repair helper: `.\scripts\Invoke-RepoScript.ps1 scripts\Set-OrchestratorGoalStatus.ps1 --status Active <goal>`; then re-run `conduct <goal> --policy Permissive`. |
| A task stays `[Failed]` / `[Running]` after the real dispatch round is dead, or a retry is blocked by stale task state | The task row is pinned even though the worker round has no useful forward path. | Confirm with `scripts\Get-GoalDispatchInventory.ps1 <goal>` and `scripts\Get-GoalTaskSummary.ps1 <goal>`, then requeue only that task: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 requeue-task --task-number <n> --note "<why>" <goal>`. |
| `Get-GoalDispatchInventory.ps1` shows a `dispatch.json` generation with **no heartbeat and no exit** | Dispatch prep failed before the worker process started; the worker never ran, so waiting will not produce output. | Stop the loop cleanly, retire the dead dispatch artifacts, requeue the task with `requeue-task`, then relaunch the conductor. |
| Goal in `Failed` lifecycle but the work is committed in the worktree | A stage process was orphaned (e.g. a loop crash). The commit is safe. | `recover <goal> --text-file <path>` then re-run the loop, or `acceptance <goal>` to reconcile + land. |
| Worker exits 0, worktree has uncommitted changes, and logs say `index.lock: Permission denied` under `.git\worktrees\<goal>` | Low-integrity worker could edit files but could not write git metadata, so conductor commit-on-behalf did not complete. | Inspect the diff, run focused tests, then from a normal-integrity operator shell run the `git -C .orchestrator-worktrees/<goal> add ...` and `git -C .orchestrator-worktrees/<goal> commit -m "<message>"` steps as separate commands; use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-Git.ps1 ...` if direct git prompts. Record `progress <goal> <task> completed --text-file <path>` + `verify-manual <goal> <task> passed --text-file <path>`, then `acceptance <goal>`. |
| Codex task fails with `exec error: Access is denied. (os error 5)` and stderr names `C:\Program Files\WindowsApps\...\pwsh.exe`, or fails immediately with `unexpected argument '<word>' found` after falling back to Windows PowerShell | Codex can run under same-user Low IL, but WindowsApps/package PowerShell activation can fail; Windows PowerShell 5.1 can also split `(Get-Content -Raw prompt)` into multiple native arguments. | Treat this as a launcher regression, not proof that Low IL is unusable. Verify `WorkerShell` pins a real PowerShell 7 host, preferring `%LOCALAPPDATA%\Programs\PowerShell\7\pwsh.exe`; extract/install a real filesystem `pwsh.exe` there if only the Store alias exists. Verify `DispatchProcessHost` removes WindowsApps from Low-IL `PATH`, then rerun focused `WorkerShellTests` / `DispatchProcessHostTests` before falling back to another Developer provider. |
| Low-IL dispatch shows no stdout/stderr files and no heartbeat for minutes, while process inspection shows `icacls ... /setintegritylevel ... /T` under `__dispatch-run` | The sandbox is still applying Mandatory Integrity labels before the worker launch. Older builds redirected `icacls` output without draining it, causing pipe backpressure, 120s startup timeouts, orphaned `icacls`, and duplicate retries; a completed `icacls` can also return nonzero on a previously used worktree. | Stop the loop with `.conduct-stop`, then stop only the exact orphan dispatch PIDs if the task was already retried. Verify `DispatchProcessHost.SetLowIntegrity` drains `icacls`, kills it on timeout, treats completed nonzero exits as non-fatal, and emits a `preparing-sandbox` heartbeat before restarting the conductor. |
| Goal genuinely dead / wrong, can't proceed | — | `abandon-goal <goal> --text-file <path> --confirm-goal-abandon` (remove its worktree first if a Low-IL `.mcg-sandbox` orphan blocks it). |
| Worker log shows exit 0 and file changes exist in `.orchestrator-worktrees/<prefix>` but the task is still `[Dispatched]` / reconcile loop shows `held` indefinitely | Orphaned dispatch reconcile — loop crashed after worker exited. The work is safe in the worktree. | `recover <goal> --text-file <path>` resets the stale dispatch → then `acceptance <goal>` (or re-run the loop) to read the worktree commits. |
| Acceptance build fails with `MSB3491` / "file is being used by another process" after the repo-wide build-server disablement | A non-build-server process such as a running dashboard/test host may still hold an output DLL. Transient; not a code defect. | Stop the exact owning process when known; otherwise `dotnet build-server shutdown` is harmless, then retry: re-run `acceptance <goal>` or let the next loop tick retry. |
| `escalated at AwaitingClarification` and you want to provide real answers, not dismiss | Spec-refiner raised design questions with stable short IDs. | `attention show <goal>` (lists questions with stable IDs), then `attention answer <goal> <id> --text-file <path>` for long answers; then re-run the loop. Answers are injected into the refined spec before the next dispatch. |
| You want two or more goals to advance concurrently | Goals with overlapping file scopes contend for the same worktree paths — running them together produces merge conflicts. | Verify non-overlapping file scopes first. Then intake all goals **before** starting a single `conduct --loop --watch --policy Permissive` — one loop tick advances every eligible goal; the slot cap (5 under Permissive) limits concurrent workers. |

### State-repair quiet window

State-repair writes must happen only while no conductor loop is mid-tick. A running tick can clobber concurrent state writes by saving its older in-memory snapshot after your repair (backlog `c7344bf2`). Use this sequence for SQLite repair commands and artifact retirement:

```powershell
New-Item -ItemType File .conduct-stop
# wait for LOOP_STOP in the loop log
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 requeue-task --task-number <n> --note "<why>" <goal>
.\scripts\Invoke-RepoScript.ps1 scripts\Get-GoalTaskSummary.ps1 <goal>
# let one next tick prove the goal is still stuck or now dispatchable, then relaunch the loop
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 5400
```

For lifecycle repairs, use `set-goal-status` only with Core-valid `GoalStatus` values: `Draft`, `Active`, `WaitingForHuman`, `Parked`, `Verified`, `Completed`, `Failed`, `Cancelled`, `Superseded`. Invalid values poison snapshot loads; malformed goal rows are quarantined on load (landed in `1d2223c2`), which preserves the rest of the store but hides the damaged goal until repaired.

### WorkspaceReady + no ready batch after Developer

**Symptom.** `conduct --loop` repeatedly holds or escalates a goal at `WorkspaceReady` / "no ready batch" after the Developer task completed, with Tester or Reviewer tasks still `Assigned`. In the 2026-06-27 incident, example goals `6d76b216` and `abf4967a` showed this pattern; future incidents will have different prefixes.

**Diagnosis.** First identify the assigned-but-not-starting verification task:

```
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 status <goal-prefix>
```

Then run the explicit dispatch preflight for that task. This must fail before any paid worker starts if the pinned `claude-cli` entry cannot authenticate from the Windows Low Integrity Level sandbox:

```
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 subscription-dispatch <goal-prefix> <task-number>
```

If the output includes `ERR_CLAUDE_AUTH_UNAVAILABLE`, the "no ready batch" is masking a Claude auth preflight failure, not a real scheduling gap. Low IL is the Windows Low Integrity Level sandbox; it can block subprocess credential/OAuth token access needed by `claude-cli`.

**Recovery.** Switch the affected role to any non-`claude-cli` subscription provider, then rebind the stalled tasks to the new catalog entry. `gpt-5.5` was the OpenAI example used in the 2026-06-27 recovery; use the current valid non-Claude subscription model when that rotates.

```
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 agent Tester OpenAI gpt-5.5
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 agent Reviewer OpenAI gpt-5.5
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 agents
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 reassign-agent <goal-prefix> <tester-task-number> <new-tester-agent-id>
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 reassign-agent <goal-prefix> <reviewer-task-number> <new-reviewer-agent-id>
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 conduct <goal-prefix> --policy Permissive
```

If a `conduct --loop --watch ...` is already running, the reassigned tasks are dispatchable on the next tick; otherwise run `conduct <goal-prefix>` or restart the loop.

Notes that will save you time:
- `recover <goal> --text-file <path>` takes a long free-text note without tripping command-length caps. Short inline notes still work; avoid `;` and other shell-special characters because the launcher mangles them.
- `subscription-dispatch <task> --confirm-limit-review --text-file <path>` records a long usage-limit review note without putting it on the command line.
- A loop **crash** (vs a graceful `.conduct-stop`) does **not** cancel in-flight tasks — they stay reconcilable — but it can leave a goal in `Failed` lifecycle that `recover`/`acceptance` clears.
- Verification roles (Tester/Reviewer) legitimately change no files; the dispatch gate accepts their `WORKER_RESULT` as evidence. If a verification task still won't pass, `verify-manual <n> passed --text-file <path>` records an operator pass.

---

## 6. Autonomous conductor-loop lifecycle

This section describes what the loop does on every goal's behalf so you can verify it ran correctly and know when to intervene.

### 6.1 Goal-state lifecycle

Each goal advances through `GoalLifecycleState` identifiers in order:

```
Created
  → WorkspaceReady          # isolated git worktree created at .orchestrator-worktrees/<8-char-prefix>
  → Dispatched              # first eligible task sent to a role-matched worker
  → Running                 # worker process active; loop ticks poll for output
  → AwaitingVerification    # worker exited; acceptance suite queued
  → Verified                # acceptance suite green; change-risk gate evaluable
  → Merged                  # goal branch merged into main (landing action)
  → Recorded                # SQLite dogfood-log entry written
  → CleanedUp               # worktree removed; goal is terminal
```

Off-path states you will see in escalations: `AwaitingClarification` (refiner raised questions), `AwaitingHumanInput` (conductor needs an operator decision), `Failed` (a task exhausted retries), `Blocked` (operator hold). These stop the normal sequence; use §5 to clear them.

**Loop-exit condition.** A plain `conduct --loop` batch run halts automatically and prints `LOOP_STOP reason=all-done-or-escalated` when every active goal has reached a terminal state (`CleanedUp`, `Failed`, `Blocked`) or been escalated. Goals created *after* a plain loop started are **not** picked up — restart `conduct --loop` to process them. Use `conduct --loop --daemon` only for controlled active-goal intake: it stays alive on an empty backlog and picks up goals submitted later, but it is not a safe "drain the backlog" mode.

### 6.2 Orchestrator-commit-on-behalf + merge to main

Workers write files only inside their isolated worktree (`.orchestrator-worktrees/<prefix>`), on branch `goal/<prefix>`. When a worker exits with uncommitted changes, the conductor auto-stages and commits them before running the acceptance suite. A successfully landed goal produces two paired commits visible in `git log main`:

```
Orchestrator-committed worker edits for goal <full-goal-id>
Integrate goal/<prefix>: <goal-title-slug>
```

The first commit is made on `goal/<prefix>` inside the worktree. The second merges that branch into `main` via the `integration` branch (fast-forward). If you see both commits for a goal, the loop ran to completion for it. If you see only the first, the acceptance gate or change-risk gate stopped the landing — `status <goal>` and `next <goal> --full` explain why.

If the worker ran at low integrity and git metadata under `.git\worktrees\<prefix>` rejects `index.lock`, the worker may exit 0 with a correct dirty worktree and no commit. Treat that as operator recovery, not an implementation failure: inspect the diff, run focused tests, commit the worker changes from a normal-integrity shell, then record manual verification and run acceptance.

### 6.2.1 Manual landing for escalated risk

Security-risk and build-system goals may intentionally stop at the landing gate even after acceptance passes. When the escalation is only "review before landing", land out-of-band from the repository root on the main checkout and record it:

```powershell
git -C .orchestrator-worktrees/<goal-prefix> diff --stat main...HEAD
git -C .orchestrator-worktrees/<goal-prefix> diff main...HEAD
git merge --no-ff goal/<goal-prefix>
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 goal-mark-landed <goal-prefix> --confirm-goal-mark-landed
```

Use this only after reviewing the diff and confirming the branch is the intended `goal/<prefix>`. `goal-mark-landed` records the out-of-band merge and lets the conductor continue record/cleanup steps; it is not a replacement for acceptance or review.

### 6.3 Concurrency caps

Two independent constraints bound useful parallelism:

**Test-slot capacity.** The acceptance test grid has 4 slots (5 under Permissive policy). Launching more concurrent goals than available slots means acceptance runs queue — they do not fail, they wait for a slot to free up.

**Provider rate-limit.** Each worker holds a `claude-cli` or `codex-cli` subscription session. Under heavy load the shared subscription can hit the provider's session rate-limit, causing workers to exit with zero bytes of output and retry in a loop. If you observe this pattern (workers exit-1 repeatedly with empty output), reduce the number of concurrent in-flight goals or stagger goal intake. There is no orchestrator-side setting that bypasses the upstream limit.

---

## 7. State & store map

Durable state lives in stores, never in `.scratch`.

| Location | What |
|---|---|
| `.orchestrator/state.db` | the kernel: goals, tasks, dispatches, verifications (SQLite, single-writer) |
| `.orchestrator/backlog.db` | the backlog (use `backlog-list`/`backlog-add`/`backlog-show`/`backlog-close`; this is the source of truth, not `BACKLOG.md`) |
| `.orchestrator/dogfood-log.db` | dogfood goal-boundary evidence (use `dogfood-log list`/`dogfood-log add`; this is the source of truth, not `DOGFOOD_LOG.md`) |
| `.orchestrator/collaboration-items.db` | clarifications / operator-input items |
| `.orchestrator/agents.json` | the agent catalog (which model each role uses) |
| `.orchestrator/logs/`, `.orchestrator/prompts/` | per-dispatch worker logs (`*.out.log`/`*.err.log`/`*.exit.txt`) and the rendered worker prompts |
| `.orchestrator-worktrees/<goal-prefix>` | the goal's isolated git worktree on branch `goal/<prefix>` |
| `.orchestrator-context/<goal-id>` | worker context artifacts for a goal |

`--text-file` is the uniform throwaway vehicle to pass long text past the command-length cap; `--brief-file` and `--body-file` remain aliases for existing scripts. The durable copy becomes the goal objective, task note, verification receipt, answer, or backlog item, so **delete the scratch input** afterward.

Dogfood goal-boundary evidence is durable SQLite state, not a tracked markdown append log.

```powershell
mcg-orchestrator.cmd dogfood-log list --limit 10
mcg-orchestrator.cmd dogfood-log add <goal-prefix>
mcg-orchestrator.cmd record-goal <goal-prefix>   # compatibility alias for add + render
```

`DOGFOOD_LOG.md` remains only as a pointer for operators and should not receive new durable entries.

For rare lifecycle/task desync repair, `scripts\Set-OrchestratorGoalStatus.ps1` / `Invoke-OrchestratorSqliteTool.ps1 set-goal-status` update both the indexed `goals.status` column and the serialized snapshot in `.orchestrator/state.db`. They are operator recovery tools, not normal workflow commands; prefer `recover`, `retry`, and `conduct` first. Use only Core-valid statuses, and make the write during a quiet window after `LOOP_STOP`.

---

## 8. Operating discipline (hard-won)

- **One canonical path.** Prefer `conduct --loop`; the manual verbs (`subscription-dispatch → start-dispatch → refresh-dispatch → accept`) are granular fallback only.
- **Backlog is candidate input, not an automatic queue.** A stale/open backlog can contain obsolete, overlapping, or underspecified work. Before daemon mode, curate a small active set with `backlog-list` + filtered `backlog-intake "<heading>" --create-simple-goal` / `--create-goal`; avoid unfiltered `goal-plan --create-*` or multi-filter batch creation unless you have reviewed dependencies and file scopes. Keep daemon runs bounded with `--max-duration` until the active set is proven healthy.
- **Keep long waits out of the foreground.** Use `scripts\Start-OrchestratorCommand.ps1` through `scripts\Invoke-RepoScript.ps1` for long acceptance/conductor runs, then poll `next <goal> --full`, `Find-OrchestratorLocks.ps1`, and `Show-OrchestratorLogArtifacts.ps1`. Avoid raw `Start-Sleep` loops and broad `.orchestrator` filesystem commands.
- **State writes vs a running loop.** Light writes (`attention answer`, `backlog-add` — the latter on a separate `backlog.db`) are safe concurrent with the loop. But a burst of HEAVY state-`db` writes — `goal --text-file`, `abandon-goal`, `park-goal` (each does a whole-kernel load+save) — racing a write-heavy tick can exhaust the SQLite busy-retry and **crash** the loop (observed twice, 2026-06-25). Serialize those *between* loop runs (stop → mutate → restart). Read-only inspection (`status`, `next`, git on worktrees, loop output) is always free.
- **Scope the test suite to the changed project**, not the whole solution; run it foreground (or poll). Use `scripts/Invoke-TestSummary.ps1 -Target <project>` for compact results.
- **Provider requirements:** `claude-cli` needs a valid model id (`claude-sonnet-4-6`/`sonnet`/`haiku`/`opus`) **and** a permission mode (the default profile carries both). `codex-cli` on a ChatGPT account accepts `gpt-5.5`. API runs (`run`/`api-run`) have **no file access** — embed needed data in the task description.
- **The brief is the unverified root of trust.** The gates verify "output matched the spec," never "was the spec right." A sloppy brief lands a plausible-but-wrong implementation on green tests. Specify external contracts (happy *and* unhappy path), observable success, ownership/lifecycle, and the verification class before dispatch.
- **Shared-service changes ripple to integration tests.** A brief that changes a widely-consumed service (the failure classifier, the binding resolver, a kernel API) must require the worker to find that service's consumers (a call-site / reverse-dependency search) and run the **dependent integration tests** (e.g. `RunGoalService_*`) in self-verify — not just the changed file's own unit tests. Otherwise the ripple is caught only by the full-suite acceptance gate, costing a Developer-retry cycle. Observed 2026-06-25: two lanes changing `DispatchFailureClassifier` and the binding resolver both escalated at acceptance on `RunGoalService_*` because their briefs scoped self-verify too narrowly.
- **Reproduce before you theorize.** When an external CLI/model/tool fails, run the smallest reproducing command before concluding a cause.

---

## 9. Where else to look

- `AGENTS.md` — output/diagnosis/spec discipline and architecture invariants (read after this).
- `.agents/skills/orchestrator-worker-verification/SKILL.md` — how to verify a worker result before trusting it.
- `next <goal> --full` and `readiness <goal>` — the live, authoritative state of any goal.
