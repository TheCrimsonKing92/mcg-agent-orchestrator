# Compiler-cap experiment protocol (version 1)

`scripts/Measure-CompilerCap.ps1` provides an inspectable plan and one explicit,
bounded experiment. It applies no policy. This tooling slice establishes no
measured optimization. Backlog `8bfb900e88d6477bb78182141ae47845` remains open
for the operator's large-build trials and subsequent decision.

## Commands and ownership

From the clean candidate worktree, inspect the default plan:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts/Measure-CompilerCap.ps1
```

Planning reads source identities and the selected SDK. It creates no experiment
directories and launches no restore, build, compiler or monitor. The JSON shows
the fixed App project, commit/tree/input digest, SDK/compiler/build hashes,
configuration, exact executable and argument arrays, proposed paths and limits.
The App's project references are built with their existing analyzer/quality
settings. Default configuration is Release; `-Configuration Debug` is supported.

Only the operator may later launch a real large-build trial. After reviewing the
plan, selecting its actual clean commit and SDK, arranging a quiet host window
and checking that no gate/worker/experiment build is running:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts/Measure-CompilerCap.ps1 -Run -ExpectedCommit <full-commit> -ExpectedSdk <actual-sdk-version> -QuietWindowDeclaration 'Quiet window; no deliberate competing jobs; unchanged physical and power conditions'
```

Use `-SetupTimeoutSeconds` and `-BuildTimeoutSeconds` to override the finite
900-second per-phase defaults (range 1–3600). Cleanup and each output drain are
bounded at five seconds; read-only process inspections are bounded at 30 seconds.
CIM observations use a two-second operation timeout. There are exactly three
arms, each with one setup and one build. No retries, background monitor, resume,
automatic repetitions, policy application or native experiment-store entry occur.
The operator must schedule the quiet window; this script does not change the
running conductor or acquire a global scheduling restriction.

The runner rejects an ambient `DOTNET_PROCESSOR_COUNT`, a dirty/mismatched
candidate, wrong SDK or an unverified SDK CscEnvironment channel. It rechecks
source/input and SDK identities around every phase. Terminal failure, timeout,
cancellation or incomplete cleanup/output stops the plan, retains partial
evidence and marks later arms skipped. A hard process kill may leave the last
atomically written `running`/phase-start receipt; this is incomplete evidence,
never success. The owned process job closes on orderly cancellation.

## Workload and isolation

The workload is `src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj`
with project references enabled, fixed configuration and unchanged analyzers.
The immutable plan records the clean commit, tree and tracked-input content
digest. Execution never checks out or repairs the candidate. It also rejects
outputs inside the source tree or redirected ancestors.

The runner uses `WorkerBuildReceipt.psm1` isolation/path checks and the diagnostic
wrapper's optional observation seam, which uses `MtpTestRunner.psm1` owned jobs.
The ordinary diagnostic wrapper contract is preserved. Experiment evidence goes
under the established isolated goal root's `artifacts/compiler-cap/<unique-id>`.
An owner receipt precedes execution. Each arm gets fresh project-scoped output,
intermediate and NuGet package roots; no compiled output is reused between arms.
CLI home, NuGet HTTP cache, scratch and process temporary paths are also invocation-scoped beneath
the arm root and recorded with the command environment.
An invocation-scoped owned target records effective quality settings and appends
the treatment to each project's existing CscEnvironment before CoreCompile.
Pre-existing processor-count entries are rejected. Source/build configuration,
shared outputs, global environment, compiler defaults, gate width, roles, CPU
affinity, priority, power settings and the acceptance manifest are not changed.

Order is baseline, then count 8, then count 4, sequentially. Baseline means the
unchanged processor-count condition under identical invocation-scoped
`UseSharedCompilation=false`. Shared compilation is already disabled by the
repository default. No `/parallel-` treatment is included. `-maxcpucount:1` and
node-reuse disabling are identical protocol settings in all arms, scoped to
these invocations. The processor count is appended through CscEnvironment only,
never inherited by MSBuild, restore or unrelated descendants.

Every arm first performs a separate, unmeasured restore. The subsequent build
uses `--no-restore` against that arm's fresh outputs. Wall time covers the build
process's launch through completion, excluding restoration and measurement
apparatus preparation. Setup is not a warmup improvement. Dependency identities
and effective settings must agree across arms after equivalent-root path
normalization. Logs and binary logs are retained separately for both phases.

## Measurements, coverage and native limitations

The primary peak metric is aggregate **compiler CPU core-equivalents**: sum
compiler-process CPU increments during a window, divided by its actual monotonic
elapsed seconds. One core-equivalent is one CPU-second per wall-second. Peak is
the maximum aggregate window, never the sum of per-process maxima, memory usage
or whole-host CPU percent. Total compiler CPU is the sum of final accounted
compiler-process lifetime CPU, excluding restore/MSBuild/other children.

The preregistered target interval is 250 ms. Eligibility requires at least ten
compiler-active windows, no active gap greater than 500 ms, known process
identities (PID, start time and image/compiler hash), complete population and
initial/final lifetime reconciliation, and available non-regressing counters.
Unobserved short-lived compilers, PID ambiguity, server work and unknown initial
or final accounting invalidate the comparison. No missing peak is interpolated.
Unknown metrics are JSON null with availability/reason fields, never zero.

The initial native adapter retains timestamped process snapshots and compiler
command lines alongside structured MSBuild build/task/diagnostic events. Its
polling cannot prove complete compiler or host populations and final lifetime
accounting. Default CPU-set and throttling facts are also unavailable. Native
comparison verdicts therefore remain **inconclusive** with explicit reasons;
synthetic positive contract tests do not establish native measurement capability.
Sample JSONL, logs and build-event receipts are useful retained observations but
cannot justify a cap. Complete lifetime/host instrumentation would require a
separately reviewed extension before any eligible native optimization verdict.
Power-plan identity is read when available; missing facts remain unavailable.
No host-pressure or contention cause is inferred from compiler-only snapshots.

Required equivalent machine facts are hardware/logical processor count, OS,
priority, affinity, default CPU-set and observable power/throttling settings.
Host CPU must be sampled alongside the complete owned experiment population.
Mean non-experiment CPU during compiler-active windows must be at most 10% of
logical capacity; baseline/treatment means may differ by at most five percentage
points. Unknown host population/accounting is inconclusive. The quiet-window
declaration cannot override missing facts or contradictory counters. Thermal and
unobserved scheduler effects remain limitations even with complete accounting.

## Report and preregistered advisory decision

Each run retains version-1 `report.json`, `owner.json`, `summary.txt`, exact
arguments, scoped changes, phase status/exit/timing, plan and identities,
structured diagnostics, coverage, sample series, measurement units/availability
and reasons. Reports are checkpointed before each phase and after its result.
Diagnostics are compared as multisets by ID, severity, project, file and full
location (line/column and end location), preserving duplicate counts. Equivalent
source/owned prefixes normalize without dropping distinct locations. Absent or
incomplete event streams are never treated as a clean empty diagnostic set.

Eligibility precedes performance calculation. Failed builds, incomplete output,
mismatched identities/settings/dependencies/host, absent actual compiler work,
diagnostic differences, missing measurements or zero baseline denominators make
a comparison inconclusive. Complete eligible comparisons use unrounded values:

| Measurement | Treatment requirement |
| --- | --- |
| Sampled compiler CPU peak | ≤ 0.80 × baseline (at least 20% lower) |
| Build wall time | ≤ 1.10 × baseline (at most 10% longer) |
| Total compiler CPU | ≤ 1.05 × baseline (at most 5% greater) |

All three inclusive thresholds plus diagnostic parity yield `exploratory
promising`. A complete eligible comparison failing a threshold is `not
justified`. Invalid evidence is `inconclusive`. One baseline/treatment pair is
always exploratory. Adoption requires repeated matched native runs, controlled
conditions, complete evidence and a separate owner action. This runner never
records adoption or changes a compiler cap.

The old small-Core measurements showed lower peaks with 14–31% longer wall time
and roughly unchanged total CPU. Every historical App-chain arm failed; its
mechanism remains undetermined without retained or controlled reproducing
evidence. This protocol does not rewrite those historical receipts.

## Verification

`CompilerCapExperimentContractTests` exercises the real entrypoint with an
explicit data-only, bounded fixture adapter. Fixtures require an owned marker
under the OS temporary directory and a separate miniature source root. They
cannot launch native processes or fall back to the real workload; their reports
are permanently labeled `synthetic-fixture`. Tests control raw counters,
identities and phase results; production report logic computes the verdicts.
Negative controls change thresholds, diagnostics, coverage, counters and host
facts independently. No wall-clock timing decides a test verdict.

Acceptance executes these tests and `WorkerBuildDiagnosticDefaultsTests` on the
exact candidate, then all unchanged normal manifest gates. Developer builds the
test project using `Invoke-WorkerBuildCheck.ps1` and defers test execution. Real
large-build trials, repeated native evidence and later policy/experiment entries
are operator-owned post-landing work and do not hold this tooling slice's landing.
