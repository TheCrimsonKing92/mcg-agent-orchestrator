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

- [OutputBase.cs:145-155,194-207,271-296](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/Output/OutputBase.cs#L145)
  processes dirty rows in order, positions the cursor, and submits each row/run separately.
- [WindowsOutput.cs:217-227,257-269,403-430](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/WindowsDriver/WindowsOutput.cs#L217)
  maps cursor movement and content to separate immediate native `WriteConsole` calls.
  The later call at lines 340-367 does not buffer the earlier calls. Counting only
  explicit `Flush` calls would miss these submission boundaries. Legacy mode also
  writes per batch (lines 419-422).
- [DriverImpl.cs:93-104](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/Terminal.Gui/Drivers/DriverImpl.cs#L93)
  refreshes by passing its output buffer to that output component.

The source-supported candidate mechanism is row/run submission before the entire
frame is committed. This is not yet an executed reproduction or attribution of the
operator's visual symptom. It neither identifies the operator's active backend nor
proves which intermediate writes the host displays.

## Bounded reproduction authored for Tester execution

`OwnerConsoleFrameReproductionTests.PagingExposesRowCommitsInPinnedOutputBase`
uses 60 board rows and a simulated 72-column, 10-row content viewport (six viewport
heights). It drives actual view Page Down, Page Up and twelve Down steps, then calls
the actual pinned `OutputBase.Write(IOutputBuffer)` implementation with an ASCII
projection of the rendered board table. No source copy of the dependency row loop
is used. The recording subclass maps cursor/content callbacks to the immediate
submission boundaries found in WindowsOutput.

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
intercept the internal WindowsOutput or make native calls. Rendering timing includes
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

## Implementation boundary requiring escalation

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
Console.Out would miss native Windows calls. No production fix was applied because
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
