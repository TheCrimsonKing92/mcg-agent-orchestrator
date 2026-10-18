# Owner console display plan

This is the long-term display plan for the Control plane epic, based on source at
`e28f1bbf8`. It recommends one display form per owner question, with separately
landable increments. This goal adds documentation and its maintenance test only.

## Inventory

The console pins Terminal.Gui to 2.5.0 at
`src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj:18`.
Paths below are repository-relative; declaration lines identify the owning type.
The main Window contains DECISIONS, BOARD and ACTIVITY frames (25%, 40%, remainder),
plus the EPIC and GATE overlays:
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenView.cs:49` and `:83-97`.

| Type and declaration | What the owner sees | Existing data source |
| --- | --- | --- |
| OwnerConsoleFullScreenView, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenView.cs:11` | Status, decisions, goal board, activity, notices and command line | `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleViewModel.cs:5` |
| OwnerConsoleEpicDialog, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleEpicDialog.cs:9` | EPIC rollup, time window, in-flight goals and plan details | `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleEpicViewModel.cs:8`, backed by `src/Mcg.AgentOrchestrator.App/Cli/EpicProgressRollup.cs:5` |
| OwnerConsoleGateDialog, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateDialog.cs:8` | GATE progress, members, lanes, elapsed time and remaining estimate | `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgress.cs:16`, formatted by `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateFormatter.cs:3` |
| OwnerConsoleDecisionDialog, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleDecisionDialog.cs:8` | Question, evidence, answer and default actions | `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleDecisionDetail.cs:26`, using `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleViewModel.cs:14` |
| OwnerConsoleExperimentDecisionDialog, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleExperimentDecisionDialog.cs:6` | Experiment text, keep/revert/inconclusive choice, evidence and action fields | Constructor inputs at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleExperimentDecisionDialog.cs:11` |
| OwnerConsoleTextDialog, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleTextDialog.cs:8` | Scrollable text and optional selectable choices | Supplied text and choice lines at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleTextDialog.cs:15` |
| OwnerConsoleGoalDialog, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGoalDialog.cs:6` | Refreshable goal details, questions and resolved answers | Its Content record at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGoalDialog.cs:8`, with `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGoalDetailFormatter.cs:7` |
| OwnerConsoleTextPage, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleTextPage.cs:8` | Wrapped text viewport, selection and scroll position inside dialogs | Text and choice-line inputs at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleTextPage.cs:21` |
| OwnerConsoleNoticeStrip, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleNoticeStrip.cs:10` | Bounded success/failure notices; failures persist until their source recovers | Notice records at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleNoticeStrip.cs:6` |
| OwnerConsoleBoardRenderer, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleBoardRenderer.cs:8` | Goal, epic, title, state, stage and age columns; recent terminal rows dimmed | `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleViewModel.cs:21` and attention key at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleBoardSortKey.cs:5` |
| OwnerFleetDiagram, `src/Mcg.AgentOrchestrator.App/Console/OwnerFleetDiagram.cs:6` | Host and executor boxes through the Fleet text dialog | `src/Mcg.AgentOrchestrator.App/Console/OwnerFleetViewModel.cs:17`, built from `src/Mcg.AgentOrchestrator.Core/Reports/RemoteExecutorReport.cs:20` |

Fleet status correction: at the premise's measured main commit `1c91486de`, fleet
diagram slice 1 (goal `3bf498ce`) was pending and not on main. It subsequently
landed on main at `e28f1bbf8` and is present at this plan's baseline. Extend that
renderer rather than duplicate the completed slice. The GATE view from goal
`56ff780e` is also landed. The fleet renderer adds box-drawing literals to console
source, including existing plain Fleet output.

The ten named console view types are all present. The fleet row additionally
documents a string renderer; it is not a Terminal.Gui subclass. The dialog
adapter constructs existing views rather than declaring another view type.
No dedicated time-series view exists at this baseline.

## Use-case classification

Each inventory row has exactly one primary class, even if it contains secondary
information. Classes without a current view still get a proposed display below.

| Primary class | Inventory members | Owner question | Information that answers it |
| --- | --- | --- | --- |
| glanceable status | OwnerConsoleFullScreenView | Is work moving, and do I need to act? | Conductor liveness, active goals, live decisions, freshness and landed/failed counts |
| drill-down | OwnerConsoleEpicDialog, OwnerConsoleGoalDialog, OwnerConsoleTextPage, OwnerConsoleTextDialog, OwnerConsoleDecisionDialog, OwnerConsoleExperimentDecisionDialog | Why is this item here, and what can I do next? | Epic rollup, goal stage and evidence, question text, default, resolution and action fields |
| time series | No dedicated view | Is this lane getting slower over recorded runs? | Ordered recorded durations per lane, sample count and scale; unknown history stays unknown |
| topology or fleet | OwnerFleetDiagram | Which executors are reachable and what capacity has been observed? | Host liveness, probe status/time, configured slots, peak attempts and accepted lane statistics |
| queue or pipeline | OwnerConsoleBoardRenderer, OwnerConsoleGateDialog | What is waiting, running or finished, and where is the gate spending time? | Board attention rank/state/stage/age; gate lane state, location, elapsed time and median or estimate |
| alerts | OwnerConsoleNoticeStrip | What failed or recovered, and does it still need attention? | Notice source, severity, text and existing recovery/expiry lifecycle |

## Recommended display form per use case

These forms compose existing Terminal.Gui 2.5.0 primitives: Label/ListView text,
FrameView borders and TableView row schemes. Source examples are the frames at
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenView.cs:83`, text body
at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleTextDialog.cs:10` and row
scheme at `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleBoardRenderer.cs:13`.
Text mockups omit color; the marker and the written state carry the same meaning.
Fixture values below are committed examples, not claims about a live session.

### Glanceable status: color-coded state glyphs

Keep one status line. Use one marker and a short label per signal, with counts
always visible. Source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleViewModel.cs:11`.

```text
> conductor running   active 1   ? decisions 1
  hidden questions 1  last event 20s   + landed today 3
```

Value origin: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleViewModelBuilderTests.cs:47`
asserts this status tuple. Glyphs and arrangement are proposed.

### Drill-down: small multiples

Use repeated, labelled information panels for a selected subject: state, reason,
evidence and action. Retain complete prose, scrolling, choices and keyboard routes
inside existing text pages; a compact panel never replaces an actionable question.
Source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGoalDialog.cs:8` and
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleDecisionDetail.cs:26`.

```text
+ Experiment -------------------+  + Evidence ------------------+
| outcome: keep                 |  | Evidence reference         |
| keep / revert / inconclusive  |  | [existing reference]        |
+-------------------------------+  +----------------------------+
+ Action -------------------------------------------------------+
| Action [existing action text]                    Submit Cancel |
+---------------------------------------------------------------+
```

Value origin: field labels and choices at
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleExperimentDecisionDialog.cs:16-25`.
Bracketed field contents are illustrative placeholders. The same repeated-panel
form accommodates EPIC counts and goal details from
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleEpicViewModel.cs:8`.

### Time series: sparklines from block characters

Give one lane a bounded, oldest-to-newest duration strip with numeric endpoints
and sample count. This answers trend rather than scheduling or verdict questions.
Source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgress.cs:13`.
Do not mix manifest estimates with measured samples or imply a regular sample
interval. When history is absent show "no recorded durations".

```text
infrastructure tests: C   n=10   oldest -> newest
345s  █▇▆▅▄▃▃▂▁▁  255s   range 255..345s
```

Value origin: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleGateProgressTests.cs:25`
records ten samples (255..345s, with the larger values older). The 99999s sample
at line 24 is older and excluded by the newest-ten rule. Glyph quantization is
proposed. Block elements are not yet rendered by console source. Slice 1 of any
block-element form first verifies the glyphs and cell widths in Windows Terminal;
only after that check may the form ship. Keep numeric values available regardless
of glyph support.

### Topology or fleet: box diagrams on frames

Retain the landed host/executor shape, with reachability labels and no invented
network links. Full-screen color can accompany explicit unknown/reachable/
unreachable states. Source: `src/Mcg.AgentOrchestrator.App/Console/OwnerFleetViewModel.cs:17`;
underlying report: `src/Mcg.AgentOrchestrator.Core/Reports/RemoteExecutorReport.cs:12`,
read by `src/Mcg.AgentOrchestrator.App/Orchestration/RemoteExecutorReportReader.cs:9`.

```text
+ Orchestrator host -----------------------------------+
| > conductor: running           executors: 2           |
+-----------------------------------------------------+
   + Executor alpha -----------------------------------+
   | + reachability: reachable    configured slots: 2   |
   | lane median 20.0s n=3   solo 30.0s conc 15.0s      |
   +---------------------------------------------------+
   + Executor beta ------------------------------------+
   | x reachability: UNREACHABLE   ssh-timeout           |
   | last: none   lanes: no accepted remote runs        |
   +---------------------------------------------------+
```

Value origin: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerFleetDiagramTests.cs:17-30`
and `:44-48`. ASCII box placement is proposed; configured slots and observed peak
attempts remain distinct. An absent probe is unknown, never evidence of failure.

### Queue or pipeline: tables with row color

Keep the existing BOARD grid and give GATE lanes the same aligned-row idiom.
Use state marker plus row color, and preserve the current attention sort and
selection. Gate lanes are a pipeline of independently observed work, not a
predicted schedule. Source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleViewModel.cs:21`
and `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgress.cs:9`.

```text
GATE cohort 776a132e,eade84cb       done 2/3   remaining ~100s
S  LANE                     ELAPSED  MEDIAN  LOCATION       OUTCOME
+  infrastructure tests: A      90s    100s  local          passed
>  infrastructure tests: B     100s    200s  local slot 2
*  infrastructure tests: C     300s    300s  machine-two    done
```

Value origin: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleGateProgressTests.cs:15-43`.
B started 80s before snapshot with a recorded 20s elapsed offset; A and C use
completion durations. The formatter deliberately displays C's unknown terminal
outcome as "done", not "passed"
(`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateFormatter.cs:36-43`).
BOARD keeps its existing goal/epic/title/state/stage/age columns. An optional
summary gauge uses the same fraction, not a second progress model.

### Alerts: color-coded state glyphs

Prefix each existing notice with severity and source. Keep failures visible
until recovery; keep the five-second success expiry. Source:
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleNoticeStrip.cs:6`.

```text
x Refresh: unavailable
+ Action: queued
x Action: unavailable
```

Value origin: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleNoticeStripTests.cs:14-30`.
Repeated refresh failures coalesce without removing action notices. Marker/color
changes must preserve that lifecycle and never signal recovery without evidence.

## Shared visual vocabulary

This is one proposed full-screen vocabulary. Color names refer to the 16-color
palette; each state also has an ASCII marker and written label. Existing lane
markers come from `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateFormatter.cs:34-36`.
Board states come from `src/Mcg.AgentOrchestrator.Core/Domain/OrchestrationEnums.cs:24`;
attention precedence comes from `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleBoardSortKey.cs:13-22`.

| State or signal | Marker | Foreground | Mapping and precedence |
| --- | --- | --- | --- |
| Running lane; WorkerRunning; GateRunning; Verifying | `>` | BrightCyan | Activity observed, not completion |
| Queued lane; WaitingForGate; Verified | `.` | Gray | Waiting; Verified still awaits acceptance |
| Cached lane | `=` | Cyan | Reused evidence, distinct from newly executed success |
| Done lane with passed; Completed; success notice; reachable executor | `+` | BrightGreen | Positive success/reachability evidence |
| Done lane with failed; AcceptanceFailed; Failed; failure notice; unreachable executor | `x` | BrightRed | Positive failure evidence; preserve written reason |
| Done lane with any other outcome | `*` | Yellow | Terminal observation with unspecified outcome; no success inference |
| NeedsYou; live question override | `?` | BrightYellow | Highest live attention rank; keep the goal state text |
| Held; WaitingForHuman | `!` | Yellow | Hold reason accompanies marker; follows NeedsYou |
| Draft; Active with Other rank | `.` | Gray | No stronger observed attention state |
| Parked | `-` | DarkGray | RecentTerminal rank, not a running wait |
| Cancelled; Superseded | `-` | DarkGray | Explicit terminal state retained as text |
| RecentTerminal / Dimmed rows | existing state's marker | DarkGray | Disabled scheme takes precedence over foreground; Failed retains `x` |
| Unknown probe, missing history or stale observation | `?` | Gray | Explicit unknown label; never turn absence into failure |

All GoalStatus values and all seven attention ranks are covered above. The current
Dimmed flag is set from RecentTerminal, with Disabled row rendering at
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleBoardRenderer.cs:13`.
These proposals add no new orchestrator state or verdict.

```text
> running  . queued  = cached  + passed  x failed  * other done
? needs you / unknown (read label)   ! held   - inactive
```

Sketch origin: the existing gate marker switch cited above; board markers are
proposals. Do not use color alone to distinguish states.

- Width: fit terminal cells through `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleLineFitter.cs:11`.
  Validate at 60 and 100 columns, matching the fleet fixture; below 60 stack
  panels, retain state labels and allow scrolling. Keep full text in the model.
- Color: use only the 16-color palette and test focus/selection/Disabled schemes.
  Preserve labels and glyphs when color is unavailable; avoid background fills
  that hide focus or reduce legibility.
- Plain mode: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleModeSelector.cs:8`
  selects explicit plain and redirected operation. Every slice must leave its
  bytes unchanged, including already-landed Fleet box characters. Proposed colors,
  glyphs and layout belong to the full-screen path only.
- Repaint: compose through the landed frame-painting adapter from goal `2dc1161d`,
  `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFrameOutput.cs:9`. It batches a
  composed frame; it is not a license for extra redraw loops. Reuse the view's
  configured refresh interval (`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenView.cs:77`),
  invalidate changed content only, and retain command, resize and notice-expiry
  events. No independent animation timers or per-cell terminal writes.
- Glyphs: box drawing already exists in Fleet. Block elements do not yet appear
  in console rendering. Any block-element form's first slice must verify glyph
  support, alignment and resizing in Windows Terminal before adoption, retaining
  ASCII/numeric fallbacks inside full-screen mode.

## Prioritized slice list

Each entry is one landable goal with a narrow verification boundary. The owner
chooses the first implementation after reviewing this plan; these are proposals,
not dispatched work. Files listed below are intended implementation/test seams.

### Slice 1: Shared lane marker colors in GATE

Apply the vocabulary to existing lane rows, preserving the exact labels and
marker outcomes. Use a small presentation helper that returns attributed row
content; keep the shared string formatter's plain output stable. Verify unknown
terminal outcomes, selection contrast and 16-color rendering. No block elements.

Files: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateDialog.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleStateStyle.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleGateViewTests.cs`
Data source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateFormatter.cs:30`
Plain mode: untouched; no change to plain-mode bytes.

### Slice 2: Board attention colors

Reuse slice 1's vocabulary on BOARD rows, using existing rank and Dimmed values.
Preserve sorting, selection and column fitting; verify that RecentTerminal's
Disabled scheme wins and that a question changes attention without hiding state.

Files: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleBoardRenderer.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleStateStyle.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleBoardAttentionTests.cs`
Data source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleBoardSortKey.cs:5`
Plain mode: untouched; no change to plain-mode bytes.

### Slice 3: Verify block glyphs and add one gate summary gauge

This is slice 1 of the block-gauge form: verify the block glyphs, cell widths,
focus contrast and resize behavior in Windows Terminal before shipping a single
bounded gauge. Drive it from Fraction and keep done/total and remaining estimate
numeric. Fall back to the existing ASCII bar inside full-screen mode. Do not
reinterpret unknown outcomes as passed or add a repaint timer.

Files: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateDialog.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateGauge.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleGateViewTests.cs`
Data source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgress.cs:16`
Plain mode: untouched; shared ASCII formatting and plain-mode bytes stay unchanged.

### Slice 4: One selected lane's duration sparkline

This is slice 1 of the sparkline form: verify its block heights and cell widths
in Windows Terminal, then show the newest ten existing recorded samples for the
selected lane. Include count, range, direction and a numeric fallback. Verify
empty/single/constant history and chronological ordering. Extract the gate
loader's existing history read into a reusable query, and pass a lane-rendering
callback from the full-screen host through its view to GATE. This keeps history
out of new view-model fields and reuses the existing duration store; collect
nothing new. The loader seam is
`src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgressLoader.cs:17`.

Files: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgressLoader.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenHost.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenView.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateDialog.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleLaneSparkline.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleLaneSparklineTests.cs`
Data source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgress.cs:13`
Plain mode: untouched; no change to plain-mode bytes.

### Slice 5: Notice severity and source markers

Render existing severity/source with the vocabulary while preserving coalescing,
success expiry, failure recovery and bounded entries. Verify that repeated
refresh failures do not erase action failures and that no extra timer is added.

Files: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenView.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleStateStyle.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleNoticeStripTests.cs`
Data source: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleNoticeStrip.cs:6`
Plain mode: untouched; no change to plain-mode bytes.

### Slice 6: Fleet reachability colors in the existing text dialog

Build on landed goal `3bf498ce`. Add attributed reachability labels in the
full-screen Fleet presentation, reusing its existing host/executor boxes. Keep
unknown probes, unavailable statistics and configured versus observed capacity
explicit; verify 60/100-column layouts. Add a typed Fleet presentation method to
the existing dialog interface and have the screen controller pass the existing
fleet model to it. This preserves state before text rendering rather than
inferring reachability from formatted strings. Do not replace the shared plain
renderer.

Files: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleScreenController.cs`, `src/Mcg.AgentOrchestrator.App/Console/IOwnerConsoleDialogs.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleTextDialog.cs`, `src/Mcg.AgentOrchestrator.App/Console/TerminalGuiOwnerConsoleDialogs.cs`, `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFleetPresentation.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleFleetPresentationTests.cs`
Data source: `src/Mcg.AgentOrchestrator.App/Console/OwnerFleetViewModel.cs:12`
Plain mode: untouched; existing Fleet diagram bytes stay unchanged.

### Slice 7: Epic summary small multiples

Arrange the existing epic rollup into repeated labelled count panels above its
details. Retain current time-window selection, scrolling and complete text;
verify empty rollups and narrow widths. This is one epic overlay change, not a
replacement of every detail dialog.

Files: `src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleEpicDialog.cs`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleEpicSmallMultiplesTests.cs`
Data source: `src/Mcg.AgentOrchestrator.App/Cli/EpicProgressRollup.cs:5`
Plain mode: untouched; no change to plain-mode bytes.

## Keys and commands

1. Every function is a typed command first; a key is an alias for a command, and help, the hint bar and the command list come from the registry.
2. A letter names a noun and keeps it in every scope: global at the top level and the item's slice inside a selected item. A view-local letter may only use a letter with no global binding.
3. A key is earned by an owner request or by unhandled-key counts in the console diagnostics trace, only for a routine view that needs no argument, with a cap of 10 top-level letters.
4. If a command takes an argument, the key opens the default and the typed form takes the argument.
5. A key whose effect leaves the console is never global and exists only on a selected item behind a confirmation or a form. Conductor start and stop stay typed, with stop needing `--yes`.
6. The reserved keys are `:` command line, `?` help, `/` filter, Esc back, Enter open, Tab pane and `j`/`k`. The `q` key quits at the top level and backs out inside a view; Ctrl+R reloads a view, and letters are not case-sensitive.
7. No key fails silently: an unbound key shows "not bound; press : for commands or ? for help", the command list shows `[key] command`, and help pairs each key with its command.

See `OwnerConsoleBindings` and `OwnerConsoleBindingsTests` for the registry and its checks.

## Non-goals

- No new data collection, probes, history stores or view-model fields.
- No web or GUI front end, screenshots or image assets.
- No change to plain-mode output or redirected output bytes.
- No console implementation in this goal; only this plan and its document test.
- No new scheduling, acceptance verdicts, inferred success or animation loops.
- No duplicate of the landed fleet diagram, and no Terminal.Gui upgrade.
- No filing or dispatching follow-up slices as goals before owner review.
