# Console frame painting: pinned output seam investigation

Candidate baseline: `ac406710d62dd1368263c7354d8a07e168f08ea6`.
`git merge-base --is-ancestor bb3d9b7c491eb93a848b25e77241818290192ea2 HEAD`
returned **0** on 2026-10-15. HEAD already contains main
`3732befa8e9ac89483e1f1a275dfff31ef722b56`; no integration or shared-view edit is needed
for this investigation. This satisfies the scrolling prerequisite, not the painting criteria.

## Source findings

`src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj:18` pins Terminal.Gui
`[2.5.0]`. The installed package's `terminal.gui.nuspec` repository commit is
`ece73d25c37df9bb17cb0c0d2171abd4d98eeaff`. Links below use that immutable revision,
not the release tag or a different version's documentation.

`OwnerConsoleBoardRenderer.cs:11-40` prepares table rows and calls `view.Update()`;
it does not write to the terminal. `OwnerConsoleFullScreenHost.cs:50` creates the
standard Terminal.Gui application. Preparing a larger table buffer here cannot
batch the driver's physical writes.

In the pinned dependency:

- [DriverRegistry.cs:111-118](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/DriverRegistry.cs#L111)
  selects **ANSI** on Windows. The owner console does not set `ForceDriver`.
  Thus the unforced pre-fix production path is `AnsiComponentFactory` / `AnsiOutput`,
  not `WindowsComponentFactory` / `WindowsOutput`. This is source-derived backend
  selection; the operator's actual live backend has not been captured here.
- [OutputBase.cs:145-155,194-207,271-296](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/Output/OutputBase.cs#L145)
  processes dirty rows in order, positions the cursor, and submits each row/run separately.
- [AnsiOutput.cs:211-281,314-339](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/AnsiDriver/AnsiOutput.cs#L211)
  sends cursor sequences through its span writer and row content through its
  `StringBuilder` writer. On Windows VT both call `WindowsVTOutputHelper.Write`
  separately; neither accumulates a whole refresh. Its constructor enables VT via
  `WindowsVTOutputHelper.TryEnable` (lines 97-114), unlike `WindowsOutput`.
- [WindowsVTOutputHelper.cs:170-176](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/WindowsHelpers/WindowsVTOutputHelper.cs#L170)
  converts each supplied payload to UTF-8 and immediately calls native `WriteFile`.
  These are submission boundaries even without an explicit flush. The constructor's
  startup flush is separate from dirty-frame rendering.
- [DriverImpl.cs:93-104](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/DriverImpl.cs#L93)
  refreshes by passing its output buffer to that output component.

The source-supported ANSI candidate mechanism is row/run submission before the entire
frame is committed. This is not yet an executed reproduction or attribution of the
operator's visual symptom. It neither identifies the operator's active backend nor
proves which intermediate writes the host displays.

## Prior bounded reproduction authored for Acceptance execution

`OwnerConsoleFrameReproductionTests.PreFixOutputBaseExposesRowCommits`
uses 60 board rows and a simulated 72-column, 10-row content viewport (six viewport
heights). It drives actual view Page Down, Page Up and twelve Down steps, then calls
the actual pinned `OutputBase.Write(IOutputBuffer)` implementation with an ASCII
projection of the rendered board table. No source copy of the dependency row loop
is used. The recording subclass maps cursor/content callbacks to the immediate
submission boundaries found in the Windows VT branch of AnsiOutput. The recorder
models the two callback boundaries; it does not execute the native backend.

The test emits JSON through the test output helper: dataset and dimensions,
timestamp frequency, separate input, model preparation, rendering/projection and
output/recorder tick durations, plus every cursor/content payload in order,
write counts, explicit flush counts and clear-screen counts. Time never determines
pass/fail. Native writes are implicit commits, not explicit flushes.

Assertions characterize twenty submissions per full dirty viewport, row order,
exact final ASCII content, consumed dirty flags, expected selection and board
focus. At the first content submission they assert the new first row coexists
with the old final row. This is intentionally a pre-fix characterization test,
not a passing one-commit contract or a RED/GREEN regression receipt.

Limits: the projection is not Terminal.Gui table layout; the recorder does not
intercept AnsiOutput or make native calls. Rendering timing includes
the projection, and output timing includes recorder work. These are synthetic phase
measurements, not live timings, throughput benchmarks, or visual evidence. The test
has been authored but **not executed** by Developer, as required by the role brief.
No timing samples, successful assertions, or before/after improvement are claimed.

Tester/Acceptance focused invocation:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~OwnerConsoleFrameReproductionTests'
```

Retain executed count, outcome and full test output. A passing characterization
would establish the dirty-row submission behavior only.

## Prior implementation boundary (superseded by owner decision below)

The smallest intended fix would buffer cursor sequences and row/run content inside
the existing output component, then submit the complete update once. The application
cannot install such a component through the pinned public initialization API:

- [ApplicationImpl.cs:38-51](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/App/ApplicationImpl.cs#L38)
  exposes component-factory constructors only internally.
- [ApplicationImpl.Driver.cs:27-104,117](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/App/ApplicationImpl.Driver.cs#L27)
  hard-codes built-in component factories when no internal factory is provided.
  Registering a descriptor does not change that switch's factory construction.
- [Application.cs:73](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/App/Application.cs#L73)
  accepts a time provider, not an output component or factory.

An output subclass in this repository alone would therefore be unwired. Replacing
Console.Out would miss native ANSI writes. No production fix was applied because
the plan requires escalation when supported integration needs dependency changes.
The missing capability is public, session-owned output/factory injection, or a
corrected pinned dependency output implementation. Resolve that dependency scope
before selecting an integration approach; private-field reflection, global writer
replacement and a replacement console architecture are not implemented.

Developer scope remains incomplete: one-commit contract/regression and compatibility
tests await a supported production seam and fix. The different-family Reviewer must
independently check the source interpretation before a consequential fix is applied.
Acceptance owns execution and negative controls. Operator-owned comparable live
timings and visual captures remain pending and do not block this Developer round.

This investigation is separate from scrolling responsiveness (backlog `0fc9adab`,
landed goal `2017d034`). Its landing permits shared-view edits; it is not evidence of
a shared cause. No data-loading or resource-pressure cause is asserted.

## Owner-approved implementation (2026-10-15 22:19Z)

The retry decision authorizes private reflection for this seam only and accepts
the preceding source diagnosis as the basis for implementation. It supersedes
the prior implementation stop above. It does not constitute live visual evidence
or an executed regression receipt.

Scope expansion: `OwnerConsoleFrameOutput.cs`, `OwnerConsoleFrameOutputInstaller.cs`
and one application-creation call in `OwnerConsoleFullScreenHost.cs`. Row preparation
cannot control native submissions, so the fix belongs at the existing output seam.
`OwnerConsoleFullScreenView.cs` and input handling are untouched.

The installer calls public `Application.Create()` first, preserving instance-model
bookkeeping and events. Before initialization it validates and assigns exactly one
private member, `ApplicationImpl._componentFactory` (private, instance, readonly,
type `IComponentFactory`). All production reflection lives in the installer. A
missing member, changed signature or reflection access failure leaves the normal
application's factory unset and emits one diagnostic line naming the member.
`OwnerConsoleFrameOutputInstallerTests.TerminalGui250ApplicationImplComponentFactorySignatureUpgradeGuard`
checks the signature and actual installation against the referenced assembly.

Correction after Reviewer `console-frame-diagnosis-wrong-backend` and
`console-frame-driver-switch-ansi-to-windows`: the earlier candidate incorrectly
switched the default driver to Windows. The owner-approved scope covers output
wrapping, not that driver change. On Windows, an unforced or explicitly ANSI
full-screen application now uses an `AnsiComponentFactory` subclass. Its inherited
ANSI input, input processor and built-in output construction retain the pre-fix VT
setup. Explicit other drivers and non-Windows applications retain their creation
path. Inline output is returned unwrapped.

[AnsiComponentFactory.cs:54-79,129-149](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/AnsiDriver/AnsiComponentFactory.cs#L54)
recognizes the concrete `AnsiOutput` when choosing/configuring its size monitor.
The adapter exposes its underlying output to the factory, which passes that exact
instance to the built-in `CreateSizeMonitor`. This retains `AnsiSizeMonitor`, native
polling configuration and ANSI size-query responses instead of silently falling
back to `SizeMonitorImpl`. The main-loop screen-offset callback is inline-specific;
inline mode remains unwrapped. No new reflection or input logic is introduced.

The factory wraps the built-in ANSI output. `OutputBase.Write(IOutputBuffer)`
still traverses dirty cells and composes attributes, Unicode and image sequences;
the adapter collects its cursor and content callbacks, then invokes the built-in
`IOutput.Write(ReadOnlySpan<char>)` once with the complete update. On Windows VT
that method makes one helper submission / native `WriteFile` call. There is no separate clear-screen or intermediate
submission from this refresh. An empty dirty update emits no write. Composition
or submission errors that reach the adapter clear its buffer in `finally` and
propagate; stale content cannot leak into the next refresh. The pinned AnsiOutput
catches output exceptions and its helper does not check `WriteFile` success; this
adapter does not add native error detection. The throwing-recorder test proves
adapter cleanup only. Startup/query writes, cursor lifecycle,
size changes and disposal continue through the built-in output. A single submission
is an application-level commit contract, not proof of atomic host presentation.

The retained `IsLegacyConsole` guard returns native output unmodified if supplied
such an output and emits one capability diagnostic. The default ANSI backend instead
uses its own degraded behavior if VT initialization fails; wrapping does not make
a degraded backend visible. Explicit other drivers, signature fallback and legacy
fallback do not receive the buffering guarantee. If the operator's real console takes one of those paths or still visibly
paints progressively, the visual criterion remains unmet; retain the evidence and
escalate rather than infer success.

`OwnerConsoleFrameReproductionTests.PageUpPageDownAndScrollCommitWholeFrames`
uses the prior long-list scenario through the production adapter and requires one
nonempty submission, exact final content and no mixed screen at the commit boundary.
`PreFixOutputBaseExposesRowCommits` keeps the unbuffered mechanism control. The
simulated ASCII table projection limitation above still applies.

`OwnerConsoleFrameCompatibilityTests` covers complete resize repaints at 72x10,
40x5, 96x15 and 12x1, repeated paging to both long-list bounds, selection/focus and
viewport retention, matching pre-fix ANSI bytes including Unicode, unchanged plain
and redirected board bytes, cursor/direct-query delegation and write-error cleanup.
The plain fixture pins goal timestamps to the harness clock. The recording seam
uses the production adapter/factory wrapping decision without native I/O.

Acceptance must run `OwnerConsoleFrameReproductionTests`,
`OwnerConsoleFrameCompatibilityTests`, `OwnerConsoleFrameOutputInstallerTests`
and the existing console suite. For an executed negative control, return the
built-in output unwrapped in `BufferOutput` while retaining the tests. The
whole-frame test then fails its adapter/one-commit contract (the mechanism-control
arm documents the row submissions); restore wrapping for GREEN. Keep revisions,
exact failures and positive executed counts. No test execution is claimed here.

Criteria 6 and 7 stay Operator-owned: comparable live per-phase timings and
before/after ffmpeg recordings are still required. Retain list size, terminal
host/version, window dimensions, backend, build configuration and revisions. This
change supplies a candidate, not those live receipts.

Retry verification: `TerminalGui250ApplicationImplComponentFactorySignatureUpgradeGuard`
now requires the installed factory's ANSI driver name to match the pinned Windows
default (the former Windows factory fails this assertion).
`InstalledFactoryKeepsAnsiInputAndComposesOutputThroughFrameAdapter` checks ANSI
input processing, the factory's adapter chain and inline bypass.
`WrappedAnsiOutputKeepsBuiltInAnsiSizeMonitor` exercises the concrete-type-sensitive
monitor decision without acquiring a terminal; removing the unwrap produces
`SizeMonitorImpl` instead. These tests are authored for Acceptance, not executed
by Developer. The previously accepted long-list regression assertions are preserved;
the output metadata now describes an ANSI-equivalent recorder. Acceptance must
rerun focused GREEN and the retained negative control on this corrected backend.

An upstream request for a public output/factory hook should be filed, referencing
[`ApplicationImpl._componentFactory` and the internal factory constructors](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/App/ApplicationImpl.cs#L38)
and [the factory-consuming driver initialization](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/App/ApplicationImpl.Driver.cs#L27).
