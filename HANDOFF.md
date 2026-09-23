# Orchestrator Handoff

Live state at the top, durable operating rules below it, nothing else. The 2026-09-14 to 2026-09-19 running log
that used to live here was moved to `.orchestrator/operator-evidence/handoff-running-log-2026-09-14-to-2026-09-19.md`
(committed versions remain in git history). Verify any goal or backlog claim with
`pwsh -NoProfile -File .orchestrator\operator-tools\Get-GoalCard.ps1 <prefix...>` or `backlog-list --text <words>`
before acting on it; goal states move.

## RESUME HERE — 2026-09-22 22:05 UTC (Claude)

**STATE IN ONE BLOCK.** Conductor pid 29064, started 19:08:27Z, next renewal ~23:08Z (maxDuration 14400s).
Worker cap 4/4 for most of the last hour. Origin current at `1f637fbca`. Eight goals reached Completed
today (`cf64014b` 16:14, `4e380a73` 16:55, `919396a7` 19:55, `725ff65b`/`9bea7a9a`/`643fbd9a` together on a
passing cohort at 20:20, `6cd11d28` 20:43, `36169fb0` 21:39); four of those carry their own integrate
commits (`85fa8ffab`, `2dccce740`, `edfd5aacd`, `1f637fbca`). Three goals were Superseded (`48677446`,
`b5271c78`, `fdb06590`).

**UNUSUAL THING ON THE BOARD, READ THIS FIRST.** `fdb06590` is a **disposable operator fixture**, not
product work. Miles authorised it. It exists to supply `7131a80c`'s criterion 4, which is operator-owned and
says *"validates a disposable stopped predecessor and active successor through the shipped CLI plus multiple
daemon ticks/reload... Do not act on unrelated live goals"* — so the pair had to be **constructed**, not
observed on the board. `fdb06590` is `Superseded` **with its Developer task still `Assigned`**, which is
exactly the precondition under test: a stopped goal holding a non-terminal task must emit no dispatch across
repeated recovery passes and a runtime renewal. Baseline tick **554**, stopped 22:02Z.
**Do not "clean it up".** Leaving it sitting IS the experiment. If it ever prepares a dispatch, that is the
defect and should be captured.
**Half-built:** the successor was NOT created. `goal-replace` needs a terminal predecessor and refuses a
`Cancelled` one (hence Superseded). Brief and reason are written at
`scratchpad/disposable-successor-brief.txt` and `scratchpad/disposable-successor-reason.txt`; the command
still needs a fresh `--request-id <guid>` and `--disposition supersede-unlanded-attempt`. After the
successor proceeds and a renewal has been spanned, record the receipt against `7131a80c` criterion 4.

**IN FLIGHT, healthy, needs nothing:** `0a336878` in the acceptance gate (this is the review-convergence goal
that was blocked 2h11m this morning on a clarification); `cd39be97` Reviewer passed, queued for the gate;
`29e8cbeb` and `e96849a6` with Developers running on freshly reset bases.

**PARKED, both deliberately:**
- `7131a80c` — park reason on the goal is now CORRECTED by a note on task 2. Its stated condition
  ("until 919396a7 lands") is satisfied in letter but NOT substance: `919396a7` landed 19:55 but covered only
  two test classes, and this goal's gate fails on
  `CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement.GoalReplaceCancelledZeroWorkCreatesFiveRoleSuccessor`,
  which was never in its scope. **The real blocker is `853c5a5a`.** Also: its Reviewer task is a PHANTOM —
  I parked the goal 60 s after dispatching it, which killed the worker with no exit recorded. Close that
  task on its receipt before unparking.
- `f57758c8` — parked 22:05Z to stop a **paid re-dispatch loop**, ~100 s per cycle. Its Developer work is
  already committed at `67b7e30c`; every round since has nothing to do and is rejected
  `post_dispatch_commits=0`. Rejections at 21:56:27 and 22:04:42. Reason is
  `verification-pattern-unmatched`, NOT `no-change-evidence` — so this is ADJACENT to `e96849a6`'s slice,
  not an instance of it; do not hand it over as a reproduction. On unpark, do NOT just close task 3 again
  (that feeds the loop) — use the mechanical-retry escape in `1cd87e1e`, and unpark only on an idle board.

**TO-DO, in order:** (1) create `fdb06590`'s successor and let the pair span the ~23:08Z renewal, then record
`7131a80c` criterion 4; (2) see `0a336878` and `cd39be97` through their gates; (3) unpark `f57758c8` on an
idle board using the mechanical-retry escape; (4) re-scope backlog `ed949b7a` to its owned-process-cleanup
half only — its integrity-query half is superseded in main and annotated.

**NEW BACKLOG FILED TODAY:** `804feeeb` gate liveness cannot be answered from any operator surface (linked
related to `176cf1ff`); `125a3f7d` a gate test fabricates PIDs 1000-1999 that collide with the live
test-host pid; `853c5a5a` the CLI acceptance-command contention residual `919396a7` did not cover.
Annotated: `0310cc2d` (worker build timeout is a two-sided deadlock, not just a low number), `ed949b7a`
(half superseded).

**PATTERNS WORTH KEEPING.**
- **Conflict count predicts nothing about a stale branch.** `6cd11d28` had 1 conflict and squashed in
  minutes; `48677446`/`b5271c78` had 2-5 and were **fully superseded** by main, where taking the branch side
  would have reverted a better implementation and the gate might well have passed. Always diff base→main per
  conflicted file BEFORE opening a hunk. `29e8cbeb` was a third case: both sides rewrote the same function
  from the same base, so no union compiles and it needed re-implementation, not merging.
- **A stop-class verb on a live dispatch orphans the worker** and leaves the task phantom-`Running`, which
  blocks batch formation for every downstream task. Hit on `7131a80c` (park) and mirrored on `f57758c8`
  (finished round whose status never advanced). Check the newest dispatch's `exit.txt` and heartbeat first.
- **A close on a `Running` task is reverted by an armed retry; a close on a `Failed` task sticks** — but
  only until the retry re-arms, which is what turned `f57758c8` into a loop.
- **`--cause UnchangedContextRepeat`** is the mechanical-reopen cause that falls through to
  `AcceptanceRegate`, re-gating with no paid round. No cause at all routes to `HumanClarification` and wedges
  the goal in `WaitingForHuman`; `EnvironmentApparatusFailure` routes to `EnvironmentalHold` and also wedges.
- **`[Introduced]` on an `unattested` attempt is not evidence of guilt.** It means the gate could not
  attribute, not that the candidate is innocent either. The discriminator is whether the candidate's diff
  reaches the failing code — for `36169fb0` it did not (host-PID flake, re-gated green with no worker round),
  for `cd39be97` it did (a real manifest/pin mismatch).
- **A manifest lane move obligates three places:** the lane filter, every exclusion, and the PINNING tests —
  and the pin's failure surfaces in the **Remainder** lane, not the lane you edited.

## Earlier — 2026-09-21 03:55 UTC (Claude)

**STATE IN ONE BLOCK.** Conductor pid 10208, `LOOP_START` 02:52:13Z, next renewal ~06:52Z. Two clean
renewals tonight (22:49Z, 02:51Z), both with `stagedSourceCommit == repositoryHead == 28c94667`, 28 s
and 21 s of downtime. Landings today: 21, most recent `28c94667`.

- `98b82ef1` — NARROWED, candidate `1366f2a6bbc8e14fb652d8623db74da6bd4f4d3e`, left in **Failed**
  deliberately (zero-cost pause, will NOT self-dispatch, keeps options open — do not park it). Developer
  and Tester adjudicated closed by the operator; criterion 7 rebound and VERIFIED IN THE STORE as
  `1366f2a6bbc8e14fb652d8623db74da6bd4f4d3e`.
  **ONE REAL BLOCKING DEFECT REMAINS and it is the first thing to fix:**
  `98b82ef1-stable-slot-cs2012-recovery-before-retry`, at
  `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs` lines 4741, 4951,
  4970-4983. *"When stableSlotLease is held, reacquireLease returns without releasing it; the code marks
  recovery then immediately retries. The conductor disposes the stable lease only after the attempt, so
  the guarded shutdown cannot remediate the CS2012 before that retry."*
  **Why it matters more than it looks:** the entire justification for removing the session-global
  shutdown is that a GUARDED shutdown still remediates CS2012. `MarkCompilerLockRemediationRequired()`
  only takes effect on lease DISPOSAL, so if the stable lease is held across the retry the safety valve
  never fires and criterion 1 is only half-present. Verified the shape in source; the finding is sound.
  **Caution:** this is gate-code, which cannot validate itself — a fix needs evidence from a real gate
  run, not just a focused class pass.
- `0285f012` — WEDGED since 22:26Z, do not expect it to move on its own. Its refinement executor
  respawns every ~15 min, prints `SPEC_REFINEMENT_WORK_COMPLETE ... claimed=false`, and exits zero.
  Filed as `03ebdb47` with the full diagnosis. Repair was deliberately deferred because `readiness`/
  `goal-recovery` run a REPAIRING SWEEP that writes to state.db and a live round was always in flight.
  **Do this on an idle board.**

**IMMEDIATE TO-DO, in order:** (1) see 98b82ef1 through the gate, rebinding criterion 7 again if the
head moves on an integrate commit; (2) after it lands and the loop is bounced, take the deferred
post-landing `peak_mem_bytes` reading of the next three Developer rounds and the next gate build
against the 2.8–3.3 GB baseline and record on `43327f1a` — the waiver RELOCATED that obligation, it did
not retire it; (3) repair `0285f012` on an idle board; (4) the staged owned-roots brief at
`scratchpad/brief-owned-roots-slice1.md` is still unfired and now unblocked once 98b82ef1 lands.

**NEW BACKLOG FILED TONIGHT:** `8368dbdc` cancel-dispatch does not stop the round; `7e4e97e7`
Get-GoalCard buries the card under exceptions; `b50ca9e8` isolated App build fails MSB3030 on main;
`3a392688` provider capacity error misclassified as missing evidence; `03ebdb47` durable outbox not
drained; `c6c5be25` the split-out shared-compilation enablement carrying all of tonight's evidence.

**THE PATTERN WORTH INTERNALISING: this board fails by SILENCE, not by error.** Three independent
instances tonight, each invisible in every summary view — a 5.8-hour unanswered `attention` item on a
goal that kept producing rounds; an outbox executor respawning forever printing COMPLETE while doing
nothing; a provider capacity error reported as a worker evidence fault. A lane with ZERO events for
many minutes is more suspicious than one throwing errors. Run `attention` as routine status.

## Earlier — 2026-09-20 19:55 UTC (Claude)

**Host:** pagefile 48 GB, commit limit 96 GB (was 76), free disk 77 GB, Windows Update paused to 09-25. Three restarts
today: 11:46, 12:14 (pagefile), and 13:25 — the third was unannounced and killed the daemon plus a running gate, so
ask for a park before any further restart. Daemon relaunched 18:49Z, conductor pid 25116 (lock says the live pid),
log `operator-conduct-loop-daemon-20260920134912.out.log`, worker cap 4, gate width 1, 4-hour renewals (next ~22:49Z),
GC-conserve 5, binary from main `1df8d72b5`.

**Two landings at 19:27Z and 19:35Z (19th and 20th this session):** `5210c79e` as `49d82c66f` (Hermes fixture git
budget and the hung-child heartbeat wall-clock retired, so two recurring apparatus reds are gone) and `743ff0ea` as
`d89005f65` (landing refusals now name the failing IsAccepted term and the sweep remedy points at the waits). Getting
743ff0ea in took FOUR passing gates and two operator rebinds: each time main advanced, the branch was re-integrated,
the candidate sha changed and all seven obligations went stale again (fa34cec88 then 21a569f04). Rebind recipe and the
systemic fix are on backlog 1822ddfe.

**Third landing at 19:50Z (21st this session):** `011dbb7c` as `28c94667`, the goal that WITHDREW its own change.
Its measurement disproved the premise (61.827 s fact, zero janitor lines, redirect startup plus cleanup 204 ms =
0.33 percent, the rest is per-fact git fixture work), and a gate then proved the precheck unsafe in production, so
the Developer removed it entirely with operator permission. What landed is ONE file, docs/negative-controls/
011dbb7c.md (39 lines), and zero production-source lines. The Reviewer's round-2 finding
CTX-011DBB7C-C1C2C7-PRECHECK-WITHDRAWN-01 was factually accurate but not a defect — it asked for the withdrawn
precheck back — so criteria 1, 2 and 7 were hand-closed as WITHDRAWN via progress + verify-manual on both tasks.
Backlog `bdab2d75` stays OPEN with its premise corrected (annotated 19:52Z, including a correction to my own earlier
"the precheck is correct" note, which two gates disproved); its criterion-6 lane reading should be taken after a
bounce expecting NO improvement, since nothing in production changed.

**Cost of that disposal, now filed as backlog `8368dbdc`:** cancel-dispatch stops the process but not the ROUND. It
leaves the worktree dirty (it even records `kind=Dirty` with the file list), the dirt escalates the goal, and
clearing the dirt is the starting gun for the loop to re-dispatch the very round you cancelled — 56 s later, here.
The second round rewrote the same files 4 s before the close intents applied, which failed the pre-landing rebase and
invalidated BOTH closes. Cost: one extra paid gpt-5.6-sol round (cpu_ms=372218) and one discarded gate attempt. The
retry only worked because the note files were already written and all four intents could be queued inside 60 s. **If
you cancel a round to stop it doing the wrong thing, write the close notes FIRST, then cancel, then queue all four
intents immediately** — otherwise you lose the race.

**Board triage 19:55Z–20:05Z.** `a3e78f34` ABANDONED: Verified but branchless, could never land. Learn
this from it — **a backlog claim does NOT release when a goal terminates**; ~170 claim rows on this
board are held by already-terminal goals. Its item `76d74a03` was already unreachable either way
(`goal --backlog-item` refuses while any goal holds the claim, and `goal-replace` needs a TERMINAL
predecessor, which Verified is not). Recovery, recorded on the item: create with `--brief-file` and
NO `--backlog-item`, then `backlog-close`. `643fbd9a` LEFT Failed deliberately — Failed costs nothing
and keeps refile options open; cancelling would make `3b7b19ed` a one-way door too. `9bea7a9a` LEFT
alone — holds no claim, has zero work (no diff vs merge-base), blocked on parked `cf64014b`.

**Defects filed:** `8368dbdc` (cancel-dispatch re-dispatch race, above) and `7e4e97e7`
(`Get-GoalCard.ps1` buries the card: a pruned TRX receipt throws FOUR exception blocks, and the task
line crashes on EVERY multi-task goal via `[int]$_.n` on an `Object[]`, so roles/statuses/agents print
as three undelimited runs).

**Shared-compiler-server mechanism found** while reading 98b82ef1's worker stderr, written up at
`.orchestrator/operator-evidence/shared-compiler-server-temp-inheritance-20260920.md` and annotated on
`43327f1a`. It is not a concurrency limit: the repo redirects TEMP per test lane, a compiler server
started inside a lane INHERITS that TEMP, and Roslyn shadow-copies analyzer DLLs into
`$TEMP\VBCSCompiler\AnalyzerAssemblyLoader\<hash>\1\` and holds them loaded — inside the directory the
lane must remove at teardown. 35 failures in one round, `UnauthorizedAccessException 0x80070005`
after 6 attempts. Almost certainly why `UseSharedCompilation=false` exists (`7ce2a5f1c`, a CS2012 fix
— same "file in use" class). Direction: pin the server's TEMP outside every lane root. NOT yet
measured; the diagnosis is proven by the path, the fix is a prediction.

**Staged and held:** `scratchpad/brief-sweep-subphase.md`, the sweep sub-phase instrumentation brief,
preflighted clean against both the 16-word and `.git` guards (guards validated against a control file
first). Do not fire it until 98b82ef1 is past its gate. Authoring found a better seam than planned:
`RunJanitorialPhase` (loop file line 1783) already wraps a named phase with failure accounting and two
of the nine operations already use it, so timing goes in ONE helper. Also learned the ratchet has only
TWO agreeing places, not three — it parses its own source as the authority, so a ceiling change is a
single-file edit. When it is created, it doubles as the `5e0b01a9` close test (first owner-tagged
brief refined on a post-`b3da0320` binary must show non-empty owner lists and a raw-output path).

**01:50Z — THE SPLIT WAS EXECUTED. 98b82ef1 is narrowed and Tester-running; the waived half is
backlog `c6c5be25`.**

Option B also failed. The Developer stopped as instructed rather than trying a third approach, and gave
the real obstacle: **`exact-blocker - copied managed-test closures still omit
Mcg.AgentOrchestrator.IsolatedDotnetProbe.dll`**, which defeats probe resolution regardless of where
TEMP points. So the probe-closure findings are NOT fundamentally a TEMP problem.

Criteria **2 and 5 waived BY EXACT TEXT** (`goal-amend`), recorded as durable "Effective acceptance
criteria corrections" with supersedes text, reason and provenance. **Mechanical note for next time: the
repo's compound-shell hook blocks `;`, and both criteria contain one, so the waive value cannot be
passed inline from Bash. A truncated prefix is REJECTED (`KeyNotFoundException ... Use its 1-based
number or exact text`). Do NOT fall back to the number. The working route is the PowerShell tool with
`--waive (Get-Content -Raw <file>).Trim()`, which has no literal `;` in the command.**

98b82ef1 now ships exactly backlog `43327f1a`'s stated problem and nothing more: session-global
`dotnet build-server shutdown` removed from ordinary lease-release and gate-phase paths (reachable only
from CS2012 remediation), per-lane DOTNET_CLI_HOME with NUGET_PACKAGES shared and NUGET_HTTP_CACHE_PATH
per lease, and the gate build maxcpucount control. Narrowing verified at `eca35a27`: Directory.Build.props
back to unconditional `<UseSharedCompilation>false</UseSharedCompilation>` and ABSENT from the diff vs
main; `ConfigureSharedCompilerTemp` has zero references (helper deleted, no dead code); and the waived
criterion-2 fact was INVERTED rather than deleted — it now asserts `false` for both the repo tree and
under McgIsolatedArtifactsPath, which is a regression guard that the revert holds.

`c6c5be25` carries the waived work with all of tonight's evidence so none is re-derived: the receipt
proving the shared compiler itself WORKS (one VBCSCompiler pid 12152, zero CS2012, both builds exit 0),
the two-lane-root cleanup failure with exact `failure_path`s, both failed mitigations and why each
failed, and the closure blocker. **It says to START WITH THE CLOSURE, NOT TEMP**, and flags the ordering
evidence — the probe findings appeared with the TEMP-pinning commit `543a39ad7` and the 22:18Z gate did
not have them, so the omission may be pre-existing but only EXPOSED when TEMP moves. Those two cases
need different fixes; determine which first.

STILL TO DO before landing: rebind `criterion-v1-7` to the FINAL candidate (bound to `b21ce6d5`, head is
now `eca35a27` and will move again on any integrate commit), then take the deferred post-landing memory
reading onto `43327f1a`.

**01:18Z DECISION on 98b82ef1 — Option A abandoned, Option B in flight, SPLIT PRE-AUTHORISED.**

Pinning TEMP so the compiler server's shadow copies leave the lane root (Option A) displaced probe
output in TWO independent classes, which is the same root cause twice:
`98b82ef1-goalworktree-probe-closure-resolution` (GoalWorktreeTests.cs `ResolveProbeOutputDirectory`
2643-2682, "failed all 6 GoalWorktreeIsolatedDotnetTests at Assert.Single(candidates)") and
`98b82ef1-verifier-probe-closure-resolution`
(GoalAcceptanceVerifierDotnetBuildSlotTestsSlotGateJobResources.cs
`ProductionRunnerCancellationKillsReadyDescendantTree` 420-462). **TEMP is not only where Roslyn puts
analyzer shadow copies; it is where these builds put outputs that other code resolves by globbing. Every
place you pin it, something else moves.** Chasing displaced paths one at a time is unbounded.

Switched to **Option B**: leave TEMP alone for test-side builds, and make the assembly temp-root cleanup
tolerate the compiler server's subtree, on the ownership argument that a server which deliberately
outlives the lane never created lane-owned files. Guarded against becoming a blanket ignore by
requiring the exemption be scoped to that subdirectory, emit a skipped-and-why line, and carry TWO
facts — retained file under the server subdir does NOT fail cleanup, retained file anywhere else STILL
does. Accepted trade-off, stated in the brief: bounded analyzer residue under lane temp roots, already
tracked as the disposable-directory reaping concern.

**SPLIT IS PRE-AUTHORISED IF OPTION B ALSO PRODUCES COLLATERAL.** The worker is told to stop rather than
try a third approach. The split: land the lease-counted shutdown removal, the per-lane DOTNET_CLI_HOME
separation and the maxcpucount control on their own; make enabling `UseSharedCompilation` its own goal
carrying tonight's gate evidence. Note criterion 2 explicitly requires a fact asserting
UseSharedCompilation is true under McgIsolatedArtifactsPath, so narrowing the goal needs
`goal-amend --waive` BY EXACT TEXT first.

Routing note: the loop stopped with `ERR_REVIEW_NO_OPEN_FINDINGS_FOR_TARGET` because both blocking
findings are `category=test-evidence` and therefore not owned by the Developer retry target. The
operator routed them to the Developer anyway with `retry --cause NewTestFinding`, because the
Developer's change caused them and it is the only role that can undo it.

Stale advisory to ignore: `98b82ef1-concurrent-build-receipt-missing` claims the criterion-5 receipt is
absent. It is present at 4,898 bytes in the goal worktree's gitignored
`.orchestrator/operator-evidence/98b82ef1/`. Advisory, not blocking.

**23:50Z UPDATE — the shared-compiler premise is CONFIRMED BROKEN by the gate, and `attention` is a
surface nobody was reading.**

**READ THIS FIRST: check `attention` as part of routine status.** `98b82ef1` carried an unanswered
operator wait `7dee910b` for **5.8 hours** — the Planner had correctly routed criterion 5 to the
operator with the reason *"the Researcher task was dispatched without build-execution tools and
reported it could not run builds"*. The role the criterion named could never satisfy it, the Planner
said so, and nothing surfaced it. Meanwhile the Reviewer failed the goal for the missing receipt and
several paid rounds burned. Answered 23:2xZ with the receipt; the Reviewer then PASSED.

**The VBCSCompiler TEMP-inheritance mechanism is now PROVEN, not predicted.** Gate attempt
`98b82ef1-0-20260920232807597` failed with, from its `result.json` (NOT the TRX — see below):
`failure_path=...\Temp\Low\mcg-tests\p7848\VBCSCompiler\AnalyzerAssemblyLoader\bf271cf8…\1\Microsoft.CodeAnalysis.CSharp.Analyzers.dll`,
`exception_hresult=0x80070005`, and the same shape under lane root `p7990`. The shared server inherits
the lane's redirected TEMP, shadow-copies analyzer DLLs inside it, outlives the lane, and
`AssemblyTempRootCleanupFixture` then cannot remove its own root — surfacing as "Test Assembly Cleanup
Failure" against innocent `ConductorSelfRelaunchTests` facts. Mechanism write-up:
`.orchestrator/operator-evidence/shared-compiler-server-temp-inheritance-20260920.md`.
**This means the goal's core premise and per-lane deletable temp roots are in direct tension, and the
goal may not be landable as one slice.** The Developer has been given three options with a decision
procedure and EXPLICIT PERMISSION to report the fix is too large and stop.

**TRAP WORTH REMEMBERING: the TRX does not always carry the failure.** Both cleanup failures read
`failure message unavailable` / `stack trace unavailable` in the TRX that the retry feedback points at.
The actual `failure_path` and exception live in the sibling `<attempt>.result.json`. An auto-retry fed
only the TRX is working blind. Always grep `result.json` for `failure_path=` when a TRX says
unavailable.

Second, unrelated gate failure: `GoalAcceptanceVerifierSplitFactParityTests` collections differ at index
262, because a new test class was added. Same guard that bit `5210c79e` today; the accepted resolution
there was to fold new facts into an EXISTING fragment class so the guarded count stays put.

**A Tester round was lost to a provider capacity error misreported as an evidence failure.** codex
returned `"Selected model is at capacity"` and died before its first turn; the classifier recorded
`required-file-change-evidence-missing` + `DISPATCH_REJECTED verification-pattern-unmatched` and
escalated for operator action. Tell: exit 1 in <10 s with `cpu_ms` in the low thousands. Filed
`3a392688`. Repair is `retry --cause ProviderInterruption`.

Also filed: `b50ca9e8` (isolated-artifact builds of the App project fail with MSB3030 ON MAIN — proven
by two controls: fails alone, fails on main; mechanism stated as hypothesis with its discriminating
check).

`0285f012` is WEDGED at `SPEC_REFINEMENT_PENDING owner=durable-outbox executor_started=false
detail=executor-launch-deferred-cadence`, unchanged across the daemon bounce. Its Researcher genuinely
completed (5.1 min, 27.8 KB stdout); the refiner executor simply never launched. Not yet diagnosed.

Daemon renewed cleanly at 22:49:45Z: `LOOP_HANDOFF` with `stagedSourceCommit == repositoryHead ==
28c94667` (fresh binary, not stale), successor **pid 31844**, `LOOP_START` 22:50:53Z, 28 s of downtime,
next renewal ~02:50Z. Creating `0285f012` beforehand is what kept a dispatchable goal on the board —
without it the running gate would have been the only live work and the daemon exits in that shape.

**22:20Z UPDATE — 98b82ef1 unblocked and gating; `0285f012` created.**

`98b82ef1` sat **Failed and idle for 93 minutes** (20:43Z to 22:16Z) because its Reviewer refused to
attest two REAL-WORLD-DEPENDENT criteria, and no one was watching. Both dispositions are operator
calls the Reviewer structurally could not make:
- **index 4** (Researcher's reproduction receipt) — the directory never existed; the Researcher never
  produced it. SATISFIED BY EVIDENCE, not waived: operator ran two concurrent isolated builds from the
  candidate (Infrastructure + Core, distinct artifact roots), both exit 0 with `Build succeeded`, ZERO
  CS2012, exactly ONE VBCSCompiler identity (pid 12152) across 6 of 7 both-alive samples, pre-existing
  servers cleared first and no shutdown issued. Receipt committed at
  `.orchestrator/operator-evidence/98b82ef1/shared-compiler-concurrency-receipt.md`.
- **index 7** — its text begins *"After landing and the bounce that stages it"*, so NO party can satisfy
  it at the decision point. Deferred via the loop's own printed `criterion-evidence-map` repair. **The
  obligation is NOT retired** — the post-landing `peak_mem_bytes` reading against the 2.8–3.3 GB
  baseline is recorded on `43327f1a` with the baseline numbers.

**A first pairing used the App project and FAILED with `MSB3030`. Not reported as a pass.** Two controls
show it is neither concurrency nor this goal: it fails ALONE, and it fails on MAIN at `28c946672`.
Filed as `b50ca9e8`. Isolated-artifact builds of App are broken on main today; the mechanism is stated
as a hypothesis with its discriminating check, not asserted.

**Ratchet checked at the final head, not assumed:** main holds `GoalAcceptanceVerifier.cs` at EXACTLY
its 9,649 ceiling, so a rebase breach was live. The candidate is net **−10** (21 added, 31 removed), so
it lands at 9,639. Safe.

**MAX-DURATION HAZARD, live right now.** Daemon `LOOP_START` 18:49:23Z with `maxDurationSeconds=14400`
→ exits **22:49:23Z**. The gate started 22:18:32Z, 26 s before a bounce was possible, and a running gate
does NOT count as active work to the max-duration path — gate + parked/escalated only ⇒ exit with NO
successor, orphaning the gate while it keeps writing progress lines. Mitigation applied: created
`0285f012` so a dispatchable goal is on the board at renewal. **Liveness = the lock file, never log
activity.** If it exits anyway: relaunch and reconcile the orphan attempt (expect an apparatus RED
reopening a Developer — cancel it).

`0285f012` "Attribute the eight unmeasured operations sharing the conductor tick's sweep label",
five-role, from `scratchpad/brief-sweep-subphase.md`. Intake returned `fileScopes` = exactly the three
intended files all `Explicit`, and `explicitConflicts=0` vs 20 comparable goals. STILL TO CHECK on it:
the refined criteria count must equal the brief's **7** on the FIRST prompt file (a refiner split at a
parenthetical asks the Planner for N+1 and costs an Opus round; repair is the exact-text waiver), and it
doubles as the `5e0b01a9` close test (first owner-tagged brief refined on a post-`b3da0320` binary must
show non-empty owner lists and a raw-output path).

**Still staged, not fired:** `scratchpad/brief-owned-roots-slice1.md` (orphaned-directory registry,
Sol's smallest first slice). It owns `DotnetBuildEnvironmentManager.cs`, which 98b82ef1 is changing, so
it must wait for that landing.

**One goal still in flight:**
- `98b82ef1` Developer running since 19:33Z (task 3/5, codex pid 30452). The Tester REJECTED at 19:33Z with three
  open blocking findings now routed to the Developer: `98b82ef1-full-verifier-class-unexecuted`,
  `98b82ef1-local-verifier-global-shutdown`, `98b82ef1-worker-build-helper-bypasses-shared-compiler`. Candidate
  `bfe1cbb0b`, 22 files / +345 / -210, ahead 3 behind 10 vs main, clean merge. It lands alone and needs a bounce
  afterward. Criterion 8 is operator-owned: read RESOURCE `peak_mem_bytes` of the next three Developer rounds and the
  next gate build against the 2.8–3.3 GB baseline and record on backlog `43327f1a`.

**Superseded detail from the 19:15 entry follows.**
- `743ff0ea` (landing hold names the term, backlog 1822ddfe): candidate `fa34cec88`, gate PASSED twice (18:57Z,
  18:59Z) but would not land: seven acceptance obligations were bound to superseded candidates (`29ce6c2ea`, one to
  `4397a9c1f`) after three later Developer commits moved the head. Rebound all seven to `fa34cec88` at 19:02Z to
  19:04Z with `criterion-evidence-map --goal 743ff0ea <index> 1 acceptance acceptance:full-gate <label> fa34cec88…`
  (verified in the snapshot). Its next attempt faulted `blocked-build-slot` ("Stable dotnet build slots busy"), so it
  needs one more gate pass at `fa34cec88` to record evidence, then it lands. The systemic fix (auto-rebind when the
  passing gate's candidate is a descendant of the bound one) is annotated on 1822ddfe.
- `5210c79e` (apparatus flakes, backlog a1451eee): candidate `a8d45f6f9`, gate running since 19:01:43Z. Its earlier
  failure was a REAL regression: the new test class joined the build-slot split family and broke
  GoalAcceptanceVerifierSplitFactParityTests three ways. The Developer fixed it better than prescribed, by moving the
  fact into an existing fragment class so the guarded count stays 13.
- `011dbb7c` (fixtures A precheck, backlog bdab2d75): REAL regression, proven by the loop itself — the cohort was
  dissolved, 743ff0ea passed alone and 011dbb7c failed alone on
  GoalWorktreesRemoveKillsUnprotectedRecordedWorkerProcess. `IsDefinitelyNotAlive` (WorkerProcessJobs.cs line 1550)
  treats a pid ABSENT from the inspection Records as definite death, so a live recorded worker is never killed.
  Commit `866264664` tried to fix it by disabling the precheck whenever a test overrides `TryKillPidTree`
  (ReferenceEquals against a captured production delegate) — test detection, rejected by the operator at 19:10Z.
  Developer re-running with `scratchpad/retry-011dbb7c-no-test-detection.txt` (cause NewSourceFinding): revert the
  gating, require `TryGetValue(...) && Status == Exited`, add an absent-record fact, touch none of the four broken
  facts. My earlier note calling the precheck sound is corrected on bdab2d75.
- `98b82ef1` (shared compiler server, backlog 43327f1a): its Developer died in the 18:17Z modem bounce with 40 minutes
  of work uncommitted. Rescued as `12bfcbaca` on the goal branch (shared compilation scoped to isolated builds,
  lease-counted shutdown, MCG_GATE_BUILD_MAXCPUCOUNT) and recovered with a note to resume from that checkpoint and
  verify it builds; criterion 3 (private DOTNET_CLI_HOME per lane) and the negative-controls doc are still missing.

**Two operating lessons recorded today:** name the exact managed-runner FILTER in a criterion, not a list of classes
(cost two paid rounds on 5210c79e), and `goal-recovery`/`readiness` run a repairing sweep rather than a read.

## Earlier — 2026-09-20 17:30 UTC (Claude) — RESTART DONE, BOARD RUNNING

**Host restart completed 17:14Z.** Pagefile 28 GB to 48 GB (his first attempt did not save: the registry still read
`c:\pagefile.sys 0 0`, system-managed; and the commit limit tracks the file's CURRENT size, so initial must equal
maximum). Commit limit 76 GB to 96 GB. Free disk 77 GB after the run-directory reclaim and the run-events VACUUM
(685 MB to 44 MB). Daemon relaunched 17:19Z: conductor pid `7508`, launcher pid 7508 log
`.orchestrator/logs/operator-conduct-loop-daemon-20260920121900.out.log`, worker cap 4, gate width 1, 4-hour renewals
(next ~21:19Z), DOTNET_GCConserveMemory=5, binary built from main `1df8d72b5`. Loop monitor re-armed (task bjsinmu32).

**Board at 17:30Z:** cohort gate running for `011dbb7c` + `743ff0ea` (started 17:25:56Z, one gate for two goals).
`98b82ef1` (shared compiler server) is at WorkspaceReady, Planner re-dispatching. `5210c79e` was recovered at 17:25Z
with a repair note (below) and its Developer re-dispatches.

**5210c79e gate failure was NOT what the truncated message said.** Four lanes hit the 40 min budget AND the Remainder
lane had four real failures. Two are candidate-caused and two are apparatus:
- CANDIDATE: the new file `GoalAcceptanceVerifierDotnetBuildSlotTestsHeartbeatFailsafe.cs` derives from
  `GoalAcceptanceVerifierDotnetBuildSlotTests`, which puts it in the build-slot split family guarded by
  `GoalAcceptanceVerifierSplitFactParityTests`. `SplitPreservesCollectionConcurrencyContracts` asserts exactly 13
  fragments (now 14); `SplitPreservesDeclaredFactAndTheoryMethodSet` needs the new identity in
  `GoalAcceptanceVerifierSplitFactBaseline.txt` (264 lines, `Owner.Method`, owner normalizes to the base class name)
  AND the fact's attribute on ONE line, because its regex is `(?:^[ \t]*\[[^\r\n]+\]\r?\n)+` and the Developer wrote
  `[Xunit.Fact(` across three lines. All three repairs are in `scratchpad/recover-5210c79e-parity.txt`.
- APPARATUS: `RepositorySourceInventoryTests.GitInventoryIncludesTrackedAndUntrackedSource` (37.4 s) and
  `GitInventorySkipsTrackedFileThroughOutsideJunction` (26.2 s), both `Expected: "git" Actual: "filesystem-fallback"`:
  the inventory's git call degraded under load and fell back. Recorded as a third instance on backlog `a1451eee`
  with the direction (bounded budget; assert inventory CONTENT, report the fallback as a diagnostic).

**2140d2d8 criterion 5, delivered by eb5133d1's timers, and it relabels the problem.** The sweep PHASE_TIMING line's
sub-phases sum to a fraction of the span: tick 4 total 13,773 ms vs 590 ms instrumented; tick 5 13,413 vs 1,267;
tick 6 33,998 vs 1,294 (git index 288, attention 546, goals 378, dependency metadata 82, 6 git spawns). Reading
ConductorBatchLoop.cs lines 463 to 613: the stopwatch labelled "sweep" spans NINE operations, only one of which is
`TerminalGoalSweep.Run` — also PersistSweepTerminalizations, the `recover-interrupted-dispatches` janitorial phase,
CountRunningDispatches, the self-relaunch drain, AwaitCanaryTasks, ReadmitResolvedSetAsideGoals,
MarkCompletedDependencyGoals and ReconcileUnscopedDispatchableGoals. So the sweep itself is cheap (about 1.3 s) and
the next slice must instrument the other eight segments, NOT optimize TerminalGoalSweep. These ticks ran beside a
cohort gate; the clean baseline is the 04:12Z set (median 5,980 ms).

Miles enlarged the pagefile (needs a reboot) and asked for a safe window. Manufactured it: `743ff0ea`, `011dbb7c`
(both Verified, queued for the gate) and `98b82ef1` (five-role, Researcher done, Planner cancelled at 3 min) are
PARKED with `scratchpad/park-restart-window.txt`. Only `5210c79e`'s gate (attempt 5210c79e-0-20260920133941118,
started 13:39Z) is in flight; when it returns (and lands if passed), the operator creates `.conduct-stop`, waits for
LOOP_STOP plus the supervisor successor's tick-0 LOOP_STOP, confirms no dotnet `conduct` process and no
`__acceptance-gate-attempt` child, then (with the daemon down) runs `sqlite3 .orchestrator\run-events.db "VACUUM"`:
that store is 685 MB with 163,195 of 175,175 pages FREE (rows are deleted but never vacuumed; only 22,209 live rows),
so the vacuum returns about 640 MB of disk; state.db has only 7,907 free pages of 203,462, leave it. Then restarts
Windows. The two heap dumps for d96a70f4 are already taken (13:55Z and 15:24Z, annotated on the item).

**After the reboot, in this order:**
1. Confirm `.conduct-stop` is still present, then remove it. Confirm `.orchestrator\conduct-loop.lock` is absent.
2. Relaunch with `scratchpad\launch-loop-240min-filepolicy-cap.ps1` (no --policy; policy file now has
   maxConcurrentPaidWorkers 4; --max-duration 14400; DOTNET_GCConserveMemory=5 experiment). Read LOOP_START for
   `configuredWorkerCap=4` and the binary git-head equal to main HEAD (b3da0320's owner mapping and eb5133d1's sweep
   timers become live here).
3. `unpark-goal 743ff0ea`, `unpark-goal 011dbb7c` (they should form one cohort gate), then `unpark-goal 98b82ef1`
   (Planner re-dispatches). Confirm the gate starts. `5210c79e` is ALSO parked: its gate (13:39Z to 15:49Z, 130 min)
   failed on two lane budget timeouts (Goal lifecycle commands and Goal worktree cleanup, both 40 m, no test red, no
   TRX) while a Developer round, a Planner and a 4 GB game shared the host; the auto Developer retry was cancelled at
   20 s by the park. After unpark it needs the mechanical reopen triple on its Reviewer task (cause
   EnvironmentApparatusFailure) so it re-gates on candidate 8ac3ad7e with no worker round; let it join a cohort.
4. Read ten sweep PHASE_TIMING lines for 2140d2d8 criterion 5 (eb5133d1 timers) and the daemon WS at LOOP_START and
   +2 h for the GCConserveMemory experiment.
5. Sol's disposable-directory analysis finished (saved at
   `.orchestrator/operator-evidence/disposable-directory-escapes-sol-20260920.md`, annotated on backlog b25053ee); cut
   its first slice (owned-root registry for runs\p<pid>-build-* and mcg-run\v2) after 98b82ef1 is in its gate.
6. Re-arm the loop monitor: `Monitor` on `bash scratchpad/poll-conductor2.sh` (persistent). It was stopped at 16:03Z
   because the loop is down on purpose for the restart.

## Earlier — 2026-09-20 04:15 UTC (Claude)

**Three more landings, then a bounce.** `1711c156` (terminal-journal cache) gate passed 03:27:50Z after 42 min and
landed as `6bda15bec`. `a319f2e0` (receipt-first Tester) and `ddf216bc` (precedent bound to answer revisions) ran
together in cohort gate cohort-v2-b5069bea (03:29Z to 04:08Z, ~39 min) and landed as `15100b59b` and `fc2f87305`.
Session total: 16 landings. With no worker or gate in flight I bounced the daemon per the runbook: `.conduct-stop`
04:10Z, LOOP_STOP tick 284 at 04:10:54Z (the daemon supervisor respawned one successor at 04:10:56Z which honoured
the stop file at tick 0 and released the lock), marker removed, relaunched with scratchpad
`launch-loop-120min-filepolicy.ps1` (no `--policy`, poll 120 s, max-duration 43200). New loop: launcher pid `39196`,
conductor pid `24788`, LOOP_START 04:12:47Z, log
`.orchestrator/logs/operator-conduct-loop-daemon-20260919231222.out.log`, binary git-head `15100b59b` = main HEAD.
Next max-duration renewal ~16:12Z.

**Criterion 6 of 1711c156 measured (04:12:51Z to 04:17:39Z, ticks 1 to 10 on the new binary):** sweep 4,466 /
5,610 / 6,422 / 8,793 / 5,562 / 8,404 / 6,229 / 7,874 / 5,708 / 5,732 ms, median 5,980 ms against the 13.6 s baseline
(56 percent lower); dependency_metadata_ms 30 to 38 and dependency_journals_read 0 on every tick, so the journal path
is solved. Above the 5 s line, so backlog `2140d2d8` stays Open with the numbers (annotated 04:18Z; receipts in
`.orchestrator/operator-evidence/1711c156-criterion6/sweep-ten-ticks.md`). Two undiagnosed observations recorded
there: sweep_cache_hits and sweep_cache_misses are 0 on every tick (the 96211ff6 cache is not consulted on this
board) and the sweep span has no sub-phase breakdown beyond dependency metadata. Next slice: per-sub-phase timers on
the sweep line (git fact index, worktree cleanup, blocker evaluation, terminalization) and read one tick before
optimizing; hypothesis is git spawns per goal. Cut it only after `743ff0ea` lands (shared files, and
ConductorBatchLoop.cs has 3 lines of ratchet headroom).

**`b3da0320` LANDED as `6c12b1333` (07:31Z; 17th landing):** refiner owner mapping by declared_index + raw refiner
output persisted beside the executor log (backlog `5e0b01a9` slice; five clarifications answered 03:55Z with
`ans-b3d-*.txt`; one Reviewer finding on the sidecar stamp fixed in round 2; gate 87 min). NOT live until the next
bounce or renewal (daemon still runs 15100b59b). Close test for 5e0b01a9 is in its 07:31Z annotation: the first
owner-tagged brief refined after the bounce must show non-empty owner lists in its snapshot and its Reviewer's
not-verifiable must defer.

**743ff0ea gate 2 FAILED too** (attempt 743ff0ea-0-20260920073031463, 07:30Z to 08:44Z, 74 min) on retry commit
`4397a9c1f`: down from 24 facts to 3, all "Completed after CLI acceptance" shapes
(CliCommandTestsPersistentRunnerCommandsAcceptance ...FinalStateIsPersistedByGoalCas, GoalWorktreeTestsRebaseMerge
...AcceptedRoutesStopHostMergeMarkLanded..., GoalWorktreeTestsRemoveCleanupLifecycleCommands ...StaysAccepted).
Mechanism from the diff: the candidate widened the projector's early return from `Status != Verified` to
`is not (Verified or Completed)`, so a just-landed Completed goal walks the candidate path, the journal outcome no
longer matches the moved main head, and IsAccepted flips false. The loop auto-routed the FINAL Developer retry (2/2)
at 08:44Z. If gate 3 fails the goal becomes AcceptanceFailed: retry the Developer with
`scratchpad/retry-743ff0ea-completed-status.txt` (`--cause NewTestFinding`), which names the guard to restore and the
whole classes to request. Retry 3 (`29ce6c2ea`, 08:56Z) took a different route: for a Completed goal whose branch
already landed it falls back to journal outcomes keyed on the branch head alone, at the cost of a GoalGitFactIndex.Build
(four git spawns) inside the projector for that case. Reviewer round 3 (08:56Z to 09:09Z) accepted the shape but
raised one residual item, "candidate-resolution-must-fail-closed", which is exactly the gate-1 mutation (force
IsAccepted false with no candidate) and contradicts criterion 5, so I cancelled the auto-opened Developer round
(ConfirmedUnchanged at 29ce6c2ea, 09:11Z), closed both tasks on the receipts (`close-743-dev.txt`, `close-743-rev.txt`,
four intents applied 09:13Z) and gate 3 ran 09:14Z to 10:44Z (attempt 743ff0ea-0-20260920091450802, 89 min). It
FAILED on two apparatus facts only: HermesExecutableIdentityTests (fixture git config 5 s budget, backlog `a1451eee`)
and GoalAcceptanceVerifier_gate_heartbeat_surfaces_hung_child_without_process_inspection (5 s wall-clock fact, backlog
`b0a6bad5`); the three CLI-acceptance facts from gate 2 PASSED, so the candidate's regression is closed. Escalated
"after 2 retries", so I submitted the mechanical reopen triple on the Reviewer task at 10:47Z
(`regate-743ff0ea-apparatus.txt`, cause EnvironmentApparatusFailure; intents 8fe1dd71, 6ddecf4c, a18c7215). Gate 4
(attempt 743ff0ea-0-20260920115939931) ran 11:59Z to 12:04Z and failed ONE apparatus fact in the cli lane:
CliGoalUnparkConflictTests.CancellationBeforeCommit_LeavesParkedStateUnchanged expected "" got "\r\n" (the bare-newline
console-capture bucket; the same lane was 77/77 green on this candidate in gate 3). Second mechanical reopen at 12:07Z
(`regate2-743ff0ea-console.txt`; intents 7980d3a0, fd48a4ab, fb0beac7, all Applied 12:09Z). Gate 5 (attempt
743ff0ea-0-20260920121105344, 12:11Z to 13:39Z) FAILED on acceptance-check-timeout only: Goal lifecycle commands lane
hit its 40 min budget with a 011dbb7c Developer round and a 4 GB game on the host; no test failed. Third mechanical
reopen 13:42Z (`regate3-743ff0ea-timeout.txt`). While the host carries a game plus a worker round, EVERY gate's
lifecycle lane (20 to 28 min quiet, 70 min loaded) is at risk of the 40 min budget; `5210c79e`'s gate (attempt
5210c79e-0-20260920133941118, started 13:39:41Z) faces the same risk. `5210c79e` Developer `8ac3ad7e` (4 test files),
Reviewer passed on merits but held criterion 4's whole-class record; the record-only Developer round was rejected
as no-change-evidence and both tasks were hand-closed 13:37Z (`close-5210-both.txt`). `011dbb7c` Developer
`4911e10fc` (13:44Z, 3 files: liveness precheck in WorkerProcessJobs.TryKillOrFallback + 6 facts) and its attribution
CONTRADICTED the bdab2d75 premise for the sampled fact: 61.8 s with ZERO janitor lines, redirect 0.4 percent, the rest
is real per-fact git fixture work. Recorded on bdab2d75 at 13:46Z with the next attribution (instrument
WorkerDispatchTestSupport setup/dispose steps; likely remedy a per-class bare template repo cloned per fact). The
precheck still lands as a correct small change; do not expect it to move the lane. `eb5133d1` LANDED as `1df8d72b5` at ~12:00Z (gate 75 min; 18th landing). After 743ff0ea lands: bounce
(makes b3da0320 owner mapping and eb5133d1 sweep timers live), then read ten sweep lines for 2140d2d8 criterion 5 and
check the first owner-tagged refinement for 5e0b01a9's close test.

**`eb5133d1` created 09:04Z** (sweep sub-phase timers + git spawn count on the PHASE_TIMING line, backlog `2140d2d8`
slice, Developer+Reviewer, brief `scratchpad/brief-sweep-subphase-timing.md`). Measure-only; criterion 5 is the
operator's ten-tick reading after the next bounce. Its refinement ran on the OLD binary (b3da0320 not live), so expect
the operator-owned criterion to have no obligation record and the Reviewer's not-verifiable to need a hand-close on the
verdict, as with 1711c156. Regions of TerminalGoalSweep.cs overlap 743ff0ea's edits (lines 690 to 740 excluded in the
brief); expect a clean merge, check at pre-landing. Developer `7dd8fb31e` (5 files, 144 lines, ConductorBatchLoop.cs
5,190) at 09:47Z; Reviewer round 1 asked for zero-filled fields when the sweep returns null, Developer `642be9cb1`
10:16Z; Reviewer round 2 passed on merits 10:24Z but the attestation rejected its not-verifiable on criteria 3 and 5
(no owner obligations, refined on the old binary as predicted), hand-closed 10:26Z with `close-eb5-rev.txt` (intents
5aa9f2cd, 1f434495). Gate queues behind 743ff0ea's.

**12:10Z to 12:35Z, Miles asked "what else can we do to accelerate":** answered with the lane data
(`.orchestrator/acceptance-lane-durations.jsonl`: Goal lifecycle commands 20 to 28 min quiet, 70 to 73 min in the
07:15Z and 11:55Z gates; fixtures A 19 to 25 min quiet, 70 min at 07:15Z) and cut two goals: `5210c79e` (Hermes
fixture 5 s git budget + hung-child heartbeat wall-clock wait, backlogs a1451eee + b0a6bad5, test-only) and
`011dbb7c` (fixtures A per-fact teardown: fallback-kill against fake pid 999999 snapshots and reaps four temp roots,
backlog bdab2d75 slice). Both Developer+Reviewer, briefs `brief-gate-apparatus-flakes.md` and
`brief-fixtures-a-teardown.md`. Also on Miles's instruction stopped Ollama entirely (tray app 27500, `ollama serve`
16100 then its restart 31072, runners 41908 and 34564): the orchestrator's `acceptance-judge` model function is bound
to Ollama qwen3:8b, so every gate start loaded a 6.4 GB runner; the judge is advisory (invalid verdict on failure), so
gates continue. Commit fell from 67 to 61 GB.

**13:53Z to 14:10Z, memory and disk work on Miles's direction:** (1) `conductor-policy.json` maxConcurrentPaidWorkers
5 -> 4 (effective at next launch; Miles wants 4 first, 3 later if it makes sense). (2) New launcher
`scratchpad/launch-loop-240min-filepolicy-cap.ps1`: no --policy, watchdog 120 min, --max-duration 14400 (4 h renewals
against the d96a70f4 growth), DOTNET_GCConserveMemory=5 experiment; MCG_BUILD_MAXCPUCOUNT deliberately unset because
Invoke-IsolatedDotnet.ps1 already defaults worker builds to 1 node while the gate's DotnetBuildEnvironmentManager uses
max(2, 24 cores / 2) = 12 nodes from the SAME variable, so a gate-only cap needs a code change. (3) d96a70f4 first
measurement: `dotnet-gcdump collect -p 24788` at 13:55Z (`scratchpad/daemon-24788-1355Z.gcdump`, 64 MB): live managed
heap 1.32 GB / 3.47 M objects inside a 2.85 GB working set (handles 554, so no connection leak); ~1.5 GB is committed
GC segments and native. Take a second dump ~15:00Z and diff. (4) Subagent reading of the build architecture (why builds
cannot share a compiler server): UseSharedCompilation=false in Directory.Build.props line 6 and -nodeReuse:false in
Directory.Build.rsp since 2026-06-15 (7ce2a5f1c, e133bd545, CS2012 obj locks); `dotnet build-server shutdown` fired on
every lease Dispose (DotnetBuildEnvironmentManager.cs 515-541, 2731) kills the other lane's compilers; 2-slot lock
grid (line 98); all lanes share DOTNET_CLI_HOME and NUGET_PACKAGES (GoalAcceptanceVerifier.cs 8167-8219). Each build
pays a cold ~3 GB MSBuild node with in-proc Roslyn. Feasible fix: private DOTNET_CLI_HOME per lane, re-enable shared
compilation, retire the global shutdown; a brief is owed. (5) Disk (895 GB used, 35 GB free before): removed 284
orphaned `mcg-dotnet-isolated\runs\p<pid>-build-*` dirs (dead pids, >24 h) = 15.0 GB and 49 stale `Temp\mcg-run\v2`
staged binaries (>36 h, not in use) = 1.9 GB with `Reclaim-OrphanedRuns.ps1` / `Reclaim-StagedBinaries.ps1` (both
have dry-run default and -Apply). Remaining consumers: acceptance-gate-attempts 5.3 GB, operator-evidence 3.3 GB,
logs 2.0 GB, worktrees 8.5 GB (34 vs 22 loaded goals), base-build-cache 1.2 GB, goals roots of landed goals reclaimed
by the sweep itself after backoff. `goal-recovery` and `readiness` run a repairing sweep (memory written).

**`98b82ef1` created 14:21Z (shared Roslyn compiler server for isolated builds, retire the session-global
build-server shutdown, private DOTNET_CLI_HOME per lane, MCG_GATE_BUILD_MAXCPUCOUNT; backlog `43327f1a`, FIVE-ROLE
because CS2012 history 7ce2a5f1c/e133bd545 must be re-established by the Researcher first; brief
`scratchpad/brief-shared-compiler-server.md`). Gate-code change: land alone, bounce after. GoalAcceptanceVerifier.cs
is AT its ratchet ceiling (9,649/9,649), so its edits must be net-zero or extracted to a partial.

**Stale board items surfaced by the scope advisory (not tonight's work, triage later):** `a3e78f34` (wall-clock
assertion sweep slice) is Verified since 09-15 with NO goal branch left, so it can never land and should be abandoned
or re-cut; `643fbd9a` (recycled parent-PID edges) escalated at Failed, 569 commits behind, Developer failed on two
unchanged cohort tests; `9bea7a9a` (cohort fairness remainder) waits on parked dependency `cf64014b` since 09-03.

**Next gate-speed cut (after the bounce):** backlog `bdab2d75`, the two lanes that run at the 40-minute budget
(Worker dispatch fixtures A = one 4,843-line serial class; Goal lifecycle commands = seven CLI-spawning classes).
Tonight's gates: 42, 39 (cohort), 86, 87, 74 min, the long ones with two worker rounds beside them and host commit at
93 percent. Its direction is attribute-then-remove per-fact teardown cost, not a budget raise.

**743ff0ea gate 1 FAILED (04:37Z to 06:03Z, 86 min, real regression):** Developer `73e0d1a3f` (9 files) passed
review at 04:37Z with evidence "executor/sweep 6/6, LandingDecisionTests 20/20" (only the new facts, not the whole
LandingExecutorTests class). The gate then failed 24 distinct facts across LandingExecutorTests (remote mirror, landing
intent, ownership hold, backlog proposal), GoalWorktreeTestsRebaseMerge, GoalWorktreeTestsRemoveCleanupLifecycleCommands,
GoalLifecycleEventWriterTests and CliCommandTestsPersistentRunnerCommandsAcceptance ("Goal ... acceptance: accepted"
not printed), all shapes where acceptance PASSED and the candidate should land or hold on ownership: one root cause,
the new hold description or IsAccepted misfires on the passed case. RepositorySourceInventoryTests
GitInventorySkipsTrackedFileThroughOutsideJunction also failed once (probably unrelated; check its history before
blaming the change). The loop auto-routed Developer retry 1/2 at 06:04Z (pid 2632) with the TRX evidence; b3da0320
took the freed gate slot (attempt b3da0320-0-20260920060357604, started 06:03:57Z). Lesson for the next brief: require
the Developer to request the WHOLE touched test class as focused evidence before review, not the new facts.

**In flight:** `743ff0ea` (landing hold names the failing IsAccepted term, backlog `1822ddfe`, Developer+Reviewer,
brief `scratchpad/brief-landing-hold-names-the-term.md`). Refiner raised five clarifications at 03:53Z (grammar,
id ordering, multi-kind rendering, sweep evidence extend-vs-replace, semantics unchanged); all answered 03:55Z with
text files `ans-743-*.txt`. A re-refinement ran 04:07:44Z, yet the loop still holds it with
`SPEC_REFINEMENT_PENDING ... executor-launch-deferred-cadence` on the new daemon too (the cadence is persisted, a
bounce does not reset it, backlog `8c11fd0d`). Expect the executor at ~04:22Z; if the hold loops past two cadences,
annotate 8c11fd0d with the goal-events messages and hand-drive the refinement.

**ddf216bc criterion 4 receipts** are at `.orchestrator/operator-evidence/ddf216bc-criterion4/receipts.md`. Disposable
workspace `C:\Users\miles\mcg-pub\ws-ddf` driven only through public verbs with the candidate dll built from
`5bf7e8631` (scratchpad `Run-Ddf-Cli.ps1` sets `MCG_ORCHESTRATOR_REPOSITORY_ROOT`): goal `4b70f41d` (operator-notes
doc) answered poll-interval-unit A "seconds" (answer `090ddbf9`) 03:01:38Z, `supersede` to B "minutes" (`9406b4ae`)
03:02:21Z; the precedent store entry was replaced to reference answer B; the item's answer_history keeps A retracted
with supersededByAnswerId = B; goal `e46d96a1` (identical brief, operator-notes-2) refined 03:06Z and its stored
decision chose B with rationale "Authoritative clarification precedent (topic: poll-interval-unit, item: 0dd54e50...,
answer: 9406b4ae..., brief: 1)" without re-asking, while its three unrelated topics did not inherit B. The ws-ddf goals
are left as they are (e46d96a1 has three Raised clarifications; no loop runs there).

**Incident 03:06Z:** I typed `goal show ddf216bc` (the verb is `status`), which CREATED five-role goal `89a35fdf`
("show ddf216bc"). The loop refined it and escalated AwaitingClarification at 03:09Z; no worker was dispatched. Abandoned
with `abandon-goal ... --confirm-goal-abandon` at 03:12Z (Cancelled). Lesson: `goal <text>` is a creator.

**Backlog `5e0b01a9` annotated 03:25Z with the owner-drop mechanism (hypothesis, raw refiner output is not persisted):**
1711c156's snapshot has the seven brief criteria verbatim and BOTH owner lists empty with no parse diagnostic.
`GoalRefinementService.ResolveAcceptanceCriteria` (line 804) keeps the brief's declared criteria and discards the
refiner's text, then `ResolveOwnedCriteria` (line 1285) keeps an owner only on EXACT trimmed text equality with the
refiner's own criterion text, so any paraphrase drops the owner silently. The existing fact
(GateOwnedCriterionRefinementTests) only echoes identical text. Successor slice direction is in the annotation
(persist raw output, map owners by declared index, loud diagnostic, paraphrase fact). Do NOT cut that goal until
`ddf216bc` lands: it touches GoalRefinementService.cs (195-line diff on that branch).

**Still owed after landings:** ten sweep PHASE_TIMING lines after 1711c156 lands and the next renewal (~09:53Z) or a
bounce (criterion 6: median under 3 s, journals_read 0 steady state; above 5 s reopens `2140d2d8`). Daemon 16204 was
3.28 GB working set at 03:19Z; host commit 61.4 of 73.2 GB.

## Earlier — 2026-09-20 02:47 UTC (Claude)

**Three goals converging on the single gate slot:**
- `1711c156` (terminal-journal cache, backlog `2140d2d8`): Developer `fe578fbb4` in 27 minutes (92-line cache class,
  runner calls it at the two-boolean seam, `dependency_journals_read` on the sweep line, 210 lines of facts, negative
  controls), Reviewer pass at 02:30Z vetoed on the operator-owned criterion (hand-closed; the new criterion-evidence-map
  diagnostic fired, meaning refinement recorded no owner for a criterion whose text says operator-owned, so backlog
  `5e0b01a9` is REOPENED with that receipt). First gate red at 02:39Z was one bare-newline console-capture fact
  (CliGoalUnparkConcurrentWriterTests, 181 passed / 1 failed in history, candidate adds no Console writes); the
  auto Developer round was cancelled ConfirmedUnchanged and the goal re-gated mechanically at 02:45Z.
- `a319f2e0` (receipt-first Tester procedure): Developer rounds `b86d7db06` and `231b11e91`, Tester pass, Reviewer
  pass vetoed on operator-owned criterion 3 (replays bound to an older candidate). Operator re-ran the four bounded
  Opus replays at 231b11e91 (`.orchestrator/operator-evidence/a319f2e0-criterion3-231b11e9/`, guidance rendered by
  reflection from the candidate's Core assembly): matching-pass closes on the receipt, stale-pass rejects and requests
  one run, matching-timeout stays inconclusive without an identical re-request (the full-contract variant narrows to
  four Name~ partitions covering all 17 facts). Three invalid runs are marked in replay.log. Reviewer hand-closed
  02:46Z; gate next.
- `ddf216bc` (clarification precedent bound to answer revisions): Developer verified the merged tree unchanged
  (93/93) and committed nothing, which the dispatch classifier counted as a failed round (hand-closed on the receipt);
  Tester passed 02:45Z; Reviewer next.
No open human waits on any of the three (checked with `attention show`). Daemon 16204 at 3.4 GB working set
(backlog `d96a70f4`).

## Earlier — 2026-09-20 01:52 UTC (Claude)

**Three goals put back in flight at 01:45Z to 01:51Z (Miles: nothing is left for a next session):**
- `1711c156` **Stop the per-tick sweep from re-reading every terminal goal's operation journal** (backlog `2140d2d8`,
  Developer+Reviewer). Mechanism verified at HEAD: LoadConductLoopKernel maps ReadConductLoopDependencyMetadata over
  every terminal summary and each call does GoalOperationJournal.Read on `.orchestrator/goal-operations/<id>.jsonl`
  with no cache (824 live files, 587 MB with archive, about 1,240 terminal goals) to derive two stable booleans. Fix
  shape: per-process cache keyed on journal file identity in a new file, plus `dependency_journals_read` and
  `dependency_metadata_ms` on the sweep PHASE_TIMING line. Ratchet headroom is 7 lines in CliPersistentStateRunner.cs
  and 5 in ConductorBatchLoop.cs. Criterion 6 is mine after landing (median under 3 s over ten ticks, above 5 s reopens).
- `a319f2e0` receipt-first Tester decision procedure (backlog `effa0696`): operator merged main with two conflicts
  resolved (both receipt-first contracts kept above main's evidence_index line; both Tester requirement facts kept),
  commit `e720d2ca1`; routed Developer retry warns that the compact-budget fact may fail and says to shorten the
  branch's compact contract strings, not main's line.
- `ddf216bc` reusable clarification precedent bound to answer revisions (backlog `8a59a34f`): operator merged main
  cleanly (`5bf7e8631`), bounded build 0 errors; routed Developer retry to run GoalRefinementTests and
  CliCommandTestsHumanInputSupersede on the merged tree and reconcile with da3d19ed's evidence_owner field.
Backlog `d96a70f4` filed for the daemon working-set growth (samples in the body).

## Earlier — 2026-09-20 01:10 UTC (Claude)

**Board was clear of active work.** `ac61f820` LANDED 01:08Z as `cc9167aec`, the thirteenth landing of the
session (headless runtime with optional dashboard host, 124 files). Its two earlier green gates (20:36Z, 21:57Z)
were refused as "acceptance verification not passed" by a hidden term: three Planner ProspectiveAcceptanceEvidence
waits for the operator-owned criterion, open 17 hours, visible only in `attention show`; answered 22:01Z, then a
mechanical reopen (2.5-minute receipt-reusing gate) landed it. Backlog `1822ddfe`, memory
`open-planner-evidence-wait-blocks-landing-as-not-passed`. Prototype dashboard and Edge stopped.

**Loop:** successor pid `16204` since the 21:53Z max-duration renewal, staged `9d2c8a4af`, so main's landings
through da3d19ed are the running binary (ac61f820's landing is not; next renewal about 09:53Z 09-20 picks it
up). Daemon working set 3.2 GB after 3 hours.

**Metadata scan (backlog `2140d2d8`) REOPENED 01:03Z by criterion 5's own rule:** sweep phase median 13.6 s over
60 ticks (11.8 to 18.7 s) on the new binary against 22 to 38 s before. The terminal-blob parse is gone; another
sweep-phase reader still costs about 13 s per tick with 24 goals. Next step is a PHASE_TIMING breakdown inside
the sweep phase (or a one-tick dotnet-trace of pid 16204) before any code change; candidates are the
terminal-goal sweep's per-goal reads and the dependency metadata reload.

**Defender:** Miles added path exclusions for `<repo>\.orchestrator` and `%LOCALAPPDATA%\Temp\mcg-run` at
01:01Z (elevated). Not yet measured against a full gate; the only gate since reused receipts (2.5 minutes).
Process exclusions (git.exe, dotnet.exe, testhost.exe) offered as an optional second step.

**Backlog filed today:** `4d1cf58c` browser smoke check preconditions, `a1451eee` HermesIdentity fixture,
`1822ddfe` hidden human-wait landing refusal. Closed: `5e0b01a9`, `2140d2d8` (then reopened), `bfe0778a`.
Stale Active goals worth a decision next session: a319f2e0 and ddf216bc (Failed Developers, 328 and 386 commits
behind, scopes overlap landed work).

## Earlier — 2026-09-19 19:27 UTC (Claude)

**Since 16:56Z:** `da3d19ed` (gate-owned criteria as obligations) LANDED 17:47Z as `9d2c8a4af`, the twelfth
landing; backlog `5e0b01a9` closed by hand (its owner was the abandoned 28292ab8). `ac61f820` is the last goal on
the board: its 18:06Z gate failed one fact again (TaskAndTasks_PreserveOutputAndTargetSelection, main baseline
green), mechanism found from the lane TRX: the branch's new RuntimeMaintenanceOwnershipTests runs a real
ConductorBatchLoop tick in the Cli test project and its Console.WriteLine output lands inside a parallel test's
console capture; Developer round 10 (`912f2ca48`) put the five console-touching Cli classes in one xunit
collection. The branch was squashed onto main (`a971ce5cc`, tag `salvage/ac61f820-presquash`) after a
pre-landing rebase conflict from operator merge commits; Reviewer round 6 passed 19:20Z (vetoed only on the
operator-owned criterion, hand-closed; the loop's redundant seventh Reviewer dispatch was cancelled). Criterion-4
receipts parts 1 to 4 are in `.orchestrator/operator-evidence/ac61f820-criterion4/receipts.log` (part 4 at the
final candidate: static dashboard/transcript exit 78 headless-only and render from the combined layout,
`dashboard --mode local` launches the host, teardown clean). The prototype dashboard is served on 5087 and Edge
DevTools is warm on 9222 for its gate; stop both after the gate (`Get-Process msedge` started 12:52 local,
the pwsh `Serve-SmokeDashboard.ps1` task). Backlog filed: `4d1cf58c` browser smoke check preconditions,
`a1451eee` HermesIdentity fixture. After ac61f820 lands or parks, the board is empty of workers: bounce the
daemon (staged 209400a8b, three landings behind) and measure the sweep phase over ten ticks for `2140d2d8`.

## Earlier — 2026-09-19 16:56 UTC (Claude)

**Since 14:05Z:** `96211ff6` (metadata scan) LANDED 16:52Z as `54756bc36`, the eleventh landing; backlog `2140d2d8`
is Done but its criterion 5 (ten-tick sweep median, baseline 22 to 38 s) is still the operator's after the next
bounce or the 21:50Z renewal. Its Reviewer pass was vetoed on the operator-owned criterion (hand-closed), and its
first gate red (HermesIdentity fixture cleanup, backlog `a1451eee`) auto-opened a Developer round that was
cancelled ConfirmedUnchanged and re-gated mechanically. `da3d19ed` (gate-owned criteria as obligations) passed
Reviewer round 3 at 16:55Z and is in its gate on `d284c6698`; its Tester looped on evidence-on-demand twice
(cancel + close on receipts, backlog `a2d5c16c`). `ac61f820`: the 14:05Z gate failed on a real inventory gap
(operator commit `efb31426f`) and the browser smoke check, which had never run in retained history and needs an
operator-served PROTOTYPE dashboard on 5087 plus a 127.0.0.1 DevTools probe (memory
`dashboard-browser-smoke-check-needs-prototype-dashboard-and-ipv4-probe`, backlog `4d1cf58c`, harness fix
`f29d68048`); the 14:32Z gate then failed on a real regression (static `dashboard`/`transcript` routed to the
missing sibling component) fixed by Developer round 8 (`b2d353b09`: static render through the dashboard assembly
when present, exit 78 otherwise, capability table corrected); Tester passed 16:47Z, Reviewer running. Serve the
prototype dashboard again before its next gate (scratchpad `Serve-SmokeDashboard.ps1`). Host memory hit 90 percent
commit twice (Miles's apps about 13 GB); the harness killed background waiters, so the loop monitor is the only
reliable wake source.

## Earlier — 2026-09-19 14:05 UTC (Claude)

**Direction (Miles, 09-19 13:20Z):** nothing on the board needs his call except elevation (Defender exclusion for
`<repo>\.orchestrator` and process exclusions; pagefile or host memory headroom); the metadata-scan fix is the
priority. Earlier (09-18): take care of it all; lane timeouts are the priority; no wind-down; experiment
directories deleted (about 20 GB); write audit done (backlog B1 to B6, `.orchestrator/operator-evidence/write-audit-2026-09-18.md`).

**State at 14:05Z (main `b1a4a0630`, ten landings this session):**
- `96211ff6` **Stop the per-tick goal metadata scan from parsing terminal snapshots** (backlog `2140d2d8`, the
  priority): Developer+Reviewer pipeline, Developer running since 14:03Z on `62387da9a`. Decisions given in its
  clarifications: CASE-gate the three json_each subqueries (no column, no schema change, no backfill); prove
  "terminal blobs never parsed" with malformed-JSON sentinels for terminal rows (json_each throws on touch);
  negative-controls file `docs/negative-controls/96211ff6.md`. Criterion 5 is mine after landing: median
  PHASE_TIMING `phase=sweep` over ten ticks against the 19 to 29 s baseline (ticks 128 to 137); expect under 2 s.
  `CliPersistentStateRunner.cs` sits exactly at its 4,863-line ratchet ceiling; the brief says net-zero there.
- `da3d19ed` **Record gate-owned criteria as obligations at refinement** (backlog `5e0b01a9`; recut of `28292ab8`,
  abandoned 13:44Z with tag `salvage/28292ab8-recut` = e99b08817): five-role, refinement completed 13:56Z, waiting
  on the executor cadence for dispatch. HEAD finding on the item: Recording.cs already defers non-worker-owned
  not-verifiable verdicts (faf947696, 09-09) but nothing creates an Acceptance-owned obligation at refinement
  (Goal.cs EnsureCriterionEvidenceObligations maps only OperatorOwnedAcceptanceCriteria), so "Acceptance executes"
  criteria still fail honest Reviewer rounds. Answers given: no backfill of old spec versions; a `met` verdict on a
  gate-owned criterion is advisory only. Scope collisions flagged with stale goals a319f2e0 and ddf216bc (both
  Failed, 328 and 386 commits behind); no dependency added.
- `ac61f820` headless runtime with optional dashboard: criterion 4 run by the operator 13:56Z to 14:01Z, receipts in
  `.orchestrator/operator-evidence/ac61f820-criterion4/` (headless 59 files 43.4 MB without ASP.NET, dashboard 65
  files, zero shared-byte differences when combined; headless-only `dashboard` exits 78 with state unchanged;
  hosted UI child Dashboard.exe dies with its App.exe parent within 3 s; successor self-check LOOP_START from a
  sealed run directory in 209 ms; legacy single-file 113 MB, working set about 11 MB higher, wall time within
  noise). Reviewer closed on the receipts, branch merged to main twice (`571a840bf` resolved GoalDispatchResults.cs
  by making main's assignment-hold records public; `bddaa0a1b` clean), unparked 14:04Z; **confirm its gate
  started** (ahead 10, 117 files).
- `03aaf13e` LANDED 13:53Z as `b1a4a0630` on its fourth gate (11.5 minutes verifier, 470 s longest lane, host
  quiet) after three apparatus reds; the third (13:07Z) was the git-to-filesystem-fallback flake, sixth receipt on
  backlog `98c02c6b`. Canary passed at 13:54Z. The landed gate code (owned build storage root) is NOT in the
  running binary until the next renewal or a bounce.
- `90e4a429` Completed 13:23Z after the operator reset its worktree to main (tag `salvage/90e4a429-prelanded`).
- Deletions done 13:26Z: 21 operator worktrees (about 3.1 GB), 56 `mcg-tests\summary-*` (2,003 MB), 994
  `%TEMP%\mcg-acc-*` (75 MB); `git worktree prune` run.

**Bounce owed, when nothing is in flight:** the running daemon (pid 43736, staged 209400a8b) predates the
5fac0245, 03aaf13e landings; renewal is about 21:50Z. A quiet window (no worker, no gate) before then is worth a
manual bounce so gates run the new build-root and canary code.

**Root cause found and fixed on the host (09-18 21:22Z):** Windows Defender on-access scanning of the test temp
roots serialised process creation host-wide whenever several spawn-heavy lanes ran together (fixtures A: 9m43s
alone, 34m52s with three peers, 10m29s with the exclusions). Miles added path exclusions (elevated) for
`%LOCALAPPDATA%\Temp\Low\mcg-tests`, `%LOCALAPPDATA%\Temp\mcg-hvp`, `%LOCALAPPDATA%\..\LocalLow\mcg-dotnet-isolated`
and `.orchestrator-worktrees`. Still scanned: `<repo>\.orchestrator` (attempt artifacts); MsMpEng sat at about
50 percent CPU during real gates, so that is the next exclusion to propose. Backlog `478f5f0f` makes the
exclusions host setup.

**Loop:** daemon pid `43736`, the max-duration successor of pid 42796, LOOP_READY 09:50:48Z 09-19 and LOOP_START
09:51:42Z with `policySource=file:` (`acceptanceWidth: 1`), `stagedSourceCommit=209400a8b` = main, so tonight's
landed loop and gate code IS the running binary (no bounce owed); next renewal about 21:50Z 09-19. Log is still
`.orchestrator/logs/operator-conduct-loop-daemon-20260918164853.out.log`. **The renewal discarded the inflight
cohort gate** (5fac0245 + a3b2c4ad, 108 minutes in; test hosts and the ac61f820 Developer round died with the old
daemon; backlog `27edd359`). The successor's tick 1 took acceptance lease `merge-train-v1-47a9f72e…` and runs it
INLINE (members per its plan: 31fa5aec, a3b2c4ad, 90e4a429): the tick is blocked for the gate's duration,
conduct-events.log is silent (monitor says STALL), and PHASE_PROGRESS in the daemon stdout is the liveness.
No intent applies and no worker reconciles until it finishes; do not bounce. Host: 47.9 GB RAM, commit limit
73.7 GB; at 05:41Z commit was 65.6 GB with Miles's apps holding about 12 GB and the harness killed a background
task for low memory; `dotnet build-server shutdown` freed 1.9 GB. Check `Get-HostMemory.ps1` (scratchpad) first.

**Landed tonight (main `209400a8b`):** 29423867 typed worker context, e389f7df typed operator-intent receipt,
b885eda9 land refused candidates only from bound main (02:33Z), 36b98d7a one active acceptance owner per goal
across cohort gates (03:36Z), 6032a3d4 explicit execution-context and teardown ownership for acceptance attempts
(04:37Z, 69 files, gate code).

**In flight at 09:10Z (all evidence in each goal's card):**
- **Merge train landed at 10:30Z** (the successor's first inline gate, 38 minutes for three members, receipt
  `merge-train-receipt-47a9f72e…`): `31fa5aec` preserve provider-interrupted Developer work, `a3b2c4ad`
  PostLandingCanaryTests in a non-parallel collection (the canary fix is on main), `90e4a429` producer artifact
  identity and shared retention. Main is `f79d5d444`; the train lands member commits linearly (no Integrate commit).
  31fa5aec and a3b2c4ad reached Completed; **90e4a429 fell from Recorded back to Verified** (four rebased commits,
  branch tip not an ancestor of main, sweep says "retirement required") and would have re-gated already-landed
  patches, so it is PARKED at 10:56Z with the landing receipt in its park reason; it needs a terminal state by hand
  (backlog `dca5bc38`; its work is on main, backlog `bfe0778a` is Done). Nine landings this session with 5fac0245.
- `03aaf13e` (owned build storage root for the acceptance verifier): its 10:31Z gate failed one fact, the split
  parity baseline missing the candidate's new storage-root fact (the candidate's own defect); the loop's automatic
  Developer round fixed the baseline in one file (candidate `0488f55155c4`, 11:33Z); the Tester then looped on the
  passing parity run (twice 2/2) and was closed on the receipts at 11:45Z; Reviewer round 3 (11:59Z) `verdict: pass`,
  `blockers: none`, criteria 1 to 4 met, criteria 0 and 5 vetoed as gate-owned; hand-closed 12:01Z. Its 12:16Z gate
  failed in six minutes on one Core fact (AssemblyTempRootChildProcessTests.KilledProcessResidueIsReapedByNextStartup,
  sharing violation on its own handshake file; the candidate does not touch Core.Tests) and the loop's second and last
  automatic Developer retry fired; cancelled (ConfirmedUnchanged) and re-gated with seven intents. **Re-gate started
  12:39Z** (attempt `03aaf13e-0-20260919123952068`), the last goal in the queue; when it lands the board holds only
  parked goals and the daemon idles in watch mode until its 21:50Z renewal. Its acceptance auto-retries are exhausted:
  another red needs the same cancel-then-seven-intents sequence.
- **`5fac0245` LANDED 12:16Z** as `9e5db1e1d` (honor acknowledged agent reassignment at the next dispatch), the ninth
  landing of the session: pre-landing rebase conflict against the train's main at 10:30Z (TaskSnapshot trailing
  parameters from 31fa5aec) resolved by operator merge, build (0 errors) and squash to `baa5eadb2`; the 11:25Z gate
  passed every lane on the first complete run of this candidate (its 06:44Z attempt lost four hosts' temp roots and
  its 08:01Z cohort attempt died at the renewal).
- `ac61f820` (headless runtime with optional dashboard host): Developer round 3 committed 64 files at 08:58Z
  (the public application facade its round-2 blocker called for), round 4 fixed the scheduler-sweep facts, Tester
  passed 09:28Z, Reviewer round 1 (09:41Z) left two blockers (inert publish inventory guard under single-file
  publish; narrowed catch filter lets an ambiguous goal prefix escape monitor-goal). Round 5 was killed by the
  09:49Z handoff with its four files uncommitted; the operator checkpointed them as `1482d4119` on goal/ac61f820
  (they address both blockers). The successor accepted the checkpoint as the round's output; Reviewer round 2
  raised one more blocker (ungated error catch in monitor-goal), round 6 fixed it, Tester passed 11:17Z, Reviewer
  round 3 at 11:22Z: `verdict: pass`, `blockers: none`, criteria 1 to 3 met, criterion 4 not-verifiable because it
  is REAL-WORLD-DEPENDENT and operator-owned. **PARKED 11:24Z at candidate `3b7779b21bb7`** with the operator
  checklist in its park reason (isolated headless and dashboard publishes via the branch's `scripts/publish-*.ps1`,
  disposable no-UI workflow, UI open/close during it, launcher/successor staging on the new layout, identities,
  exits, child ownership, inventory, cold/warm startup and RSS under matched conditions). Do not close the Reviewer
  on its verdict for this one: that would land a deployment layout the brief says must be measured first. When the
  receipts exist, attach them, `progress`+`verify-manual` task 5 citing them, unpark, gate.
- `28292ab8` (make acceptance obligations truthful across stages) PARKED 02:43Z pending Miles: 343 commits behind,
  a trial merge produced 13 conflict hunks in 9 files, three semantic (its extracted CLI acceptance finalizer vs
  main's evolved inline transaction; its integration-branch landing flow vs what b885eda9 landed). Recommendation:
  abandon and recut from backlog `5e0b01a9` on current main naming the salvageable parts; the worktree is clean at
  e99b08817.

**Apparatus reds tonight and how they were closed (all mechanical re-gates, none a worker round):**
- Canary `retained-capture-incomplete` (foreign pwsh/cmd/dotnet/git child holds an inherited capture handle):
  11 hits since 09-15, five tonight (5fac0245 00:32Z, b885eda9 01:28Z, 31fa5aec 05:30Z and 07:58Z). Backlog
  `d9c6e10e` has the mechanism (OwnedProcessGroup.cs:726-731) and the two fix halves; `a3b2c4ad` is the test-host
  half and is in the running cohort. Memory `canary-retained-capture-incomplete-is-a-foreign-inherited-handle`.
- Temp roots of live gate test hosts vanished under running git (06:23Z, four hosts, 26 facts; 07:34Z, one host):
  backlog `badb8ea5`. Not attributable today because TempRootJanitor and the assembly cleanup key roots by pid
  alone and write no deletion receipt. Open hypothesis with its check on the item: both events fell inside
  90e4a429's StorageRetentionMaintenanceTests executions; StorageRetentionMaintenance.cs:246 falls back to the
  real profile when LOCALAPPDATA is empty.
- 6032a3d4's gate failed twice on three deterministic tests introduced by its own refactor (prefix no longer
  threaded through the environment); Developer round 4 fixed them in two files.
- Reviewer gate-owned veto (criterion "not-verifiable" because the gate executes it): hand-closed on 6032a3d4 six
  times and on 03aaf13e once. Backlog `5e0b01a9` is the fix; 28292ab8 was its goal.
- Tester evidence-on-demand loop (backlog `a2d5c16c`): two more shapes tonight on 90e4a429, five and two rounds.
  Break it with `cancel-dispatch --goal <g> 4`, then `progress`+`verify-manual` on the passing receipt.
- Planner output contract rejected all three first-round plans after 6032a3d4 landed (one heading-style mapping,
  two citation paths). Annotated `e2fc9db6`; filed `eece9c19` (Planner prompt lacks the repository-root citation
  rule). Scratchpad `Check-PlanCitations.ps1 -LogPath <planner out.log>` lists unresolved citations with candidates.
- Branches carrying operator merge commits fail the pre-landing rebase; squash onto main first (salvage tag, then
  `reset --soft main` and one commit) and re-run `git merge main` if main moved in between (31fa5aec's first squash
  silently dropped the 36b98d7a landing until redone).

**Backlog filed tonight:** `d9c6e10e` canary inherited handle; `eece9c19` Planner citation rule; `badb8ea5`
temp-root loss attribution; plus the six write-audit items (B1 to B6) and `478f5f0f` Defender exclusions.
Annotated: `d1a68bfa` (mechanism corrected), `e2fc9db6`, `a2d5c16c` twice, `d9c6e10e` three times, `badb8ea5`.

**Waiting on Miles (elevation only):** Defender exclusion for `<repo>\.orchestrator` and process exclusions
(elevated); host memory headroom for overnight runs (a larger pagefile or fewer desktop apps; commit charge hit
89 percent). Everything else listed here earlier (28292ab8 recut, ac61f820 criterion 4, 90e4a429 terminal state,
write-audit deletions) was done by the operator on 09-19 per his 13:20Z direction.

## Operating rules (verified this week; details in memory and `docs/operator-runbook.md`)

- **CLI:** `.\mcg-orchestrator.cmd <verb>`. `dotnet run --project …` fails state verbs with SQLite Error 14.
  Long text always via `--text-file` / `--brief-file`; the shell hook blocks `;`, `&&`, `||`, backticks, `$(...)`
  and heredocs, including inside grep patterns, so use the Grep tool for patterns that need them.
- **Gate width is policy, not parking:** launch WITHOUT `--policy` so `conductor-policy.json` (`acceptanceWidth: 1`)
  applies. Focused-evidence attempts count as live acceptance occupants and run beside the gate. Keep at least one
  dispatchable goal or a long `--max-duration`, or the daemon exits without a successor.
- **Never bounce with a worker or gate in flight.** `.conduct-stop` (repo root) detaches; relaunch with the
  scratchpad launcher or the runbook form.
- **Intents apply one per tick.** Poll every id (`poll-intent.ps1 -Ids a,b`) before reporting done. A retry on a
  Developer is rejected while its Tester runs: `cancel-dispatch --goal <g> <task#>` first. A cancel right after a
  dispatch yields one transient "stale dispatch recovery" escalation.
- **Mechanical reopen = intents in ONE response:** `retry <g> <last-task#> --text-file … --cause <cause> --mechanical`,
  then `progress`+`verify-manual` on EVERY task the retry reopened (a reopened Developer invalidates Tester and
  Reviewer too). Causes: EnvironmentApparatusFailure, MainDriftConflict (branch repair), NewTestFinding,
  NewSourceFinding, ContractClarification (Planner or worker contract repair), ProviderInterruption.
- **Apparatus RED:** read the TRX first (`Grep` for `outcome="Failed"` and `<Message>` in the attempt dir). Cancel the
  auto-reopened Developer within minutes (expect `CANCELLATION_CANDIDATE_EVIDENCE ConfirmedUnchanged`), then the
  mechanical reopen. A REAL defect in the candidate's own files: route the Developer with file, line, expected and
  actual, and a decision procedure.
- **Reviewer `verdict: pass` with a gate-owned criterion `not-verifiable` is vetoed.** Close the Reviewer task on
  its verdict with `progress`+`verify-manual`, after checking `blockers: none` in its out.log.
- **Pre-landing rebase conflict on a branch with merge commits:** squash (tag `salvage/<g>-presquash`, `git merge main`,
  `reset --soft main`, one commit, verify `git diff main --stat` shows only the goal's files), then mechanical reopen
  of the last task with `--cause MainDriftConflict`.
- **Goal creation:** `goal --brief-file <md> --backlog-item <id> --backlog-coverage full|slice
  [--pipeline developer-reviewer]`; the brief linter blocks 16 words (auth, authentication, authorization,
  credential(s), delete, destructive, migration, permission(s), production, rollback, secret(s), token) and
  `.git<non-alnum>`. Refiner clarifications: `attention show <g>`, answer by topic key.
- **Diagnosis:** liveness is the TICK line in the daemon stdout log, not conduct-events.log; `ls` prints local
  time, logs are UTC; filter attempt artifacts by attempt id; `WATCH_TRANSITION files=N` is the round's delta;
  the goal card answers state in seconds, `status <g>` gives the refined criteria, `task <g> <n>` the timeline.
- **Workers:** Developer and Tester are codex `gpt-5.6-sol`; Planner and Reviewer are claude-opus-5 (Reviewer
  sometimes openai gpt-5.6-terra). Test classes sit in the global namespace; a namespaced `FullyQualifiedName~`
  filter selects zero tests.
