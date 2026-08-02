# Orchestrator Handoff

**Updated:** 2026-08-02 ~03:10 UTC by Claude. Live status, not durable policy — verify before mutating.

## Repository state

**THE GATE WORKS AGAIN. `fcf8669a` landed autonomously at 05:37 UTC as `9f0ee27e Integrate goal/fcf8669a`** —
acceptance passed, pre-landing rebase succeeded, conductor integrated it with no operator action. That is the
first goal through the pipeline since the gate broke, and it is the end-to-end proof that the git-identity fix
plus the three known-folder fixes are correct.

Main is at `9f0ee27e`. Working tree clean.

Landed 2026-08-01/02: `a68cc155`, `e89f9110`, `fb53d9c0`, `def0a48e`, `e7835e32`, `13d9d5e6`, `b8988202`,
`82186ad5`, `fb686e53`, `e553625e`, `90d77e31`, `c70a99e7`, `b66d363c`, `84c1222d`, `8a3c8961`, `2bd95504`.
Goals landed: `c012c6fc`, `4c001f19`, `fcf8669a`.

**PENDING VALIDATION:** `c28a87de` is the live before/after evidence for what `fcf8669a` just fixed — a goal
whose branch is merged into main should now auto-terminalize instead of escalating at Verified forever. It was
still `Verifying` immediately after the landing, which is EXPECTED: the running loop predates `9f0ee27e`, and
the max-duration handoff does not rebuild. Bounce via the launcher when no worker is in flight, then watch
whether `c28a87de` terminalizes on its own. If it does, `b9398a95` (the abandoned 4-round predecessor of
`fcf8669a`) can be stopped using the recipe below.

## THE LANDING BLOCKER IS FIXED — confirmed live

Every landing was failing at `pre-landing_rebase_failed: ... Rebasing (1/6)`. Root cause: the hermetic
verification environment clears the child env and repoints HOME/USERPROFILE at an empty profile root, so git
inherited NO IDENTITY. Read-only git was unaffected, which hid it — but the pre-landing rebase REPLAYS
COMMITS and exits 128 ("unable to auto-detect email address") on the first one. Manual rebases always worked
because an operator shell has the real HOME.

Fixed in `90d77e31`: the hermetic env now sets `GIT_AUTHOR_NAME/EMAIL` and `GIT_COMMITTER_NAME/EMAIL` to a
deterministic gate identity (those four outrank every config file, and admit no operator config).

CONFIRMED: on the first tick of the rebuilt loop, `5146fab4` cleared the rebase and entered
`verification-check`. It had never reached verification before.

Two related fixes in the same commit: git's bare-CR progress output is now collapsed so the fatal line
survives single-line rendering (it was showing "Rebasing (1/6)" and no fact), and `82186ad5`'s rebase retry
was reverted — its "transient" premise was wrong.

## FINISH THIS FIRST — two uncommitted fixes, both with proven failures

The gate now runs, and it immediately found two real defects. Both are edited and BUILT but NOT committed,
because a gate held the build slots. Test and commit them before anything else.

1. **`GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable`** — MY REGRESSION from `90d77e31`.
   The four new `GIT_*` variables were not added to the allow-list predicate, so the gate's own hermeticity
   assertion fails: `Assert.All() Failure: 4 out of 20 items`. Fix adds the four names.
   THIS IS A RED TEST ON MAIN AND IT BLOCKS EVERY LANE'S ACCEPTANCE. It is why `5146fab4` and `fcf8669a`
   both failed their gates at 03:02 and 03:0x.
   Why I missed it: the test lives in class `HermeticVerificationEnvironmentTests`, NOT
   `GoalAcceptanceVerifierTests` — both in `GoalAcceptanceVerifierTests.cs`. My
   `--filter-class "*GoalAcceptanceVerifierTests*"` run went 25/25 green and never touched it.
   **Run BOTH classes, or the file, or the project.**

2. **`WorkerShell.WindowsPowerShellCandidates`** — the gate runs every lane under Windows PowerShell 5.1.
   Proven: `ProcessTreeGuiSuppression_..._for_descendants` fails with `Probe did not write output.
   stdout=Windows PowerShell` — the 5.1 banner. Under 5.1 `ProcessStartInfo.ArgumentList` does not exist, so
   the probe's argument passing degrades to an interactive REPL.
   `b8988202` did NOT fix this. Removing 5.1 from the candidate list only changed WHICH LINE selected it:
   MEASURED on this host, pwsh 7 is not on PATH at all (machine PATH carries only
   `C:\WINDOWS\System32\WindowsPowerShell\v1.0`), so `FindOnPath("pwsh")` misses and
   `FindOnPath("powershell.exe")` lands on the same 5.1 binary.
   Fix: read the `LOCALAPPDATA` VARIABLE before falling back to `GetFolderPath`. The gate already writes the
   REAL per-user location into that variable; `GetFolderPath` re-expands `%USERPROFILE%\AppData\Local`
   against the child's redirected env and cannot see it. Candidate ordering is now a pure function
   (`WorkerShellCandidatesForTests`) with two new tests, because the old test only asserted `File.Exists`,
   which 5.1 satisfies.

## The known-folder trap has THREE sites — two fixed, watch for more

`Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` expands the REG_EXPAND_SZ literal
`%USERPROFILE%\AppData\Local` against the CURRENT PROCESS's environment block. The gate rewrites USERPROFILE
to a hermetic profile root, so ANY code that calls it in a gate-spawned child silently resolves into
`<temp>\mcg-hvp\AppData\Local`, which has never held a PowerShell install and is not writable.

PROVEN experimentally 2026-08-01: with USERPROFILE repointed and LOCALAPPDATA set to the real path, an
already-running PARENT returns the REAL folder from GetFolderPath, but a freshly spawned CHILD returns the
REDIRECTED one. That asymmetry is why this looks inconsistent when probed casually — always measure in a
child.

THE ROOT CAUSE was the third site, and it made the first two look like they had not worked: the hermetic
environment ITSELF was not idempotent. The gate applies it TWICE — the conductor configures the
`__acceptance-gate-attempt` process (`ConductorParallelAcceptanceAttempts.cs:1160`), and that process,
already hermetic, configures each lane (`GoalAcceptanceVerifier.cs:6016`). The inner call re-derived
APPDATA/LOCALAPPDATA from `GetFolderPath` against the USERPROFILE the outer call had already repointed, so it
wrote a PROFILE-NESTED LOCALAPPDATA into every lane. Reading "the variable" therefore read an already-wrong
value. Fixed in `8a3c8961` via the pure seam `ResolvePerUserFolders`.

Sites found and fixed — ALL THREE ARE LOAD-BEARING, each verified by removing it and reproducing the failure:
1. `WorkerShell.WindowsPowerShellCandidates` — `c70a99e7`. Removing it reproduces the gate signature exactly:
   `stdout=Windows PowerShell` plus `$psi.ArgumentList` null (`You cannot call a method on a null-valued
   expression`) — 5.1 has no `ArgumentList`.
2. `AssemblyTempRedirect.EnumerateCandidateRoots` — `84c1222d`. Without it, and even with a REAL
   LOCALAPPDATA, the test temp lands at `...\mcg-hvp\AppData\Local\Temp\Low\mcg-tests` and every
   workspace-building test dies with `UnauthorizedAccessException`. A hand-created directory there also
   fails: the real `%LOCALAPPDATA%\Temp\Low` carries a LOW mandatory label that a copy does not, and the MTP
   exe runs at Low integrity.
3. `GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment` — `8a3c8961`, the root cause above.

The fix in all three is the same: read the `LOCALAPPDATA` VARIABLE first, then fall back to `GetFolderPath`.

**DO NOT consolidate these into one shared helper.** Site 2 is a `[ModuleInitializer]` in the TEST assembly
whose entire purpose is to redirect TMP/TEMP *before anything else resolves a temp path*. Calling into
Infrastructure from it would trigger Infrastructure's static initializers — including
`WorkerShell.Executable` — ahead of the redirect, which is the exact ordering it exists to guarantee. The
duplication is deliberate; each copy carries the reasoning.

**If you find a FOURTH caller of `GetFolderPath` on a per-user folder in code that runs under the gate, it
has this bug.** Measure it in a freshly spawned CHILD, never in the current process: an already-running
parent returns the real folder while its child returns the redirected one, so an in-process probe will tell
you everything is fine when it is not. That asymmetry made one of my tests vacuous before the negative
control caught it.

## Gate failures still unexplained — RE-MEASURE, do not file yet

`5146fab4`'s gate failed 5 checks. Two are fixed above. These three are NOT yet diagnosed, and they ran
under 5.1, so some may be symptoms of fix 2 rather than independent defects. Re-run the gate after landing
both fixes before filing anything:

- `GoalAcceptanceVerifier_real_process_shards_keep_receipts...` — `BuildLockBlockedException`
- `Known-green fixture runs through the freshly built public acceptance entrypoint...` — `BuildLockBlockedException`
- `InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory` — script exited 86

On that last one: its message reads `Expected owner token: 'goal-reuse-goal'; found owner token:
'goal-reuse-goal'` — IDENTICAL strings. That line prints on EVERY reuse failure
(`Invoke-IsolatedDotnet.ps1:798`), including the assembly-not-found and multiple-match branches, so it buries
the real `Reason`. The ownership check at `:389` passed. Do not chase ownership.

## The expensive lesson from today: I fabricated a defect and it cost three goals

Goal `05cfd4da` Planner task 1 was recorded Failed despite exiting 0. I wrote a manual-verification note
asserting reconciliation "concluded from its pid record that the dispatch had died". That was an INFERENCE.
The real diagnostic was in the SAME verification record:

    Planner output contract failed: required section 'acceptance criterion mapping' lacks its mechanical
    evidence marker; no readable orchestrator-workspace or model-home plan artifact was referenced.

That is `BackgroundDispatchRunner.cs:1000-1006`. No pid involved. Verified today, independently and by a
second reviewer: `TryCompleteFromExitFile` (`:634`) CANNOT fail a successful worker — owned-pid state only
selects defer (`:647-651`, `:679-683`) vs proceed (`:685-693`), and existing test
`BackgroundDispatchRunnerReconcileHoldsExitFileWhenOwnedPidStillRunning` asserts exactly that. Every
exit-0-to-1 flip in `BuildCompletedProcessOutcome` is EVIDENCE-based: `:972`/`:982` Researcher contract,
`:1002`/`:1016` Planner contract, `:1062` build check, `:1122` dirty worktree, `:1157` missing commit
evidence.

The proposed fix would also have REVERSED a deliberate decision recorded at `:1166-1172`: "WORKER_RESULT is
advisory only ... not from the worker's self-reported field shape, which produced recurring false-failures".

Cost: backlog `d9bb23f0` (closed as fabricated), goals `1c3a7b60`, `14b9ab0e`, `f8f71793` — all cancelled.
The real defect is filed as `7453fa80` and is cheap: the marker regex is
`\b(?:map|maps|mapped|mapping)\b` matched against the section BODY, which EXCLUDES the heading, so a correct
plan with "mapping" in its heading fails.

RULE: a manual-verification note is a diagnostic artifact that outlives you and gets mined for backlog
premises. Never write a because-clause into one. Also check `git merge-base --is-ancestor <sha> HEAD` on any
commit a brief cites as its baseline — `14b9ab0e`'s cited `97bcf627`, which was never on main.

## Operator stop verbs: the order that actually works

**Cancel EVERY TASK FIRST, while the goal is still non-terminal. THEN stop the goal.**

1. `progress <g> <task#> cancelled "<msg>"` for every non-terminal task.
2. Confirm every task reads Completed/Cancelled.
3. `cancel-goal <g> "<reason>" --confirm-goal-stop` (or `supersede-goal`).
4. Re-verify after the next tick.

Do NOT do it the other way round. `StopGoal` (`AgentOrchestratorKernel.GoalLifecycle.cs:716-751`) only calls
`goal.SetStatus(terminalStatus)` at `:744` — it does NOT reopen tasks, contrary to earlier belief. The real
failure is a DEADLOCK: once the goal is terminal it leaves conductor scope, so a task `progress` intent comes
back **`Rejected progress: goal was not found in conductor state`** (verified on intents `24d6dd7b`/eef5ffd9
and `65771ff3`/14b9ab0e). The tasks then stay non-terminal, and `ReopenTerminalGoalWithNonTerminalTasks`
(`:816-827`) sees terminal-goal + non-terminal-task and flips the goal back to **Active**.

That is why `f8f71793` stuck (task 1 already Completed, task 2 cancelled while still Active) and `eef5ffd9`
did not.

HAZARD that defeats even the correct order: the post-loop-stop auto-requeue can flip a task back to
Assigned/Running between steps 1 and 3 — exactly what happened to `eef5ffd9` (cancelled 02:49:58, requeued
02:50:22). Re-check task states immediately before the goal stop.

SEPARATELY: after any stop within ~15 min of a loop bounce, run
`scripts\Get-GoalDispatchInventory.ps1 -GoalPrefix <goal>` and look for `exit=running hb=running`. On
`eef5ffd9` I cancelled a task at 02:49:58; at 02:50:22 `Auto-requeued interrupted dispatch after conductor
loop stop` reversed it, and at 02:55:02 a 62,736-char Opus 5 Reviewer launched on a superseded goal. My own
DELIBERATE `.conduct-stop` bounce armed it. Nothing surfaces this. Filed as `69b01e10`.

## Lanes

- `5146fab4` — Verified, gate FAILED 03:02 on the two fixes above plus three unexplained. Re-gate after landing.
- `fcf8669a` — Verified, gate running at 03:0x, will fail the same way. Re-gate after landing.
- `d4897de1` — held at `WorkspaceReady`, "Assigned tasks exist but no ready batch". CAUSE FOUND: its worktree
  is DIRTY — five uncommitted modified files (`git -C .orchestrator-worktrees\d4897de1 status --porcelain`).
  A dirty goal worktree blocks all dispatch behind exactly that generic hold. Do NOT hand-commit them: the
  Reviewer's standing blocker is that `LauncherScriptTests.cs` was never executed at HEAD `958393d5` and the
  round rewrote three previously-red assertions plus ~120 lines of never-run PowerShell. Send it back via
  `retry` instead.
  NOTE FOR REBASING IT: that worktree adds a `WorkerShell.PowerShell7Executable` (registry App Paths +
  `FileVersionInfo.ProductMajorPart >= 7`, deliberately allowing WindowsApps for medium-IL conductor
  launches). It calls `WindowsPowerShellCandidates()` with no arguments. Main's fix keeps a no-arg overload,
  so the call site still compiles — the conflict is textual, not semantic, and the two changes are
  complementary (theirs is for conductor launches, main's is for the worker/gate path).
  Its task-1 blocker is genuinely OPERATOR-OWNED: `McgOrchestratorAutoResume` is not registered on this host,
  and a Low-IL worker cannot register a scheduled task. Someone at Medium IL must capture the
  STARTUP_DIAGNOSTIC line before/after — that capture is the only evidence that will confirm or refute the
  claim that `RunLevel Limited -> Highest` fixes the worktree-create denial.
- `c28a87de` — merged into main (`56b12392`), all tasks Completed, escalating at Verified for over a day.
  `goal-mark-landed c28a87de --confirm-goal-mark-landed` fixes it; kept as live before/after evidence for
  `b9398a95`.
- `b9398a95` — Active. Legitimate and unrelated to the fabricated premise: a merged goal sticks in Verifying
  because the loop escalates it as Verified while `goal-mark-landed` refuses it as Verifying.
- `14b9ab0e`, `f8f71793`, `1c3a7b60` — CANCELLED, fabricated premise (above).
- `da216fc3`, `eef5ffd9` — CANCELLED, duplicates of `c012c6fc` (landed as `0a303590`).

## Filed 2026-08-01, each with a verified reproduction

- `7453fa80` Planner output contract fails correct plans on a keyword-presence check (see above).
- `69b01e10` post-loop-stop auto-requeue resurrects CANCELLED tasks and spends a paid round.
- `2070c98e` delete the `GoalWorktrees` cleanup statics and split the shared `exclusiveResourceKey`. Step 2 of
  `c012c6fc`, never filed until now. Both "Goal lifecycle commands" (238.29s) and "Goal worktree cleanup"
  (238.28s) still share `xunit:GoalWorktreeCleanupHooks`, so ~476s sits on the critical path as an
  unbreakable serial chain. The key is currently CORRECT and cannot be removed until the tests stop mutating
  the `GoalWorktrees.Cleanup*` statics that `GoalWorktreeCleanupHooks.Default` forwards to
  (`GoalWorktrees.cs:104-122`).
- `6a9a70ea` CORRECTED — its original concurrency-race premise was wrong; annotated with the real git-identity
  cause. There is no overlap between the rebase and acceptance: `ConductorDriver.cs:1521` runs the rebase and
  only then `:1535` runs verification, in one call.
- `71d4d76c` auto-review-retry throws on reviews that DO have open blocking findings. STILL TOP PRIORITY.
- `6fcd9a2b`, `cde3093b`, `aad287f0`, `bd7cb465`, `b640d889`, `b949c9f5` — see prior handoff.

## Operating notes worth keeping

- Invoke the CLI as `.\mcg-orchestrator.cmd <verb>` — RELATIVE. The absolute path does not match the
  `PowerShell(.\mcg-orchestrator.cmd *)` allowlist blanket and falls through to the auto-mode classifier,
  which hard-denies sensitive verbs with no prompt.
- Never take a build lease (MTP exe, `dotnet test`, `stable-slot-dotnet`) while a gate holds a slot. It
  produces `ACCEPTANCE result=corrupt-artifacts`. Recovery is
  `build-lease-cleanup --confirm-build-lease-cleanup`.
- The `--max-duration` handoff spawns the PREBUILT executable. Code landed mid-generation does NOT arm at the
  next handoff. After landing loop-affecting code, bounce deliberately via
  `scripts\Start-OrchestratorCommand.ps1`, which rebuilds. Confirm by checking that
  `src\...\App.dll.git-head` matches HEAD.
- Clarification ids display truncated to 8 chars and the resolver needs more, so two questions sharing a
  prefix are BOTH permanently unanswerable (`95a21e4f`). Workaround: state the decisions in the brief.
