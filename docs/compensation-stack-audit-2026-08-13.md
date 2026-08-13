# Compensation-stack audit, 2026-08-13

Audit of the code that **actually runs**, not of code nobody calls. "This is unreachable, delete it" costs
nothing to produce and changes nothing about the system being operated; it is the laziest available critique.
The question here is design and implementation quality in the hot path.

The lens is one phrase: **layers stacked, each compensating for the other.** Not "this is ugly" but
*mechanism X exists only to work around mechanism Y, which exists only because decision Z was wrong.* Name
the root, not the surface.

Two independent passes produced this: one by reading (Claude), one by an independent `gpt-5.6-sol` run at
high reasoning effort given the same brief with the first pass's leads marked explicitly as *claims to
verify or refute, not conclusions to agree with*. Three of the first pass's five leads were refuted with
citations. Those disagreements are preserved below rather than smoothed over, because the disagreements are
where the remaining uncertainty lives.

## The one-line diagnosis

The system repeatedly **throws away distinctions** — typed outcomes become text, transactions become
snapshots, resources become "slots", test boundaries become fully-qualified-name strings — and then spends
thousands of lines reconstructing those distinctions with heuristics, leases, caches, retries, markers and
repair passes.

Every stack below is an instance of that.

## Measured surface

    src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs               8790
    src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs                              5035
    src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs                           4596
    src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs              4499
    src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs                               3189
    src/Mcg.AgentOrchestrator.Infrastructure/Persistence/SqliteOrchestratorStateRepository.cs   2451
    src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs        2384
    src/Mcg.AgentOrchestrator.App/Orchestration/ConductorParallelAcceptanceAttempts.cs          2369
    src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs                     2307
    src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs                 2268

`GoalAcceptanceVerifier.cs` has 480 members in one file. Its vocabulary is dominated by leasing: `SlotIndex`
×119, `SlotLease` ×75, `Lease` ×46.

## Stack 1 — split authority over workflow state

Worst stack: it threatens correctness on every tick.

1. **Wrong decision at the bottom:** treat an in-memory aggregate snapshot as the workflow authority while
   other processes may mutate its database representation.
2. The conductor therefore cannot use the normal transaction path — its transaction would live for the whole
   loop and re-dispatch forever after a crash (`CliPersistentStateRunner.cs:127-134`, which states this
   explicitly).
3. So it carries per-tick baselines and checkpointed snapshots (`CliPersistentStateRunner.cs:887`, `:1010`).
4. So persistence performs a bespoke three-way merge between baseline, stored state and stale snapshot
   (`SqliteOrchestratorStateRepository.cs:657`, `:1547`), with every field manually classified tick-owned or
   store-owned (`:1573`, `:1705`).
5. So merge inconsistencies require reconstructing an entire kernel just to repair the resulting status
   (`:1620`).
6. So selected CLI mutations need a *second* state system — operator intents — to avoid colliding with the
   snapshot writer (`CliPersistentStateRunner.cs:83`, `:122`, `:205`).

**Right design:** SQLite is the authority. Each action is a typed command against one goal with an expected
version. Commit the state transition and append its domain event atomically. External work gets an `attempt`
row with epoch, owner, lease expiry and terminal outcome; the conductor claims one due transition
transactionally, does the work, and completes that exact attempt with compare-and-swap. Concurrent
transitions conflict explicitly rather than being field-spliced. Delete the three-way merge.

## Stack 2 — worker completion is destroyed, then reconstructed from debris

Most expensive worker-path stack: it causes paid retries and can convert success into failure and back.

1. **Wrong decision:** use one exit code plus free-form stdout/stderr to represent transport completion,
   provider failure, worker verdict, verification, human input and workspace outcome.
2. Add detached wrapper and child exit files (`BackgroundDispatchRunner.cs:263`).
3. Add heartbeats and hang detectors.
4. Parse semantic contracts back out of logs (`:1147`, `:1282`).
5. Inspect Git to decide whether the worker really succeeded; commit on its behalf (`:1332`).
6. Rewrite exit zero to failure when evidence is thin (`:1424`); rewrite non-zero to success when child,
   result and Git agree (`:1503`).
7. Encode typed orchestrator failures back into stderr as magic strings (`DispatchFailureClassifier.cs:71`)
   so a downstream classifier can rediscover them — which must then deliberately ignore `WORKER_RESULT`
   blocks so a model *quoting* an error does not become an error (`:2043`, `:2107`).

A flush race in this machinery previously misclassified ~21 successful workers (`:269`).

**Right design:** one atomic `DispatchCompletion` envelope with separate typed fields for transport,
provider, worker, workspace and artifact outcomes. Raw logs become referenced evidence, never decision input.
Provider adapters translate their own output once, at the boundary. Raw exits stay immutable. The state
machine assigns one semantic verdict from the envelope. The orchestrator always owns commits; workers only
edit their worktree.

## Stack 3 — the acceptance gate is a home-grown CI scheduler compensating for test topology

This is where 8,790 lines came from.

The manifest hand-partitions tests with long positive/negative FQN filters, hand-entered durations and global
resource keys (`config/acceptance-manifest.json:14`, `:41`, `:108`). The verifier implements longest-first
scheduling and its own exclusive-resource lock graph (`GoalAcceptanceVerifier.cs:1180`, `:1225`), re-runs
failed partitions once because races are expected (`:1422`), caches green partition verdicts across attempts
with a forced full rerun every fifth (`:1375`, `:2922`), and records full reruns at 20-25 minutes versus 2-7
cached (`:2981`). A second subsystem reopens TRX files to prove every discovered test was accounted for
(`TestCoverageInvariant.cs:255`).

**Root:** large assemblies mix process-spawning, environment-mutating and worktree-cleanup tests with pure
unit tests. Rather than making those boundaries physical, the gate discovers and schedules them with text
filters.

**Right design:** split test projects by actual resource boundary; remove process-wide mutable hooks from
ordinary tests; let project-level execution be the scheduling unit; run projects through a small matrix with
declared resource classes. Most of the lane compiler, resource-key scheduler, time balancing, retry policy
and cache journal then disappears.

## Stack 4 — "slot" is one number pretending to be three resources

`DotnetBuildEnvironmentManager.cs:93-95`:

    public const int BuildConcurrencySlotCount = 2;
    // Compatibility name for callers migrating from the former artifact-slot grid.
    public const int StableSlotCount = BuildConcurrencySlotCount;

One integer serves artifact identity, build concurrency, and whole-gate admission — and its cardinality is
inherited from a **retired** firewall-era mechanism, preserved under a compatibility alias with a single live
caller (`ConductorParallelAcceptanceAttempts.cs:360`).

**Correction, 2026-08-13, after this document first landed.** The sol pass claimed the coupling rests on a
false comment — that `ConductorBatchLoop.cs:30` pins gate parallelism to build concurrency on the stated
grounds that every gate holds a build slot, while `GoalAcceptanceVerifier.cs:3755` releases the permit before
running tests. **That claim is wrong and was repeated here without being checked.** Line 3755 is the
signature of `RunManagedMtpExecutableCheckAsync`, not a release. Measured from the live event stream for goal
`22d6a9d8`:

    20:44:14  ACCEPTANCE_LEASE_ACQUIRE   permit=build-0
    20:44:14  ACCEPTANCE_LEASE_HANDOFF   permit=build-0
    21:00:01  ACCEPTANCE_LEASE_RELEASE   permit=build-0

The permit is held for the entire 15m47s gate, build and test phases alike. The comment at
`ConductorBatchLoop.cs:30` is **accurate**, and the capacity-2 cap is not free throughput being wasted —
raising it would oversubscribe the build environment. The comment even records that the slot-exhaustion and
second-attempt-yields tests encode capacity-2 semantics deliberately.

What survives is narrower and still worth doing: **only the build phase needs the permit.** Holding it across
the test phase — roughly ten of those sixteen minutes — is what couples gate width to build concurrency. The
change is to release after the build and re-acquire only if a rebuild is required, which is a lifetime
redesign rather than a constant bump.

**Right design:** three types. `BuildPermitPool` (capacity 2, held **only during builds**, not across the
test phase); `AcceptanceAdmission` (independently configured from measured CPU/memory); immutable
content-keyed `BuildArtifactId`. Remove `stableSlotIndex` from acceptance APIs.

**Method note, which is the point of this correction.** This document argues that the system's recurring flaw
is records asserting things nobody verified. The original text of this section was exactly that: a citation
taken from another agent, restated with confidence, never opened. It was then repeated in a backlog item and
in an operator briefing before anyone read line 3755. Treat every file:line in this document as a claim to
check, including the ones that survived.

## Stack 5 — mutable build output turned two-phase MTP into a lease protocol

Building once and invoking the generated MTP executable directly (`GoalAcceptanceVerifier.cs:3697`) is a
reasonable shape. The problem is that the build output is a **mutable, reusable, goal-owned directory**,
which requires owner metadata, stale-owner recovery, build permits, custody markers held while tests read,
takeover refusal, integrity probes, and cleanup policy (`ConductorParallelAcceptanceAttempts.cs:878`).

**Right design:** immutable content-addressed artifacts —
`hash(commit/tree + project graph + configuration + framework + toolchain)`. Build into a temp directory,
validate, publish atomically. Tests receive an immutable reference. No owner takeover, no custody protocol;
garbage collection becomes age/reference based.

One live absurdity: the gate runs `dotnet build-server shutdown` at start and again on every permit release
(`GoalAcceptanceVerifier.cs:437`, `DotnetBuildEnvironmentManager.cs:2368`) while `Directory.Build.props:6`
and `Directory.Build.rsp:1` already disable shared compilation and node reuse globally.

## Stack 6 — the worker test ban compensates for executable identity

Developer and Tester briefs forbid raw `dotnet test`/`dotnet build` because "raw test execution can create
per-worktree **testhost** firewall prompts" (`WorkerArtifactWriter.cs:276`, `:372`).

**There is no testhost.** Under MTP each test project builds a self-contained apphost and runs as
`<Project>.exe`. Residue remains at `DotnetBuildEnvironmentManager.cs:1236` (classifies processes by matching
`"testhost"` in a command line — can never fire) and in the abort heuristics at
`GoalAcceptanceVerifier.cs:1424`, `:4196`, `:4293`, `:5129`, `IsTransientTesthostAbort` at `:5132`.

But the **prohibition is still justified**, for a different reason than it states. Measured on the operator
host 2026-08-13: 16 firewall rules named `MCG-testhost-slot*` (the retired mechanism's own, still installed
and still matched by `scripts/Remove-TestSlotFirewallRules.ps1`, deliberately unrun), **plus** a second
population auto-created by Windows prompts, named after the executable
(`mcg.agentorchestrator.infrastructure.tests.exe`), in Allow/Private and Block/Public pairs across six
distinct artifact paths. Windows prompts on a *new executable path* that binds a listening socket, regardless
of which test framework spawned it.

**And it had got worse, not better.** The retired stable slot grid gave a fixed, finite, pre-authorizable set
of paths. Per-goal and per-run artifact roots mint a *new* path — and therefore a new prompt — for every goal
and every run. Artifact isolation improved; firewall authorization silently regressed, because "slot" was
also the authorization boundary and that second role was never carried forward.

**Root cause found and fixed (main `603d4714`).** `DashboardHost.CanBindAnyIPv4Port` bound `IPAddress.Any`
and called `Start()` **purely to probe whether a port was free**, then discarded the listener.
`DashboardHostTests.HostedDashboardLanFlagBindsAllInterfaces` exercises that path, so every test run from a
fresh artifact root minted a rule. It was also a TOCTOU check the caller did not need — it probes, then binds
for real afterwards. Every other listener in the repository already binds `IPAddress.Loopback`. It now probes
loopback; `IPAddress.Any` appears nowhere in the codebase except the comment explaining why.

Verified: 8/8 dashboard host tests green, and no firewall rule exists for the apphost path the verifying run
executed from.

**This is the decomposition lesson.** The dashboard is a human convenience, not on the critical path, and the
orchestrator runs without it. A port probe inside it set the round-trip cost of the entire development
pipeline: prompts → bounded path set → "slot" as authorization unit → workers forbidden to run tests → every
cheap defect costs a paid worker round plus a gate attempt. **Interface components must not impose constraints
on the core.** Narrower rule with teeth: argument parsing must not touch the network —
`ParseDashboardHostArgs` binds sockets.

## Where the two passes disagreed

Preserved deliberately; these are the least settled claims.

| Lead | Verdict |
|---|---|
| Retired two-slot grid caused the lease pile | **Partly confirmed, and sol's counter-claim was itself wrong.** Artifact roots are per-goal now, so the "slot = artifact isolation" framing was stale. But the comment at `ConductorBatchLoop.cs:30` is accurate — lease events show the permit held for the whole gate. The live defect is permit *lifetime*, not a false comment. See the correction in Stack 4. |
| Criterion prose is mined into pattern-absence checks | **Root cause refuted, observation real.** `AcceptanceCriteriaParser.TryClassify` (`:134-149`) emits only `test-removal`, `file-exists`, `command-exit` — no pattern type. But `RunGrepCheckAsync` (`GoalAcceptanceVerifier.cs:3512-3543`) does run `git grep` and emits the exact `"pattern still present"` string observed in a live receipt for pattern `[Xunit.Theory]`. The mechanism exists and fired; how it was reached is still untraced. Its results are advisory. |
| Worker test ban caused the gate lease machinery | **Causally refuted.** Independent acceptance would be required regardless. The ban costs feedback latency, not gate architecture. |
| Two-phase MTP execution caused the artifact machinery | **Partly confirmed.** Two-phase is fine; mutable in-place reuse is the wrong decision. `Invoke-IsolatedDotnet.ps1` is operator tooling, not the conductor hot path, and should not be counted as such. |
| Operator intents lose a sub-15s race; `Verifying` has no exit | **Refuted in current source.** Enqueue writes a wake edge (`OperatorIntentStore.cs:118`); the conductor applies intents before selecting goals and excludes mutated goals from same-tick dispatch (`ConductorBatchLoop.cs:580`, `:690`). Intents remain a patch over split authority, but the stated race is fixed. |

## Recommended order

Make SQLite transitions authoritative → replace worker-log inference with typed completion envelopes → split
and hermeticize the test estate → separate build permits from gate admission and make artifacts immutable and
content-addressed.

Everything else is trimming branches from trees whose roots are still rotten.

## A recurring shape worth naming separately

Several defects share a form distinct from the stacks: **a record that reports an intention or a hope rather
than an observation.**

- A goal held 316 consecutive ticks across a conductor restart on "All tasks done; awaiting task verification
  gates — auto-reconcile will advance goal to Verified." Auto-reconcile was never going to advance it; both
  tasks lacked verification records. Nothing distinguished "waiting" from "waiting forever".
- The intake scope-collision check returned a confident verdict having compared 24 of 77 goals, counted
  Parked goals as conflicts, and treated goals with no declared scopes as non-conflicting.
- `ConductorBatchLoop.cs:30`'s comment asserts a coupling the verifier explicitly breaks.

A check that cannot examine its whole domain must say so **in its verdict**, not only in a payload field.
`no-overlap-detected` and `no-overlap-found-in-the-third-we-looked-at` are different claims.
