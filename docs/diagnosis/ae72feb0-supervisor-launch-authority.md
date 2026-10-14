# Supervisor launch authority evidence request

## Current focused repair: distinguish child renewal from pipe drainage

Receipt `finding-evidence-9831c9c41c927fa1ebbd35f6` binds candidate
`8b9cf1671308f535916e7da178a27e06efcd5259`: 44 executed, 43 passed, one failed,
zero skipped, runner exit 2. The actual retained TRX was opened and SHA256 verified:
`6DAAABED63AF7248C226E1D89AFF4E987AE989929F46200AD4F5569C9F01A2E5`.
Repository-root relative receipt:
`.orchestrator/pre-review-evidence-attempts/ae72feb013c64f6a809c2519de38bbd6/ae72feb0-pre-review-20261013223809197-c7351f45c39b48e4bd0efe0657aba076-candidate.reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-conductorcontinuitysupervisorhan-84b075dd53335795.trx`.
`RealSupervisorTransfersHealthyChildAndSuccessorTicksAfterPredecessorBoundaryCloses`
failed at line 198: `Pipe custodian did not exit after adopted child renewal.`

The same receipt establishes transfer to successor 16348 (started
`2026-10-13T22:56:29.2717174Z`) and progress from three to four ticks by adopted child
32064 (started `22:56:11.9194662Z`). It contains no adopted-child `LOOP_STOP` or exit
receipt. Therefore the assertion did not establish its prerequisite, child renewal;
the reason the child had not exited remains undetermined. This is not evidence for
changing native launch policy or production pipe custody.

The fixture now observes the exact adopted child's exit separately: exit 0 is required
before a 15-second custodian drain wait starts. After drainage, the exact child's log
must contain its `max-duration seconds=60` terminal marker. The successor consumes the
exit artifact, so the assertion uses the drained log instead. The child's 60-second
renewal interval has a separate bounded 90-second exit-observation window. Failure records the exact child identity, exit
artifact, predecessor exit state, elapsed time and log paths. Waiting uses asynchronous
process-handle observations. Existing continuity, identity, lease, boundary closure,
successor decision tick and cleanup assertions remain required.

The probe's previous two-minute watchdog began before a one-minute handoff wait,
two possible 30-second progress/lease waits and the predecessor wait. It could cancel
the child during those admitted observation phases. The controller now passes the
renewal interval and a derived 255-second custodian watchdog as explicit fixture CLI
arguments. The probe validates and records both, and emits a receipt if the watchdog
expires. The test asserts those received values. These fixture budgets do not prove
why the earlier child failed to exit.

Acceptance owns one repeat on the repaired candidate:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~ConductorContinuitySupervisorHandoffTests|FullyQualifiedName~ConductorSupervisorLeaseTests|FullyQualifiedName~ConductorSupervisorReadinessIdentityTests|FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorRealHandoff|FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority|FullyQualifiedName~WorkerProcessJobsTestsBuildOutputLocator|FullyQualifiedName~ProcessStartInfoSourceGuardTests'
```

Return
complete terminal output/TRX and candidate identities, including the resolved-root and
budget receipts, actual child renewal/exit, bounded custodian exit, both boundary
closures, subsequent successor tick and stop acknowledgment. A child-exit timeout,
watchdog expiration or pipe-drain timeout remains a distinct failed observation.
Full deterministic acceptance and final-candidate Operator review remain pending.

## Prior focused repair: disposable maintenance roots

Receipt `finding-evidence-fa01fcc3535c20ca75192f21` binds candidate
`a5ac8e4d5e26590efd2c6ca27b1c5ffb6d7b7e74`: 43 executed, 42 passed, one failed,
zero skipped, runner exit 2. The actual retained TRX was opened and its SHA256 verified as
`E692EA57B68FC16955018AE3353395B359D74E9289C6C37949CD3DE23048C220`:
Repository-root relative `.orchestrator/pre-review-evidence-attempts/ae72feb013c64f6a809c2519de38bbd6/ae72feb0-pre-review-20261013212252563-e7f5ba3e22854801943274268b3202c4-candidate.reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-conductorcontinuitysupervisorhan-b8a6996eff78af61.trx`.
`RealSupervisorTransfersHealthyChildAndSuccessorTicksAfterPredecessorBoundaryCloses`
failed at line 144: `Real supervisor did not publish a completed handoff; inspect retained fixture logs.`

Stage A published `LOOP_READY` at `21:24:24.9837326Z`, `LOOP_START` at
`21:24:46.1681019Z`, its sole healthy tick at `21:25:11.7261133Z`, and max-duration
stop at `21:25:12.7585268Z`. The stage B launch was then recorded, but no completed
handoff arrived within the fixture's one-minute wait. The output records build-root
discovery under the operator's shared `LocalLow/mcg-dotnet-isolated` tree and MTP
retention examining machine results. The receipt proves these external discovery
inputs and insufficient progress within the existing bound; it does not prove which
operation caused the elapsed time, contention, or the historical native error 5.

The fixture now scopes the supported build-storage override, `TEMP`/`TMP`, and
`LOCALAPPDATA` to directories under its disposable root. This confines build-root,
temporary-artifact, and MTP-result maintenance discovery for the predecessor and its
descendants. The probe records the actual resolved build, temp and MTP roots in
`fixture-storage.json` and stdout; the real handoff test asserts those exact roots.
Without the environment change these assertions select the inherited machine roots.
All healthy-tick, exact identity, lease, predecessor exit, boundary-survival and bounded
cleanup checks and their time limits remain required. Production launch policy is unchanged.

Acceptance owns one repeat on the repaired candidate, with the prior requested custody
and native job controls plus the affected console source guard:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~ConductorContinuitySupervisorHandoffTests|FullyQualifiedName~ConductorSupervisorLeaseTests|FullyQualifiedName~ConductorSupervisorReadinessIdentityTests|FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorRealHandoff|FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority|FullyQualifiedName~WorkerProcessJobsTestsBuildOutputLocator|FullyQualifiedName~ProcessStartInfoSourceGuardTests'
```

Return complete terminal output/TRX, executed/skipped counts, candidate source/build
identity, runner breakaway configuration and resolved-root receipts. Verify the same
adopted child ticks after transfer and the same successor ticks after predecessor exit
and both boundary closures. An absent receipt, skipped/zero execution, timeout or failed
cleanup remains pending. Build success cannot credit repaired runtime behavior. Full
acceptance remains Acceptance-owned; Operator inspects final-candidate isolated lifetime
and the complete source verdict. The current owner ruling permits temporary Codex
GPT-6.1 Sol review identified as SAME-FAMILY, with cross-family availability reconsidered
after the recorded provider resets.

## Prior focused repair: adopted-child renewal budget

Retained receipt `finding-evidence-39150392e54d04ffea1a9755` covers starting candidate
`de1862b3ae54751258398368e33bab22dd916c29`: 27 executed, 26 passed, one failed, zero skipped,
runner exit 2. The actual retained TRX was inspected and its SHA256 matches
`C5550D742C01149423FF4661C350BE886B7308B9C2DD9E45E7F281A19162B8E3`:
`.orchestrator/pre-review-evidence-attempts/ae72feb013c64f6a809c2519de38bbd6/ae72feb0-pre-review-20261013184129606-08eb22b26783449f9bda52b41d32fc66-candidate.reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-conductorsupervisorrealhandoffpr-62a4ffde29cc750d.trx`.
`RealSupervisorTransfersHealthyChildAndSuccessorTicksAfterPredecessorBoundaryCloses`
failed with `System.TimeoutException : Same adopted child did not tick after lease transfer.`

The adopted child 19944 (started `2026-10-13T18:44:55.0984423Z`) emitted its three healthy ticks
at `18:45:02.4367334Z`, `18:45:08.4666688Z`, and `18:45:12.6188323Z`. Transfer to successor 2516
was published at `18:45:13.4338588Z`; that child's log then records
`LOOP_STOP tick=0 rechecks=0 reason=max-duration seconds=12` at `18:45:13.6763620Z`, with no
fourth tick. The successor subsequently launched a new child, which emitted ticks into a different
log. Source inspection confirms the fixture passed the same 12-second duration to both initial and
adopted children. The failed predicate kept polling the adopted child's finished log; it cannot be
satisfied by the successor's later child's progress. This receipt does not indicate a native launch
or pipe-custody failure, and does not diagnose the historical error 5.

The probe now supplies per-child CLI duration through the existing system process host's start-info
factory: stage A retains 12 seconds to trigger staging; the adopted stage B child gets 60 seconds
for activation, real supervisor readiness/transfer, and a subsequent tick before its normal renewal.
The actual successor supervisor retains its original 12-second arguments. Each launch emits its
exact App path, selected duration, arguments and stdout path. Launch, pipe handling, readiness,
lease transfer, inherited-child observation and cleanup still use production implementations.
The fixture retains the same post-transfer tick increase and post-boundary successor tick increase;
it additionally checks the adopted child's exact live identity during every progress poll and emits
the observed before/after tick counts. No production launch policy or lifecycle code changed.

Acceptance owns one execution of the failed class and affected console source guard on the repaired candidate:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorRealHandoff|FullyQualifiedName~ProcessStartInfoSourceGuardTests'
```

Return complete terminal output/TRX, executed/skipped counts, source/build identity and runner
breakaway configuration. Inspect the per-child duration receipts, adopted-child tick counts,
successor lease/identity, normal predecessor exit, both predecessor boundary closures, subsequent
successor tick and bounded stop. The repaired behavior is pending this execution; build evidence
does not establish it. Full acceptance remains Acceptance-owned, and Operator criteria 3 and 4
remain pending final-candidate isolated-lifetime inspection and complete cross-family Grok review.

## Prior focused repair: successor log discovery

Retained receipt `finding-evidence-7663eee4a46327f2612b1c3d`, candidate
`0f37a96c8468a5949734c57c9f3ddd7cc01d3535`, executed 27 tests: 26 passed, one failed,
none skipped. Its actual TRX SHA256 is
`80733E580A9FB0651D0C4BCD590929E990869BC51A2841BA09C40B239B276D15`.
Receipt path:
`C:/Users/miles/vcs/mcg-agent-orchestrator/.orchestrator/pre-review-evidence-attempts/ae72feb013c64f6a809c2519de38bbd6/ae72feb0-pre-review-20261013181957145-1f9923acaced4c93a5de1ba58026e6d4-candidate.reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-conductorsupervisorrealhandoffpr-f2cfe51c63716891.trx`.
`RealSupervisorTransfersHealthyChildAndSuccessorTicksAfterPredecessorBoundaryCloses`
failed at line 111 with `Assert.Single() Failure: The collection was empty`.
The retained output places its successor log in `.orchestrator/continuity/logs`, while
the fixture searched `.orchestrator/continuity`. The relevant fixture and production
handoff source are unchanged between that receipt's candidate and this round's starting HEAD.

The fixture now searches the production log directory and matches the claimed handoff token.
It reports the exact path/token/successor identity after boundary closure and reports the
expected pattern and found handoff logs if discovery fails. The same complete-line reader,
post-closure tick increase, exact successor identity, exclusive lease and bounded stop
assertions remain required. Newly launched children's output is forwarded to this supervisor
log; observing the previously adopted child's log alone cannot prove post-predecessor progress.
This repair changes no production launch flags, policy, transfer, custody or fallback.

Acceptance owns one focused execution on the repaired candidate:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorRealHandoff'
```

Return complete terminal output/TRX, executed/skipped counts, candidate source/build identity,
runner breakaway configuration and the emitted successor log/identity receipt. A build alone
does not prove a subsequent tick or lifetime survival. Operator criterion 3 inspection and
criterion 4 final-candidate Grok review remain pending. The historical native-error-5 mechanism
remains undetermined; this receipt does not authorize a launch-policy correction.

The retained repaired baseline passed: 20 executed, 20 passed, zero failed/skipped; wrapper exit 0,
owned PID 33636 exited with ownership cleanup confirmed. Receipt:
`C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/summary-20261013T165247352-9f40e347/summary-Mcg.AgentOrchestrator.Infrastructure.Tests-1-r1-FullyQualifiedName-ConductorSupervisorWindowsLaunchAuthorityProcessSpawningTest-904446d16aac89d2.trx`;
SHA256 `FA525E6B955ECBEF29F7AD7A3AE0E54C5D4716F556169820633D7B50F98A8F29`.

The controls discriminate these named boundaries: direct restrictive outer fails with error 5;
direct permissive outer and nested permissive outer/inner launch and survive outer closure;
nested restrictive outer/permissive inner launches successfully but remains in the outer job and exits
when it closes. CreateProcess success and residual any-job membership do not establish whether a
particular lifetime boundary was escaped. These results establish the fixture mechanisms; the original
incident's ancestry and cause remain undetermined. Launch flags, console suppression, diagnostics and
fallback are preserved. No production launch-policy correction is justified by the available evidence.

This candidate binds readiness to captured PID/start time, token, commit and run directory, rejects
inbound records without a live current incumbent and matching build, atomically claims each inbound
artifact once, and cleans up the exact pending successor on cancellation before transfer. Handoff
events additionally retain successor and adopted-child start times. These custody repairs are
independent of the unobserved incident ancestry.

Acceptance owns criterion 1 execution. Run the final candidate's managed fixture with a runner that
allows breakaway, preserving complete terminal/test output, TRX, executed/skipped counts and candidate
identity:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority'
```

The baseline above supersedes the Operator's earlier discovery-only retained run
`summary-20261013T151034408-b1e3eafc/summary-Mcg.AgentOrchestrator.Infrastructure.Tests-1-r1-FullyQualifiedName-ConductorSupervisorWindowsLaunchAuthorityProcessSpawningTests.trx`
executed four arms, all failing at probe discovery (`Assert.NotNull() Failure: Value is null` at the previous line 39),
before any predecessor launch. It supplies no job-policy comparison. The original no-build attempt
selected zero tests (exit 24); the managed build/run returned exit 2, owned PID 14016, exit confirmed.

Discovery now checks the directory containing the exact executing test assembly first and requires
both `Mcg.AgentOrchestrator.SupervisorLaunchProbe.dll` and its runtimeconfig in that directory.
A partial local copy is rejected as a candidate, then discovery tries the existing build-bound sibling /
exported-root lookup for a complete directory. This covers native builds that copy runtimeconfig/deps
beside the test assembly while retaining the probe DLL and runtimeconfig together in the sibling
project output. Files are never combined across outputs and timestamps are never consulted.
Each arm emits the chosen directory, required files and every tried directory before launch, including
on discovery failure. Retain these receipts together with the candidate identity and complete output.
Acceptance also owns the focused discovery regressions in `WorkerProcessJobsTestsBuildOutputLocator`;
run those separately with the managed helper, filtered to that class. The 20-test receipt includes
these regressions and all four comparison arms.

The four theory arms change only the named outer job's breakaway limit within each direct/nested
pair. They use the actual production `LaunchDetachedWindows` with unchanged flags, explicit inherited
output handles and console suppression. Every predecessor starts suspended before fixture-job
assignment. Assertions check each named membership, the launch error against the diagnostic receipt,
effective flags, innermost limits, exact successor PID/start time, and the result of closing the named
predecessor boundary. A permissive inner job beneath a restrictive outer job provides the negative
lifetime control: successful creation alone cannot prove escape. Survivors must acknowledge after
boundary closure; contained processes must positively exit. Cleanup uses fixture signals and owning
job/process handles only, with bounded waits and receipts.

The launcher records executable path, candidate assembly path/MVID, incumbent PID/start time/image,
flags, innermost job limits and query error separately from the immediately captured CreateProcessW
error. Its NULL-handle job query explicitly cannot identify ancestors. The test retains named fixture
job handles; Acceptance must record its runner job configuration and any additional enclosing boundary.
An unexpected result, diagnostic query failure, skip or missing acknowledgment is inconclusive for
mechanism selection and requires inspection rather than relaxing an assertion.

Expected fixture relationships follow the documented [nested job contract](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs)
and [innermost query scope](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject).
Those contracts describe the controls; they are not incident evidence.

Developer owns only a mechanism correction supported by discriminating evidence and its regression
controls. Criterion 2 remains Acceptance-owned; run the new custody controls and retain
`ConductorSupervisorReadinessIdentityTests`,
`ConductorContinuitySupervisorHandoffTests`, `ConductorSupervisorBuildIdentityTests`,
`ConductorSupervisorLeaseTests`, `ConductorFailedSuccessorStopTests`, and
`ConductorSuccessorIdentityPropagationTests`, plus existing real conductor lifetime/console controls.
The focused custody request is:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~ConductorSupervisorReadinessIdentityTests|FullyQualifiedName~ConductorContinuitySupervisorHandoffTests|FullyQualifiedName~ConductorSupervisorBuildIdentityTests|FullyQualifiedName~ConductorSupervisorLeaseTests'
```

The latest retained focused run of candidate `720007b8ea0070ec3f37b9b035c41ec29b718356`
executed 24 tests: 23 passed, one failed, none skipped. Receipt:
`C:/Users/miles/AppData/Local/Temp/Low/mcg-tests/summary-20261013T173620121-1d1e3b4c/summary-Mcg.AgentOrchestrator.Infrastructure.Tests-1-r1-FullyQualifiedName-ConductorSupervisorRealHandoffProcessSpawningTests-FullyQual-d9ec384d43f482a8.trx`;
SHA256 `15957421856955E162B7BBAC7C2CA50D273F58B97D7AA10A27A93B61282ABB9E`.
`RealSupervisorTransfersHealthyChildAndSuccessorTicksAfterPredecessorBoundaryCloses` failed
in `CountTicks` at `File.ReadAllLines` with an IOException opening the still-written child log.
The production writer holds write access; that reader's sharing excludes writers. The receipt
records incumbent 17636, successor 14740 and adopted child 11836, but does not prove boundary survival.

The repaired fixture uses the existing `ProcessLogReader.ReadComplete` with writer/delete sharing
for live tick logs, event logs and diagnostic output. An unavailable read fails explicitly;
only newline-terminated records count, and the existing bounded progress waits remain.
`LiveTickLogAllowsActiveWriterAndCountsOnlyCompleteLines` holds the writer open while checking
complete and unfinished tick records. Acceptance must rerun both tests in the class below on the
new candidate, retain complete output/TRX and identify the candidate closure. A successful build
alone supplies no handoff or lifetime evidence.

Criterion 3 remains Operator-owned and pending execution of the repaired disposable scenario:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorRealHandoff'
```

The predecessor probe hosts the production `ConductorContinuitySupervisor` with the system process host,
system handoff seam and SQLite event store. Staging and the per-child renewal duration are supplied
by the fixture: two separate
directories contain the same exact candidate closure and unchanged commit marker. The successor runs
the actual App entry point. Both conductor children are real daemon/watch loops using empty isolated
stores, a private project registry and cleared provider/behavior environment. This does not replay
the historical old/new commit pair or claim its ancestry.

Before resume, the fixture assigns the predecessor to its named permissive kill-on-close job. It
requires three real activation ticks, exact readiness/child identities, exclusive successor lease
ownership and an additional tick from the same adopted healthy child after transfer. The predecessor
must exit normally after finishing pipe custody; then both its owning process group and the named
predecessor job close, and the exact successor must
produce a subsequent decision tick and retain the lease. Cleanup uses the disposable stop file, owning
job handle and validated successor identity with bounded waits. Full candidate identity, named boundary,
handoff and complete process logs are returned in test output; failure artifacts are retained at the
reported temporary root. Keep skip/zero tests, missing receipts, timeout or cleanup failure pending.

Acceptance must return complete focused output/TRX and candidate identity before final review.
Operator must inspect the real-supervisor receipts for criterion 3. Criterion 4 remains pending the
assigned Grok final-candidate review (native file/search tools, no terminal tool) and Operator inspection
of its complete verdict. Full acceptance remains Acceptance-owned. The incident's launch mechanism
still needs evidence identifying its actual boundary; do not infer it from this fixture or authorize
a restrictive-job fallback that loses required lifetime escape.

## Focused verification after candidate 3abe155 fixture corrections

The retained pre-review receipt for `3abe1555901c02ba839bb2c0fd214ed96ae02a5b` reports
793 total, 792 executed, 790 passed, two failed and one timeout. The source guard found missing
`CreateNoWindow` on the four-arm fixture's predecessor start. The real-handoff fixture failed
before launch because discovery stopped at a partial test-directory copy, although the same build's
sibling probe directory contained the DLL and runtimeconfig. This round sets predecessor console
suppression and resolves a complete build-bound directory; production native launch flags are unchanged.

Acceptance must execute the corrected candidate using the supported Infrastructure.Tests selector:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority|FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorRealHandoff|FullyQualifiedName~WorkerProcessJobsTestsBuildOutputLocator|FullyQualifiedName~ProcessStartInfoSourceGuardTests|FullyQualifiedName~ConductorSupervisorReadinessIdentityTests|FullyQualifiedName~ConductorContinuitySupervisorHandoffTests|FullyQualifiedName~ConductorSupervisorBuildIdentityTests|FullyQualifiedName~ConductorSupervisorLeaseTests|FullyQualifiedName~ConductorFailedSuccessorStopTests|FullyQualifiedName~ConductorSuccessorIdentityPropagationTests'
```

The unrelated cohort preland timeout remains inconclusive, with mechanism undetermined.
The packaged review finding says a rerun is not indicated; this repair changes neither that
test nor ConductorBatchLoop.
Return complete terminal output, TRX, executed/skipped counts, exact source/build identity, runner
breakaway configuration, chosen probe directory and tried directories. Confirm that native isolated
output and the managed helper's copied closure both resolve a complete directory from their own build;
synthetic locator regressions alone do not prove real output reachability. Preserve all existing
readiness, custody, cancellation, live-reader and lifetime assertions. No final review/full gate until
focused evidence converges. Criterion 3 remains pending Operator inspection of final-candidate real
successor survival, and criterion 4 remains pending the complete cross-family Grok review.

## Focused verification after af850df0 acceptance guard failures

The retained Remainder receipt
`ae72feb0-0-20261014051918584-7342871902db4eb291da4c992083c749.infrastructure-tests-remainder.trx`
records 9,641 executed, 9,639 passed and two failed: console replacement installation placement and
the supervisor fixtures' substring lane membership. The supervisor fixture classes and filenames now
start with `DispatchProcessHostTests`, the existing Process spawning lane substring. The standalone
probe installs its diagnostic writers through its own `ConsoleCapture.cs` installation seam.
Fixture assertions, production launch behavior, the manifest and source guards are unchanged.
Historical TRX paths above retain their original class names; executable selectors use the new names.

Acceptance owns execution of the repaired guards and renamed fixtures:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -AllowBreakaway -Filter 'FullyQualifiedName~ConsoleReplacementSourceGuardTests|FullyQualifiedName~AcceptanceLaneMembershipTests|FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorRealHandoff|FullyQualifiedName~DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority|FullyQualifiedName~WorkerProcessJobsTestsBuildOutputLocator|FullyQualifiedName~ProcessStartInfoSourceGuardTests'
```

Return complete terminal output, retained TRX, nonzero executed counts and candidate/build identities.
Keep the native job-policy controls, real-supervisor lifetime observation and full deterministic
acceptance gates in scope. Prior receipts remain bound to their recorded candidates; this source-only
repair does not claim current-candidate test execution or completion of Operator-owned final review.
