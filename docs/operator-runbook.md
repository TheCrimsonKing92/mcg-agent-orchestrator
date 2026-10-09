# Operator Runbook

On a new Windows host, run `.\mcg-orchestrator.cmd host-exclusions --apply` once from an elevated PowerShell as the same user account that runs the orchestrator. Run `host-exclusions` without a switch to inspect the required roots first; it changes nothing. New orchestrator roots must be created through `OrchestratorTempRoot`, `DotnetBuildEnvironmentManager` root resolution, `TempRootJanitor` low-integrity root resolution, or the `GoalAcceptanceVerifier` root-name members and included by `HostScanExclusionRoots`, so this verb lists the roots that the creating code owns.

Repository scripts, diagnostic probes, and evidence utilities use PowerShell and .NET. Do not use Python for this work or introduce a Python installation requirement for repository users.

**If you are a new AI instance about to operate this orchestrator, read this first.** It is the canonical guide for *driving, observing, and recovering* goals. At session start, read the [operator lessons](cli-reference.md#operator-lessons) for the applicable scope; record new situation-to-rule lessons with evidence in that store. It supersedes the older "Core Loop" in `docs/cli-reference.md` (manual verbs) and the manual sequence in `.agents/skills/orchestrator-dogfood/SKILL.md` — those are fallbacks, not the default path.

Most commands below are shown through the launcher: `.\mcg-orchestrator.cmd <command> ...` (or `./mcg-orchestrator.cmd` from a POSIX shell). For Codex/operator foreground CLI calls, prefer `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 <orchestrator-args...>`; it runs the built app through the repo-bounded helper prefix and avoids repeated permission prompts from ad-hoc `dotnet` commands. For other checked-in helper scripts, prefer `.\scripts\Invoke-RepoScript.ps1 <repo-relative-script.ps1> ...`; for source windows, use `.\scripts\Invoke-RepoScript.ps1 scripts\Show-RepoFileSlice.ps1 <path> <start> <count>` instead of PowerShell pipelines. For compact monitoring, use `.\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal1> <goal2>`; for SQLite store reads/repairs, use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 ...`. In human-facing status boards and transition updates, lead with a short meaningful goal description and put the goal prefix in parentheses; never make the operator decode a stream of bare hashes. Never run the bare launcher with no command — it opens an interactive REPL that holds build-output locks.

On a clean checkout, a read-only launcher may report that its current artifact and no-restore build assets are unavailable. Establish that availability prerequisite while online with `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-PackageBootstrap.ps1`, then retry the read. The bootstrap restore disables auditing only for that one asset-recovery invocation. Run the separate fail-closed vulnerability audit with `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-PackageAudit.ps1`; if that audit fails after writing error-bearing assets, rerun bootstrap before routine reads. Both online commands access configured package sources and can update NuGet's user cache; routine read-only helpers never invoke either one themselves.

For exact process ownership, command-line, or lineage inspection, use `.\scripts\Invoke-RepoScript.ps1 scripts\Get-RepoProcessInfo.ps1 -Id <pid> -IncludeChildren` (optionally `-Newest <n>`). It replaces ad-hoc `Get-CimInstance` and inline process-tree snippets. Keep every Codex harness command shell-plain and single-purpose: do not join commands with `;`, `|`, or `&`, and do not construct PID lists with comma expressions. Those forms are evaluated as separate or unmatched permission segments and cause avoidable approval prompts or sandbox routing. When an operation needs compound logic, put it in or extend a checked-in repo-bounded helper, then invoke that helper through `Invoke-RepoScript.ps1`.

**Codex harness timeout floor:** distinguish the outer shell/tool budget from an orchestrator operation's own timeout. Give `Get-OrchestratorSnapshot.ps1` at least **120 seconds**, even for a bounded goal list; give ordinary foreground status/backlog reads at least **60 seconds**. For a helper that waits, builds, tests, or runs acceptance, set the outer budget to the helper's documented/internal maximum plus at least 30 seconds of startup and result-reconciliation margin. If a tool call yields a running cell, resume that cell with the same remaining budget instead of starting the command again. An outer timeout means the result was not collected; it is not evidence that the underlying operation failed or stalled.

---

<a id="landing-progress-discipline"></a>
## Landing progress discipline

For implementation dogfood, measure progress by accepted changes reaching `main` with required evidence. Worker completions, review rounds, diagnostics, and busy acceptance slots are intermediate work. They do not establish that the landing pipeline works. Keep the priority order: reliability and stability, landing throughput, then token efficiency without weakening quality.

**Reassess before another correction or full gate run when the same blocker recurs, when separate goals fail each other's repaired tests, or when completed work accumulates without landings.** Do not wait days for this pattern to resolve itself. Inspect current main, exact candidate commits, individual failed tests and retained receipts, and live operation handles. A long-running operation that is demonstrably advancing is a verified wait; an expired observation window is not permission to restart it.

1. **Find the shared landing constraint and give it an owner.** Record the next landing candidate, each exact blocking failure, when it was first observed, and the next discriminating experiment or repair. Use existing backlog items and goal records; keep durable evidence out of scratch files. Separate product defects, test defects, evidence-ownership defects, and unresolved mechanisms. A shared check name or concurrent execution does not prove common cause or baseline attribution.
2. **Stop feeding a blocked landing path.** Pause unrelated new intake and expansion of existing rearchitecture work while repairing the shared constraint. Continue independent repairs that can unblock landing, and preserve useful in-flight work. Do not serialize everything or kill healthy workers merely to reduce the queue. Increase parallelism only where it can increase accepted throughput; workers cannot compensate for a broken gate or a saturated serial phase.
3. **Integrate dependent repairs before paying for acceptance again.** If A's gate fails the defect repaired by B and B's gate fails the defect repaired by A, validate a combined candidate in an isolated workspace. Give one goal explicit ownership of the integration scope, obtain cross-family review of the combined diff, and bind execution evidence to that candidate. Record which of the other goal's obligations are superseded and which remain open to prevent duplicate or divergent landings. Parent approvals and test receipts do not prove the merged tree. Remaining failures still block according to the normal acceptance policy; combining fixes is not a waiver.
4. **Repair impossible evidence ownership instead of recycling workers.** Use the role capability matrix to distinguish worker-verifiable criteria from acceptance execution, operator observation, and context-packaging obligations. A reviewer cannot produce acceptance evidence when acceptance itself waits for that reviewer. Keep those obligations pending and enforceable at their actual owner; do not drop criteria, invent evidence, or mark the goal passed to escape the cycle. A bounded, evidence-backed operator recovery is an interim measure, not a substitute for fixing the ownership rule.
5. **Exercise judgment over cross-family review.** Require consequential findings to identify the violated requirement, concrete evidence, and what would resolve the finding. Challenge incorrect claims with source or execution evidence. Preserve legitimate blockers, but do not automatically promote advisories or reviewer preferences into mandatory correction rounds. Required cross-family review remains required. Record disputed blocking findings and the evidence-backed rebuttal in the existing review or goal record; resolve them through the normal review, operator, or escalation authority rather than silently downgrading them. Before another round, identify what new evidence or changed source makes it useful; repeated agreement or reworded feedback is not new verification.
6. **Make each expensive run answer a changed question.** Reproduce narrowly, repair, then run the required acceptance scope on the candidate intended to land. Do not knowingly repeat an unchanged doomed full gate. Deferring a rerun leaves acceptance failed or pending; it never credits an unrun candidate as passed or bypasses required acceptance. Preserve failures and retries, and distinguish a diagnostic improvement from a repair of the diagnosed defect. Record known failures before integration validation, but do not automatically label them inherited, or new failures candidate-caused: causal attribution still requires discriminating evidence. Never remove assertions, weaken gates, or reduce the requested scope just to obtain green results.

At each meaningful operator checkpoint, report what reached main since the previous checkpoint, the next landing candidate, the oldest unresolved landing blocker and its age, and the action that can resolve it. If nothing landed, say so and distinguish new decision-changing evidence from repeated observation. Resume broader intake when the shared landing constraint is demonstrably resolved and the pipeline can accept changes; a locally green test or a larger review queue is not that demonstration.

This discipline comes from the September 2026 dogfood failure: completed branches accumulated behind reciprocal test failures and acceptance-owned criteria rejected at review, while the driving agent spent rounds improving diagnostics and expanding work. The lesson is to restore the landing path and its ownership rules, not to count activity as delivery.

## 1. Golden path (conductor-first)

The conductor drives a goal through its **entire** lifecycle. You almost never call the manual verbs.

```
# 1. Create an ordinary implementation goal
mcg-orchestrator.cmd goal "<objective>" --request-key <unique-operator-key>       # Planner + Researcher + Developer + Tester + Reviewer
mcg-orchestrator.cmd goal --brief-file <path> --request-key <unique-operator-key> # long objective via throwaway file (then delete the file)

# Explicit exception only: genuinely mechanical low-risk work, or direct operator instruction
mcg-orchestrator.cmd simple-goal "<objective>" --request-key <unique-operator-key> # single Developer task

# 2. (If the refiner raised clarifications) clear them so the goal can flow
mcg-orchestrator.cmd attention dismiss <goal-prefix>    # proceed with the brief as written

# 3. Drive it (and every other active goal) to landing
mcg-orchestrator.cmd conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 5400

# 4. Observe
mcg-orchestrator.cmd next <goal-prefix> --full          # one-shot full inspection
```

For ordinary implementation goals, use `goal`: the normal dogfood pipeline is the five-role SDLC flow through Planner, Researcher, Developer, Tester, and Reviewer. `simple-goal` is an explicit exception for genuinely mechanical, low-risk work or direct operator instruction. It is not a throughput shortcut, and narrow backlog items still use `goal` unless they meet that exception.

Supply a fresh `--request-key` for each intended creation. If the foreground caller times out, do not invent a new key: run `goal-intake-status <request-key>` or repeat the exact original command with the same key. The receipt state distinguishes `still-committing`, `created`, and `failed`; a terminal `created` replay returns the original goal, while reuse with different effectful inputs fails with `GOAL_INTAKE_PAYLOAD_CONFLICT`. Identical unkeyed objectives remain separate goals by design.

Set [hard dependency links](cli-reference.md#goal-dependencies) at goal creation, before starting dispatch, so dependent work waits for its prerequisites. Adding a link does not interrupt a worker already in flight.

Before writing the objective or its acceptance criteria, assign every criterion to an evidence owner using
[`role-capability-matrix.md`](role-capability-matrix.md). Put full-suite/gate evidence in acceptance,
post-landing observation with the operator, and cross-goal receipts in context-packaging instead of asking a
worker role to produce evidence outside its enforced boundary.

When a goal deliberately removes tests, declare every removed discovered identity as its own acceptance
bullet using `test-removal: Namespace.TestClass.TestMethod`. The acceptance gate credits only declarations
corroborated as present on main and absent from the candidate, and the tamper guard additionally requires
the named method to be removed by the test-file diff. Code tokens in ordinary criterion prose never create
pattern-presence or pattern-absence checks; those checks must come from an explicit structured manifest.

`conduct --loop` runs `ConductorBatchLoop`: each tick advances every eligible goal one policy-gated step through its state machine, creates worktrees, dispatches workers, waits on them, runs the acceptance suite against the worktree, applies the change-risk gate, fast-forward-merges into `main`, records the dogfood entry in SQLite, and removes the worktree. The loop ends on its own when all goals are done or escalated (`LOOP_STOP reason=all-done-or-escalated`). A landing that changes conductor/verifier/gate/build/dispatch infrastructure triggers self-relaunch by default: the loop stops admissions, drains workers, and delegates the build, self-check, and handoff to its continuity supervisor. Set `MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED=false` (or `0`) before starting the loop to disable it. The supervisor retains the prior staged build until the new build reports `LOOP_START` and completes `MCG_ORCHESTRATOR_ACTIVATION_HEALTHY_TICKS` ticks (default 3, minimum 1). It restores the prior build on an early exit, missing `LOOP_START`, or stalled ticks and records `activation/reverted`; the same failed HEAD is suppressed until HEAD advances. If the restored build also fails, `activation/failed-both` requires operator attention. This fallback requires the normal supervisor restage setting to remain enabled. The separate bounded-run successor handoff runs when `--max-duration` is reached while active work remains and, by default, builds and self-checks a content-addressed successor from repository HEAD before launching it.

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

**Stop a loop deliberately** with `mcg-orchestrator.cmd conductor stop`. Check it with `mcg-orchestrator.cmd conductor status`; start it deliberately with `mcg-orchestrator.cmd conductor start` (`--clear-stop` clears a prior stop request). A `.conduct-stop` is a detach, not a drain: at the next stop check the loop starts no new dispatches, attempts to detach live workers, persists the detached state, and exits without waiting for those workers to finish. A successful detach leaves the task running for a successor to reconcile; if detachment fails, the fallback cancels the dispatch so it can be requeued. Prefer a quiet window with no live workers before a deliberate stop. Ctrl-C is not equivalent: the conduct-loop path has no `Console.CancelKeyPress` handler, so Ctrl-C terminates without the detach/checkpoint path.

`mcg-orchestrator.cmd conductor setup` creates or upgrades every versioned registered store and prints its name, recorded version, and database path. It works with or without a running conductor and is safe to re-run. `conductor start` performs the same setup automatically before launching; a setup error prevents launch.

### Manual bounce fallback after loop-affecting code lands

Use this procedure when self-relaunch is disabled, when activation reverts, or when the running supervisor predates the automatic activation implementation. Re-arm the landed code deliberately:

1. Inspect active goals with `Get-OrchestratorSnapshot.ps1` and exact dispatch inventories; when practical, wait until no worker is in flight because `.conduct-stop` detaches rather than drains.
2. Run `mcg-orchestrator.cmd conductor stop`, record the reported PID, and wait for `LOOP_STOP` in `.orchestrator\logs\conduct-events.log`. Use `conductor status` to inspect the current state.
3. Confirm that recorded PID is no longer running with `Get-RepoProcessInfo.ps1 -Id <pid> -IncludeChildren`. An orderly exit normally removes `conduct-loop.lock`; if it remains, remove it only after the owner PID is confirmed dead.
4. Run `mcg-orchestrator.cmd conductor start --clear-stop`. It uses `Start-OrchestratorCommand.ps1` without `-AppDll`, whose HEAD/source freshness check rebuilds the app when the landed code is newer than the current binary.

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal-prefix>
$conductorPid = Get-Content -LiteralPath .orchestrator\conduct-loop.lock -TotalCount 1
.\mcg-orchestrator.cmd conductor stop
# Wait for LOOP_STOP, then verify the recorded lock PID is dead:
.\scripts\Invoke-RepoScript.ps1 scripts\Get-RepoProcessInfo.ps1 -Id $conductorPid -IncludeChildren
# Only after the PID is confirmed dead; a live-owner lock failure must remain visible:
if (Test-Path -LiteralPath .orchestrator\conduct-loop.lock) {
    Remove-Item -LiteralPath .orchestrator\conduct-loop.lock -ErrorAction Stop
}
.\mcg-orchestrator.cmd conductor start --clear-stop
# Fallback launcher when the conductor CLI is unavailable:
$env:MCG_DISPATCH_MAX_RUNTIME_MIN = '120'
.\scripts\Invoke-RepoScript.ps1 scripts\Start-OrchestratorCommand.ps1 -Name conduct-loop-daemon conduct --loop --daemon --watch --poll-seconds 120 --max-duration 43200
```

#### Is the loop alive? Read the lock, then verify that PID

Do not answer this from the process list. Every direct way of asking "is the conductor running" returns a
false negative, and all of them look identical to death:

- The conductor runs as **`dotnet`**, never as `Mcg.AgentOrchestrator.App`. Filtering by the product name
  finds nothing.
- `tasklist` truncates image names at 25 characters, so even the right name may not match a longer pattern.
- `tasklist /FI "..."` is mangled by Git Bash path conversion (`/FI` becomes a path). Filter in PowerShell,
  or pipe `tasklist` to `grep` with no `/`-flags.
- `Get-Process` exits non-zero when the process is absent, which is indistinguishable from a lookup error.

The authoritative answer is the lock file, which names the current owner:

```powershell
Get-Content -LiteralPath .orchestrator\conduct-loop.lock   # line 1 = PID, then heartbeat timestamps
Get-Process -Id <pid> | Select-Object Id, ProcessName, StartTime
```

**A `LOOP_STOP` event does not mean the loop is gone.** At `--max-duration` the incumbent exits and a
successor takes the lock within seconds. Compare the lock's timestamp to the `LOOP_STOP` timestamp: a lock
written *after* the stop is a successful handoff, and the new PID's `StartTime` will match the stop moment.
Treating that as a dead loop and relaunching starts a second conductor against a lock another process owns.

Liveness is the **tick counter advancing**, not gate progress and not worker activity. An idle loop with an
empty `blocked=` list is healthy, not wedged. See the stuck-goal playbook before concluding otherwise.

#### Which binary is the running conductor actually executing?

The conductor does not execute the in-tree build. It runs from a pinned copy under `%TEMP%\mcg-run\<hash>\`,
so `src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\...dll.git-head` tells you only what the last build
produced. Those two diverge exactly when it matters: after a landing rebuilds the tree while a stale loop
keeps running. Read the marker from the run directory named on the process command line:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Get-RepoProcessInfo.ps1 -Name dotnet -CommandContains conduct -Newest 10
Get-Content -LiteralPath "$env:TEMP\mcg-run\<hash-from-that-command-line>\Mcg.AgentOrchestrator.App.dll.git-head"
git rev-parse HEAD
```

Equal means the running conductor is current. If they differ, quantify the gap and check whether the missing
commits touch the behaviour you are reasoning about, because that is what separates "staleness explains this
symptom" from "staleness is real but irrelevant here":

```powershell
git log --oneline <marker-sha>..HEAD
git log --oneline <marker-sha>..HEAD -- <paths-you-care-about>
```

Note that the conductor runs as `dotnet.exe`, not as `Mcg.AgentOrchestrator.App`, so filtering the process
list by the product name finds nothing and reads as "the loop is dead".

#### The max-duration handoff re-stages and verifies the successor

At `--max-duration` the supervised child exits, then its still-running continuity supervisor builds repository
HEAD into the existing commit-keyed staging area, validates the staged `.dll.git-head`, self-checks the
content-addressed run directory, and launches that staged DLL with `--continuity-child`. The supervisor waits
for the successor's first `LOOP_READY ` line before recording success. The successor emits that stable prefix,
with a `ts=` timestamp, to its generation stdout and `conduct-events.log` after acquiring the loop lease and loading state, before its pre-loop terminal sweep;
`LOOP_START ` remains the operator-visible signal that the sweep finished and the tick loop began. If staging
or readiness fails, the supervisor records the typed cause and relaunches the incumbent command; after repeated
staging failures it stops retrying builds for the lifetime of that supervisor and keeps renewing with the
incumbent command.

Tail `.orchestrator/logs/conduct-events.log` for terminal evidence. Success is `LOOP_HANDOFF` with both
`stagedSourceCommit=<sha>` and `repositoryHead=<sha>`; failure is `LOOP_HANDOFF_FAILED phase=<phase>
reason=<cause>`. `LOOP_HANDOFF_STAGING` makes the bounded build gap visible. Set
`MCG_ORCHESTRATOR_MAX_DURATION_RESTAGE=0` (or `false`) before launch only as an operator opt-out to restore the
previous incumbent-binary renewal behavior. This setting is independent of
`MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED`.

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
| `readiness <goal-prefix>` | **start blockers** — high-risk objective terms and ownership approval (run this first when a goal won't dispatch); read-only terminal-goal diagnosis reports pending repairs without applying them |
| `readiness-repair [goal-prefix]` | apply the repairing terminal-goal sweep, surface attention, and print start blockers; can change state, as does `goal-recovery <goal-prefix>` |
| `durations` | role/complexity runtime medians plus attempts-per-task; use it to spot slow lanes and retry redundancy before changing worker mix or loop policy |
| `durations --by-model` | the same duration report sliced by model/provider; use it when a role looks slow but provider choice may be the real variable |

Listen to the stable conduct event stream first, starting at the current end so old runs are not replayed:

```bash
tail -n 0 -F .orchestrator/logs/conduct-events.log
```

```powershell
Get-Content -LiteralPath .orchestrator\logs\conduct-events.log -Tail 0 -Wait
```

It is JSON lines with `timestamp`, `eventKind`, `goalId`, and `detail`; the stable path survives rotation. Key `eventKind` values include `loop-start`, `loop-stop`, `loop-handoff`, `loop-relaunch`, `loop-relaunch-rollback`, `watch-transition`, `goal-escalation`, `goal-stalled`, `gate-progress`, `acceptance`, and `lock-blocker`. Treat `watch-transition` and `goal-escalation` as the immediate onward-or-operator-decision signals. `goal-stalled` is the delayed watchdog for other stable holds; do not omit it from a filter intended to detect every stuck lane. The `detail` field preserves the compact loop line (`TICK`, `held`, `escalated`, `GOAL_STALLED`, `LOOP_STOP`, `LOOP_RELAUNCH_*`, `LOOP_HANDOFF`, `LOOP_HANDOFF_FAILED`, `PHASE_TIMING`). Infrastructure-triggered `loop-relaunch` events occur only when self-relaunch was enabled. A `loop-relaunch-rollback` or infrastructure-triggered `LOOP_HANDOFF_FAILED` names the triggering goal and failed phase and reports whether the incumbent can continue; do not assume authority was reclaimed unless the event says so. `gate-progress` records phase changes and 30-second heartbeats for long acceptance checks, so a quiet worker does not require state or process polling while those heartbeats continue. `PHASE_TIMING` receipts are the tick latency profile: `sweep`, `prewalk`, `per-goal-walk`, and `dispatch-prep` show where the conductor spent the tick. Use them when ticks feel slow before blaming a worker.

The `gate-phase-breakdown` progress line reports `shard_concurrency_effective` (the scheduler cap after host-core limiting), `shard_concurrency_peak` (the observed simultaneous shard count), `longest_lane_ms`, and `slot_wait_ms` in addition to phase durations. A value of `unavailable` means the attempt never established that measurement; it is not zero. Content-keyed reuse covers partitioned lanes and the whole-project core tests check (`partition_id=core-tests`), emitting `reuse_rule=closure`, `source_attempt_id=<attempt>`, and `closure_hash=<sha256>` in the partition-cache audit text. Reuse requires matching manifest identity, lane-filter hash (or whole-project check identity), and project dependency-closure hash. Core tests consult the closure index only when their tree-scoped lookup has no green verdict; no index entry emits `missed_lane=core-tests:no-green-verdict-for-whole-project-closure:<closure_hash>`. Forced full reruns and unavailable closure hashes execute normally, and the CLI and acceptance execution-owner whole-project checks still require an identical tree.

Transient state-store lock exhaustion is goal-scoped. `TICK_CHECKPOINT_HOLD` (or `TICK_LOAD_HOLD` for a scheduled reload) records `store`, `database`, `operation`, typed `sqlite_code`/`sqlite_extended_code`, `attempt`, `elapsed_ms`, terminal `disposition`, and `holder=unknown` unless a controlled fixture supplied an observed holder. The conductor keeps its lease, leaves the last durable baseline unchanged, and continues other goals. `TICK_CHECKPOINT_RETRY` means a later bounded checkpoint is still held; `TICK_CHECKPOINT_RECOVERED` means the pending snapshot became durable and the goal was re-admitted; `TICK_LOAD_RECOVERED` clears a scheduled-load hold after a later reload succeeds. Recovery receipts repeat the recovered episode's typed SQLite code and the last failed acquisition's `elapsed_ms` so the hold and recovery can be correlated without inference; `elapsed_ms` is not the cross-tick hold duration. Do not stop or restart the conductor merely for these receipts. Investigate only if recovery receipts do not arrive across later ticks, using the named database and operation rather than inferring a lock owner from concurrent activity.

An `ERR_REVIEW_FINDING_IDENTITY_MOVED` contract failure means an open stable finding ID was submitted at a different file/region anchor and the old anchor was not present in the system-derived touched-anchor set (`src/Mcg.AgentOrchestrator.Core/Domain/ReviewFindings.cs:858-879`). Dispatch-owned touch context comes from the reviewed commit and derived anchors (`src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.Recording.cs:606-621`); a missing reviewed-commit baseline leaves that proof unavailable. The conductor cannot safely infer whether the code legitimately moved or the Reviewer changed the finding's identity, so it does not send ambiguous repair work to Developer. It emits an immediate `goal-escalation` with `suppression=missing-system-derived-round-diff-proof` for operator adjudication (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs:1279-1288`); normal classifiable Reviewer `needs-work` still emits only its onward `watch-transition`. Use `goals` to reconcile the derived `active-with-failed-task` condition across all roles; this visibility is report-only and does not resume pre-existing parked work.

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

For slow goal creation, launch with `--request-key`; the launcher JSON echoes `requestKey` with `pid`, `stdoutPath`, and `stderrPath`. Poll durable state instead of treating an empty or partial log as failure:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Start-OrchestratorCommand.ps1 -Name goal-intake goal --brief-file <path> --request-key <unique-operator-key>
.\mcg-orchestrator.cmd goal-intake-status <unique-operator-key>
```

An abrupt process exit may leave a receipt in `still-committing`; automatic takeover is intentionally unsupported because a bare PID is not sufficient ownership proof. Confirm the original process and logs before escalating or choosing a new operator-approved key.

For worker logs, use the same bounded helper instead of ad hoc `.orchestrator` PowerShell reads:

```
.\scripts\Invoke-RepoScript.ps1 scripts\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix <goal> -TaskPrefix <task> -TailLines 20
```

---

### Remote lane offer policy

During an acceptance gate, `REMOTE_LANE_POLICY` reports each eligible lane's `decision=offer|keep-local` and `reason`. `L`/`Ln` are the local duration estimate in seconds and sample count, `R`/`Rn` the remote estimate and accepted sample count, and `F` the expected local finish in seconds. Every line includes `Fn`, the consecutive remote failure count for that lane/filter binding. A `remote-failing` line also includes `Fage`, the newest counted failure's age in hours to one decimal place (negative if its timestamp is ahead of the decision clock).

Rules run in this order: `short` keeps lanes under 60 seconds local; `remote-failing` keeps a lane local after at least **3** consecutive failures while the newest is less than **6 hours** old; otherwise `remote-slower` keeps it local when the remote estimate exceeds `F`, and `fits` offers it. At exactly 6 hours or later, the normal duration rules apply again, allowing a probe. A failed probe restarts the 6-hour hold; an accepted probe clears the count. Keeping a lane local does not extend the hold.

The count comes from the last 500 ledger lines in append order: `remote-red`, `late-after-fallback`, `trx-incomplete`, `lease-expired`, `lane-timeout`, and `unexpected-not-executed` count since the latest `accepted` record. `not-eligible-exclusive-resource`, `shadow-skipped-no-executor`, `cancelled-after-grace`, and unknown outcomes neither count nor reset the streak. Remote duration estimates still use accepted runs only.

## 5. Stuck-goal playbook (symptom → first command)

This is the most important section. Match the **observable symptom** to its cause and first command.

Before driving or reviving a parked or long-idle goal, verify that its premise still holds: inspect current `main`, check whether the requested content already landed, and reproduce the smallest reported failure or measurement. Do this before `recover`, `unpark-goal`, or another paid dispatch. If the brief is stale but the goal is still useful, replace it in place with `revise <goal> --brief-file <path> --reason-file <path>`; the new version becomes authoritative while in-flight dispatches and completed tasks remain unchanged.

Known direct-command limitation: [acceptance](cli-reference.md#acceptance-and-gate-reads) is a state-changing gate/landing operation, not a status read; it can finish the full suite and then fail at `stage=state-guard` even when the worktree evidence is otherwise usable (tracked as `550cf0f0`). Use the linked read-only gate report for inspection. After one such direct-command failure, stop; do not repeat the suite as its error text suggests. Preserve the worktree and verification evidence, then run/resume the conductor so its sweep performs the acceptance and landing path automatically.

| Symptom (in loop output or `status`) | Cause | First action |
|---|---|---|
| `escalated at WorkspaceReady - No tasks in ready batch` **but `status` shows tasks Assigned** | The goal's file scope touches a **high-risk ownership area** (`scripts/`/`.ps1`, `src/.../Infrastructure/`, build-system, config, skills) — under a non-Permissive policy these need operator approval. (Or a high-risk *objective term* like "production"/"auth".) | `readiness <goal>` to read the exact blocker; use `readiness-repair <goal>` to apply terminal-goal repairs. Re-run the loop under `--policy Permissive` (auto-approves ownership). |
| `escalated at Failed - operator action required` | A task is in `Failed` status. | `status <goal>` to find the Failed task. If that role can repair the failure, `recover <goal> --text-file <path>` resets stuck/Failed/Cancelled tasks to dispatchable; re-run the loop. If that role cannot act on the missing work (e.g. a Tester/Reviewer on a Developer defect), route the upstream task with `adjudicate --goal <goal> <upstream-task-number> route --cause <cause> --text-file <path> --evidence <reference>`; name the Developer task for a source defect. This retries the named task and invalidates downstream evidence. |
| `escalated at AwaitingClarification` | The spec-refiner asked questions. | `attention dismiss <goal>` (proceed with the brief) or answer them, then re-run. |
| Workers repeatedly invent evidence for an infeasible acceptance criterion | The authoritative refined brief still requires the criterion; retry notes cannot change it. | During a quiet window, run `goal-amend <goal> --waive <criterion-number|exact-text> --reason-file <path> [--actor <name>]`. The durable waiver, reason, actor, and timestamp appear in subsequent briefs and goal events. |
| A task shows `[Cancelled]` (e.g. a loop was stopped mid-dispatch on an older build) | Interrupted dispatch. | `recover <goal> --text-file <path>` — it now revives Cancelled tasks too. |
| `escalated at Verified - Acceptance verification failed` | The acceptance build/test suite failed against the worktree (a real defect, a worker-written test bug, or a gate defect). | Inspect the worktree, run the focused failing check there, fix + commit in the worktree, then re-run the conductor and let its sweep retry acceptance. If a gate defect blocks an otherwise green goal, file and fix the gate bug, then re-run acceptance through the conductor sweep rather than by repeatedly invoking direct `acceptance`; do not bypass the gate. |
| `acceptance <goal>` prints **"not accepted"** with `Tasks passed: N/5` | A task isn't verified yet (often a verification-role task); [acceptance is an operation](cli-reference.md#acceptance-and-gate-reads), so use the linked read for inspection. | `status <goal>` → if a Tester/Reviewer is `Failed`, recover only when that role can repair the failure; otherwise use the upstream Developer route above. Re-run the conductor; the goal reconciles `Failed → Active`. |
| `status <goal>` shows goal `Completed` while one or more tasks are still `[Assigned]` after a retry | Lifecycle/task desync from a retry or failed conductor pass. The conductor may refuse to start the assigned task because the persisted goal status is terminal. | First try `recover <goal> --text-file <path>`. If it remains `Completed`, use the repo-bounded repair helper: `.\scripts\Invoke-RepoScript.ps1 scripts\Set-OrchestratorGoalStatus.ps1 --status Active <goal>`; then re-run `conduct <goal> --policy Permissive`. |
| Goal is intentionally `Parked` and needs to resume | Operator parked it to stop churn or wait for external context. | `unpark-goal <goal> --text-file <path> --confirm-goal-unpark`, then re-run the loop. Use `park-goal <goal> --text-file <path> --confirm-goal-park` to pause it again. |
| A task stays `[Failed]` / `[Running]` after the real dispatch round is dead, or a retry is blocked by stale task state | The task row is pinned even though the worker round has no useful forward path. | Confirm with `scripts\Get-GoalDispatchInventory.ps1 <goal>` and `scripts\Get-GoalTaskSummary.ps1 <goal>`, then requeue only that task: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 requeue-task --task-number <n> --note "<why>" <goal>`. |
| `Get-GoalDispatchInventory.ps1` shows a `dispatch.json` generation with **no heartbeat and no exit** | Dispatch prep failed before the worker process started; the worker never ran, so waiting will not produce output. | Stop the loop cleanly, retire the dead dispatch artifacts, requeue the task with `requeue-task`, then relaunch the conductor. |
| Goal in `Failed` lifecycle but the work is committed in the worktree | A stage process was orphaned (e.g. a loop crash). The commit is safe. | `recover <goal> --text-file <path>`, then re-run the loop so the conductor reconciles and lands it. |
| Worker exits 0, worktree has uncommitted changes, and logs say `index.lock: Permission denied` under `.git\worktrees\<goal>` | Low-integrity worker could edit files but could not write git metadata, so conductor commit-on-behalf did not complete. | Inspect the diff, run focused tests, then from a normal-integrity operator shell run the `git -C .orchestrator-worktrees/<goal> add ...` and `git -C .orchestrator-worktrees/<goal> commit -m "<message>"` steps as separate commands; use `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-Git.ps1 ...` if direct git prompts. Submit `adjudicate --goal <goal> <task> close --text-file <explanation-path> --evidence trx:<test-receipt-path>`, confirm it applied, then re-run the conductor. |
| Codex task fails with `exec error: Access is denied. (os error 5)` and stderr names `C:\Program Files\WindowsApps\...\pwsh.exe`, or fails immediately with `unexpected argument '<word>' found` after falling back to Windows PowerShell | Codex can run under same-user Low IL, but WindowsApps/package PowerShell activation can fail; Windows PowerShell 5.1 can also split `(Get-Content -Raw prompt)` into multiple native arguments. | Treat this as a launcher regression, not proof that Low IL is unusable. Verify `WorkerShell` pins a real PowerShell 7 host, preferring `%LOCALAPPDATA%\Programs\PowerShell\7\pwsh.exe`; extract/install a real filesystem `pwsh.exe` there if only the Store alias exists. Verify `DispatchProcessHost` removes WindowsApps from Low-IL `PATH`, then rerun focused `WorkerShellTests` / `DispatchProcessHostTests` before falling back to another Developer provider. |
| Low-IL dispatch shows no stdout/stderr files and no heartbeat for minutes, while process inspection shows `icacls ... /setintegritylevel ... /T` under `__dispatch-run` | The sandbox is still applying Mandatory Integrity labels before the worker launch. Older builds redirected `icacls` output without draining it, causing pipe backpressure, 120s startup timeouts, orphaned `icacls`, and duplicate retries; a completed `icacls` can also return nonzero on a previously used worktree. | Stop the loop with `.conduct-stop`, then stop only the exact orphan dispatch PIDs if the task was already retried. Verify `DispatchProcessHost.SetLowIntegrity` drains `icacls`, kills it on timeout, treats completed nonzero exits as non-fatal, and emits a `preparing-sandbox` heartbeat before restarting the conductor. |
| Goal genuinely dead / wrong, can't proceed | — | `abandon-goal <goal> --text-file <path> --confirm-goal-abandon` (remove its worktree first if a Low-IL `.mcg-sandbox` orphan blocks it). |
| Worker log shows exit 0 and file changes exist in `.orchestrator-worktrees/<prefix>` but the task is still `[Dispatched]` / reconcile loop shows `held` indefinitely | Orphaned dispatch reconcile — loop crashed after worker exited. The work is safe in the worktree. | `recover <goal> --text-file <path>` resets the stale dispatch, then re-run the loop to read the worktree commits. |
| Acceptance build fails with `MSB3491` / "file is being used by another process" after the repo-wide build-server disablement | A non-build-server process such as a running test host may still hold an output DLL. Transient; not a code defect. | Stop the exact owning process when known; otherwise `dotnet build-server shutdown` is harmless. If the goal is `AcceptanceFailed` and all work remains verified, run `acceptance-retry <goal> "<reason>" --confirm-acceptance-retry`; the next conductor tick re-runs the gate without a worker round. |
| `escalated at AwaitingClarification` and you want to provide real answers, not dismiss | Spec-refiner or another typed human wait raised design questions with stable request IDs. | `attention show <goal>` lists executable resume commands with stable IDs. Run the displayed `attention answer <goal> <id> <answer>` command verbatim (or replace `<answer>` with `--text-file <path>` for long answers); a globally unique full or short request ID also works without `<goal>`. Then re-run the loop. Use top-level `answer` only when attaching `--gate-deliverable` evidence. |
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

- A failed acceptance verdict is scoped to its recorded branch/main candidate pair. If either HEAD has changed, `acceptance <goal>` records the old verdict as superseded, restores the otherwise-ready goal to `Verified`, and runs one fresh gate against the current pair without consuming or bypassing the `acceptance-retry` cap. An unchanged pair remains `AcceptanceFailed` and requires the ordinary failure-specific repair path.
- If a retry is swallowed because the same completed dispatch exit artifact keeps being reconciled, stop the loop, identify the exact stale `.exit.txt` from `task <goal> <n>` or `Get-GoalDispatchInventory.ps1`, move that single exit artifact aside with a `.retired` suffix, then run `recover <goal> --text-file <path>` and `retry <goal> <task-number> --text-file <path>`. Do not delete broad log sets; preserve stdout/stderr for evidence.
- `recover` can over-reset tasks that had already passed. Bridge those back with one evidence-backed decision: `adjudicate --goal <goal> <task-number> close --text-file <explanation-path> --evidence operator-evidence:<evidence-path>`. Keep the evidence file available until the intent is `Applied`.

- **Brief revisions are versioned; verify stored criteria and next-dispatch delivery separately.** Use `revise <goal> --brief-file <path> --reason-file <path>`. Current `AgentOrchestratorKernel.ReviseGoalBriefCore` re-derives the refined criteria when the new brief declares a nonempty changed list under an H2 heading such as `## Acceptance criteria`. A plain `Acceptance criteria:` line is not recognized by `AcceptanceCriteriaParser`; inspect the declared/refined counts before dispatch. The revision receipt prints `refined-criteria=<count>` or `unchanged`, plus not-yet-started, in-flight and completed task sets. In-flight workers continue with their original dispatch snapshots; completed work is retained. Confirm the intended text in the next emitted prompt using a narrow `rg -n --count` against that exact prompt file. On 2026-09-06, a heading-only correction on `7c57509f` produced `refined-criteria=4` while retaining completed tasks. The earlier 2026-08-20 failure to regenerate refined criteria (including 27 repeated rounds on `21b284a0`, backlog `2861b909`) remains a historical warning to verify delivery, not evidence that the current revision API cannot update criteria. `answer` supplies clarification responses; `reassign-agent` affects the next dispatch.
- **`Applied` is not `delivered`. A `recover --text-file` note can be recorded and still never reach the worker.** The flag parses and the text is persisted to both `.orchestrator/goal-events/<goal>.jsonl` and `state.db`, so every journal says the guidance exists — and the next dispatch prompt can contain none of it. Operator text renders in the retry-history block (`- [still-open] Retry N of M; ...; TaskRetried: <text>`), which only appears once the retry counter is engaged; a plain `recover` of a `Failed` task can produce a prompt with no such block at all. Before concluding that guidance "did not help", grep the emitted prompt under `.orchestrator/prompts/` for a distinctive phrase from your note. On 2026-08-20 goal `ab933e32` burned a third identical worker round on unchanged instructions, and the repeat failure was initially misread as the advice being wrong rather than absent. Tracked as backlog `2861b909`.
- **Operator note text is truncated in the middle, not at the end.** Long notes render as `...[truncated N chars for prompt budget]...` with the head and tail preserved, so framing sentences survive and the decisive content in the middle is what disappears. On goal `21b284a0` this removed exactly the executed/passed/failed counts and the failing test names from an operator retry. Front-load numbers, test ids, and file paths into the first few lines; never bury them mid-note.
- **Re-apply `reassign-agent` after any `recover` or task-failure reset.** A per-task agent assignment made with `reassign-agent <goal> <task-number> <agent-id>` can revert to the role default across a reset, and the revert is silent — the next `reassign-agent` reports `from agent '<original>'`, which is the only visible sign it was undone. Confirm the intended agent in `status <goal>` after every recovery step. Tracked with the note above under backlog `2861b909`.
- `retry`, `progress`, `verify-manual`, and `adjudicate` always append typed requests to `.orchestrator/operator-intents.db`. The tick applies each request as the sole `state.db` writer; when no conductor is running, requests remain pending until one starts or until `conductor apply-intents <goal-prefix>` applies one goal's requests (see Applying intents while no conductor runs, below). `operator-intent-status <intent-id>` exposes the terminal outcome and audit fields. Wake files are one-shot notifications; SQLite remains the durable queue.
- **`Pending` is not `Applied`. Poll `operator-intent-status <intent-id>` before treating a submitted intent as delivered, and before reporting it as such.** Submission returns an id, not an outcome; the tick may apply the intent, or reject it. Rejection is normal and expected — the retry guard refuses an upstream retry while a downstream task holds a running process (`Cannot retry Developer task ... while downstream Reviewer task ... has a running process`), and a rejected intent is silently dropped, not queued for later. On 2026-08-14 a diagnosis was queued, rejected by that guard, and reported as routed; the Reviewer spent forty minutes re-deriving evidence for failures whose cause was already known but never delivered. Resubmit once the blocking lane clears, and confirm `status=Applied` rather than inferring it from the absence of an error. For recovery, submit one `adjudicate --goal <goal> <task-number> <close|reopen-regate> --text-file <path> --evidence <reference>` intent and confirm its terminal status. Use `reopen-regate` for an `AcceptanceFailed` goal and `close` otherwise.
- A lingering goal-level `Failed` display while retryable tasks are already in flight is expected noise during recovery. Judge the live state by the task process, dispatch inventory, and event stream before applying another repair.
- Classifier and recovery notes include the rule and matched evidence that triggered them. Read that evidence before retrying; it usually distinguishes provider limits, empty-output flakes, dirty worktree recovery, sandbox commit blocks, and real test failures.

### Adjudicate: the single recovery action

Use `adjudicate --goal <goal> <task-number> <close|reopen-regate|route> --text-file <explanation-path> --evidence <reference>` for evidence-backed task recovery. `close` completes and verifies a task; `reopen-regate` does that after mechanically reopening an `AcceptanceFailed` goal; `route` retries with `--cause <cause>`. Repeat `--evidence` for each receipt. Named references resolve as `trx:<path>`, `focused-evidence:<pointer-or-candidate-sha>`, `acceptance-attempt:<attempt-id>`, or `operator-evidence:<path>`. File paths may be absolute or relative to the workspace and must still exist when the tick applies the intent. Add `--reversibility <reversible|reversible-with-cost|irreversible>` and `--precedent <decision-id|rule>` when those attributes apply. The tick handles this as one intent; poll `operator-intent-status <intent-id>` for `Applied` and inspect the decision and effect receipts. An unresolved named reference is rejected with `evidence-reference-unresolved` and leaves task state unchanged.

For candidate-bound [criterion evidence mappings](cli-reference.md#criterion-evidence-mappings), copy obligation ids and other stable identifiers verbatim from the hold text and use its printed brief number for the criterion. Rebind mappings to the current tested candidate before merge when the hold reports a stale binding, and confirm the mapping intent is applied before recording evidence.

### Applying intents while no conductor runs

Starting a conductor is the normal way to apply pending intents. When one goal's pending intents must take effect while the loop stays down, run `mcg-orchestrator.cmd conductor apply-intents <goal-prefix>`. It refuses with exit 1 while a conductor runs, because that conductor applies pending intents on its next tick. Otherwise it holds `conduct-loop.lock` for the run, so no conductor can start meanwhile, and it applies only that goal's pending intents as the single `state.db` writer. It prints each applied line and exits 0, or prints `No pending operator intents for goal` with the goal prefix and exits 0. If the goal's state cannot be saved, it prints `Not applied:` with the reason and exits 1, and the claimed intents stay for the next applier run or conductor tick. An unknown or ambiguous prefix exits 1 without applying anything. Afterwards, poll `operator-intent-status <intent-id>` for each submitted intent exactly as for a tick-applied intent.

### Recovering after cancel-dispatch

After `cancel-dispatch` is `Applied`, the task is `Cancelled` and awaits an operator retry. The `CANCEL_DISPOSITION` note and CLI output name the next command, using the goal prefix and 1-based task number:

```text
adjudicate --goal <goal-prefix> <task-number> route --cause <cause> --text-file <note> --evidence <reference>
```

Choose the [retry cause and route](cli-reference.md#retry-causes) before retrying, then supply the explanation file and evidence reference described above; `adjudicate route` rejects `Unknown`. Alternatively, use `retry --goal <goal-prefix> <task-number> --text-file <note> --cause <cause>`. Keep the conductor running and poll `operator-intent-status <intent-id>` until the recovery intent is `Applied`. `close` and `reopen-regate` do not apply to a cancelled task; their rejection keeps the reason code and appends `; task is Cancelled (cancel-dispatch): use route --cause <cause>`.

### State-repair quiet window

Direct SQLite repair tools and artifact retirement still require a quiet window because they bypass the typed operator-intent inbox. Routine `retry`, `progress`, `verify-manual`, and `adjudicate` commands do not: keep the loop running and let its next tick apply them. Use this stop sequence only for direct repair commands:

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
- Long text belongs in throwaway files: `retry`, `note`, `progress`, `verify-manual`, `adjudicate`, `recover`, `answer`, and `add-task` all accept `--text-file <path>`; `goal` accepts `--brief-file <path>` (also `--text-file`); `backlog-add` accepts `--body-file <path>` (also `--text-file`). Delete the scratch input after the command succeeds.
- `subscription-dispatch <task> --confirm-limit-review --text-file <path>` records a long usage-limit review note without putting it on the command line.
- A loop **crash** can leave in-flight tasks reconcilable, but it can also leave a goal in `Failed` lifecycle that `recover` followed by the conductor sweep clears. A graceful `.conduct-stop` follows the detach/fallback behavior documented in §1.
- Verification roles (Tester/Reviewer) legitimately change no files; the dispatch gate accepts their `WORKER_RESULT` as evidence. If a verification task still won't pass, `verify-manual <n> passed --text-file <path>` records an operator pass.

---

<a id="steward-and-author"></a>
## Steward and Author

### Steward: bounded recovery

The Steward detects three recovery cases and builds retry feedback containing the current criteria, trigger evidence, a decision procedure, diagnosis and instruction. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardTriggers.cs` ::ConductorStewardTriggerKind; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardRetryTemplate.cs` ::Compose.)

| Trigger | Retry instruction |
|---|---|
| Developer no-change/no-commit rejection with a confirmed failing candidate | Reproduce the named failing test on the same candidate and input, quote the assertion, fix it and recheck. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardTriggers.cs` ::Detect and ::TryFindConfirmedRed; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardRetryTemplate.cs` ::Compose.) |
| Planner output contract rejection (`planner-output-contract-rejected`) | Check each citation against an exact tracked path at HEAD and each required mapping field against the output contract. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardTriggers.cs` ::Detect; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardRetryTemplate.cs` ::Compose.) |
| Acceptance failure naming a disabled-collection class added by the candidate, with its collection resolved | Rename the class so its name contains the required acceptance-lane substring for that collection, then check the collection guard. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardTriggers.cs` ::Detect; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardDeterministicRoute.cs` ::BuildCollectionText.) |

Recognized Planner and collection-guard diagnostics can produce a deterministic route; otherwise the Steward invokes a model round. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardDeterministicRoute.cs` ::TryBuild; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.DeterministicRoute.cs` ::StartRound.)

A permitted proposal submits an Agent-actor `adjudicate` intent with a `route` payload for the triggering task; the route policy permits only `NewTestFinding` or `ContractClarification` causes and reversible actions. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.cs` line 305; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardAdjudication.cs` ::ConductorStewardRoutePolicy.RejectionReason.)

The Steward never closes a task or re-gates: `close`, `reopen-regate` and `verify-manual` proposals are rejected by the route policy. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardAdjudication.cs` ::ConductorStewardAdjudicationParser.Parse and ::ConductorStewardRoutePolicy.RejectionReason; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.cs` ::HarvestCompleted.) It raises an owner question rather than executing a rejected proposal; an explicit `ask-owner` or a recurring trigger after an applied route also raises a question, holds the goal with `steward-owner-question`, and emits `goal-escalation`. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.cs` line 279 and ::ServiceTick, ::HarvestCompleted, ::Question.)

Under the workspace's orchestrator directory, `steward-triggers.db` tracks triggers and `steward-rounds/` holds timestamped JSON model-round receipts containing the trigger, session, exit code, output and failure. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.cs` ::CreateDefault; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardModelRound.cs` ::WriteReceipt.) Outcomes appear as `steward` events in `logs/conduct-events.log`; owner questions also appear in `goal-events/`. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.cs` ::Record and ::Question; `src/Mcg.AgentOrchestrator.App/Orchestration/OrchestratorWorkspace.cs` ::ConductEventsLogPath and ::GoalLifecycleEventsDirectory; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductEventLogWriter.cs` ::CurrentFileName.)

To turn the Steward off, set `MCG_ORCHESTRATOR_STEWARD_ENABLED` to `false` (case-insensitive) or `0` before starting the conductor; unset means enabled, and the setting is read when the default host is created. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.cs` ::EnabledEnvironmentVariable and ::CreateDefault.)

### Author: evidence-backed answers

The Author handles unanswered, nonterminal clarification items with a `spec-clarification:` correlation key and open `SpecClarification` human-input requests attached to a `WaitingForHuman` task; it skips items with a pending or claimed human answer intent. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorItems.cs` ::Detect and ::HasQueuedHumanAnswer.) Its checked answer is submitted as an Agent-actor `answer` intent with source-line evidence references and recorded as `answer-submitted`. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorOwnerClassCheck.cs` ::Evaluate; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::SubmitCheckedAnswer.)

The Author escalates model `ask-owner` results, acceptance-feasibility forks, and answers flagged for authority widening, acceptance weakening, spending beyond budget, irreversible action, external disclosure or missing source-line evidence. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::ProcessCompleted and ::SubmitCheckedAnswer; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorOwnerClassCheck.cs` ::Rules and ::Evaluate.) Escalation records `owner-question`, holds the goal with `author-owner-question`, and emits a `goal-escalation` conduct event plus a goal lifecycle escalation. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` line 213 and ::Escalate; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorOwnerQuestion.cs` ::Raise.)

Claim state lives in `author-claims.db` under the workspace's orchestrator directory, in the `author_claims` table: `identity`, `claimed_at`, `outcome` and `intent_id` identify the item, round and submitted intent; active outcomes are `in-flight` and `ready`. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::CreateDefault; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorClaimStore.cs` ::Open, ::TryClaim and ::Preserve.) Completion outcomes include `answer-submitted`, `owner-question`, `model-failure`, `unparseable`, `submit-failed`, and `superseded` when the item is no longer eligible, including when a human answer is already queued. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::Harvest, ::ProcessCompleted, ::IsStillEligible and ::SubmitCheckedAnswer; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorItems.cs` ::HasQueuedHumanAnswer.)

An Author model round has a ten-minute process timeout; its JSON receipt records exit code, output and failure in `author-rounds/`, and outcomes emit `author` conduct events in `logs/conduct-events.log`. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorModelRound.cs` ::DispatchAsync and ::WriteReceipt; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::CreateDefault and ::Record; `src/Mcg.AgentOrchestrator.App/Orchestration/OrchestratorWorkspace.cs` ::ConductEventsLogPath; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductEventLogWriter.cs` ::CurrentFileName.) To turn the Author off, set `MCG_ORCHESTRATOR_AUTHOR_ENABLED` to `false` (case-insensitive) or `0` before starting the conductor; unset means enabled, and the default host reads the setting at creation. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::EnabledEnvironmentVariable, ::ResolveEnabled and ::CreateDefault.)

### When to defer and when to intervene

1. For a clarification or human-input item, give the Author about ten minutes, then inspect that item's `author_claims.claimed_at`, `outcome` and `intent_id` before answering. The ten-minute allowance follows the model timeout, while claim state distinguishes a round still awaiting processing from a completed one. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorModelRound.cs` ::DispatchAsync; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorClaimStore.cs` ::TryClaim, ::Preserve and ::Complete.) If it remains `in-flight` beyond `claimed_at` plus ten minutes and no conductor is running, treat the round as failed for this procedure; do not wait indefinitely for that claim. Claims are inserted once with `INSERT OR IGNORE`, and normal shutdown records unfinished rounds as `model-failure`. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorClaimStore.cs` ::TryClaim; `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::Stop.) For any other `in-flight` or `ready` claim still pending after the allowance, stop passive waiting and inspect conductor progress and goal holds before proceeding: a `ready` claim is skipped while an owner-question hold excludes the goal or another round for the goal is running. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::Harvest and ::HarvestReady.)
2. Answer by hand only when the claim is `owner-question`, the round failed (`model-failure`, `unparseable` or `submit-failed`, or the failed-round condition in step 1), or the Author's answer is wrong; read the escalation or round receipt first, when available. These outcomes and receipts are recorded by `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorHost.cs` ::Harvest, ::ProcessCompleted and ::SubmitCheckedAnswer, and `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorModelRound.cs` ::WriteReceipt.
3. Correct a wrong answer by superseding it rather than submitting a duplicate: use `supersede <goal-id> <item-id> --text-file <replacement-file>` for both a collaboration clarification and an answered human-input clarification, using the corresponding clarification or request id. (`src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Tasks.cs` lines 621-640 and line 656.) A duplicate answer to a resolved item is rejected. (`src/Mcg.AgentOrchestrator.App/Orchestration/OperatorIntentCoordinator.Answers.cs` line 72 and line 108.)
4. For any of the three Steward trigger kinds, let the round finish and inspect its `steward` outcome event before retrying by hand; if it submitted a route, check that intent's outcome before issuing another retry. Route submission and reconciliation are recorded separately. (`src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardHost.cs` ::Record and ::ReconcileSubmittedRoutes.)

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

**Loop-exit and handoff conditions.** A plain `conduct --loop` batch run halts automatically and prints `LOOP_STOP reason=all-done-or-escalated` when every active goal has reached a terminal state (`CleanedUp`, `Failed`, `Blocked`) or been escalated. Goals created *after* a plain loop started are **not** picked up after that terminal stop — start a new `conduct --loop` to process them. Landing conductor infrastructure triggers a drain/build/self-check activation by default. `MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED=false` disables this path; use the manual bounce in §1 then. `LOOP_RELAUNCH_ROLLBACK` or `LOOP_HANDOFF_FAILED` reports a failed handoff, while `ACTIVATION_REVERTED` reports a successor that failed during its healthy-tick window and names both builds and the reason. Separately, when `--max-duration` expires while active work remains, the continuity supervisor stages repository HEAD, waits for the successor's `LOOP_READY` line (emitted after lease acquisition and state load, before the pre-loop terminal sweep), and emits `LOOP_HANDOFF` with both source commits on success or `LOOP_HANDOFF_FAILED` with the typed cause before falling back to the incumbent command. Manual relaunch is still needed after a deliberate `.conduct-stop`/Ctrl-C, after creating new goals following an all-done stop, and once after landing a change to the handoff implementation itself because the already-running supervisor is old code. Use `conduct --loop --daemon` only for controlled active-goal intake: it stays alive on an empty backlog and picks up goals submitted later, but it is not a safe "drain the backlog" mode.

### 6.2 Orchestrator-commit-on-behalf + merge to main

Workers write files only inside their isolated worktree (`.orchestrator-worktrees/<prefix>`), on branch `goal/<prefix>`. When a worker exits with uncommitted changes, the conductor auto-stages and commits them before running the acceptance suite. A successfully landed goal produces two paired commits visible in `git log main`:

```
Orchestrator-committed worker edits for goal <full-goal-id>
Integrate goal/<prefix>: <goal-title-slug>
```

The first commit is made on `goal/<prefix>` inside the worktree. The second merges that branch into `main` via the `integration` branch (fast-forward). If you see both commits for a goal, the loop ran to completion for it. If you see only the first, the acceptance gate or change-risk gate stopped the landing — `status <goal>` and `next <goal> --full` explain why.

If the worker ran at low integrity and git metadata under `.git\worktrees\<prefix>` rejects `index.lock`, the worker may exit 0 with a correct dirty worktree and no commit. Treat that as operator recovery, not an implementation failure: inspect the diff, run focused tests, commit the worker changes from a normal-integrity shell, then record manual verification and run acceptance.

### 6.2.1 Split-history rebase materialization

With `core.autocrlf=true`, a rebase can replay a byte-exact content commit before the later commit that declares the path `-text`. Git can exit 0 with the final blob and attributes correct while the working file remains CRLF-materialized; porcelain status may report it dirty or remain cache-blind. `workspace rebase <goal-prefix>` therefore treats Git exit 0 as provisional: `GoalWorktrees` scans tracked EOL materialization independently of porcelain status, then verifies the resolved worktree, branch/index/blob identity, effective attributes, operation state, raw bytes, and final clean status before reporting integration readiness.

When every dirty path is a tracked regular file with a clean index, effective `-text`, no binary/filter/diff driver, and a byte difference proven to be CRLF-only, `GoalWorktrees` preserves exact preimages under the worktree Git metadata, checks out only those paths, and prints a rematerialization receipt naming the writer, paths, and preimage directory. Any semantic or unowned change, bare-CR/mixed binary shape, unsupported mode, active Git operation, identity drift, command failure, or incomplete verification returns `IncompleteMaterialization` and refuses readiness. Inspect the named worktree and preserved preimages; copy any preimages needed for later diagnosis before `workspace remove`, because worktree removal deletes their per-worktree Git metadata. Do not reset the branch, rewrite its history, change expected hashes, waive whitespace gates, or apply process-wide conversion flags.

### 6.2.2 Manual landing for escalated risk

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

### 6.2.3 Optional remote mirror

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

**Paid-worker admission capacity.** `maxConcurrentPaidWorkers` is bounded by the paid-worker admission pool, currently 9. This is a memory-headroom limit, independent of build capacity: the 47.9-GiB operator host recorded a paid-worker peak of 1,750,343,680 bytes on 2026-08-08, and allocating no more than one third of physical memory to paid workers gives `floor(51,385,864,192 / 3 / 1,750,343,680) = 9`. The remaining two thirds cover the OS, builds, and acceptance gates. A gate-ready goal reserves one admission slot, so the effective cap is at most 8 while that reservation is active. `LOOP_START` reports the configured cap, admission capacity, reserved slots, and effective cap; policy-file values above the capacity produce a load warning. This fixed host-derived limit should be revisited when memory-aware admission is implemented.

**Build capacity and test artifacts.** Builds share two machine-wide file locks under the isolated dotnet root. Test execution is not slot-confined: acceptance receipts live beneath `.orchestrator/acceptance-gate-attempts/<goal-id>/`, pre-review focused receipts beneath `.orchestrator/pre-review-evidence-attempts/<goal-id>/`, and operator test commands use invocation-owned directories. Both receipt roots follow the goal artifact retention plan and `Show-TestDurations.ps1` reads both by default. Per-goal build artifacts remain reusable under the isolated dotnet goal root.

Two historical Windows Firewall rule populations can remain on an operator machine. Rules named `MCG-testhost-slot*` belong to the retired stable-slot test hosts and become removable after the slot-free gate has soaked cleanly for at least three days. Prompt-generated, per-executable rules are separate: they may still serve deliberately retained application or worktree executables and must remain until an administrator confirms that each executable path is no longer used.

An administrator may partially reverse the obsolete rule population once:

```powershell
.\scripts\Remove-TestSlotFirewallRules.ps1
```

The script is idempotent, matches only `MCG-testhost-slot*`, and writes every removed rule definition to `.orchestrator/firewall-rule-removal/` before reporting success. Keep that receipt so a rule can be reconstructed if the soak premise proves wrong. It does not inspect or remove prompt-generated per-executable rules. Reversing those requires an elevated, rule-by-rule review in Windows Firewall: verify the executable path is retired, export or otherwise preserve the rule definition, and remove only that confirmed rule. This is a one-time operator action; the orchestrator never mutates firewall policy at runtime.

**Provider rate-limit.** Each worker holds a `claude-cli` or `codex-cli` subscription session. Under heavy load the shared subscription can hit the provider's session rate-limit, causing workers to exit with zero bytes of output and retry in a loop. If you observe this pattern (workers exit-1 repeatedly with empty output), reduce the number of concurrent in-flight goals or stagger goal intake. There is no orchestrator-side setting that bypasses the upstream limit.

---

## 7. State & store map

For a project whose trunk has another name, register it with `project create <name> --root <path> --integration-branch master`. `project show <name>` displays the integration branch. Older registrations and the default workspace use `main`. This first slice configures workspace fallback/diff and landing refs; acceptance and other conductor paths still require the follow-up branch-setting slices before end-to-end operation on such a repository.

Durable state lives in stores, never in `.scratch`.

| Location | What |
|---|---|
| `.orchestrator/state.db` | the kernel plus goal-intake request ledger: goals, tasks, dispatches, verifications, and keyed `still-committing`/`created`/`failed` receipts (SQLite, single-writer) |
| `.orchestrator/backlog.db` | the backlog (use `backlog-list`/`backlog-add`/`backlog-show`/`backlog-close`; this is the source of truth, not `BACKLOG.md`) |
| `.orchestrator/dogfood-log.db` | dogfood goal-boundary evidence (use `dogfood-log list`/`dogfood-log add`; this is the source of truth, not `DOGFOOD_LOG.md`) |
| `.orchestrator/collaboration-items.db` | clarifications / operator-input items |
| `.orchestrator/experiments.db` | pre-registered experiment specs and single-shot outcomes (`experiment-add`, `experiment-show`, `experiment-decide`) |
| `.orchestrator/agents.json` | the agent catalog (which model each role uses) — see the roster-change caveat below |
| `.orchestrator/logs/conduct-events.log` | canonical structured conduct event stream (JSON lines with `eventKind`; stable path, rotated by size; listen from the current end) |
| `.orchestrator/logs/`, `.orchestrator/prompts/` | per-dispatch worker logs (`*.out.log`/`*.err.log`/`*.exit.txt`) and the rendered worker prompts |
| `.orchestrator-worktrees/<goal-prefix>` | the goal's isolated git worktree on branch `goal/<prefix>` |
| `.orchestrator-context/<goal-id>` | worker context artifacts for a goal |

`--text-file` is the uniform throwaway vehicle to pass long text past the command-length cap for `retry`, `note`, `progress`, `verify-manual`, `adjudicate`, `recover`, `answer`, and `add-task`. `goal --brief-file <path>` and `backlog-add --body-file <path>` are the preferred command-specific forms, with `--text-file` aliases still accepted by current CLI help. The durable copy becomes the goal objective, task note, verification receipt, answer, or backlog item, so **delete the scratch input** afterward.

### Experiments

Register a trial with `experiment-add --spec <path>`; it prints a stable id, also usable by unique prefix. The JSON spec has this shape:

```json
{
  "hypothesis": "A shorter brief reduces rounds per landing",
  "intervention": { "kind": "brief-or-prompt-change", "description": "Remove the repeated preamble manually" },
  "baseline": { "kind": "before-after-window", "since": "2026-10-01T00:00:00Z", "until": "2026-10-02T00:00:00Z" },
  "metrics": ["rounds-per-landing", "landings-per-hour"],
  "guardrail": { "metric": "productive-rounds", "breachIf": { "metric": "productive-rounds", "op": "<", "changePercent": -10 } },
  "stopRule": { "count": 5, "unit": "goals" },
  "decisionRule": {
    "keepIf": [{ "metric": "rounds-per-landing", "op": "<=", "changePercent": -10 }],
    "revertIf": [{ "metric": "rounds-per-landing", "op": ">", "changePercent": 10 }]
  }
}
```

Intervention kinds are `config-flag`, `policy`, `brief-or-prompt-change`, `model-swap`, and `evidence-only-code-spike`. Baselines are `before-after-window`, `alternating-gates` (with window bounds), or `twin-goal` (with `twinGoalId`). Optional `epicId` must resolve in the portfolio. The metric menu is `rounds-per-landing`, `productive-rounds`, `expected-overhead-rounds`, `wasted-rounds`, and `landings-per-hour`; select one to three distinct metrics and one different guardrail metric. Stop units are only counted `gates`, `goals`, or `ticks`, with a positive count.

`experiment-show <experiment> [--as-of <timestamp-with-offset>]` prints the spec, stop progress, a fresh `reading: keep|revert|inconclusive`, and a separate stored `outcome:`. A before/after reading compares the baseline window with baseline end to show time. Round metrics retain RoundValueReport's terminal-goal cohort by final dispatch time; landings per hour counts completed goals' earliest `GoalLanded` events in each half-open window divided by its declared duration. Keep and revert rules each require all conditions; conditions compare percentage change `(comparison - baseline) / baseline * 100` with `<`, `<=`, `>`, or `>=`. Conflicting rules, missing data or a zero baseline yield inconclusive. A breached guardrail prevents keep; revert still requires the revert rule. Landings per hour with no landings or zero duration is unavailable. Alternating-gates and twin-goal readings, and gate/tick progress, are explicitly unavailable in this slice. Goals progress counts terminal goals by final dispatch from comparison start (or creation when no window is recorded).

For a `config-flag` experiment, an optional intervention `flagTarget` names `fileKind: "conductor-policy"`, `propertyName`, and boolean `valueToApply`. Allowed properties are `acceptanceAttemptBelowNormalPriority`, `cascadeTesterCheapFirst`, `followerGatesEnabled`, and `cascadeMechanicalReworkCheap`. Omit `priorValue`: the conductor captures it on first apply. Existing specs without a target remain readable and use the manual intervention flow.

Submit `experiment-apply-flag <experiment> [--operator-actor <actor>] [--actor-kind <human|agent>] [--idempotency-key <key>]`, then poll `operator-intent-status <intent-id>`. This queues the workspace-scoped `experiment-apply-flag` intent; its handler requires an authenticated **Mutate** tier or higher, validates the open experiment and allowlist, captures the prior value before writing, and atomically changes only the named property in `<workspace>/.orchestrator/conductor-policy.json`. Other properties, including unknown ones, are preserved. The file is untracked host state; apply/revert creates no commit or land record. Missing or invalid policy files are rejected. Replaying an applied flag is a no-op; decided experiments reject apply. Apply failures are recorded on the intent. A missing property captures its effective policy default, and revert restores that value explicitly. Follow the conductor restart/relaunch procedure when a running process must reload policy settings.

On each eligible tick, an open, applied config-flag experiment with a **revert** reading or a breached **guardrail** queues one workspace `experiment-revert-flag` intent using the conductor's existing Steward Mutate assurance. The same handler restores the recorded prior value, including when the experiment has already been decided. The controller records `refuted` with `operator-intent:<intent-id>` evidence after successful submission; the intent status proves whether the file restoration completed. A stable persisted idempotency key suppresses duplicate intents across ticks and conductor restarts. Submission failure leaves the experiment open for retry. There is no revert CLI command in this slice; the revert verb is a typed intent.

A **keep** reading changes no policy file and remains an **owner decision**, raised through the existing question. Record it, or the result of a manually applied intervention without a flag target, once with `experiment-decide <experiment> --outcome <confirmed|refuted|inconclusive> --evidence <reference> --action <text>`. Both evidence and action are required. A decided outcome cannot be replaced, and showing never changes it. Add/show/decide write only the experiment store and read goal state without taking a writer transaction. Delete a scratch spec after add; its durable copy is in the experiment store. Recording the brief-preamble trial is a post-landing Operator step.

On a conductor tick with eligible goals, the experiment observer reads the current persisted goal history through the same read-only state path as `experiment-show`, including terminal goals excluded from the conductor's working set. An experiment automatically refuted by the flag revert controller skips owner questions. For other open experiments, the observer appends an `experiment-reading-due` decision event when the experiment reaches its `stop-rule` (goals unit only) or breaches its `guardrail`. The detail carries the experiment id, trigger, observed stop count, stop-rule-met flag, verdict and guardrail-breached flag. The guardrail is evaluated from its own metric even when keep or revert metrics are unavailable, so it can trip before the stop rule is met while `experiment-show` still prints inconclusive. Missing guardrail data does not count as a breach. Each trigger raises one goal-less decision question in the attention queue, keyed `experiment-reading-due:<experiment id>:<trigger>`, asking the owner to run `experiment-show` and record the result with `experiment-decide`. Both triggers can fire for the same experiment. Persisted questions suppress repeat events and items across ticks and conductor restarts, including after acknowledgement; a recovering guardrail does not re-arm them. After a recorded decision, the next eligible tick automatically resolves both trigger questions. Idle ticks skip this observer, and the owner console does not yet display these goal-less questions. Observer failures go to the console error stream and leave the tick running; it opens no goal-state writer transaction.

Two experiments overlap when the union of each experiment's metrics and guardrail shares at least one exact metric name and their active windows strictly intersect. Each window starts at baseline end (or creation when no baseline end exists) and ends at decided-at, or remains open-ended while undecided. Windows that only touch at a boundary, or whose end is at or before their own start, do not overlap; decided experiments still count when their windows intersect. `experiment-add` still stores the new experiment and prints one `overlap:` warning per overlapping experiment with its full id, hypothesis and shared metrics before printing the new id as usual. Overlap only warns and never refuses an add. `experiment-show` appends an `overlaps:` section with each other experiment's full id, open or decided state and shared metrics, or `overlaps: none`; it writes no store and leaves the reading and stored outcome unchanged. Overlap uses the active windows, independently of `--as-of`.
### Provider profiles, dispatch sandboxes, and goal worktrees

These operating details and validation receipts were moved from AGENTS.md. Dated validation describes the recorded check; verify current profiles before dispatch.

Task descriptions also drive complexity classification: start inspection work with `Summarize `/`Report `/`Inspect ` and avoid risk keywords (auth, migration, rollback) unless the task genuinely carries that risk.

Goals that touch files should get an isolated workspace: `workspace create` adds a git worktree under `.orchestrator-worktrees/<goal-prefix>` on branch `goal/<goal-prefix>`, and dispatches/verifications for that goal then run there instead of the shared repository root. `acceptance` fast-forwards the goal branch automatically when possible and prints the manual merge command otherwise; `workspace remove` cleans up after merge. Worktrees contain committed files only - uncommitted config does not ride along.

Anthropic subscription work goes through the `claude-cli` profile (claude CLI in print mode), validated end-to-end 2026-06-10. Two requirements: the model must be a valid claude CLI name (full ids like `claude-sonnet-4-6` or CLI aliases `sonnet`/`haiku`/`opus`; the old default `claude-sonnet` is rejected with exit 1), and the command must carry a permission mode — without one, print-mode claude denies every file edit, replies BLOCKED, and still exits 0, which the exit-code auto-pass records as a completed task. The default profile carries both, profile repair upgrades stale saved catalogs, and the patch-capability gate refuses Developer dispatch through permission-less claude templates. Claude resolves relative paths from the dispatch working directory correctly (no absolute-path requirement like qwen).

OpenAI subscription work goes through the `codex-cli` profile. On a ChatGPT account, codex accepts only `gpt-5.5`; `gpt-5.3-codex` and `gpt-5.5-codex` are rejected with 400 (validated 2026-06-11). Built-in defaults use gpt-5.5 and stale saved catalogs repair on load. There is no CLI surface for a custom subscription alias — `agent <role> <provider> <model>` reapplies the built-in default; hand-edit the workspace `agents.json` if a custom alias is needed.

Dispatch sandboxes resolve by task role (2026-06-11): the templates carry `{sandboxMode}`/`{permissionMode}` placeholders, and Developer/Tester dispatches expand to codex `workspace-write` / claude `bypassPermissions` while Planner/Researcher/Reviewer expand to `read-only` / `plan`. Non-implementation roles cannot modify the worktree; their prompt requirements state this too. (Enforced in code/tests; first live pipeline validation still pending.)

Local-model file work uses `qwen-code-cli` as the harness (how) against LlamaCpp `llama-server` at `http://127.0.0.1:8080/v1` (what). The profile takes `{openaiBaseUrl}` / `{openaiApiKey}` from the selected provider, `{approvalMode}` from the role (`yolo` for Developer/Tester, `plan` for read-only roles including Ideation), and `--bare` so the startup prompt fits a 16k server context (default qwen-code at repo root is ~25728 tokens and 400s). Thinking must be disabled via the repo's `.qwen/settings.json` (`generationConfig.reasoning: false` per model). Task briefs should state absolute target paths because qwen-code's write tool rejects relative paths. qwen-code rewrites `.qwen/settings.json` at exit with its startup view — never hand-edit that file while a qwen process is running. Start the backend with `.\scripts\Start-LlamaServer.ps1` (do not pass `-ot` through `Invoke-RepoScript.ps1`; `;` splits the tensor override). Default context is 16384.

### Changing which model a role uses

`agent <role> <provider> <model> [name]` replaces the role's primary agent. `agent-add <role> <provider> <model> [name]` adds a second entry at `route=alternate` with a derived id, leaving the primary in place — that is the right verb when you want one goal's task moved to a different model via `reassign-agent` without switching the whole role.

**Without subscription flags, both verbs set the API model and can leave the subscription half stale, and the subscription half is the one that dispatches.** They write `Model.ModelName`, while omitted subscription or complex-model values can retain values from whatever previously occupied that id or role. Under the default `ExecutionPolicy=PreferSubscription`, the worker CLI is invoked with `{subscriptionModelName}` and `{subscriptionReasoningEffort}` from `workers.json` templates — so verify both halves after every roster change.

The `agents` listing prints both halves on one line without marking the conflict:

```
Developer: Codex developer id=openai-developer ... api=OpenAI/gpt-5.6-sol reasoning=medium subscription=codex-cli model=gpt-5.5 reasoning=low
                                                    ^^^ what the command set                ^^^ what actually dispatches
```

Use `--subscription-model <alias>` and `--subscription-reasoning <effort>` with `agent` or `agent-add` to set the subscription launch values. After any roster change, re-run `agents` and confirm the API and subscription halves agree before letting a dispatch form; edit `.orchestrator/agents.json` only for fields that the command did not set, such as an independently selected `ComplexModel.ModelName`. Tracked as backlog `fd4a5ed5`.

An acknowledged `reassign-agent` is resolved again at the final application pre-start boundary: the next command takes its harness, model, and reasoning from the assigned agent, never from the former dispatch record. A reassignment before start authorization keeps the prepared attempt pending and rebuilds its command at that boundary. A reassignment after an authorized start leaves that running attempt and its recorded owner unchanged, reports `effect=next-attempt`, and applies to the following attempt. A role mismatch, missing agent, unavailable profile, or unsupported API-only harness rebind produces a typed hold and launches nothing; it never falls back to the former provider.

Dogfood goal-boundary evidence is durable SQLite state, not a tracked markdown append log.

```powershell
mcg-orchestrator.cmd dogfood-log list --limit 10
mcg-orchestrator.cmd dogfood-log add <goal-prefix>
mcg-orchestrator.cmd record-goal <goal-prefix>   # compatibility alias for add + render
```

`DOGFOOD_LOG.md` remains only as a pointer for operators and should not receive new durable entries.

### Move the default project's state to the user data root

Use a [state repair quiet window](#state-repair-quiet-window): all goals must be terminal (Completed, Failed, Cancelled or Superseded), with no running dispatches or processes, no held conductor lock and no pending `.conduct-stop`. Draft, Parked and Verified goals still block the move. Stop other board writers too. Follow the stop steps in the existing [graceful stop and relaunch procedure](#manual-bounce-fallback-after-loop-affecting-code-lands), wait for `LOOP_STOP`, and confirm the recorded conductor PID is no longer running before proceeding.

The stop sequence leaves `.conduct-stop` in the repository root. Only after `LOOP_STOP` and that PID check, remove it with `Remove-Item -LiteralPath .conduct-stop` immediately before running the move or undo below; the verb refuses while the file is present. Removing it lifts launcher stop authority, including the auto-resume guard, so keep launchers and auto-resume paused throughout the remaining quiet window. After a successful move or undo, relaunch through the linked procedure.

```powershell
mcg-orchestrator.cmd project move-default-state --dry-run
mcg-orchestrator.cmd project move-default-state
# To reverse, in another quiet window:
mcg-orchestrator.cmd project move-default-state --undo --dry-run
mcg-orchestrator.cmd project move-default-state --undo
```

The verb verifies the copied tree and state database, retains `.orchestrator.backup-<UTC timestamp>` beside the original location, and records a per-repository destination under the user data root. The old `.orchestrator` path continues to work through a directory junction (a directory symlink on non-Windows hosts). Undo restores the current moved tree to a real directory, falling back to the backup only if the moved tree cannot be read, and clears the registry record. Backups and the moved tree are retained for manual cleanup; a retained destination blocks another forward move until the operator removes it. Run this on the live board only after the relocation goal lands.

### State backup and restore

Use `scripts\Backup-OrchestratorState.ps1` for machine-local `.orchestrator` backups. It writes timestamped archives to `%USERPROFILE%\backups\mcg-orchestrator\` by default, snapshots SQLite stores with `sqlite3 .backup`, includes the goal/event/log artifact directories plus `agents.json` and `workers.json`, verifies an extracted copy with row counts when `-VerifyRestore` is supplied, and retains the latest 7 daily plus 4 weekly archives unless overridden.

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Backup-OrchestratorState.ps1 -VerifyRestore
.\scripts\Invoke-RepoScript.ps1 scripts\Backup-OrchestratorState.ps1 -Install -DailyAt 03:20
```

To restore after a machine loss, first restore git branches and worktrees from origin. Then stop the conductor, extract the selected backup zip, copy the extracted `.orchestrator` directory back to the repository root, and restart normal operator commands. For a single-store repair, keep the current `.orchestrator` directory intact and replace only the intended restored `*.db` file after verifying the archive in a scratch directory.

For rare lifecycle/task desync repair, `scripts\Set-OrchestratorGoalStatus.ps1` / `Invoke-OrchestratorSqliteTool.ps1 set-goal-status` update both the indexed `goals.status` column and the serialized snapshot in `.orchestrator/state.db`. They are operator recovery tools, not normal workflow commands; prefer `recover`, `retry`, and `conduct` first. Use only Core-valid statuses, and make the write during a quiet window after `LOOP_STOP`.

---

## 8. Operating discipline (hard-won)

- **Carry standing provider authorization forward.** The operator has authorized Anthropic adversarial review of this repository, including source, paths, commit IDs and test evidence (reiterated 2026-09-06). Ordinary review payload changes, task boundaries and context compaction do not reset that authorization. If automatic approval review rejects an authorized review, report the existing authorization and concrete review scope when resolving that tool failure; continue unaffected implementation and validation. Do not park goals or ask the operator for the same permission again. This guidance does not disable platform approval controls or authorize unrelated disclosures.

- **One canonical path.** Prefer `conduct --loop`; the manual verbs (`subscription-dispatch → start-dispatch → refresh-dispatch → accept`) are granular fallback only.
- **Backlog is candidate input, not an automatic queue.** A stale/open backlog can contain obsolete, overlapping, or underspecified work. Before daemon mode, curate a small active set with `backlog-list` + filtered `backlog-intake "<heading>" --create-goal --backlog-coverage <full|slice>` for ordinary implementation goals; use `backlog-intake "<heading>" --create-simple-goal --backlog-coverage <full|slice>` only for the explicit `simple-goal` exception, not because an item looks narrow. Choose `full` only when the goal covers the complete source item; choose `slice` when remaining work must stay Open. Any `goal-plan --create-*` command likewise requires `--backlog-coverage <full|slice>`; avoid unfiltered or multi-filter batch creation unless you have reviewed dependencies and file scopes. Keep daemon runs bounded with `--max-duration` until the active set is proven healthy.
- **Keep long waits out of the foreground.** Use `scripts\Start-OrchestratorCommand.ps1` through `scripts\Invoke-RepoScript.ps1` for long acceptance/conductor runs, then keep one listener on `.orchestrator/logs/conduct-events.log`. Use `next <goal> --full`, `Find-OrchestratorLocks.ps1`, or `Show-OrchestratorLogArtifacts.ps1` only at a decision point or after expected stream heartbeats stop. Avoid raw `Start-Sleep` loops and broad `.orchestrator` filesystem commands.
- **State writes vs a running loop.** `retry`, `progress`, `verify-manual`, and `adjudicate` are safe during the loop because they append to `operator-intents.db` and the tick alone mutates `state.db`. `backlog-add` also uses a separate store. Direct repair tools and remaining whole-kernel verbs such as `goal --brief-file`, `abandon-goal`, `park-goal`, and `unpark-goal` still bypass the inbox; serialize those between loop runs until their migrations land. Read-only inspection (`status`, `next`, git on worktrees, loop output) is always free.
- **Check for `Verifying` goals before committing to `main`.** Any commit to `main`, including docs-only commits, changes the main identity and makes every in-flight acceptance gate against the previous main stale. Defer the commit until those gates finish.
- **Use the bounded worker build path.** Routine Developer/Tester builds run as `scripts/Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]`, never as a raw build when the helper can express the target. The helper pins errors-only console behavior, retains complete captured output beneath the isolated goal artifacts root, returns a one-line project/error/warning/character/elapsed summary on success, and on failure returns every exact compiler-error line plus bounded context and the complete-log path. A log-write failure is a failed build apparatus result, never PASS.
- **Keep exceptional diagnostics bounded.** If the normal helper cannot express the required build arguments, use `scripts/Invoke-WorkerBuildDiagnostic.ps1 build <project-or-solution> <diagnostic-arguments...>`. It passes an argument array without evaluating shell text, closes child stdin, drains stdout/stderr, preserves the child exit code, retains the full output in isolated goal artifacts, and returns only counts, elapsed time, and the log path. This diagnostic summary is not normal worker build evidence; inspect or excerpt the retained log only for a named missing diagnostic.
- **Scope the test suite to the changed project**, not the whole solution; run it foreground (or poll). Use `scripts/Invoke-TestSummary.ps1 -Target <project>` for compact results. The helper builds by default and launches the managed .NET 10 Microsoft.Testing.Platform assembly through `dotnet`; use `-NoBuild` only when the existing managed assembly is known current. Do not substitute raw `dotnet test`, which launches the generated native apphost on Windows.

  ### Managed test filter syntax

  `Invoke-TestSummary.ps1 -Filter` accepts a bounded Boolean grammar: `FullyQualifiedName~Class`, `FullyQualifiedName!~Class`, `Name~Method`, `Name!~Method`, and `Category!=Trait`. Use `|` only for positive alternatives of the same predicate kind, use `&` between representable clauses of different kinds or exclusions, and use parentheses for grouping; `&` binds more tightly than `|`. Current manifest filters that refine one class alternative with a nested class exclusion are also supported. Expressions a single managed-runner invocation cannot represent—such as `FullyQualifiedName~A|Name~B`, `FullyQualifiedName~A&FullyQualifiedName~B`, or a negative predicate under `|`—terminate before build, discovery, or execution with `MTP test filter '<expression>' is unsupported: <specific construct> Supported syntax: ...`; no TRX is produced. `Name` matches the method symbol. Display text shown by a test framework is not a method symbol, so `DisplayName` is rejected; use `Name` or `FullyQualifiedName` rather than copying display text from test output.
- **Provider requirements:** `claude-cli` needs a valid model id (`claude-sonnet-4-6`/`sonnet`/`haiku`/`opus`) **and** a permission mode (the default profile carries both). `codex-cli` on a ChatGPT account accepts `gpt-5.5`. API runs (`run`/`api-run`) have **no file access** — embed needed data in the task description, and route file-touching work through subscription dispatches.
- **The brief is the unverified root of trust.** The gates verify "output matched the spec," never "was the spec right." A sloppy brief lands a plausible-but-wrong implementation on green tests. Specify external contracts (happy *and* unhappy path), observable success, ownership/lifecycle, and the verification class before dispatch.
- **Shared-service changes ripple to integration tests.** A brief that changes a widely-consumed service (the failure classifier, the binding resolver, a kernel API) must require the worker to find that service's consumers (a call-site / reverse-dependency search) and run the **dependent integration tests** (e.g. `RunGoalService_*`) in self-verify — not just the changed file's own unit tests. Otherwise the ripple is caught only by the full-suite acceptance gate, costing a Developer-retry cycle. Observed 2026-06-25: two lanes changing `DispatchFailureClassifier` and the binding resolver both escalated at acceptance on `RunGoalService_*` because their briefs scoped self-verify too narrowly.
- **Reproduce before you theorize.** When an external CLI/model/tool fails, run the smallest reproducing command before concluding a cause.
- **Retire terminal ghosts durably.** `goal-mark-landed` now writes a retired terminal disposition after out-of-band landing, and terminal sweeps suppress standing retired dispositions. Once 4f970f1d lands, `abandon-goal` will do the same for abandoned goals; until then, treat abandon retirement as in progress rather than guaranteed.

### Manual verbs and subscription execution checks

- **Build/test processes:** `Directory.Build.props` (`UseSharedCompilation=false`) + `Directory.Build.rsp` (`-nodeReuse:false`) disable the Roslyn/MSBuild build servers REPO-WIDE and prevent the old CS2012 lock-holding daemons.
- At a landing, the conductor records the dogfood entry in `.orchestrator/dogfood-log.db`; you still close the finished backlog item (`backlog-close`) and add newly discovered ones (`backlog-add`).

**Manual lower-level verbs (fallback / granular control only — prefer `conduct --loop`):** `subscription-dispatch <n>` → `start-dispatch <n> --confirm-dispatch-start` (the cost guard blocks ONLY on an *anomalous* prompt — disproportionate to task complexity, ≥2× the per-complexity ceiling, or batch total ≥2× the batch ceiling; routine/legitimately-large Complex briefs proceed silently, so `--confirm-large-paid-subscription-start` is needed only when a genuinely bloated prompt trips it) → wait on the printed pid → `refresh-dispatch <n>` → operator gate → `accept`. `acceptance`/`accept` fast-forwards only when main has not advanced mid-goal; otherwise run the printed merge on a scratch verification branch first, then land on `main` only once the evidence is acceptable. Direct `acceptance` also has a known state-guard caveat; follow the runbook's stuck-goal guidance before invoking or retrying it. ApiOnly tasks (e.g. the local Reviewer) run via `run <n>` with no file access — output reflects prompt text, not branch state; close HUMAN_INPUT with `answer <request-id> --text-file <path>`, then `verify-manual <n> passed --text-file <path>`. To put an operator note into an undispatched task's brief, use `progress <n> running --text-file <path>` → `progress <n> failed --text-file <path>` → `retry <n> --text-file <path>`.

When the task involves subscription/dogfood execution:

- Verify worker profiles before starting subscription tasks.
- Treat `Write-Output {promptPath}` profiles as echo-only, not real execution.
- After dispatch, confirm evidence, process logs, exit code, and verification records; never trust task status alone.
- When credits are constrained, inspect existing continuations/evidence/logs and cancel stale work before starting new agents.

### Guidance receipts

Historical examples moved from worker guidance are retained here under their source topics.

**Dispositive decisions — outcomes chosen after discriminating evidence was discarded upstream:** Five instances of this shipped in a single day.

**Architecture & Design Discipline — modeling a judge as a worker role:** It was first hacked in as `AgentRole.Judge` and immediately required an `AgentRoles.Worker` exclusion set everywhere roles are enumerated; that churn was the tell.

**Architecture & Design Discipline — registry ownership:** `config/acceptance-manifest.json` is the worked example: it names every test class in literal lane-filter strings, and `AcceptanceGateEngineSettingsTests` restates the whole manifest again as a hardcoded census of lane names and durations. On 2026-08-14 three goals blocked on that one file, two of them within ten minutes of a single landing — and the landing that caused both was a **deletion** goal, not a decomposition one: removing dead production code removed its test classes, which required editing lane filters. Any goal that adds, removes, renames, or relocates a test touches it. Sequencing the decomposition goals against each other did not help, because the collision arrived from a third goal nobody had sequenced against.

**Architecture & Design Discipline — control signals:** (Scar: backlog `0624e65d`.)

**Architecture & Design Discipline — serial fraction:** Receipt (2026-08-13): the acceptance gate ran 17 shards in 798s wall, but its longest single shard was 473s and its shortest 28s — a 17x spread, so 473s is the floor at any width, and the two slowest were slow because they were *serialized by collection attributes*, not because they were large. Splitting those files would have bought nothing; making them hermetic is the only thing that moves the floor. Measure before prescribing: that spread was invisible until per-shard elapsed times were read out of the event stream.

**Specification Discipline — observable integration:** (the Discord listener that deleted its own forum post compiled and passed fake-API tests; the gateway built but never hosted; `postResult` left optional so the operator saw nothing)

**Specification Discipline — external contracts:** (Discord interaction-ack semantics were unspecified → improvised wrongly.)

**Diagnosis Discipline — empirical reproduction:** A web search said `gpt-5.3-codex-spark` was "exec-restricted on ChatGPT accounts" and the outcome scorecard rated it Avoid (0 completed / 2 failed); both were misleading. A single ~17-second `codex exec --model gpt-5.3-codex-spark ... </dev/null` proved spark works fine and isolated the real bug: the judge runner never closed the child's stdin, so codex blocked forever on "Reading additional input from stdin…" → 180s timeout. One empirical test turned "unsalvageable" into a two-line fix (`RedirectStandardInput=true` + `StandardInput.Close()`).

**Diagnosis Discipline — ambient causes:** Worked example: a Tester's test command returned exit 124 and it was blamed on resource pressure; the worker `.err.log` proved a codex ~124-second tool-call timeout killed a build+test run bundled into one command — a specific, fixable mechanism, not an ambient condition.

**Safety — firewall:** (root cause and one-time setup: DOGFOOD_LOG 2026-06-11 firewall entry)

---

## 9. Where else to look

- `AGENTS.md` — output/diagnosis/spec discipline and architecture invariants (read after this).
- `.agents/skills/orchestrator-worker-verification/SKILL.md` — how to verify a worker result before trusting it.
- `next <goal> --full` — authoritative live goal inspection; `readiness [goal]` reads terminal-goal diagnosis and start blockers through the read-only route, without outbox draining. Bare `readiness` keeps the session current goal when its id is present in goal metadata; otherwise it selects the newest-created goal (including terminal goals), with metadata enumeration order breaking creation-time ties. It loads only the selected goal for the report. `readiness-repair <goal>` is the only readiness form that repairs; it and `goal-recovery <goal>` run repairing terminal-goal sweeps and can change state.
