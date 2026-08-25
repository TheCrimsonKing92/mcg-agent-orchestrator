# Orchestrator Handoff

**Updated:** 2026-08-24 by Claude. Live status, not durable policy — verify before mutating.

**Pruned 2026-08-24:** removed ~1380 lines of superseded RESUME HERE sections (08-05 through 08-18), old
session results, and items already marked resolved or void. What was kept: the reading rules, the two current
sections, the operator-CLI safety section whose closure is still unverified, the 08-03 velocity analysis, two
sections about `fb13475c` that are still live, and the durable diagnostics at the tail. Deleted content is
recoverable from git history.

## HOW TO READ THIS FILE — folklore warning

This file accretes by session. **Anything below the current session's sections is a point-in-time
record, not current truth.** On 2026-08-07 an audit found that a section marked `⚠ CRITICAL` described
a code gap that had since been closed, and a list headed "highest-value open items" had **4 of its 5
items already Done**. An operator followed both as live guidance for a full day.

Rules for using and maintaining this file:

- **Verify before acting on any directive older than the top section.** Goal states move
  (`status <goal>`), backlog items close (`backlog-show <id>`), and code gets fixed. All three are one
  command away.
- **A struck-through heading with a re-checked date means the claim was audited.** Absence of one means
  nobody has checked, not that it is still true.
- **When you invalidate a claim, mark it in place** — do not delete the reasoning. The receipts and the
  dead ends are the durable value; the conclusions expire.
- **Durable lessons belong in `### Operating lessons worth keeping`; live state belongs in the top
  section.** Everything else is history.

## RESUME HERE — 2026-08-24 22:05 UTC, ten landings; the gate artifact defect is FIXED and landed

Ten goals landed on 08-24: `1d6b3fae`, `9c3885b2`, `ee57cc09`, `a04cdfe9`, `8c7fb174`, `d7585642`,
`604b8a93`, `f28c201d`, `dd6ba0f8` (`e4701815`), `98430a7c` (`7cf14424`). Two more (`8612dcf0`,
`e68a6324`) landed just before midnight on 08-23.

### ⚠ READ THIS BEFORE DIAGNOSING ANY GATE FAILURE — a passing TRX may not be that failure's receipt

**A check reported failed whose TRX shows all-passing is NOT a lying exit code.** Acceptance telemetry paths
derive from the attempt prefix plus `Slug(check.Name)`, and each invocation deletes the existing TRX at that
path first. Two checks named `infrastructure tests: Remainder` in one plan therefore share one path:

    Remainder A runs, fails    -> gate records child exit 1/2 in memory
    Remainder B deletes the TRX at that path and reuses it
    Remainder B runs, passes   -> writes a green TRX and an exit-0 heartbeat
    result.json still holds A's failure, sitting beside B's receipt

The verdict never reopens the TRX to reconcile. Cited at
`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs` lines 6263, 6287, 3397, 7348
and 729.

**FIXED AND LANDED as `98430a7c` (`7cf14424`, 2026-08-24 16:20 local).** Gate artifacts produced after that
commit should be the failing invocation's own receipts again, and the normal TRX-first diagnosis works.
**Artifacts retained from attempts BEFORE it remain corrupted** — every gate attempt earlier on 08-24 and
before may pair a failed verdict with another invocation's green TRX. When reading an older attempt, still
prefer the failed process's stdout or the partition-cache receipt. The fix also had to close a second path:
the lock-remediation retry constructed its result without a run ordinal, which the Reviewer caught before it
landed.

**Consequences you will hit:**
- The failing invocation's TRX **and heartbeat** are gone. Read the failed process's stdout, or the
  partition-cache receipt, which retains both executions (one RED, one GREEN).
- `scripts/Get-AcceptanceFailureCensus.ps1` inherits the blind spot. It reported `failed=0
  distinctSignatures=0` for two real multi-failure attempts. The tool is not wrong; its input is.
- MTP exit 2 is `AtLeastOneTestFailed` and exit 1 is `GenericFailure`. **Exit 2 beside an all-green TRX is
  self-contradicting on its face** — that is your tell.

Cost on 08-24: goal `dd6ba0f8` was closed as a non-attributable flake **three times** on fabricated
evidence, one of its hidden failures being in the subject area it was changing. It also produced a false
board-wide alarm — three failures across two goals with no passes for two hours, escalated as critical, and
it was neither. Another gate passed shortly after, and the onset (16:35:30Z) **preceded** the loop handoff
(16:39:56Z) that had been blamed.

### AUTO-RESUME IS DISABLED — a dead loop stays dead

`Get-ScheduledTask McgOrchestratorAutoResume` returns **State=Disabled**, last successful run 2026-08-02,
`NumberOfMissedRuns = 1919`. It has been off for three weeks.

Measured cost: on 08-24 the loop stopped at 05:43 UTC on `reason=max-duration` with no `.conduct-stop`
present, and stayed down **6 hours 36 minutes** until relaunched by hand. Two goals sat frozen in
`Verifying` throughout.

**After any `LOOP_STOP`, relaunch manually.** Re-enabling needs elevation the Claude Code classifier blocks,
so it is an operator action.

The `--max-duration` self-renewal handoff still works and was verified **twice** on 08-24, at 16:41 and
20:42, staging `312b3f52` and then `e4701815`. Both times the retry counter mattered: the first attempt logs
`repositoryHead=resolve-pending` and it succeeded on attempt 2 and 3 respectively. **Verify a handoff by
checking `stagedSourceCommit` equals `repositoryHead`, then confirming an additional `LOOP_START` appears in
the same log file** — the successor inherits the log path, so a new file is not created.

### Gate flakes are the throughput tax, measured

Across retained conduct logs: **66 gate attempts, 26 failed — a 39% failure rate.** Of six failures
diagnosed by hand on 08-24, five were caused wholly or partly by flakes rather than defects. With **two**
gate slots and 10-25 minute gates, that consumes roughly four in ten of the scarcest resource.

`a04cdfe9` is the clearest case: it lost **three consecutive gates** to three unrelated causes — a
documented flake, OS pid recycling (`stage=duplicate-or-recycled-pid`), and a guard false positive — while
its content had been correct since the first fix. It passed on the fourth attempt.

Landed on 08-24 to reduce this: `ee57cc09` (a 250ms wall-clock budget truncating a retry loop),
`d7585642` (a 2-second `FileSystemWatcher` budget that reddened four separate goals), `8c7fb174`
(structural coverage no longer deleting a test project's lane partitioning).

Still open: backlog `4519b61b` — a git helper in `GoalAcceptanceEvidenceBundleTests` treats a git call that
returns **exit 0 with empty stdout** as a failure. Independent sol analysis on 08-24 identified the same
exit-0/empty-stdout shape behind seed-isolation failures, so it is one family, not two.

### Roster and config changes made 08-24

- **Planner moved to `gpt-5.6-sol`** (operator direction) — **confirmed live after the 20:42 bounce**, where
  a goal's Planner task shows agent `openai-p` rather than the previous `anthropi`. A roster change needs a
  bounce to take effect, and in-flight tasks stay pinned to their old agent. The `agent` verb changes less
  than it appears:
  the first invocation moved only the API model and left `Subscription.ModelAlias` on `gpt-5.5` at
  `reasoning=low`, which is what actually runs under `ExecutionPolicy=PreferSubscription`. Pass
  `--subscription-model` and `--subscription-reasoning` too, and note `--complex-model` silently resets
  `ComplexModel.ReasoningEffort` to null. Verify the printed line, do not trust the verb.
- Loop running `--policy Permissive --max-duration 14400`, `effectiveWorkerCap=5`,
  `workerAdmissionCapacity=9`, `reservedGateSlots=1`.

### Live board at handoff

In flight: `a9ea57dd` (ratchet burns gate cycles — at its gate), `da16a801` (cohort observability),
`ac89d2c8` (status reprint).

**`a9ea57dd` needed an operator merge resolution on 08-24 and it is worth understanding before touching it.**
`f28c201d` deleted the entire inline-landing acceptance path when it consolidated the two execution paths, so
`a9ea57dd`'s source-size guard on `RunInlineLandingAcceptance` was attached to a method that no longer exists.
Resolution taken: main's deletion, for both `ConductorDriver.AcceptanceLanding.cs` and its test file, which
main had rewritten for the consolidated path. Its other three guards survive — cohort
(`ConductorDriver.cs:3736`), parallel landing (`:4037`), merge train (`ConductorDriver.MergeTrains.cs:104`) —
as do its four tests in `GoalAcceptanceVerifierTestsSourceSizePreflight.cs`. The merge was verified to build
with 0 errors before gating, and the Reviewer independently raised the same integration concern.

**A suspected regression in `dd6ba0f8` was investigated and DISPROVEN.** An earlier operator note suspected
`WorkerDispatchBuildEvidenceClassificationTests.MissingEvidencePassingCheckCompletesAndCommits`. A full gate
subsequently passed, so it was not real. That suspicion originated from the TRX overwrite defect above.

**Four goals are `Verified` with unmerged branches and are NOT landed:** `58ae2e59`, `9deb2c19`,
`b9398a95`, and `fb13475c` (the last dating to 08-03 — see its two sections below, both still accurate).
`git branch --merged main` lists none of them.

`58ae2e59` is the concerning one — the sweep reported it `merge-evidence-terminalized` with
`integrateSha=83d0ea3a` at 05:42 UTC, yet that commit is not an ancestor of HEAD and the branch is unmerged.
**Do not treat a `merge-evidence-terminalized` sweep repair as proof of a landing.** Check
`git branch --merged main`.

### Operator direction in force (Miles, 08-24)

Wind the board down: drive what is live to completion, keep filing defects as they are found, but do not
intake new work unless it is critical. Defects that are not blocking go to the backlog rather than the board.

## RESUME HERE — 2026-08-20 20:05 UTC, eleven landings; gate throughput is now the binding constraint

Board recovered fully from the deadlock: **11 landings on 08-20** against 2 on 08-18 and 1 on 08-19. Most
recent `bc01a13c` (`9bf2b63a`, base build cache). Six goals ran concurrently after the paid-worker cap was
corrected from 5 to 6 (see below).

**The constraint moved from workers to gates.** Four goals finished their pipelines within forty minutes and
then queued behind one gate each, because acceptance serialises them. Raising worker concurrency did not
raise throughput, it just moved the queue downstream.

### The isolation finding, which is the most important thing on this page

Operator direction (Miles, 08-20): decompose not only to kill god classes but to get **finer-grained test
isolation so the grouping features can engage**. Measured evidence says the seams as previously scoped
deliver none of that.

Goals serialise on TWO reservation keys, both from `RepositoryOwnershipMap.Classify`:

    ownership:shared-infrastructure                                        (Core + Infrastructure + Providers, ONE key)
    ownership:test-project:tests/mcg.agentorchestrator.infrastructure.tests

A pair needs BOTH distinct to gate concurrently, cohort, or join a merge train. At 19:53 four queued goals
had all six pairs excluded. `3d016860` is the clean proof: it touches only a docs file and one test file,
has NO production key at all, and is still excluded from pairing with everything.

Consequences, each verified:
- Class-level extraction inside `src/Mcg.AgentOrchestrator.Infrastructure/` does not change either key. Seams
  4, 3, and 2 all landed and moved neither.
- Seam 6 (`08537608`) created a genuinely separate collaborator and then put its tests back in the monolith
  test project. Real extraction, **zero** isolation gain.
- This is why acceptance cohorts have never executed across hundreds of planning ticks, and why the merge
  train would not have formed on this queue either — `ConductorMergeTrains.Overlaps` excludes on shared
  `ResourceKeys`, which is a parallelism rule misapplied to a mechanism that gates members as ONE attempt.

Work filed: goal `f43caad4` (production key split, in flight), backlog `caef9efe` (test-project split, **the
binding half**), backlog `3e1a0ad7` annotated with the merge-train overlap fix, backlog `8aae1018` now
requires each seam to land its tests in its own test project.

### Defect species found 08-20: correct work blocked by an undisclosed protocol

Five instances, all found by watching goals fail rather than by reading code. Four cost paid rounds.

| Backlog | Defect | Documented remedy | What actually happened |
|---|---|---|---|
| `0699a50d` | Tester reporting failing tests has its dispatch DISCARDED (`HasCompletedVerification` requires no failing tests) | report failures | discarded; four rounds lost, and a Tester reported `blockers=none` on a candidate that did not compile |
| `6c1f59ee` | `AdvanceFault` set-aside makes a Verified goal invisible to the loop | `recover` | accepted, mutated nothing, no effect |
| `b204ee6e` | Build break cannot be routed upstream; `retry` refused while any downstream task runs, and Tester↔Reviewer never idles | `retry` | refused twice, no alternative named |
| `9fbce159` | One unresolvable backticked span discards an entire Planner plan | — | 7 plans discarded in one day |
| (Reviewer) | `blockers: none` does NOT close findings; a prior finding not resubmitted stays Open (`ReviewFindings.cs:566`) | — | `ae9dccd4` burned three full cycles |

### Corrections issued against earlier claims on this page and in reports

- **"Retention made gates 40% faster" is RETRACTED.** Five timed gates split 47.7 min mean (busy board) vs
  28.4 min (quiet board), and retention landed between them. The discriminating test — one gate under full
  contention — came back at **46m39s** (`9bf2b63a`, 19:12:21→19:59:00). Gate wall-clock tracks contention,
  not retention. Target concurrency and lane duration, not serial I/O plumbing.
- **Base build cache was never a gate-latency lever.** Measured on real cache data: hash+copy total ~320 ms
  per attempt against gates of tens of minutes. `docs/measurements/basebuildcache-io.md` has the numbers.
  Requiring a committed measurement artifact is what caught this.
- **"The ownership key is the cheaper half worth landing alone" is wrong.** It unblocks nothing for a queue
  whose goals all add tests to one test project. `caef9efe` is the binding half.
- **Decomposition line-count target is unreachable as written.** `GoalAcceptanceVerifier.cs` went 6,342
  (07-27) → 8,713 (08-20) while ~761 lines were extracted. Judge seams on isolation, not size.

### Operating notes worth keeping from this session

- **The conductor ran 48 commits stale for ~19.5 hours.** The max-duration handoff spawns a
  `--continuity-child` that reuses the parent's pinned `%TEMP%\mcg-run\<hash>` directory. **Check the marker
  in the run-dir the PROCESS NAMES, not `src/.../bin/Debug`** — those diverge exactly when it matters. See
  the new runbook section "Which binary is the running conductor actually executing?" (`fde5f80f`).
- **`--policy Permissive` silently overrode `conductor-policy.json`.** The flag selects a built-in preset
  (cap 5) and the file is loaded only to validate it before being discarded, so `PermissiveCap6Trial`'s cap
  of 6 had been inert. Relaunch WITHOUT `--policy` to honour the file.
- **`--help` is curated.** The full verb list is `CliArgumentParser.CommandCatalog.cs`. Verbs absent from
  help but present and useful: `cancel-dispatch`, `backlog-depends`, `backlog-update`, `backlog-supersede`,
  `loop-health`, `durations`, `provenance`.
- **`cancel-dispatch <goal> <task#>` is the escape hatch** when `retry` on an upstream task is refused.
  Cancel the downstream worker, then retry upstream immediately — the cycle re-dispatches within ~60s.
- **The AdvanceFault remedy** is `retry <goal> <last-task#> --mechanical`, then `progress ... completed`,
  then `verify-manual ... passed`. It costs one real dispatch on the tick between reopen and close. Hit four
  times today.
- **The grok Researcher death is fixed operationally.** `--permission-mode plan` headless has no approver, so
  a tool outside the allow set is cancelled in ~1 ms and grok exits 0 — 2 of 4 Researchers died before the
  fix. `grok-researcher` is now `Status: Offline` in `agents.json` (an agents.json edit specifically so
  `recover` cannot revert it) and the codex researcher is primary. Root cause is
  `WorkerProfileDispatcher.cs:3118`; the misclassification as `researcher-output-contract-rejected` is the
  worse half and is unfixed (backlog `d8ff610b`).

## SUPERSEDED 2026-08-20 20:05 — kept for its receipts. RESUME HERE — 2026-08-20 09:35 UTC, the fourteen reds are FIXED and landed; board draining

**RESOLVED at `c0ad1c47`.** Main no longer carries the fourteen red tests that made the acceptance gate
unpassable for every goal. A combined candidate carrying all fourteen fixes gated with **zero failures
across every lane** and landed. `af19fd2a` (`6241ed3f`, gate-latency velocity) landed behind it.

**How it was resolved, because the mechanism matters.** The two fix goals DEADLOCKED each other:
`4fa6af44` fixed six Planner-contract reds, `c30eb2fe` fixed eight local-provider reds, and any candidate
touching `src/Mcg.AgentOrchestrator.Infrastructure/` runs the whole Infrastructure suite
(`RepositoryTestImpactPlanner.cs:194`), so each one's gate met the other's reds and failed. Proven, not
inferred: `4fa6af44`'s gate showed all six of its own fixed, zero introduced, and still failed on exactly
`c30eb2fe`'s eight. The exit was to merge `goal/4fa6af44` into `goal/c30eb2fe` so ONE gate run could see
all fourteen fixes at once. **That merge was manual and only necessary because the gate cannot distinguish
"you broke this" from "this was already broken" — backlog `92f531b4`, which carries the full proof.**

Root causes worth keeping:
- Six Planner reds: `MarkdownHeadingNormalizer`'s `(?m)(?<=\S)(#{1,6}[ \t]+\S)` — the first `#` of a
  start-of-line `## Heading` satisfied the lookbehind, so the replace demoted it to `# Heading`. Fixed to
  `(?<=[^\s#])`. One product bug behind all five failures, found by a grok Tester whose dispatch was then
  rejected on output shape.
- Eight local-provider reds: stale Ollama expectations after the LlamaCpp switch, plus one real bug —
  `OrchestratorHealthInspector.IsApiRouteUsable` treated `Mode == "LocalBridge"` as an API route.

**Still open and worth knowing before you drive:**

| Goal | State |
|---|---|
| `21b284a0` | **held deliberately.** Its criterion demands the focused-evidence path that `9a9c7e8e` breaks; it burned 32 attempts. The Developer fix is done. Read the note on task 4 before removing the hold. |
| `fe37d616` | `GoalAcceptanceVerifier` seam-4 extraction, recovered and current. **This is the only genuine test of whether the post-landing canary fires** — it is the sole engine-touching candidate. |
| `9f64cd98` | squashed from 12 commits (with merges) to **1 linear commit** to stop a rebase treadmill that recurred every time main moved. |
| `13b3be0d`, `ab933e32` | draining normally against a green base. |

`4fa6af44`'s root cause is worth keeping: `MarkdownHeadingNormalizer`'s `(?m)(?<=\S)(#{1,6}[ \t]+\S)`
demoted a start-of-line `## Heading` to `# Heading`, because the first `#` satisfied the lookbehind.
One product bug behind all five reds. Fixed in `c85b9bdc`, confirmed by a focused receipt: 26 tests
executed, passed.

`c30eb2fe`'s eight cluster around local provider routing and are **hypothesised** (not diagnosed) to be
stale Ollama expectations after the LlamaCpp switch. Verify before fixing.

**How that was nearly missed, and the lesson.** The first read of a failing gate said "worker profiles:
failed: 1". That came from grepping two of roughly twenty lane receipts. Always sweep every lane:

```
grep -oh 'testName="[^"]*"[^>]*outcome="Failed"' <attempt-dir>/<attempt-id>.*.trx | grep -o 'testName="[^"]*"' | sort -u
```

**Roster: codex is back on Developer** (`openai-developer`, `gpt-5.6-sol`, `codex-cli`, medium). Verify
which harness actually ran by the prompt filename suffix (`-codex-cli.md` / `-grok-cli.md`) or the
`Dispatched to ...` reason — **not** by `agents` output or `status`, both of which lied all evening. See
backlog `fd4a5ed5`: a roster change and a `reassign-agent` can both report success and never reach
dispatch.

**Five defects filed tonight, all orchestrator-side, all cost real rounds:**

| Id | Defect |
|---|---|
| `9a9c7e8e` | a rejected focused-evidence request is replayed verbatim forever; burned 16 retries on `21b284a0`. Trigger is **multi-selection** requests only — a single selection works |
| `2861b909` | operator text in `recover`/`retry --text-file` is recorded, reports `Applied`, and never reaches the prompt when the retry counter is not yet engaged |
| `fd4a5ed5` | `agent`/`agent-add` leave the subscription alias stale, and task agent assignments revert silently |
| `9fbce159` | the Planner contract rejects `File.cs:442-479`; a line range discards the whole plan. Mitigated for future Planners by `045930f8` |
| `7009ffbd` | the eight local-provider reds above |

**Operating notes that saved or cost time tonight** — all now in `docs/operator-runbook.md`:
verify a conductor rebuild with `App.dll.git-head` vs `git rev-parse HEAD` (run-dir hash proves nothing;
handoff rebuilds are intermittent); never grep a .NET assembly for a string you added (UTF-16, gives
false negatives on strings that are demonstrably present); put worker guidance in the **brief**, not a
recover note; and front-load numbers in operator notes because truncation eats the middle.

Prior section follows.

## ~~⚠ CRITICAL~~ — OPERATOR CLI COMMANDS KILLED LIVE WORKERS — **GAP APPEARS CLOSED, VERIFY BY RECEIPT** (re-checked 2026-08-07)

> **Read this box before the section below.** The described gap — the sweep killing a live victim
> without checking whether its owning conductor was alive — is **not present in current code**.
> `WorkerProcessJobs.cs:77-82` now evaluates owner liveness first and retains unless the owner is
> `DeadOrRecycled`, with a `retain-unknown-victim` path at `:85-96` and a `GracefullyDetached` branch
> at `:42-75`.
>
> This was verified by reading the code, **not** by receipt. Four reaping receipts in `state.db`
> established the original defect, so confirm the fix the same way before relying on it.
>
> It remains a live, unproven candidate for the unexplained `root_exit_code=1 / child_exit_code=0`
> Developer failures on `3a7afb95`, which occurred while an operator was running recovery verbs. Check
> `state.db` reaping receipts against those timestamps.
>
> **An operator followed this section as current guidance through all of 2026-08-06.** Tracked in the
> command-safety program, backlog `c2ed4680`. The historical account below is preserved as the
> reasoning record.

**`WorkerProcessJobs.cs:30` treats every live shared-registry entry as an orphan and kills it (lines 39-58)
WITHOUT checking whether its owning conductor is alive.** `Program.cs:447` runs that sweep on
lifecycle-owning App startups and `Program.cs:397` enables destructive startup cleanup for nearly EVERY
stateful command — the exemption list at `CliPersistentStateRunner.cs:419` is narrow.

**Age: ~6 weeks.** `SweepStartupOrphans` landed 2026-06-22 in `3f654221` and never checked owner liveness.
`MatchesLiveProcess` (line 41) confirms the VICTIM is alive — that is the entry condition for killing it.

**CORRECTION to the first version of this note:** `backlog-add` is EXEMPT (`SkipsKernelState` line 433;
`RequiresKernelBacklogState` line 452 for `--depends-on`) and cannot have caused the 01:19:30 reaping — I
asserted that from a timeline without checking whether the verb reaches the sweep. Trigger for that instance
is UNIDENTIFIED; the mechanism is confirmed. `backlog-intake` and `attention` are in neither list and DO sweep.
`authorityTransferRequested` returns at Program.cs:461 before sweeping, so max-duration handoffs are safe.
Sweeping verbs = everything not exempted: `recover`, `retry`, `progress`, `verify-manual`, `goal`,
`backlog-intake`, `attention`, `pending` — i.e. precisely the RECOVERY verbs, reached only when live work is
already fragile. Prior regression of this same class: `bc642e02` (2026-07-30) re-enabled the sweep for
read-only `backlog-list`; the deny-list guards a destructive DEFAULT, so every newly added CLI verb is unsafe
until someone remembers to exempt it.

CONFIRMED by `.orchestrator/state.db` reaping receipts, not inference:
| PID | goal | `startup-reaped` at | the command I ran |
|---|---|---|---|
| 37108 | `b3ada0bb` | 01:19:30.715Z | `backlog-add` |
| 35200 | `1332b2ea` | 01:19:31.045Z | same sweep, 330ms later |
| 35712 | `b3ada0bb` | 02:20:09.288Z | `backlog-intake --create-goal` |
A fourth (Reviewer `b7b88f90`, 02:29) coincides with `attention show`/`answer`. Victims spanned BOTH
providers (codex Developer, claude Reviewer) and both roles — consistent with an indiscriminate sweep, and
it rules out any provider-specific theory.

**Why it is worse than "a worker died":** killing the host skips the ONLY exit-artifact writer
(`DispatchProcessHost.cs:1364`, end of a `finally`); the tree is in a kill-on-close job
(`OwnedProcessGroup.cs:19`) so all descendants vanish and the heartbeat freezes at `state: running`. The
reconciler then MANUFACTURES exit 1 (`BackgroundDispatchRunner.cs:683-684` → written at 1253-1255), and
`DispatchRecoveryPolicy.cs:53` / `DispatchStateSurface.cs:203` track only artifact EXISTENCE, not
native-vs-synthetic provenance — so a fabricated failure is indistinguishable downstream. **This is the
mechanism behind the long-standing "dispatch manufactures failures for successful workers" note.**
Careful: success is NOT proven for these — `b3ada0bb`'s stderr ends mid-diff, `1332b2ea`'s during context
ingestion. Honest verdict is "interrupted after useful work". Recovery must PRESERVE work without ASSERTING
success (quarantined WIP snapshot, not a result-bearing commit).

**OPERATOR RULE UNTIL FIXED: do NOT run any App CLI command while workers are live** — including
`backlog-add` and `attention show`. Batch operator actions into worker-free windows, or accept that each
command may destroy a paid round. HANDOFF/file edits are safe (no sweep).
**NOT YET FILED as a backlog item** — filing requires `backlog-add`, which would kill live workers. File it
from `scratchpad/backlog-startup-sweep-kills-workers.txt` during the next worker-free window.
Fixes, in order: (1) sweep must check owner liveness; (2) invert the exemption list so commands opt IN to
destructive cleanup; (3) record the SWEEPER's pid/argv in the receipt (today it names only the victim, which
is why this needed a PID-targeted `state.db` query to find); (4) distinguish synthetic from native exit
artifacts (overlaps `f254171c` item 1 / goal `b3ada0bb`).

**FILED 2026-08-03/04 (this drive):**
- `7a81b5f6` startup sweep kills live workers. Annotated twice: sol's adversarial review, and the operator
  UNIX DEFERRAL scope decision. Design conclusion: the command-name list is the flaw in EITHER polarity;
  orphanhood must be decided from recorded owner-liveness evidence, not from who is asking. With Unix
  deferred, reclaim has NO constituency (Windows KILL_ON_JOB_CLOSE already reclaims), so the scoped fix is
  `startup-live-deferred` inside `SweepStartupOrphans` at the capability seam. STILL IN SCOPE regardless:
  `MarkReleased` keys on PID not entry id (`SpawnRegistry.cs:79`), and `GoalWorktreeOrphanSweepScheduler.SweepNow`
  at `Program.cs:465` is a SECOND destructive action on every stateful command.
- `8b2e0873` gate cancellation escalates with an untyped reason. Six causes (null record, Active, Parked,
  AcceptanceFailed, Cancelled, Superseded, Failed) collapse to one `Func<bool>`, then a message naming no
  disposition, then a 40-char clip. Cost ~39 min of dead lane time on `65e85ad6` tonight.
- `51926605` test cleanup swapped ACL-aware helper for `Directory.Delete` + `catch { }` and dropped
  `[Collection]`; gate's own log proves these trees resist deletion ("Directory deletion failed after ACL reset").
- `54b0a707` Reviewer rounds consumed by "finding-identity contract repair"; n=2 across goals, and on
  `b3ada0bb` one emitted `pass` WITHOUT re-reviewing a changed HEAD - which is how `51926605` reached a gate.

**SAFE vs SWEEPING CLI VERBS (verified in source, corrects the earlier blanket rule).**
`RunsStartupCleanup = !SkipsKernelState && !RequiresKernelBacklogState` (`Program.cs:402`).
SAFE (never sweep): `backlog-add` (without `--depends-on`), `backlog-update/annotate/close/supersede/link/reopen/view`,
`backlog-list/show/depends`, `gate-status`, `acceptance-engine`, `cleanup-status`, `repo-process-info`, `run-event`, `project`.
SWEEPING: `recover`, `retry`, `progress`, `verify-manual`, `goal`, `backlog-intake`, `attention`, `pending` -
i.e. precisely the RECOVERY verbs. Holding ALL verbs (my earlier rule) costs velocity for no safety gain.
CONSEQUENCE: dispatching `7a81b5f6` needs `backlog-intake`, a sweeping verb - the fix cannot be dispatched
without triggering the defect it fixes. The sweep is now a CONCURRENCY CEILING, not just a paid-round risk.

**LANDED #35: `d3829298`** - main `b32a6493 Integrate goal/d3829298`. Conductor continuity: durable
lifecycle records, supervisor restart/backoff/caps, AND both self-stop fixes (watch-mode blocked-recheck
budget at `ConductorBatchLoop.cs:620`, plus the one-shot `no-progress-no-watch` path at `:1115-1125`).
NOTE: handoffs spawn the PREBUILT exe, so this fix only armed when the loop was relaunched via
`Start-OrchestratorCommand.ps1` (which rebuilds) at 14:51Z. Loops started before that do NOT have it.

**GATE TRIAGE RECEIPTS (2026-08-04) - three goals, useful reference for what real vs apparatus looks like:**
- Apparatus signature: gate fails in 28-57s at "core tests" with `UnauthorizedAccessException` on
  `...mcg-hvp\AppData\Local\Temp\Low\...`. Fixed at host level (see temp-label note above); code fix filed
  as `302b92f6`.
- Real signature: gate runs 12+ min and returns assertion failures. After the host repair, `b3ada0bb`
  surfaced FIVE genuine failures that the apparatus noise had been hiding, then 3 of 5 were fixed by ONE
  line (removing `WorkTaskStatus.Completed` from the reconciler's early-return guard at
  `BackgroundDispatchRunner.cs:501`).
- `d3829298`'s passing gate ran 18 infrastructure shards in ~156 SECONDS (vs 12-19 min typical), which is the
  shared base-build cache working with a warm main sha.

**VELOCITY GOAL IN FLIGHT: `10075221`** = backlog `d87d26be` partition-level pass caching (re-run only failed
partitions on a gate re-roll; item estimates re-rolls 20-25 min -> 2-7 min). This was the ONLY one of three
velocity candidates that survived a staleness check - see the velocity note above.

**LANDED #34: `65e85ad6`** - main `a92b2989 Integrate goal/65e85ad6`. Note it passed with 2 files from
Developer and 0 from BOTH Tester and Reviewer (thin five-role goal; cf. unlanded `3a7afb95`).

**`b3ada0bb` IS TERMINALLY Failed BY A FORMAT BUG - ITS CODE WAS SOUND.** Root cause found and recorded in
`54b0a707`. `ReviewFindingLocation` is `(File, Region, Hunk)` and `ToString()` renders `{File}::{Region} [{Hunk}]`
only when Hunk is non-empty (`ReviewFindings.cs:132-141`), while `SameAnchor` compares File + Region and NEVER
Hunk (`:526-542`). So `Region="...predicate" + Hunk="L1934-L1946"` and `Region="...predicate [L1934-L1946]" +
Hunk=null` RENDER IDENTICALLY but are unequal anchors. `NormalizeRegion` (`:544-554`) should strip the trailing
range but its regex arms want `[<digit>` or whitespace-then-`l`; the real form `[L1934-L1946]` is bracketed AND
L-prefixed and matches NEITHER. Result: `ERR_REVIEW_FINDING_IDENTITY_MOVED` on genuinely-equal-looking values,
`MaxReviewFindingContractRepairsPerRound`=2 exhausted, goal Failed. The escalation message contains violation
code, both ids, both locations, open count AND the operator remedy - and the record kept 40 CHARACTERS of it
(that clip is `8b2e0873`). Full text is recoverable from the conductor stdout log, not from the escalation.
NOTE: I first hypothesised "line ranges are a volatile anchor that shifts under Developer edits" - WRONG,
Hunk is never compared. Retracted in the annotation.

RECOVERY SEQUENCE (from the truncated message, recovered from the loop log) - ALL THREE ARE SWEEPING VERBS:
`retry b3ada0bb <task#> "<reason>" --mechanical`, then `progress <task#> completed`, then
`verify-manual <task#> passed`. Re-escalates harmlessly at every loop start (re-derived from stored review
state, NO new dispatch, zero paid rounds), so there is no time pressure - wait for a genuinely worker-free
AND gate-free window.

**GATE TRX TRIAGE - FILTER ON `outcome='Failed'`, NOT `outcome != 'Passed'`.** `NotExecuted` means SKIPPED and
carries no ErrorInfo message, so a `!= Passed` filter reports skips as message-less "failures".
`LockAttribution_handle_probe_returns_results_for_real_held_file` and
`GoalAcceptanceVerifier_real_runner_smoke_is_opt_in` are opt-in/environment tests that are NotExecuted in
EVERY gate including passing ones. I mis-triaged them as environmental failures twice.

**RELAUNCH TRAP: `conduct --loop` DEFAULTS TO `Conservative`. ALWAYS PASS `--policy Permissive`.**
The flag is `--policy <Conservative|Permissive|Manual>` (`CliCommandHandlers.Goals.cs:1294`), default
`ConductorAutonomyPolicy.Default` = Conservative. Handoff relaunches inherit the incumbent's policy, so this
only bites on a MANUAL relaunch - which is exactly when an operator is already recovering from something.
Symptoms under Conservative, all of which look like unrelated defects:
- Every goal whose write-set touches a high-risk ownership area holds forever with
  `No tasks dispatched; all ready tasks require operator approval ... high-risk ownership area requires
  operator approval: Script scripts/<name>.ps1; reserved write-set resource: ownership:scripts`.
  `ParallelExecutionPlanner.cs:28` gates this on `approveHighRiskOwnership && !hasGeneratedPath`.
- Goals escalate to `Failed` at role boundaries with the generic
  `Goal_is_in_Failed_state;_operator_action`, indistinguishable from a real failure.
Confirmed live 2026-08-04: after relaunching without the flag, `d3829298` and `b3ada0bb` held indefinitely and
`9de9649d` (whose Planner had just SUCCEEDED with blockers=none, confidence=high) escalated to Failed.
The tick line is the tell - it prints the policy: `[Conservative]` vs `[Permissive]`. Check it after any
manual relaunch. Correct command:
`.\scripts\Start-OrchestratorCommand.ps1 -Name "<label>" conduct --loop --watch --policy Permissive --max-duration 5400`

**VELOCITY BACKLOG - VERIFY BEFORE INTAKE, TWO OF THREE WERE ALREADY DONE (checked 2026-08-04).**
There is a filed, sequenced gate-throughput program: `4a50857f` shared base-build, `d87d26be` partition
pass-caching, `a9d5e81d` diff-scoped suite selection, `5daadb79` parallel gates, `3e1a0ad7` merge train,
`201b9b3c` candidate-keyed verdicts.
- `396fd25d` (reuse gate receipts across acceptance+landing) is **STALE**: `65e85ad6` landed with exactly ONE
  gate attempt directory. Auto-promote already consumes attempt results; only the operator CLI path re-ran,
  and that is not the path in use.
- `4a50857f` (shared base-build layer) is **ALREADY IMPLEMENTED**: gate logs emit
  `BASE_BUILD_CACHE base-build-cache main_sha=... build_phase_ms=... projects=Core=miss,Infrastructure=changed,...
  built_projects=... evictions=none`.
- `d87d26be` (partition-level pass caching for re-rolls) is **LIVE and is the one to take**: `b3ada0bb`
  gate 1 failed ONE shard, gate 2 re-ran all 19. Item estimates re-rolls ~20-25 min -> ~2-7 min.

**TEST TEMP LEAK: 3,298 directories** under
`C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\`, dating to Aug 1. This is the
concrete cost of backlog `51926605` (ACL-aware `GoalWorktrees.DeleteDirectory` replaced by
`Directory.Delete` + `catch { }`): cleanup failures are now invisible. Related but NOT root-caused: a
`d3829298` gate failed in 28s with `UnauthorizedAccessException` under that tree in
`AgentHarnessDocsDriftTests`. The directory is empty and writable now and the tree carries no mandatory
label, so the Low-IL theory does NOT hold - cause unconfirmed. The auto-retry Developer went straight to
`tests/Mcg.AgentOrchestrator.Core.Tests/AssemblyTempRedirect.cs`, which is the right seam.

**WORKFLOW DEADLOCK ON TWO-ROLE GOALS (filed `c0171e99`).** The Developer lane is restricted to
`Invoke-WorkerBuildCheck.ps1` (no testhost). Only the Tester role executes tests, and two-role goals
(Developer+Reviewer) have no Tester - so a Reviewer asking for executed evidence deadlocks the goal, since the
gate cannot run until the Reviewer passes. Cheapest fix is making `not-verifiable` non-actionable by contract
so a Developer is never dispatched to clear one. Related: `1e178ba0` - rule (l) controls cannot produce a
receipt when RED is a HANG; give such tests an explicit iteration/deadline bound so RED is a bound-exceeded
assertion.

**ACCEPTANCE PROCESSES ARE IN THE SPAWN REGISTRY** (`GoalAcceptanceVerifier.cs:6168`, owner `acceptance:{dir}`),
so a sweeping verb during a GATE kills the gate's own shard processes - not just workers. A worker-free window
is NOT sufficient; it must also be gate-free.

**TWO WORKERS DIED WITHOUT EXIT ARTIFACTS IN THE SAME MINUTE (01:19-01:20)** — `b3ada0bb` and `1332b2ea`,
both `blocked_by_stale_dispatch`, reason `no live process, exit-absent, heartbeat present but process
liveness is absent`. Systemic, not isolated. NOT diagnosed — correlation only: free RAM was 2.4GB under load
earlier and recovered to 3.4GB once both died, with the conductor alone at 883MB. Host is 15.9GB total.
That is consistent with memory pressure killing workers but I did NOT establish causation. **Relevant to the
"why not 3 build permits" question: if workers already die under load at 2 permits, that is evidence against
raising it — but measure per-gate peak RSS before concluding.**
Recovery that worked, in order: (1) `recover <goal> "<reason>"` resets the task; (2) then CHECK
`git status` in the worktree — `b3ada0bb` was left holding **239+60 lines of uncommitted work** in the exact
files it targets, which blocks ALL dispatch behind the generic `Assigned_tasks_exist_but_no_ready_batch`
hold; (3) COMMIT that work on the goal branch (`f6c56831`), do not discard it. A stranded edit discarded
earlier in this project turned out to BE the fix, so look before dropping.

**CORRECTION — "only a deliberate relaunch clears a pre-landing rebase escalation" is TOO STRONG.** I said
that four times tonight based on `809634e8` and `ef46e924`, both of which needed relaunches. But `09912b1f`
cleared and went to gate after only a MAX-DURATION HANDOFF (00:36), with no deliberate bounce. What differed:
I had fixed that branch's HISTORY with a real `git rebase main` (resolving the add at the offending commit),
not just its final tree. I cannot cleanly attribute the clear to the handoff vs. the history fix vs. a tick
re-evaluation — so do NOT bounce on the assumption it is required. Fix the branch history, verify with
`workspace rebase` reporting fast-forward, then WAIT a generation before spending a relaunch.

**WHICH ESCALATION CLASSES `recover` ACTUALLY FIXES** (learned by trial tonight — the runbook should say
this): it WORKS for `dispatch-exit-reconciled` (goal `f9bc03c9`) and for `advance-fault` (goal `09912b1f`,
slot-busy) — both reset the affected task to dispatchable with completed work intact, no worker round lost.
It is a NO-OP for **pre-landing rebase conflicts** (`809634e8`, `ef46e924`) — only a conductor relaunch
clears those, and `recover` prints "nothing to recover" while the goal stays set aside.

**`3fcf1286` — build-slot shortage ESCALATES the goal, third site of the same defect.** `advance threw:
Stable dotnet build slots busy` turned a routine two-permit contention into an operator-attention escalation
of a blameless goal (its Developer round had just completed cleanly). Log also shows `pid=5576` — the
CONDUCTOR ITSELF — holding `slot-1` while requesting a lease for the goal. The same exception class was made
non-fatal TWICE today (`809634e8` canary, `8e8afe96` round 4 evidence) and neither prevented this third
instance, because both were local patches. That is the argument for the written rule in one sentence.

**`d3829298` round 2 introduced a DETERMINISTIC HANG — caught by the Reviewer, not by me.**
`ConductorBatchLoop.cs:606-646` `continue`s at :645 without incrementing `totalTicks`, so the max-iterations
guard at :254-256 can never fire; `BatchLoopPersistingRebaseConflictStaysSetAside` hangs forever rather than
failing. Note the irony to carry into review: the goal exists to stop the loop stopping when it should not,
and the first cut made it never stop when it should. Both directions of one boundary.

**`f9bc03c9` PARK IS HOLDING** (`park-goal <id> <reason> --confirm-goal-park`; dry-run by default, and it
reported `running dispatches to cancel: 0` before confirming). No re-escalation since 23:06, where it had
been firing every generation. PROVISIONAL evidence against the old "park reverts to Active" note — watch it
across a full generation before trusting.

**`Goal_is_in_Failed_state` HAS AT LEAST TWO DISTINCT CAUSES — do not treat it as one defect.**
- `f9bc03c9` (five-role): Planner exited **0**, root and child both, with a well-formed WORKER_RESULT and
  `blockers: none`. Goal Failed ~10s later, nothing in the journal. STILL UNEXPLAINED — filed `3a7afb95`.
- `d3829298` (TWO-role): Developer exited **1** while reporting `tests: pass - build 0 errors; focused tests
  GREEN`, `blockers: none`. That is the exit-1-after-success class, and sol's audit gives it a candidate
  mechanism — `BackgroundDispatchRunner.cs:2390` reads invalid exit-file text as exit code 1 (in
  `f254171c`). Worker also hit the ~124s codex command cap on two whole-class test attempts (`b9d93945`).
So `3a7afb95`'s "appears specific to the five-role workflow" framing is only safe for the EXIT-0 case. Check
the exit code before assuming which defect you are looking at. `recover` fixed both, but note it reset the
DEVELOPER task on `d3829298` rather than advancing to Reviewer, so that round re-runs.

**`merge-tree` IS THE WRONG PRE-LANDING CHECK — THE CONDUCTOR REBASES.** `git merge-tree --write-tree
--name-only main HEAD` tests whether the FINAL TREES merge. A rebase replays EVERY COMMIT, so a branch that
ADDS a file in one commit and DELETES it in a later one still conflicts at the add, even though its end state
is clean. On `09912b1f` I ran merge-tree, got a clean tree, declared the conflict resolved — and the
conductor escalated on `pre-landing_rebase_conflict` minutes later. **Verify with an actual
`git rebase main` in the worktree**, not merge-tree. Resolution for that shape: `checkout --ours` (main's
copy) at the add, then `rebase --skip` the now-moot doc commits — verify afterwards with
`diff --stat main...HEAD` that the goal's real work survived, and with `workspace rebase <goal>` reporting
"can already fast-forward".

**ADD/ADD ON A SHARED DOC CANNOT BE FIXED BY EDITING EITHER SIDE.** Two branches that independently ADD the
same path have no common ancestor for it, so `git merge-tree --write-tree --name-only main HEAD` keeps
reporting `CONFLICT (add/add)` no matter how the content is reconciled. Goal `09912b1f`'s worker merged the
two versions into a better 130-line document and the conflict persisted unchanged. The ONLY resolution is to
delete one side. Resolved mechanically at `07b9b39d` (branch deletes, main is canonical); verified with
`merge-tree` returning a clean tree, and `diff --stat main...HEAD` confirming the goal's own 10 files
survived. **Salvage before deleting** — that worker's version contained two things the canonical doc lacked
(persisted state vs read-time availability must not overwrite each other; retries must not turn exhaustion
into success). Preserved to `scratchpad/branch-discipline-doc.md` and filed as `6f400169`.

**AUTO-RETRY OUTRUNS OPERATOR GUIDANCE — plan around it.** The gap between `WATCH_TRANSITION` and the next
dispatch is ~10s. Reading a Reviewer finding, deciding, writing a considered retry and submitting it takes
longer, so the retry is REJECTED (`Task is already running` / `downstream task has a running process`) and
the round proceeds on the Reviewer's findings instead. Both guidance retries I attempted tonight lost that
race. Implications: (1) operator retries only land if you CANCEL the running task first, or catch the goal
while ESCALATED; (2) for everything else, rely on Reviewer findings — they independently reached the same
conclusion as my rejected retries in both cases; (3) reserve operator retries for when the Reviewer is WRONG,
or the decision is genuinely operator-owned (scope, premise, policy), and expect to cancel first.

**OPERATOR TRAP — "Operator intent queued" does NOT mean it will apply.** A `retry` on an upstream task is
REJECTED if a downstream task still has a running process
(`Cannot retry Developer task ... while downstream Reviewer task ... has a running process`). The submit
output still prints `status=Pending`, and the rejection is visible ONLY via
`operator-intent-status <id>`. I fired a retry mid-Reviewer-run, saw "queued", and it sat Rejected for 40
minutes while I reported the goal as "retry pending". **Poll the id the command hands you**, or submit only
when no downstream task is running.

**Operator decisions made on `09912b1f` (record these, they are policy):** unreadable circuit FAILS CLOSED
after a tightly-bounded retry, with an operator-visible item. Rationale is asymmetry — fail-open risks
unverified code landing past a verifier we have DETECTED is lying; fail-closed risks a cheap reversible
pause. Two constraints: the halt must be operator-visible (this circuit halted all landings for 29 min
unnoticed), and the retry budget must stay well under a second because `Read()` is a synchronous block and
`8e8afe96` landed tonight to take a blocking call OFF the tick thread. Also stated: a fail-closed policy that
reports `Unhealthy` for UNKNOWN state is the same defect sign-flipped — `Unavailable` must be its own value
with policy applied on top. Rejected: stale-cache fallback (a cache is most wrong exactly when it matters).

**Filed tonight, all from measured evidence:**
| id | defect | status |
|---|---|---|
| `aca3d2fc` | shared-doc collision | LANDED as `dd3b44fe` |
| `cfe34c0e` | canary slot-busy halts all landings | LANDED as `809634e8` |
| `1732dc79` | pre-review evidence blocks the tick | LANDED as `8e8afe96` |
| `7ab385bc` | 2 remaining synchronous evidence call sites | open |
| `bcbf92fc` | Developer cannot execute tests → `tests: deferred` everywhere | goal `1332b2ea` |
| `18ccc733` | CS2012 baseline build recorded as a verdict | goal `f9bc03c9` (blocked) |
| `f490241d` | conductor continuity unmeasurable, self-stops = 41% of dead time | open, HELD for `ef46e924` |
| `4fd42301` | canary test pins exact main HEAD | goal `1046bed1` |
| `3a7afb95` | five-role goal → Failed on clean Planner exit | open |

**`3a7afb95` has an operator trap worth knowing before you hit it:** `recover` REPORTS SUCCESS on that goal
("reset task 2 to dispatchable") and the goal fails again on the next Planner round. Following the runbook
loops, and each cycle burns a full paid Planner round. Two cycles confirmed. Park rather than recover until
the defect lands.

**DESIGN RULE WRITTEN AND ALREADY PAYING — `docs/dispositive-decision-discipline.md`** (pointers +
shared-anchor entries in BOTH `AGENTS.md` and `CLAUDE.md`). The rule: *a dispositive decision must be made in
the presence of the evidence that discriminates its alternatives, and must record that evidence.* The
enforceable check: **could I write the justification for this outcome from what is in scope right here?**
Two independent audits (sol + Fable, near-disjoint results) found **13 MORE violations** within an hour:
| id | finding | confidence |
|---|---|---|
| `e8b5cd1b` | **SAFETY: acceptance circuit FAILS OPEN** — unreadable state returns `Healthy`, a tripped circuit permits landings | verified by direct read |
| `893f8be9` | outcome-class token drift poisons the routing scorecard DURABLY (`provider-Sandbox1312` interpolated vs `provider-sandbox-1312` literal) | verified by direct read |
| `f0b431d9` | candidate mechanism for `438b18d3` stop-verbs-don't-stick | convergent, UNTRACED |
| `f254171c` | six remaining, incl. invalid exit-file → exit 1 (candidate mechanism for "dispatch manufactures failures") | mixed |
Fable's overall read: **the runtime conductor path is now well-hardened**; surviving violations cluster in
the reporting layer that re-derives cause from strings AFTER typed evidence already existed. `f254171c`
carries four REFERENCE PATTERNS where the rule is already applied correctly — use those as fix templates.

**MY WORST PATTERN TONIGHT — file paths in backlog bodies, and direct commits to main mid-flight.**
Three goals derailed by the same two habits, all within ~90 minutes, after I had already written the lesson
down once:
| goal | damage | cause |
|---|---|---|
| `d3829298` | worker added 226 lines to a THROWAWAY analysis script instead of touching the conductor | I named the script path in the backlog body |
| `09912b1f` | worker authored a competing copy of the discipline doc → `CONFLICT (add/add)`, branch could not land | I named the doc path in the backlog body, then committed my own to main |
| `c4d02669` | gate failed on a red main + worker made an off-topic drift-test change | I committed an anchor-list edit to main WITHOUT grepping what asserts it |
**Rules for me:** (1) describe provenance WITHOUT paths — "the rule this came from", never the filename;
(2) do NOT commit a file to main while any in-flight goal lists that path in scope; (3) before editing any
contract file (`AGENTS.md`, `CLAUDE.md`, shared-anchor lists), grep for what asserts it and RUN that test
before committing — `AgentHarnessDocsDriftTests` pins the anchor list to in-file `## Section` headings, and
shared-home docs like `test-design-discipline.md` are referenced by POINTER ONLY, never listed as anchors.
Containment was luck: only one lane happened to gate during the 20-minute red-main window while three sat in
worker rounds.

**OPERATOR LESSON, my error: do NOT name incidental tooling paths in a backlog body.** `f490241d`'s body
named `scripts/Extract-LoopUptime.ps1` while explaining why current data is untrustworthy. Scope inference
latched onto the filename, and goal `d3829298`'s Developer spent a full round adding 226 lines to that
THROWAWAY ANALYSIS SCRIPT instead of touching the conductor. Scope inference cannot distinguish "this is the
broken thing" from "this is how I noticed". Retry `2dce9999` sent with corrected scope + explicit do-not-touch
list for all four scratch scripts (`Extract-LoopUptime`, `Extract-GoalVelocity`, `Extract-GoalFactsV2`,
`Analyze-GoalVelocity` — none are product code).

**Recurring architectural flaw, now 5 instances in one day — the boundary between "the measuring apparatus
failed" and "the candidate failed" is drawn independently at each site and got it wrong every time: canary
slot-busy (verdict←apparatus), `8e8afe96` `-001` terminal outcomes (verdict←apparatus), CS2012 baseline
(verdict←apparatus), `EmptyReceipt`→`EnvironmentFault` (apparatus←verdict, the reverse), and the goal→Failed
transition in `3a7afb95` leaving no typed record. This is a design rule waiting to be written, not five bugs.

## VELOCITY ANALYSIS 2026-08-03 (me + sol + Fable, two rounds each, 756 goals / 7 weeks)

- **COUNTING RULE — `git log --grep="Integrate goal/" | count` IS NOT A LANDING COUNT.** 408 Integrate
  commits but only **372 distinct goals** (`| sort -u`). 36 are re-integrations: `a2f929ec` ×5, `a645091f`
  ×4, three more ×3. Inflation is ~9% overall and up to **33% on peak days** (Jun 25: 21 commits ≈ 14 goals).
  I quoted 21/day as landings AND defended it when challenged; both wrong. Any throughput metric built on raw
  commit counts is inflated, and early-era-vs-later comparisons are unsafe because re-integrations may
  cluster early (one `a2f929ec` message says "hand-landed past the acceptance fingerprint guard").
- **#1 LEVER, ranked first by BOTH independent analyses: conductor continuity.** Dead-time attribution over
  371 merged gaps — `all-done-or-escalated` self-stops precede **41.2% of all dead time** (14,369 min, 90
  gaps), `stop-file` 27.5%, one-shot/stub rows 23.7%, `max-duration` handoff only 7.3% (median gap 16.8 min
  but p90 195 min). The Jul 22–Aug 1 collapse contains three mega-gaps (3,971 / 5,073 / 2,609 min) each
  opened by such a stop. Steady-coverage eras ran 11.9–12.5 landings/day vs 3.3–4.8 when coverage collapsed.
  Filed as backlog **`f490241d`**; goal `ef46e924` targets one instance.
- **The collapse was a FLOW collapse, not slower processing.** Goals that landed in Jul 24–Aug 1 were the
  FASTEST of any July era (median wall 161 min vs 229 at peak). Intake fell ~14–23/day → 6.1/day and dispatch
  starts 13.1 → 5.7. Round count doubling in that era was an EFFECT (goals spanning many generations), not a
  cause. **Check uptime BEFORE concluding the pipeline got slower.**
- **Rejected by both analyses: synchronous evidence runs are NOT the aggregate throughput driver.** They
  barely existed before ~Jul 12 yet latency had already doubled; collapse-era tax ≈7% of tick capacity; and
  the era with the MOST evidence activity (Aug 2–3) had the BEST throughput. What survives is tail risk —
  one goal (`e7fd7951`) ran **159 evidence runs / 1,184 min** and never landed, with no per-goal cap. So
  `8e8afe96` is worth landing for board-wide blocking, but I oversold it as the top lever.
- Gate first-pass by era: **67.4% → 50.9% → 56.0%** (E3 → collapse → Aug 2–3). My earlier "~40%" pooled
  across the collapse and was wrong.
- Pre-dispatch queue wait is **negligible** — median 0.1–6.7 min, p90 ≤21 min, <3% of wall, stable across
  eras. That question is settled; stop investigating it.
- Partition re-execution: **3,312 executions, 15 reuses (0.5%), 0 forced reruns.** Median = p90 = **19
  partition runs per gate attempt**; ~89% of partition work re-verifies what already passed; upper bound on
  cross-attempt green re-runs ≈ **1,468 runs (44%)**. Cannot be priced in minutes — no partition durations.
- Build lock/slot blocking genuinely vanishes after ~Jul 20, **but the throughput effect was invisible** —
  the heaviest-blocked era was the throughput PEAK, so those were grind-holds, not stoppers.
- **MY EXTRACTION SCRIPTS HAVE KNOWN DEFECTS — do not reuse without fixing.**
  `scripts/Extract-LoopUptime.ps1`: includes one-shot/stub logs (51% of rows; filter `loopStarted=True`),
  and 45 rows carry a −5h offset (filename parsed local, mtime taken local). Recorded uptime is a LOWER
  bound. Proper source is `LOOP_START`/`LOOP_STOP` in `conduct-events.log`, but **that log rotates and
  retains <1 day** — which is why the analysis needed log archaeology at all.
  `scripts/Extract-GoalVelocity.ps1` / `Extract-GoalFactsV2.ps1`: landing match drops goals with no journal
  file, and v2 aggregates away per-event timestamps that were needed to date the lock/slot cessation.

- **`1732dc79` IS THE TOP DEFECT ON THE BOARD — reproduced live tonight, root-caused to one line.**
  `ConductorDriver.cs:480` blocks the tick thread on an already-async method:
  `RunFocusedEvidenceAsync(...).GetAwaiter().GetResult()`. It is UNCONDITIONAL — no config switch — so it
  cannot be mitigated operationally. Receipt in `goal-operations/badac7c6...jsonl`: focused reviewer evidence
  ran **17:55:45 → 18:27:03 = 31m18s synchronously inside the tick**. Corroborated independently by
  `conduct-events.log` holding **ZERO events of any kind** across 17:55:23 → 18:27:04, every goal resuming in
  the same millisecond. Collateral: `20912699` PASSED its gate at 18:08 and could not be reaped or landed
  until 18:27 — a finished, landable goal held 19 minutes behind an UNRELATED goal's handoff. It runs BEFORE
  Reviewer dispatch, so every Developer→Reviewer handoff pays it.
  **Diagnostic lesson:** per-goal events legitimately go quiet while a worker runs, so silence there is NOT a
  stall signal — I misread it as healthy. The signal that distinguishes a running worker from a frozen loop is
  the tick-emitted `eventKind:"acceptance"` poll; when those stop, the tick is blocked.
- **Loop handed off at 18:34** (`LOOP_STOP tick=104 reason=max-duration`), successor pid **38560** alive and
  ticking, `424f2d45`'s in-flight gate survived and kept completing shards. Handoff spawns the PREBUILT exe,
  so `20912699`'s landed code is NOT armed in this generation — and neither will `1732dc79` be when it lands.
  Bounce deliberately via the launcher after landing it.
- **INTAKED as goal `8e8afe96`** (2026-08-03 18:54, Developer+Reviewer, Complex). This is the `1732dc79`
  fix. Scope-collision advisory: 0 explicit conflicts across 24 compared goals.
- **`424f2d45` LANDED 18:46 as `1c474a25`** — 24 landings. Then the loop self-stopped
  `LOOP_STOP tick=27 reason=all-done-or-escalated`, because BOTH remaining goals escalated at once with
  `pre-landing_rebase_conflict (docs/test-design-discipline.md)`. NOTE: contrary to the old rule, this
  all-done stop left **no stale lock** — `conduct-loop.lock` was already gone and pid 38560 was dead.
- **I hand-resolved both conflicts (2026-08-03 ~18:50).** `badac7c6` → `8d105de7`, `809634e8` → `de3cbfe3`.
  Both were PURELY POSITIONAL: each goal appends an independent "Negative-control record for goal X"
  paragraph at the SAME anchor (right after `fa009044`'s, before "Motivating incidents:"), and main had just
  gained `424f2d45`'s. Resolution was a union in landing order — zero judgment content. Verified after each
  rebase that `diff --stat main...HEAD` shows ONLY that goal's own files (badac7c6 = 8 files, 809634e8 = 13),
  so neither branch absorbed foreign work. **Both branches changed, so both need a re-gate.**
  Filed the systemic cause as backlog **`aca3d2fc`** — the shared doc both serializes parallel acceptance
  (only-shared-path overlap) AND escalates landings (same-anchor insert). Fix has two halves: exclude
  `*.md`/`docs/**` from the overlap/reservation computation, and stop making one shared doc the write target
  for per-goal records.
- **Loop relaunched 18:54 via the launcher (REBUILDS): pid 38288**, name `conduct-loop-tickfix-lane`. This
  generation therefore ARMS `20912699` + `424f2d45`. Board on relaunch: `badac7c6` + `809634e8` rebased and
  awaiting re-gate, `8e8afe96` fresh.
- Superseded: backlog `1732dc79` (pre-review evidence blocks the tick) is now goal `8e8afe96`.
- **`badac7c6` LANDED 19:11 as `fb9fb42d` — 25 landings.** Its re-gate PASSED at tick 62, so my hand-resolved
  union rebase (`8d105de7`) is validated by executed tests. Post-landing sweep terminalized it cleanly.
- **`809634e8` escalated a SECOND time on the same doc**, exactly as predicted: `badac7c6` landing put another
  paragraph at the same anchor. Re-resolved by union rebase → `dfc73043`, still only its own 13 files.
  `workspace rebase 809634e8` now reports "can already fast-forward into main; no rebase needed".
  **BUT the escalation state persists independently of git state** — the goal reads `Status: Verifying` with
  both tasks Completed, yet the SWEEP phase still reports `set_aside=1` and prewalk `eligible=1`, so the loop
  will not re-gate it on its own. `recover` is a NO-OP here ("nothing to recover"). `attention show` lists no
  open item. The conductor's own guidance is `use 'workspace rebase' to resolve` then `Next: acceptance
  809634e8`. **Deliberately NOT running manual `acceptance` while `8e8afe96`'s Reviewer is about to hand off
  to a gate** — a slot-leased command overlapping a gate has wiped lane receipts before. Options when the
  board is quiet: run `acceptance 809634e8`, or graceful-stop + relaunch (a relaunch cleared exactly this
  escalation class earlier tonight at 18:54).
- **`8e8afe96` round 1 REJECTED by the Reviewer — and the Reviewer was right.** This is the depth we want:
  it found two blocking defects I did not, by tracing the new dispatch kind through parent/child
  reconciliation rather than reading the diff. Retry queued as intent `cd6817c4` (task 1).
  - **`-001` (ConductorDriver.cs ~2289-2328), blocking:** terminal-without-run outcomes (BlockedBuildSlot /
    BlockedBuildLock / ProcessDied / CorruptArtifacts / LaunchFailed / Cancelled / StaleCandidate) are
    synthesized as `FocusedEvidenceRunResult(Accepted:false)`, and the `!Accepted` branch consumes that as a
    REJECTED EVIDENCE REQUEST → Tester reopen or `PRE_REVIEW_MAPPING_NEEDS_INPUT` escalation. The acceptance
    path it copied treats the identical set as RETRYABLE (`ConductorBatchLoop.cs:2090-2095, 2447-2453`).
    Permit acquisition uses `TimeSpan.Zero` (`ConductorParallelAcceptanceAttempts.cs:873-889`), so
    BlockedBuildSlot is ROUTINE on the 2-permit board, and a restart-killed child yields ProcessDied on the
    same path — **it would escalate goals under exactly the conditions the goal exists to fix.**
  - **`-002` (ConductorParallelAcceptanceAttempts.cs ~664-699), blocking:** the child coordinator always gets
    `tryRunPreSlot: RunParallelLandingAcceptancePreSlot`, so a pre-review child runs the LANDING-ONLY
    already-merged short-circuit, journals a FALSE `Acceptance skipped:skip-already-merged`, and returns
    `Early(Done(Verified))` with no evidence. Gate on `Kind == GateDispatchKind`.
  - **`-003` is OPERATOR-OWNED AND MINE.** Criterion 4's rule (l) negative control has no execution receipt
    (Developer honestly reported `tests: deferred`). I must restore the synchronous
    `.GetAwaiter().GetResult()`, run `FullyQualifiedName~ConductorDriverTests`, capture actual RED, revert.
    **Do this only AFTER the -001/-002 fix lands** — running it now measures code about to change. I told the
    worker to state in WORKER_RESULT exactly which edit produces RED for each new test so I need not guess.
  - Scope calls I made: `-006` (missing terminal-lane coverage, lease path stubbed null) IN; `-005` (evidence
    coordinator outside the 2-slot admission budget while competing for the same permits) IN **only if it is
    wiring, not restructuring** — report rather than force; `-004` OUT, already backlog `7ab385bc`.
- **Loop relaunched 19:20, pid 28656**, name `conduct-loop-async-fix-round2`. Again NO stale lock after an
  all-done stop — that is twice now, so treat the old "always leaves one behind" rule as obsolete.
  The relaunch ALSO cleared `809634e8`'s stuck escalation (it re-gated at 19:21:40 without manual
  `acceptance`), and applied the queued retry intent. **A relaunch is the reliable clear for a stale
  landing-escalation; `recover` is not.**
  Caution when reading this generation: **redirected stdout is BLOCK-BUFFERED**, so
  `operator-conduct-loop-*.out.log` lags reality by minutes. I misread that lag as an 18-minute startup hang.
  Confirm liveness by the process's CPU climbing, not by the log's mtime.
- **`8e8afe96` round 2 (`96ac38fc`, 3 files) fixes BOTH blockers — verified by reading, not by claim:**
  `-002` gates the pre-slot on `attempt.Kind == GateDispatchKind`. `-001` routes
  `ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun` to `Held … retry on next conduct tick`,
  and `IsTerminalWithoutRunOutcome` (`ConductorParallelAcceptanceAttempts.cs:1700-1707`) covers EXACTLY the
  seven outcomes the Reviewer named. +157 test lines cover `-006`.
  **`-005` was neither implemented nor mentioned in WORKER_RESULT** — I asked for "do it if wiring, report if
  restructuring" and got neither. Re-raise it if the Reviewer does not.
- **Round 2 was NOT complete — `-001` has a residual, and my "genuinely fixed" verdict was overconfident.**
  I verified `IsTerminalWithoutRunOutcome` covers the seven named outcomes (it does) but never checked the
  OTHER entry path. `Failed` is deliberately absent from that predicate, and `FromArtifact`
  (`ConductorParallelAcceptanceAttempts.cs:1631`) maps a fault to a run with `FocusedEvidence == null`, so an
  infrastructure exception in the evidence child arrives as a **Completed** decision, hits
  `ConductorDriver.cs:2304-2311`, and is synthesized as `Accepted:false` → `MappingNeedsInput` + Tester
  reopen. Same defect class, different door. Remedy (Reviewer's): treat Completed-with-null-FocusedEvidence
  as retryable — `MarkReconciled` + `Held("…retry on next conduct tick…")` matching the 2289-2301 branch —
  plus a test driving a Fault evidence attempt asserting Held + null receipt, whose RED comes from removing
  that new guard. Auto-retry carried it to **Developer round 3**; no operator relay needed.
  **Lesson for verifying this class of fix: checking that a predicate's SET is complete is not the same as
  checking every path that reaches the consuming branch.**
- **`809634e8` gate failed 489/491 on its OWN new tests** (`Post-landing canary sink failure ...`, both
  `breakSqliteStore` cases, `exitCode 2`). Not from my rebase (that touched 2 doc lines). Auto-retry fixed it
  in 2m4s (`b4dae627`) by INVERTING an assertion: `Unhealthy` → `Healthy`.
  **I checked this rather than trusting it, because an inverted assertion is normally a stop-and-ask signal.
  It is legitimate.** The goal exists to stop a transient slot shortage tripping the circuit, so
  "canary failure stays non-blocking" IS the intended behavior. The safety net is preserved: the enum gained
  a distinct `Deferred` kind (`PostLandingCanaryState.cs:78-88`) for the slot-busy path, while the health
  read still returns `Unhealthy` for ANY `Failed` receipt (`PostLandingCanaryState.cs:472-486`). Slot-busy is
  non-fatal; a real canary failure still fails closed. Matches sol's recommendation from earlier tonight.
- **WATCH: both in-flight goals now modify `tests/…/ConductorDriverTests.cs` from the same base
  (`e7c3e60b`)** — `8e8afe96` appends tests near line 3141, `809634e8` edits ~1868. Far apart so it will
  probably auto-merge, but this is the first genuine CODE overlap tonight (all prior ones were the shared
  doc). If the second landing conflicts, resolve as a union the same way — the regions are independent.
- **`8e8afe96` gate FAILED 20:22 on INFRASTRUCTURE, not code.** Check `structural test coverage:
  infrastructure tests`, exitCode 1, `resultSummary: "trusted main baseline build failed"`, cause
  `CSC : error CS2012 ... file may be locked by 'csc' (32188)` in the isolated
  `main-coverage-baseline` artifacts dir. The goal's code was never evaluated. The conductor still recorded
  it as a failed verdict and auto-dispatched a paid Developer round. **Filed as backlog `18ccc733`** —
  classify baseline-build file-lock/IO faults as retryable infrastructure, same shape as the canary fix in
  `809634e8`. Recurring principle: an exception in the MEASUREMENT APPARATUS must never be recorded as a
  MEASUREMENT of the candidate. That is now three instances tonight (canary slot-busy, `8e8afe96` `-001`,
  this).
  **This also INVALIDATES my earlier flakiness estimate.** I concluded "gate failures are real defects, not
  flaky" from finding only 2 RED→GREEN `partitionVerdict` flips. A baseline BUILD failure never emits a
  partition verdict, so that whole class was invisible to the check. The ~40% first-pass rate is depressed by
  an unknown number of infrastructure failures — recount before trusting it.
- **`8e8afe96` round 4 (`cdff3ea8`) is legitimate, not phantom-chasing.** It correctly did NOT try to fix the
  CS2012 (gate infrastructure, not its code). It extended the retryable branch to catch
  `DotnetBuildSlotsBusyException`/`BuildLockBlockedException`/`OperationCanceledException` from
  `attemptDecision.Run?.Exception`, and split build-environment attempt naming into `pre-review-evidence-*`
  vs `parallel-acceptance-*` so lease telemetry attributes evidence children correctly. Its only blocker is
  the operator-owned rule (l) receipt. **Developer round 4 — the retry cap is CUMULATIVE, so the next
  Reviewer round must come back clean.**
- **OPERATOR-OWNED RULE (l) NEGATIVE CONTROL FOR `8e8afe96` — STILL OWED, deferred 5× on slot contention.**
  The Reviewer DOWNGRADED it at round 4 (`state:open, severity:advisory, category:test-evidence`) and passed
  the goal, so it can land with the control never having run. The obligation stands regardless: rule (l) says
  a test earns authority only by being shown capable of failing, and these tests' power is unproven.
  Hypothesis worth watching, NOT a claim: round 4 sat at the cumulative retry cap, where any blocker
  escalates instantly — that creates structural pressure to downgrade rather than block.
  **Run against MAIN, not the goal worktree** — the worktree is cleaned on landing, and main is where the
  tests will live. For each: apply the edit, run `FullyQualifiedName~ConductorDriverTests` through the slot
  runner, capture the ACTUAL red text, then REVERT.
  1. Restore unconditional pre-slot invocation (undo the `attempt.Kind == GateDispatchKind` gate in
     `ConductorParallelAcceptanceAttempts.cs` ~744) → `PreReviewAttempt_FocusedKind_SkipsLandingPreSlot`
     must fail its 0/1 invocation assertions.
  2. Remove the terminal-without-run hold (`ConductorDriver.cs` ~2287) →
     `PreReviewEvidence_ProcessDiesAfterRestart_RelaunchesWithoutReceipt` must fail its Held/null-receipt
     assertions.
  3. Restore the synchronous `.GetAwaiter().GetResult()` pre-review call →
     `PreReviewEvidence_InFlightRun_ReturnsThenReconcilesAfterRestart` must fail its first-walk
     Held/zero-run assertions.
  4. (added round 4) Remove the `DotnetBuildSlotsBusyException or BuildLockBlockedException or
     OperationCanceledException` arm from that same branch → its round-4 test must fail.
  **If ANY fails to go RED that is a defect in the test, not a formality — file it immediately** rather than
  letting the goal's evidence stand.
  **Do NOT run while any gate holds a build slot** — a slot-leased command mid-gate wipes lane receipts.
  **The deferral pattern IS the argument in backlog `bcbf92fc`:** an evidence obligation only an operator can
  discharge, in windows a busy board rarely provides, will keep slipping.
- **Filed backlog `7ab385bc`:** two focused-evidence call sites remain SYNCHRONOUS on the tick thread after
  `8e8afe96` — `ConductorDriver.cs` ~1197 (reviewer-issued evidence-on-demand) and ~1541 (conductor-derived
  substitution), both passing `CancellationToken.None`. `8e8afe96` correctly fixes only the pre-review path,
  which is the path tonight's 31-minute freeze actually came through (verified: the run sat between Developer
  completion and Reviewer dispatch). Do these AFTER `8e8afe96` lands so there is one mechanism.
  **Attribution trap:** "reviewer mapped project evidence" is a check name generated inside
  `RunFocusedEvidenceAsync` (`GoalAcceptanceVerifier.cs:1104/1140/2124`) and appears for EVERY caller — use
  the surrounding transition sequence to attribute a freeze, not that string. Brief written to `scratchpad/brief-1732dc79.txt` — grounded in the 478930/162961/123499 ms
  measurement, copies the acceptance gate's existing async shape rather than inventing a second one, carries
  the "do NOT build event-driven handoffs (66ms, measured)" negative result, and splits the perf measurement
  into an OPERATOR-OWNED post-landing criterion because the feasibility pass makes live-conductor/wall-clock
  observation infeasible for a worker role. Fire it with `goal --brief-file` once a slot frees.
  **Do not intake while a gate holds a build permit** — heavy write racing an active gate crashes the loop.
  All four fix defects MEASURED tonight; three of them cost operator time in the last four hours.
- **`20912699` carries a HAND-RESOLVED REBASE.** I resolved its pre-landing conflict manually rather than
  delegating: `ProgressKind` union (`PreReviewMappingEscalationSuppressed=28` from main, `OperatorTaskNote=29`,
  `OperatorGateSatisfied=30`) plus a `docs/test-design-discipline.md` section merge. Its re-gate then failed on
  three `Cli_note_*` tests. **Cause established (a): the tests were stale, not the merge.** `CliCommandHandlers`
  `.Tasks.cs:111,458` call `RecordOperatorTaskNote`, which writes the new kind, so tests asserting `TaskNote`
  could not pass. Fixed in `991a80ec` (4m20s). The split is deliberate and consistent: `TaskNote` = kernel/system
  note (still written from 7 sites in `Recording.cs`), `OperatorTaskNote` = operator-authored. "Any note"
  consumers (`TaskBriefs.cs:1237`, `TaskOutcomeClassification.cs:84`, `PromptContextFormatter.cs:257`) match
  BOTH; only the gate-source resolvers narrow to the operator kind — which is the point of the goal.
- **Correction to my earlier warning: `ProgressKind` ORDINALS ARE FREE.** I told the worker (and wrote here) to
  treat the numbers as persisted. They are not. `SqliteOrchestratorStateRepository.cs:2058-2061` registers a
  `JsonStringEnumConverter`, so kinds round-trip **by name**; the stores that do serialize enums numerically
  (`OperatorIntentStore`, `ProgressiveReviewSteeringStore`, `CollaborationItemStore`, `PortfolioStore`) carry no
  `ProgressKind` at all. The real constraint is on the NAME. Renumbering is safe — do not avoid a correct
  renumber on my say-so.
- **`10bd7223` is the standout.** Its Developer CONFIRMED the console-flash hypothesis rather than fabricating:
  it found `GetConsoleWindow()` insufficient, added `GetConsoleProcessList` and proved **attachment** is the
  causal state (unattached incumbent → successor allocated a visible window at +98ms; attached incumbents → no
  window). Post-fix: 3 handoffs, 0 windows, `CREATE_NEW_CONSOLE` positive control, visual capture, RED controls
  with exact compiler errors, 187/187. Flags pinned `0x01080600`, both forbidden flags absent, guards untouched.
- **`f00622a9` was wedged 2.5h and nothing surfaced it.** A progressive-review glance returned
  FundamentalMisdirection at 00:29:55 because the diff went outside `docs/test-design-discipline.md`, which it
  called "the authoritative trusted scope" — that is the CANNED refiner field (identical Includes list appears
  on `10bd7223` and on freshly filed backlog items; the brief itself says `Scope confidence: unknown`). The
  worker was killed after 336 CPU-seconds with 612 insertions left uncommitted, and every tick since held on
  "Reviewer blocked: predecessor is Cancelled, not Completed" — no requeue path exists for a glance cancel.
  Recovered by committing the stranded work as `f68f7444` and submitting retry intent `07d183d3`. Filed
  `ce36eff9`. **If a goal is quiet, check for this shape: Cancelled predecessor + Assigned dependent.**
- **`e7fd7951` is PARKED** pending `f00622a9`. Its work is committed (12 test files, zero production, plus
  `cb80328d`), scope adjudicated test-only, but every dispatch minted a NEW human-input request so answering
  could not clear it. Re-intake after `f00622a9` lands.
- **`b3c2a121` was ABANDONED TWICE** — premise disproven mid-flight, criteria unamendable. The first abandon
  (01:03) was UNDONE at 01:45 when a fresh loop generation replayed the identical 00:58 Reviewer escalation at
  **tick 1**. Expect the second abandon (02:56) to be undone the same way at the next handoff. Filed `c1fbf766`.
  This is NOT `438b18d3` — that goal has no open task, so its documented "cancel the tasks first" workaround
  does not apply.

## Optional follow-up: one stranded edit preserved, deliberately NOT landed

`fb13475c` reached Verified then failed its pre-landing rebase on a dirty worktree — one uncommitted file,
`ProgressiveReviewSteeringTests.cs`. The edit was REAL, not junk: it wires `FakeCollaborationItemStore` into a
test and improves an assertion to dump `result.ProgressLines` on failure.

**I discarded it from the worktree on purpose.** The gate that passed and the Reviewer that approved both ran on
the COMMITTED branch head — this edit was in neither. Committing it would have landed ungated, unreviewed code
and broken the evidence chain between the gate verdict and what reaches main.

Saved as a patch before discarding, at
`…/scratchpad/fb13475c-stranded-edit.patch` (1619 bytes, session-scoped so it will not survive indefinitely).
Re-apply as a small follow-up if wanted — it is an improvement, not a fix, and the committed state gates green
without it.

**The general rule this establishes:** when a stranded edit blocks a pre-landing rebase, the question is not
"is this edit good" but "was it gated". Preserve it, discard it, land what was actually verified. That differs
from the `f00622a9` case earlier tonight, where the stranded work was the ONLY copy of a killed worker's output
and nothing had gated yet — there, committing was correct.

**BUT I GOT THE SECOND HALF WRONG, and the gate proved it at 09:19.** I called that edit "an improvement, not a
fix". It was the fix. The gate failed on exactly that test —
`ProgressiveReviewSteering_requeues_and_preserves_goal_worktree_when_restart_preparation_throws`,
`ProgressiveReviewSteeringTests.cs:932`, `Assert.True(result.MutatedTaskState)` False. The edit passed a
`FakeCollaborationItemStore` into `NewCoordinator`, whose `attentionStore` parameter is **optional and defaults
to null** (`:945`), so removing it compiled silently while the restart-failure path — which raises attention —
stopped completing.

**The discard was still correct; the CHARACTERIZATION was not.** Landing ungated code would have been worse.
What I should have done in the same breath was hand the Developer the edit's exact content instead of filing it
as an optional follow-up. Done now, at 09:21, with the diff inline and an instruction to verify the mechanism
rather than trust me.

**Rule to carry:** an optional parameter that defaults to null makes a deletion invisible to the compiler. When
a stranded edit only *adds arguments*, assume it is load-bearing until a test proves otherwise — and route it
to the worker immediately rather than parking it as a nicety.

## `fb13475c` is Verified-but-set-aside — waiting on a handoff, NOT broken

It passed review and gate, then failed its pre-landing rebase at 07:16 on a dirty worktree. I cleared the dirty
file at 07:17 (see the stranded-edit section) — but the goal then sat with **zero events for 45+ minutes**.

That is not a stall and not a new defect. Escalation SETS THE GOAL ASIDE for the remainder of that loop run
(`ConductorBatchLoop.cs:872`, per sol's trace), so the repaired worktree is never re-tried within the same
generation. `goal-recovery` confirms **"Findings: none"** — the goal is healthy. A new loop generation
re-includes it, so the natural `--max-duration` handoff clears it.

**Operator lesson:** repairing a goal worktree does NOT re-arm the goal. The repair only takes effect when a
later generation re-walks it. Don't bounce the loop to force this while workers are in flight; wait for the
handoff.

Also found: `goal-recovery fb13475c` reports its build lease ORPHANED (`ownerPid=12720 ownerAlive=False
canCleanup=True`). **`build-lease-cleanup` is GLOBAL with no goal-prefix parameter**, and it aborts on the first
lease it cannot delete — here `goal-80f4bd56`, whose artifacts are file-locked by a live pre-review evidence run
that continued after the goal was parked. So one locked lease blocks cleanup for every other goal. Retry once
that evidence run finishes.

## BLOCKED DISPATCH IS INVISIBLE — the highest-value diagnostic learned tonight

**`status` shows `[Assigned]` for a task that is BLOCKED and cannot dispatch.** Nothing distinguishes
"about to run" from "stuck and going nowhere". Goal `5694679a` sat blocked for ~2 HOURS while reading
`1. [Assigned] Developer:` — and I reported it as making progress in five separate status updates.
Meanwhile **726 insertions of finished worker output sat uncommitted in its worktree, the only copy.**

**THE DIAGNOSTIC — maps a janitorial exception to its owning goal in seconds:**

    grep -rl "<task-id>" .orchestrator/goal-operations/

The journal line then carries the blocking reason verbatim:

    <task-id> provider=codex-cli reason=dirty-worktree: blocked: worktree has 14 uncommitted change(s)
    before dispatch

First instance cost me two hours. Second cost ninety seconds.

**THE SYMPTOM TO WATCH:** `LOOP_JANITORIAL_FAILED ... InvalidOperationException: Task '<id>' has no dispatch to
execute`, repeating every tick. That is blocked dispatch, NOT a corrupt or dangling record. It fired every ~13
seconds for two hours across two loop generations and produced no escalation and no goal-level signal.

**THE FIX, every time:** commit the stranded work on the goal branch (`git add -A` + commit), which cleans the
tree and lets dispatch form on the next tick. Verified on `5694679a` (`00b5096c`) and `ce8597ad` — the exception
stopped within a minute of each.

**This has now been needed FOUR times tonight** — `f00622a9`, `fb13475c`, `5694679a`, `ce8597ad`. Identical
operator action every time. Filed as scope item 5 of `e0dc2b2d`: auto-commit stranded output rather than
blocking indefinitely.

**Caveat — do NOT blindly commit.** If a GATE has already passed on the branch head, the stranded edit was not
gated and committing it lands unverified code; discard and preserve a patch instead. See the stranded-edit
section above for the two cases and how to tell them apart.

## THE TICK BLOCKS ON SYNCHRONOUS TEST RUNS — measured, and it kills the "event-driven handoff" idea

Filed as `1732dc79`. Two things every future measurement of this system needs.

**1. `WATCH_TRANSITION ... elapsed=` RESETS when a worker restarts.** Back-computing a round's start time by
subtracting it from the transition timestamp produces PHANTOM IDLE GAPS. I did that and published a wrong
"26% idle" figure. For loop cost use `PHASE_TIMING phase=per-goal-walk elapsed_ms`; treat
`WATCH_PROGRESS elapsed=` as a live liveness read only.

**2. Role handoffs are already instant. The loop is BUSY, not asleep.**

    11:10:39.526  tick=67 ... adca4b85 ... Executed
    11:10:39.592  WATCH_TRANSITION Developer -> next=Reviewer ... "Reviewer dispatched"
    11:10:39.593  WATCH_PROGRESS role=Reviewer elapsed=0s pid 24048 alive

**66 ms**, same tick. So backlog `cd753fd9` (event-driven handoffs, "every handoff costs up to a full tick") is
aimed at a cost that does not exist on this path. Do not build it without re-measuring first.

The real cost is pre-review evidence running a REAL TEST SUITE synchronously inside `per-goal-walk`. Across
~300 ticks of one generation, only THREE exceeded 10s — 478930 ms, 162961 ms, 123499 ms — but those three ate
**12.75 minutes, ~14% of the generation**, and the walk is serial so EVERY goal freezes. The acceptance gate is
already async (`acceptance_verification_running_in_background`); pre-review evidence is not. That asymmetry is
the fix.

Also note what a blocked tick costs beyond throughput: while blocked, the loop cannot apply operator intents or
notice stopped goals.

## Liveness lives in the STDOUT log, not the event log

`conduct-events.log` records **decisions**, not liveness. A healthy loop with workers running and nothing to
decide emits NOTHING there for many minutes. A monitor watching only that file cannot tell quiet from dead —
which is a real gap in the silence-breaker as built, and it produced one false alarm at 07:21.

Per-tick liveness is in the loop's own stdout log (`.orchestrator/logs/operator-<name>-<ts>.out.log`):

    PHASE_TIMING tick=26 phase=sweep elapsed_ms=3189 goals=16 ...
    PHASE_TIMING tick=26 phase=prewalk scoped=15 candidates=15 eligible=2 excluded_terminal=0 ...
    WATCH_PROGRESS goal=6cd812af role=Reviewer elapsed=5m16s liveness="alive" ...

To check liveness properly: read that tail, and/or `Get-Process -Id <lock pid>` and look at **CPU** — a loop
consuming CPU is working. The lock pid is line 1 of `.orchestrator/conduct-loop.lock`.

**TIMEZONE TRAP — bit me twice.** `ls` prints **local time (UTC-5)**; log timestamps and `date -u` are **UTC**.
Comparing an `ls` mtime against a UTC log timestamp makes a 12-second-old file look 5 hours stale, and makes a
healthy loop look dead. Either compare `ls` mtime to `date` (local), or compare log-line timestamps to
`date -u`. Never mix the two.

Note `scoped=15` of `goals=16`: the missing one is the goal evicted per `60d80486`, so that counter is a cheap
way to see eviction actually happening.

## Operator gotchas learned tonight

- **`WATCH_TRANSITION files=N` is the LAST ROUND's delta, not the branch total.** Use
  `git diff --stat main...<branch-head>`. It caused two false alarms.
- **Filter artifact directories by ATTEMPT ID.** Attempt folders accumulate siblings; I read a three-hour-old
  TRX and filed a wrong diagnosis from it (`7dfecf10`, since retracted).
- **A cancelled dispatch strands its partial edits**, which dirties the worktree and blocks the pre-landing
  rebase. Check `git status --porcelain` in the goal worktree after any cancel — and READ the diff before
  discarding, because one of them was a real fix worth committing.
- **`goal-mark-landed` is NOT broken** — it records the landing and defers terminalization to a sweep that
  never flips the status. I wrongly called it broken from a single `status` check.
