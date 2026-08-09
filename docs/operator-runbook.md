# Operator Runbook

**If you are a new AI instance about to operate this orchestrator, read this first.** It is the canonical guide for *driving, observing, and recovering* goals. It supersedes the older "Core Loop" in `README.md` (manual verbs) and the manual sequence in `.agents/skills/orchestrator-dogfood/SKILL.md` — those are fallbacks, not the default path.

Most commands below are shown through the launcher: `.\mcg-orchestrator.cmd <command> ...` (or `./mcg-orchestrator.cmd` from a POSIX shell). For Codex/operator foreground CLI calls, prefer `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 <orchestrator-args...>`; it runs the built app through the repo-bounded helper prefix and avoids repeated permission prompts from ad-hoc `dotnet` commands. For other checked-in helper scripts, prefer `.\scripts\Invoke-RepoScript.ps1 <repo-relative-script.ps1> ...`; for source windows, use `.\scripts\Invoke-RepoScript.ps1 scripts\Show-RepoFileSlice.ps1 <path> <start> <count>` instead of PowerShell pipelines. For compact monitoring, use `.\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal1> <goal2>`; for SQLite store reads/repairs, use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 ...`. Never run the bare launcher with no command — it opens an interactive REPL that holds build-output locks.

For exact process ownership, command-line, or lineage inspection, use `.\scripts\Invoke-RepoScript.ps1 scripts\Get-RepoProcessInfo.ps1 -Id <pid> -IncludeChildren` (optionally `-Newest <n>`). It replaces ad-hoc `Get-CimInstance` and inline process-tree snippets. Keep every Codex harness command shell-plain and single-purpose: do not join commands with `;`, `|`, or `&`, and do not construct PID lists with comma expressions. Those forms are evaluated as separate or unmatched permission segments and cause avoidable approval prompts or sandbox routing. When an operation needs compound logic, put it in or extend a checked-in repo-bounded helper, then invoke that helper through `Invoke-RepoScript.ps1`.

---

## 1. Golden path (conductor-first)

The conductor drives a goal through its **entire** lifecycle. You almost never call the manual verbs.

```
# 1. Create an ordinary implementation goal
mcg-orchestrator.cmd goal "<objective>"                 # Planner + Researcher + Developer + Tester + Reviewer
mcg-orchestrator.cmd goal --brief-file <path>           # long objective via throwaway file (then delete the file)

# Explicit exception only: genuinely mechanical low-risk work, or direct operator instruction
mcg-orchestrator.cmd simple-goal "<objective>"          # single Developer task

# 2. (If the refiner raised clarifications) clear them so the goal can flow
mcg-orchestrator.cmd attention dismiss <goal-prefix>    # proceed with the brief as written

# 3. Drive it (and every other active goal) to landing
mcg-orchestrator.cmd conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 5400

# 4. Observe
mcg-orchestrator.cmd next <goal-prefix> --full          # one-shot full inspection
```

For ordinary implementation goals, use `goal`: the normal dogfood pipeline is the five-role SDLC flow through Planner, Researcher, Developer, Tester, and Reviewer. `simple-goal` is an explicit exception for genuinely mechanical, low-risk work or direct operator instruction. It is not a throughput shortcut, and narrow backlog items still use `goal` unless they meet that exception.

`conduct --loop` runs `ConductorBatchLoop`: each tick advances every eligible goal one policy-gated step through its state machine, creates worktrees, dispatches workers, waits on them, runs the acceptance suite against the worktree, applies the change-risk gate, fast-forward-merges into `main`, records the dogfood entry in SQLite, and removes the worktree. The loop ends on its own when all goals are done or escalated (`LOOP_STOP reason=all-done-or-escalated`). Infrastructure-triggered self-relaunch is **opt-in**: set `MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED=true` before starting the loop to have a landing that changes conductor/verifier/gate/build/dispatch infrastructure stop admissions, drain current workers, build and self-check a content-addressed successor, and hand off. It is disabled by default, so without that setting no `LOOP_RELAUNCH_*` or infrastructure-triggered `LOOP_HANDOFF` event is expected; use the manual bounce below after a loop-affecting landing. The separate bounded-run successor handoff still runs when `--max-duration` is reached while active work remains.

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

**Stop a loop** by creating a `.conduct-stop` file in the repo root or by using Ctrl-C. A `.conduct-stop` is a detach, not a drain: at the next stop check the loop starts no new dispatches, attempts to detach live workers, persists the detached state, and exits without waiting for those workers to finish. A successful detach leaves the task running for a successor to reconcile; if detachment fails, the fallback cancels the dispatch so it can be requeued. Prefer a quiet window with no live workers before a deliberate stop. Remove `.conduct-stop` before starting a new loop.

### Manual bounce after loop-affecting code lands

Unless self-relaunch was explicitly enabled before the current loop started, landed conductor/verifier/gate/build/dispatch changes are not active in that incumbent. Re-arm them deliberately:

1. Inspect active goals with `Get-OrchestratorSnapshot.ps1` and exact dispatch inventories; when practical, wait until no worker is in flight because `.conduct-stop` detaches rather than drains.
2. Record the conductor PID from the first line of `.orchestrator\conduct-loop.lock`, create `.conduct-stop`, and wait for `LOOP_STOP` in `.orchestrator\logs\conduct-events.log`.
3. Confirm that recorded PID is no longer running with `Get-RepoProcessInfo.ps1 -Id <pid> -IncludeChildren`. An orderly exit normally removes `conduct-loop.lock`; if it remains, remove it only after the owner PID is confirmed dead.
4. Remove `.conduct-stop`, then relaunch through `Start-OrchestratorCommand.ps1` without `-AppDll`. That path invokes `mcg-orchestrator.cmd`, whose HEAD/source freshness check rebuilds the app when the landed code is newer than the current binary.

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal-prefix>
$conductorPid = Get-Content -LiteralPath .orchestrator\conduct-loop.lock -TotalCount 1
New-Item -ItemType File .conduct-stop
# Wait for LOOP_STOP, then verify the recorded lock PID is dead:
.\scripts\Invoke-RepoScript.ps1 scripts\Get-RepoProcessInfo.ps1 -Id $conductorPid -IncludeChildren
# Only after the PID is confirmed dead:
Remove-Item -LiteralPath .orchestrator\conduct-loop.lock -ErrorAction SilentlyContinue
Remove-Item -LiteralPath .conduct-stop
.\scripts\Invoke-RepoScript.ps1 scripts\Start-OrchestratorCommand.ps1 -Name <batch-name> conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 5400
```

### Auto-resume after reboot or loop crash

Long-running unattended drives can opt into a reboot/crash watchdog. Launch the drive through `scripts\Start-OrchestratorCommand.ps1`; when the command is `conduct --loop`, the script overwrites `.orchestrator\last-drive.json` with the batch name, `AppDll` if any, original conduct arguments, policy, poll interval, and duration cap. This is a dumb last-drive journal: it is replaced on every new loop launch and is not a queue.

Install the watchdog only when you explicitly want it:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Install-OrchestratorAutoResume.ps1
```

The installer registers one per-user scheduled task in the operator's interactive logon context. It has two triggers: at logon and a 10-minute repetition trigger. Remove it cleanly with:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Install-OrchestratorAutoResume.ps1 -Remove
```

The scheduled task runs `scripts\Resume-OrchestratorLoop.ps1`. The resume script is idempotent: if `Get-RepoProcessInfo.ps1 -ConductLoop` finds a running loop, or `.conduct-stop` exists, it exits 0 with a `RESUME_SKIPPED` reason and launches nothing. With no running loop and no stop file, it reads `.orchestrator\last-drive.json`, increments the batch suffix (`batch60` -> `batch61`, otherwise appends `-handoff-1`), relaunches through `Start-OrchestratorCommand.ps1`, and prints the launch receipt JSON. A deliberate stop therefore stays stopped until you remove `.conduct-stop`.

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
| `task <goal-prefix> <n>` | one task's detail: assigned agent, dispatch command, exit code, and latest verification/failure preview; previews may be truncated |
| `verifications <goal-prefix> <n>` | the task's stored verification history, including orchestrator-authored stdout/stderr such as planner-contract and acceptance state-guard diagnostics; use the printed artifact path when a long value is previewed |
| `readiness <goal-prefix>` | **start blockers** — high-risk objective terms and ownership approval (run this first when a goal won't dispatch) |
| `durations` | role/complexity runtime medians plus attempts-per-task; use it to spot slow lanes and retry redundancy before changing worker mix or loop policy |
| `durations --by-model` | the same duration report sliced by model/provider; use it when a role looks slow but provider choice may be the real variable |

Listen to the stable conduct event stream first, starting at the current end so old runs are not replayed:

```bash
tail -n 0 -F .orchestrator/logs/conduct-events.log
```

```powershell
Get-Content -LiteralPath .orchestrator\logs\conduct-events.log -Tail 0 -Wait
```

It is JSON lines with `timestamp`, `eventKind`, `goalId`, and `detail`; the stable path survives rotation. Key `eventKind` values include `loop-start`, `loop-stop`, `loop-handoff`, `loop-relaunch`, `loop-relaunch-rollback`, `watch-transition`, `gate-progress`, `acceptance`, and `lock-blocker`. The `detail` field preserves the compact loop line (`TICK`, `held`, `escalated`, `LOOP_STOP`, `LOOP_RELAUNCH_*`, `LOOP_HANDOFF`, `LOOP_HANDOFF_FAILED`, `PHASE_TIMING`). Infrastructure-triggered `loop-relaunch` events occur only when self-relaunch was enabled. A `loop-relaunch-rollback` or infrastructure-triggered `LOOP_HANDOFF_FAILED` names the triggering goal and failed phase and reports whether the incumbent can continue; do not assume authority was reclaimed unless the event says so. `gate-progress` records phase changes and 30-second heartbeats for long acceptance checks, so a quiet worker does not require state or process polling while those heartbeats continue. `PHASE_TIMING` receipts are the tick latency profile: `sweep`, `prewalk`, `per-goal-walk`, and `dispatch-prep` show where the conductor spent the tick. Use them when ticks feel slow before blaming a worker.

When an operator item or status line names a receipt such as `run-event:496445`, recover its complete stored text with one command: `run-event show 496445`. This works for goal-less events such as `CanaryGateFailure`; add `--format json` for structured output. An unknown sequence exits non-zero with a not-found message. Do not reconstruct a `monitor-goal --from-cursor` call to read a single receipt.

Post-landing canary subprocess output is retained under `.orchestrator/logs` as `post-landing-canary-<full-sha>-<session>-<ordinal>-<operation>.out.log` and the matching `.err.log`. When a `CANARY_GATE` line reports a fault, glob by its SHA and use the operation suffix to distinguish worktree setup, repository inspection, build, probe, and cleanup output. Three consecutive faults with the same reason and complete normalized detail stop retrying and report `escalation=repeated-identical-fault`; the landing remains unverified rather than becoming a failed product verdict.

Keep one persistent listener when the harness can stream it. If the harness buffers a long-lived follower, make a sparse bounded read such as `Get-Content -LiteralPath .orchestrator\logs\conduct-events.log -Tail 8` after a meaningful interval; do not replace the listener with rapid state-file, process, or log polling. Query `next <goal> --full`, dispatch inventory, or an exact process lineage only when an event creates a decision point or expected heartbeats stop. A higher-level subscription command is a valid replacement only when it starts from the current cursor and demonstrably honors its goal/event filters; historical or cross-goal replay is not an operator signal.

Incident history lives in `docs/incidents/`; use it when a symptom needs narrative context beyond the current transactional records.

Judge worker progress by **worktree file changes**, not stdout bytes: `git -C .orchestrator-worktrees/<prefix> status --short` and `git -C .orchestrator-worktrees/<prefix> log --oneline main..HEAD`.

For focused goal inspection, prefer the repo helpers over broad `.orchestrator` reads:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Get-GoalDispatchInventory.ps1 <goal-prefix>
.\scripts\Invoke-RepoScript.ps1 scripts\Get-GoalTaskSummary.ps1 <goal-prefix>
.\scripts\Invoke-RepoScript.ps1 scripts\Get-WorkerResultTail.ps1 <goal-prefix> [task-prefix] [-Chars 800]
```

`Get-GoalDispatchInventory.ps1` is the ground truth for round status: it lists each dispatch generation with exit and heartbeat evidence. Use it when `status` says Running/Dispatched but the round may have died. `Get-GoalTaskSummary.ps1` shows each task's status, last failure, and blockers; use it before repair commands. `Get-WorkerResultTail.ps1` prints the newest `WORKER_RESULT`; add `[task-prefix]` when several tasks have recent results, and `-Chars` when the default tail is too short.

Per-batch loop stdout/stderr logs are fallback evidence, not the canonical event source. Use them only when the JSONL stream is missing or you need raw stdout around a specific batch:

```bash
scripts/watch-goal-pulse.sh <goal-prefix> [goal-prefix...]
scripts/watch-loop-events.sh <goal-prefix> [goal-prefix...]
```

`watch-goal-pulse.sh` is the lightweight goal pulse while workers run. `watch-loop-events.sh` follows terminal events for the named goals from per-batch logs; prefer `conduct-events.log` when it is available.

For long-running conductor/acceptance commands, keep the operator seat free by launching a bounded background command and listening to the conduct event stream. Read per-command artifacts only when the stream reports a failure or stops producing expected heartbeats:

```
.\scripts\Invoke-RepoScript.ps1 scripts\Start-OrchestratorCommand.ps1 -Name <goal>-conduct conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 5400
.\scripts\Invoke-RepoScript.ps1 scripts\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix operator -TaskPrefix <goal>-conduct -TailLines 20
```

For worker logs, use the same bounded helper instead of ad hoc `.orchestrator` PowerShell reads:

```
.\scripts\Invoke-RepoScript.ps1 scripts\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix <goal> -TaskPrefix <task> -TailLines 20
```

---

## 5. Stuck-goal playbook (symptom → first command)

This is the most important section. Match the **observable symptom** to its cause and first command.

Before driving or reviving a parked or long-idle goal, verify that its premise still holds: inspect current `main`, check whether the requested content already landed, and reproduce the smallest reported failure or measurement. Do this before `recover`, `unpark-goal`, or another paid dispatch. If the brief is stale but the goal is still useful, replace it in place with `revise <goal> --brief-file <path> --reason-file <path>`; the new version becomes authoritative while in-flight dispatches and completed tasks remain unchanged.

Known direct-command limitation: `acceptance <goal>` can finish the full suite and then fail at `stage=state-guard` even when the worktree evidence is otherwise usable (tracked as `550cf0f0`). After one such direct-command failure, stop; do not repeat the suite as its error text suggests. Preserve the worktree and verification evidence, then run/resume the conductor so its sweep performs the acceptance and landing path automatically.

| Symptom (in loop output or `status`) | Cause | First action |
|---|---|---|
| `escalated at WorkspaceReady - No tasks in ready batch` **but `status` shows tasks Assigned** | The goal's file scope touches a **high-risk ownership area** (`scripts/`/`.ps1`, `src/.../Infrastructure/`, build-system, config, dashboard-api, skills) — under a non-Permissive policy these need operator approval. (Or a high-risk *objective term* like "production"/"auth".) | `readiness <goal>` to see the exact blocker. Re-run the loop under `--policy Permissive` (auto-approves ownership). |
| `escalated at Failed - operator action required` | A task is in `Failed` status. | `status <goal>` to find the Failed task → `recover <goal> --text-file <path>` (resets stuck/Failed/Cancelled tasks to dispatchable) → re-run the loop. |
| `escalated at AwaitingClarification` | The spec-refiner asked questions. | `attention dismiss <goal>` (proceed with the brief) or answer them, then re-run. |
| Workers repeatedly invent evidence for an infeasible acceptance criterion | The authoritative refined brief still requires the criterion; retry notes cannot change it. | During a quiet window, run `goal-amend <goal> --waive <criterion-number|exact-text> --reason-file <path> [--actor <name>]`. The durable waiver, reason, actor, and timestamp appear in subsequent briefs and goal events. |
| A task shows `[Cancelled]` (e.g. a loop was stopped mid-dispatch on an older build) | Interrupted dispatch. | `recover <goal> --text-file <path>` — it now revives Cancelled tasks too. |
| `escalated at Verified - Acceptance verification failed` | The acceptance build/test suite failed against the worktree (a real defect, a worker-written test bug, or a gate defect). | Inspect the worktree, run the focused failing check there, fix + commit in the worktree, then re-run the conductor and let its sweep retry acceptance. If a gate defect blocks an otherwise green goal, file and fix the gate bug; do not bypass the gate or repeatedly invoke direct `acceptance`. |
| `acceptance <goal>` prints **"not accepted"** with `Tasks passed: N/5` | A task isn't verified yet (often a verification-role task). | `status <goal>` → if a Tester/Reviewer is `Failed`, `recover` it and re-run the conductor; the goal reconciles `Failed → Active`. |
| `status <goal>` shows goal `Completed` while one or more tasks are still `[Assigned]` after a retry | Lifecycle/task desync from a retry or failed conductor pass. The conductor may refuse to start the assigned task because the persisted goal status is terminal. | First try `recover <goal> --text-file <path>`. If it remains `Completed`, use the repo-bounded repair helper: `.\scripts\Invoke-RepoScript.ps1 scripts\Set-OrchestratorGoalStatus.ps1 --status Active <goal>`; then re-run `conduct <goal> --policy Permissive`. |
| Goal is intentionally `Parked` and needs to resume | Operator parked it to stop churn or wait for external context. | `unpark-goal <goal> --text-file <path> --confirm-goal-unpark`, then re-run the loop. Use `park-goal <goal> --text-file <path> --confirm-goal-park` to pause it again. |
| A task stays `[Failed]` / `[Running]` after the real dispatch round is dead, or a retry is blocked by stale task state | The task row is pinned even though the worker round has no useful forward path. | Confirm with `scripts\Get-GoalDispatchInventory.ps1 <goal>` and `scripts\Get-GoalTaskSummary.ps1 <goal>`, then requeue only that task: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 requeue-task --task-number <n> --note "<why>" <goal>`. |
| `Get-GoalDispatchInventory.ps1` shows a `dispatch.json` generation with **no heartbeat and no exit** | Dispatch prep failed before the worker process started; the worker never ran, so waiting will not produce output. | Stop the loop cleanly, retire the dead dispatch artifacts, requeue the task with `requeue-task`, then relaunch the conductor. |
| Goal in `Failed` lifecycle but the work is committed in the worktree | A stage process was orphaned (e.g. a loop crash). The commit is safe. | `recover <goal> --text-file <path>`, then re-run the loop so the conductor reconciles and lands it. |
| Worker exits 0, worktree has uncommitted changes, and logs say `index.lock: Permission denied` under `.git\worktrees\<goal>` | Low-integrity worker could edit files but could not write git metadata, so conductor commit-on-behalf did not complete. | Inspect the diff, run focused tests, then from a normal-integrity operator shell run the `git -C .orchestrator-worktrees/<goal> add ...` and `git -C .orchestrator-worktrees/<goal> commit -m "<message>"` steps as separate commands; use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-Git.ps1 ...` if direct git prompts. Record `progress <goal> <task> completed --text-file <path>` + `verify-manual <goal> <task> passed --text-file <path>`, then re-run the conductor. |
| Codex task fails with `exec error: Access is denied. (os error 5)` and stderr names `C:\Program Files\WindowsApps\...\pwsh.exe`, or fails immediately with `unexpected argument '<word>' found` after falling back to Windows PowerShell | Codex can run under same-user Low IL, but WindowsApps/package PowerShell activation can fail; Windows PowerShell 5.1 can also split `(Get-Content -Raw prompt)` into multiple native arguments. | Treat this as a launcher regression, not proof that Low IL is unusable. Verify `WorkerShell` pins a real PowerShell 7 host, preferring `%LOCALAPPDATA%\Programs\PowerShell\7\pwsh.exe`; extract/install a real filesystem `pwsh.exe` there if only the Store alias exists. Verify `DispatchProcessHost` removes WindowsApps from Low-IL `PATH`, then rerun focused `WorkerShellTests` / `DispatchProcessHostTests` before falling back to another Developer provider. |
| Low-IL dispatch shows no stdout/stderr files and no heartbeat for minutes, while process inspection shows `icacls ... /setintegritylevel ... /T` under `__dispatch-run` | The sandbox is still applying Mandatory Integrity labels before the worker launch. Older builds redirected `icacls` output without draining it, causing pipe backpressure, 120s startup timeouts, orphaned `icacls`, and duplicate retries; a completed `icacls` can also return nonzero on a previously used worktree. | Stop the loop with `.conduct-stop`, then stop only the exact orphan dispatch PIDs if the task was already retried. Verify `DispatchProcessHost.SetLowIntegrity` drains `icacls`, kills it on timeout, treats completed nonzero exits as non-fatal, and emits a `preparing-sandbox` heartbeat before restarting the conductor. |
| Goal genuinely dead / wrong, can't proceed | — | `abandon-goal <goal> --text-file <path> --confirm-goal-abandon` (remove its worktree first if a Low-IL `.mcg-sandbox` orphan blocks it). |
| Worker log shows exit 0 and file changes exist in `.orchestrator-worktrees/<prefix>` but the task is still `[Dispatched]` / reconcile loop shows `held` indefinitely | Orphaned dispatch reconcile — loop crashed after worker exited. The work is safe in the worktree. | `recover <goal> --text-file <path>` resets the stale dispatch, then re-run the loop to read the worktree commits. |
| Acceptance build fails with `MSB3491` / "file is being used by another process" after the repo-wide build-server disablement | A non-build-server process such as a running dashboard/test host may still hold an output DLL. Transient; not a code defect. | Stop the exact owning process when known; otherwise `dotnet build-server shutdown` is harmless. If the goal is `AcceptanceFailed` and all work remains verified, run `acceptance-retry <goal> "<reason>" --confirm-acceptance-retry`; the next conductor tick re-runs the gate without a worker round. |
| `escalated at AwaitingClarification` and you want to provide real answers, not dismiss | Spec-refiner raised design questions with stable short IDs. | `attention show <goal>` (lists questions with stable IDs), then `attention answer <goal> <id> --text-file <path>` for long answers; then re-run the loop. Answers are injected into the refined spec before the next dispatch. |
| You want two or more goals to advance concurrently | Goals with overlapping file scopes contend for the same worktree paths — running them together produces merge conflicts. | Verify non-overlapping file scopes first. Then intake all goals **before** starting a single `conduct --loop --watch --policy Permissive` — one loop tick advances every eligible goal; the slot cap (5 under Permissive) limits concurrent workers. |

### Choose the recovery verb by what is wrong

| Verb | Use when | Effect |
|---|---|---|
| `recover <goal> <note>` | A task or lifecycle record is stuck, orphaned, or out of sync. | Repairs task/lifecycle state and may make tasks dispatchable again. |
| `acceptance-retry <goal> <reason> --confirm-acceptance-retry` | The goal is `AcceptanceFailed`, every task is already `Completed` or deliberately `Cancelled`, and the gate failed for an environmental reason. | Returns the goal to `Verified` for the next conductor tick, preserves task and dispatch evidence, resets the automatic acceptance retry budget, and records the operator re-gate. It does not reopen a task or emit `WorkerDispatch`, and is capped at three calls per goal. |
| `retry <goal> <task-number> <reason>` | The implementation or verification evidence is wrong and a worker must revise it. | Reopens that task, clears its latest gate evidence, and allows another worker round; downstream tasks may also be invalidated. |
| `revise <goal> --brief-file <path> [--reason-file <path>]` | The goal premise or brief is stale, but the goal should continue with corrected authority. | Creates a new authoritative brief version and reports the not-yet-started, in-flight, and completed task sets. In-flight dispatches continue from their prior snapshot and completed tasks stay unchanged. Use `revise <goal> --history` to audit versions. |
| `goal-amend <goal> --waive <criterion-number|exact-text> --reason-file <path> [--actor <name>]` | An in-flight goal has an infeasible or mis-specified acceptance criterion. | Records an auditable waiver and re-renders the criterion as explicitly waived in future worker briefs. This whole-kernel verb currently requires a quiet window. |

Additional recovery notes:

- If a retry is swallowed because the same completed dispatch exit artifact keeps being reconciled, stop the loop, identify the exact stale `.exit.txt` from `task <goal> <n>` or `Get-GoalDispatchInventory.ps1`, move that single exit artifact aside with a `.retired` suffix, then run `recover <goal> --text-file <path>` and `retry <goal> <task-number> --text-file <path>`. Do not delete broad log sets; preserve stdout/stderr for evidence.
- `recover` can over-reset tasks that had already passed. Bridge those back with explicit receipts: `progress <goal> <task-number> completed --text-file <path>` followed by `verify-manual <goal> <task-number> passed --text-file <path>`.
- `retry`, `progress`, and `verify-manual` always append typed requests to `.orchestrator/operator-intents.db`. The tick applies each request as the sole `state.db` writer; when no conductor is running, requests remain pending until one starts. `operator-intent-status <intent-id>` and the dashboard operator-intent panel expose the terminal outcome and audit fields. Wake files are one-shot notifications; SQLite remains the durable queue.
- A lingering goal-level `Failed` display while retryable tasks are already in flight is expected noise during recovery. Judge the live state by the task process, dispatch inventory, and event stream before applying another repair.
- Classifier and recovery notes include the rule and matched evidence that triggered them. Read that evidence before retrying; it usually distinguishes provider limits, empty-output flakes, dirty worktree recovery, sandbox commit blocks, and real test failures.

### State-repair quiet window

Direct SQLite repair tools and artifact retirement still require a quiet window because they bypass the typed operator-intent inbox. Routine `retry`, `progress`, and `verify-manual` commands do not: keep the loop running and let its next tick apply them. Use this stop sequence only for direct repair commands:

```powershell
New-Item -ItemType File .conduct-stop
# wait for LOOP_STOP in .orchestrator/logs/conduct-events.log
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 requeue-task --task-number <n> --note "<why>" <goal>
.\scripts\Invoke-RepoScript.ps1 scripts\Get-GoalTaskSummary.ps1 <goal>
# let one next tick prove the goal is still stuck or now dispatchable
Remove-Item -LiteralPath .conduct-stop
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 5400
```

For lifecycle repairs, use `set-goal-status` only with Core-valid `GoalStatus` values: `Draft`, `Active`, `WaitingForHuman`, `Parked`, `Verified`, `Completed`, `Failed`, `Cancelled`, `Superseded`. Invalid values poison snapshot loads; malformed goal rows are quarantined on load (landed in `1d2223c2`), which preserves the rest of the store but hides the damaged goal until repaired.

### WorkspaceReady + no ready batch after Developer

**Symptom.** `conduct --loop` repeatedly holds or escalates a goal at `WorkspaceReady` / "no ready batch" after the Developer task completed, with Tester or Reviewer tasks still `Assigned`. In the 2026-06-27 incident, example goals `6d76b216` and `abf4967a` showed this pattern; future incidents will have different prefixes.

**Diagnosis.** First inspect the emitted `READY_BLOCKED` / held reason. A `dirty-worktree`
diagnostic includes the commit-worthy paths that prevented dispatch. Confirm it from the goal
worktree before changing policy or providers:

```powershell
git -C .orchestrator-worktrees/<goal-prefix> status --short --untracked-files=all
```

Preserve real work. Remove only a confirmed disposable artifact; never broadly clean, stash, or
ignore the goal worktree. `recover` reports these paths and deliberately does not mutate them.

If no dirty-worktree diagnostic is present, identify the assigned-but-not-starting verification task:

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
- Long text belongs in throwaway files: `retry`, `note`, `progress`, `verify-manual`, `recover`, `answer`, and `add-task` all accept `--text-file <path>`; `goal` accepts `--brief-file <path>` (also `--text-file`); `backlog-add` accepts `--body-file <path>` (also `--text-file`). Delete the scratch input after the command succeeds.
- `subscription-dispatch <task> --confirm-limit-review --text-file <path>` records a long usage-limit review note without putting it on the command line.
- A loop **crash** can leave in-flight tasks reconcilable, but it can also leave a goal in `Failed` lifecycle that `recover` followed by the conductor sweep clears. A graceful `.conduct-stop` follows the detach/fallback behavior documented in §1.
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

**Loop-exit and handoff conditions.** A plain `conduct --loop` batch run halts automatically and prints `LOOP_STOP reason=all-done-or-escalated` when every active goal has reached a terminal state (`CleanedUp`, `Failed`, `Blocked`) or been escalated. Goals created *after* a plain loop started are **not** picked up after that terminal stop — start a new `conduct --loop` to process them. Landing conductor infrastructure triggers an immediate drain/build/self-check handoff only when `MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED=true`; the default-off path requires the manual bounce in §1 to arm the landed code. When enabled, `LOOP_RELAUNCH_ROLLBACK` or `LOOP_HANDOFF_FAILED` reports a failed infrastructure relaunch and whether the incumbent can continue. Separately, when `--max-duration` expires while active work remains, the bounded run attempts a successor handoff and emits `LOOP_HANDOFF` on success or `LOOP_HANDOFF_FAILED` on failure; that run needs manual relaunch after a failed renewal. Manual relaunch is also needed after a deliberate `.conduct-stop`/Ctrl-C or after creating new goals following an all-done stop. Use `conduct --loop --daemon` only for controlled active-goal intake: it stays alive on an empty backlog and picks up goals submitted later, but it is not a safe "drain the backlog" mode.

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
git switch -c verify/<goal-prefix> main
git merge --no-ff goal/<goal-prefix>
# run the goal's own new test classes here, on the scratch branch
git switch main
git merge --no-ff goal/<goal-prefix>
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 goal-mark-landed <goal-prefix> --confirm-goal-mark-landed
```

Use this only after reviewing the diff and confirming the branch is the intended `goal/<prefix>`. Pre-landing verification merges happen on a scratch branch such as `verify/<prefix>`, never on `main`; `main` moves only at the actual landing step. Until the c0624af9 verification fix lands, a goal can reach `Verified` without executed self-tests, so before any hand-landing run the goal's own new/changed test classes and keep the receipts. `goal-mark-landed` records the out-of-band merge, writes a durable retired terminal disposition, and lets the conductor continue record/cleanup steps; it is not a replacement for acceptance or review.

### 6.2.2 Optional remote mirror

Remote mirroring is disabled unless `config/mirror.json` opts in. The file names git remotes already configured in the repository; the conductor uses plain `git push`, so it works with GitHub, GitLab, Gitea, Bitbucket, and bare SSH/filesystem remotes without forge APIs.

```json
{
  "enabled": true,
  "remotes": ["origin", "backup"],
  "push": { "main": true, "goalBranch": true, "tags": true }
}
```

After a durable landing, the conductor enqueues mirror debt and starts a trusted background mirror worker; later conduct/reconcile ticks start the same background worker for due retries. The terminal sweep does not run `git push` inline. Each remote is independent: reachable remotes can push while unreachable/auth-failing remotes record `conductor:mirror:<remote>` as `MirrorFailed classification=TRANSIENT` and retry on later ticks with backoff. Mirror state lives in `.orchestrator/git-mirror-state.json`; mirror failure never escalates the goal or blocks record/cleanup.

### 6.3 Concurrency caps

Three independent constraints bound useful parallelism:

**Paid-worker admission capacity.** `maxConcurrentPaidWorkers` is bounded by the paid-worker admission pool, currently 9. This is a memory-headroom limit, independent of build capacity: the 47.9-GiB operator host recorded a paid-worker peak of 1,750,343,680 bytes on 2026-08-08, and allocating no more than one third of physical memory to paid workers gives `floor(51,385,864,192 / 3 / 1,750,343,680) = 9`. The remaining two thirds cover the OS, dashboard, builds, and acceptance gates. A gate-ready goal reserves one admission slot, so the effective cap is at most 8 while that reservation is active. `LOOP_START` reports the configured cap, admission capacity, reserved slots, and effective cap; policy-file values above the capacity produce a load warning. This fixed host-derived limit should be revisited when memory-aware admission is implemented.

**Build capacity and test artifacts.** Builds share two machine-wide file locks under the isolated dotnet root. Test execution is not slot-confined: acceptance receipts live beneath `.orchestrator/acceptance-gate-attempts/<goal-id>/`, pre-review focused receipts beneath `.orchestrator/pre-review-evidence-attempts/<goal-id>/`, and operator test commands use invocation-owned directories. Both receipt roots follow the goal artifact retention plan and `Show-TestDurations.ps1` reads both by default. Per-goal build artifacts remain reusable under the isolated dotnet goal root.

After the slot-free gate has soaked cleanly for at least three days, an administrator may remove the obsolete inbound rules once:

```powershell
.\scripts\Remove-TestSlotFirewallRules.ps1
```

The script is idempotent, matches only `MCG-testhost-slot*`, and writes every removed rule definition to `.orchestrator/firewall-rule-removal/` before reporting success. Keep that receipt so a rule can be reconstructed if the soak premise proves wrong. This is a one-time operator action; the orchestrator never mutates firewall policy at runtime.

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
| `.orchestrator/logs/conduct-events.log` | canonical structured conduct event stream (JSON lines with `eventKind`; stable path, rotated by size; listen from the current end) |
| `.orchestrator/logs/`, `.orchestrator/prompts/` | per-dispatch worker logs (`*.out.log`/`*.err.log`/`*.exit.txt`) and the rendered worker prompts |
| `.orchestrator-worktrees/<goal-prefix>` | the goal's isolated git worktree on branch `goal/<prefix>` |
| `.orchestrator-context/<goal-id>` | worker context artifacts for a goal |

`--text-file` is the uniform throwaway vehicle to pass long text past the command-length cap for `retry`, `note`, `progress`, `verify-manual`, `recover`, `answer`, and `add-task`. `goal --brief-file <path>` and `backlog-add --body-file <path>` are the preferred command-specific forms, with `--text-file` aliases still accepted by current CLI help. The durable copy becomes the goal objective, task note, verification receipt, answer, or backlog item, so **delete the scratch input** afterward.

Dogfood goal-boundary evidence is durable SQLite state, not a tracked markdown append log.

```powershell
mcg-orchestrator.cmd dogfood-log list --limit 10
mcg-orchestrator.cmd dogfood-log add <goal-prefix>
mcg-orchestrator.cmd record-goal <goal-prefix>   # compatibility alias for add + render
```

`DOGFOOD_LOG.md` remains only as a pointer for operators and should not receive new durable entries.

### State backup and restore

Use `scripts\Backup-OrchestratorState.ps1` for machine-local `.orchestrator` backups. It writes timestamped archives to `%USERPROFILE%\backups\mcg-orchestrator\` by default, snapshots SQLite stores with `sqlite3 .backup`, includes the goal/event/log artifact directories plus `agents.json` and `workers.json`, verifies an extracted copy with row counts when `-VerifyRestore` is supplied, and retains the latest 7 daily plus 4 weekly archives unless overridden.

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Backup-OrchestratorState.ps1 -VerifyRestore
.\scripts\Invoke-RepoScript.ps1 scripts\Backup-OrchestratorState.ps1 -Install -DailyAt 03:20
```

To restore after a machine loss, first restore git branches and worktrees from origin. Then stop the conductor/dashboard, extract the selected backup zip, copy the extracted `.orchestrator` directory back to the repository root, and restart normal operator commands. For a single-store repair, keep the current `.orchestrator` directory intact and replace only the intended restored `*.db` file after verifying the archive in a scratch directory.

For rare lifecycle/task desync repair, `scripts\Set-OrchestratorGoalStatus.ps1` / `Invoke-OrchestratorSqliteTool.ps1 set-goal-status` update both the indexed `goals.status` column and the serialized snapshot in `.orchestrator/state.db`. They are operator recovery tools, not normal workflow commands; prefer `recover`, `retry`, and `conduct` first. Use only Core-valid statuses, and make the write during a quiet window after `LOOP_STOP`.

---

## 8. Operating discipline (hard-won)

- **One canonical path.** Prefer `conduct --loop`; the manual verbs (`subscription-dispatch → start-dispatch → refresh-dispatch → accept`) are granular fallback only.
- **Backlog is candidate input, not an automatic queue.** A stale/open backlog can contain obsolete, overlapping, or underspecified work. Before daemon mode, curate a small active set with `backlog-list` + filtered `backlog-intake "<heading>" --create-goal --backlog-coverage <full|slice>` for ordinary implementation goals; use `backlog-intake "<heading>" --create-simple-goal --backlog-coverage <full|slice>` only for the explicit `simple-goal` exception, not because an item looks narrow. Choose `full` only when the goal covers the complete source item; choose `slice` when remaining work must stay Open. Any `goal-plan --create-*` command likewise requires `--backlog-coverage <full|slice>`; avoid unfiltered or multi-filter batch creation unless you have reviewed dependencies and file scopes. Keep daemon runs bounded with `--max-duration` until the active set is proven healthy.
- **Keep long waits out of the foreground.** Use `scripts\Start-OrchestratorCommand.ps1` through `scripts\Invoke-RepoScript.ps1` for long acceptance/conductor runs, then keep one listener on `.orchestrator/logs/conduct-events.log`. Use `next <goal> --full`, `Find-OrchestratorLocks.ps1`, or `Show-OrchestratorLogArtifacts.ps1` only at a decision point or after expected stream heartbeats stop. Avoid raw `Start-Sleep` loops and broad `.orchestrator` filesystem commands.
- **State writes vs a running loop.** `retry`, `progress`, and `verify-manual` are safe during the loop because they append to `operator-intents.db` and the tick alone mutates `state.db`. `backlog-add` also uses a separate store. Direct repair tools and remaining whole-kernel verbs such as `goal --brief-file`, `abandon-goal`, `park-goal`, and `unpark-goal` still bypass the inbox; serialize those between loop runs until their migrations land. Read-only inspection (`status`, `next`, git on worktrees, loop output) is always free.
- **Scope the test suite to the changed project**, not the whole solution; run it foreground (or poll). Use `scripts/Invoke-TestSummary.ps1 -Target <project>` for compact results. The helper builds by default and launches the .NET 10 Microsoft.Testing.Platform apphost directly; use `-NoBuild` only when the existing apphost is known current. Do not substitute raw `dotnet test`, whose VSTest target is unsupported for these projects.
- **Provider requirements:** `claude-cli` needs a valid model id (`claude-sonnet-4-6`/`sonnet`/`haiku`/`opus`) **and** a permission mode (the default profile carries both). `codex-cli` on a ChatGPT account accepts `gpt-5.5`. API runs (`run`/`api-run`) have **no file access** — embed needed data in the task description.
- **The brief is the unverified root of trust.** The gates verify "output matched the spec," never "was the spec right." A sloppy brief lands a plausible-but-wrong implementation on green tests. Specify external contracts (happy *and* unhappy path), observable success, ownership/lifecycle, and the verification class before dispatch.
- **Shared-service changes ripple to integration tests.** A brief that changes a widely-consumed service (the failure classifier, the binding resolver, a kernel API) must require the worker to find that service's consumers (a call-site / reverse-dependency search) and run the **dependent integration tests** (e.g. `RunGoalService_*`) in self-verify — not just the changed file's own unit tests. Otherwise the ripple is caught only by the full-suite acceptance gate, costing a Developer-retry cycle. Observed 2026-06-25: two lanes changing `DispatchFailureClassifier` and the binding resolver both escalated at acceptance on `RunGoalService_*` because their briefs scoped self-verify too narrowly.
- **Reproduce before you theorize.** When an external CLI/model/tool fails, run the smallest reproducing command before concluding a cause.
- **Retire terminal ghosts durably.** `goal-mark-landed` now writes a retired terminal disposition after out-of-band landing, and terminal sweeps suppress standing retired dispositions. Once 4f970f1d lands, `abandon-goal` will do the same for abandoned goals; until then, treat abandon retirement as in progress rather than guaranteed.

---

## 9. Where else to look

- `AGENTS.md` — output/diagnosis/spec discipline and architecture invariants (read after this).
- `.agents/skills/orchestrator-worker-verification/SKILL.md` — how to verify a worker result before trusting it.
- `next <goal> --full` and `readiness <goal>` — the live, authoritative state of any goal.
