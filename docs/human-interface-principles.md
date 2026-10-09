# Human interface principles

Owns: design rules for every surface a person reads or drives (the owner console in both modes, CLI output meant for people, notices, and any future web or chat surface).
Applies to: briefs that change a human surface, and the Reviewer's check of them.

I wrote these after a day of using the owner console against the live board (2026-10-08 and 09). Each one comes from something that confused or misled me. The quotes are what I said at the time. Each rule ends with a check you can run on a screen, a test, or a screenshot, so a worker or Reviewer can apply it without guessing what I'd think.

## 1. Serve the person reading it

The console exists so I can see what my agents are doing and decide whether I'm needed. Every line should make sense to someone who doesn't know the conductor's internals. "Conductor handoff completed: none" told me nothing ("What's a human supposed to make of that? ... Remember who this needs to serve").

Check: read each line aloud to someone who has never seen the code. If they ask "what does that mean?", rewrite it in terms of goals, stages and outcomes. Internal event names, enum values and field dumps stay out of human text.

## 2. Only say "you need to act" when I actually do

The attention signal is the most valuable thing on the screen, and it stops working the moment it cries wolf. ACTIVITY showed nine "Needs you" lines in an hour for questions the Author answered within minutes. "What this means" told me to act while DECISIONS said nothing needed me ("This doesn't actually tell me anything about what needs me!").

Check: every "Needs you" line and every "Yes" to "Do you need to act" points at an item listed in DECISIONS right now. If the Author, the operator or the conductor will handle it, say who, and answer No.

## 3. Report the true state, including in-between states

Our actions are asynchronous: answers are queued intents, gates run for minutes, loads happen on demand. When the console collapses "queued" into "failed" it lies. I answered a question and was told "answer was not accepted" when it had been queued and was then applied.

Check: for anything asynchronous, the screen distinguishes at least *pending*, *done* and *failed*, and updates when the state changes. Nothing reports failure because a result hasn't arrived yet.

## 4. Never show a blank or ambiguous screen

A blank pane looks broken or empty, and I can't tell which. The epic view opened blank until its data arrived.

Check: every view that loads data shows a loading state immediately, a failure state with the reason and a way to retry, and an empty state that says what is empty and why (for example "No epics are defined yet" versus "No epic activity in the last 24 hours"). Those three never look alike. A quiet period also has to be distinguishable from missing data ("Has there really been no activity since 11:37 or is the activity pane not exposing everything?").

## 5. Connect every item to its context

An event floating free of its goal is close to useless. A "Resolved" line told me a question was answered, but not which goal asked it, at which stage, or who answered.

Check: every activity line, dialog and notice names the goal (prefix, plus title where there's room) and, where it applies, the stage, the task role and the actor (Author, operator, owner, conductor or a worker role). From any item I can get to the goal detail it belongs to.

## 6. Show the substance

If I have to leave the console to learn what happened, the console didn't do its job. Examples: the resolved question didn't show the answer, and a Reviewer send-back said only "a problem needs correction".

Check: a failure shows the failing test (Class.Method) and the first line of its message. A send-back shows the first finding. A resolved question shows the answer text. A hold shows the recorded blocker. "Reason not recorded" appears only when no record exists anywhere.

## 7. Keep detail human-readable

Opening goal 4f9fe089's detail gave me "a huge bunch of JSON that's basically human-unreadable".

Check: no JSON, stack traces, GUID-only references or serialized records in any human view. Long identifiers are shortened to their prefix. Raw records belong in the CLI or in files that a "show raw" action opens on purpose.

## 8. Fit text to the space you actually have

Text should use the room on screen. Titles were cut at about 40 characters on a 190-column pane with most of the line empty, while other lines ran off the edge. Mixed truncation looks careless and hides what I need.

Check:
- Single-line rows (ACTIVITY, BOARD, DECISIONS) are fitted at render time to the current width and re-fitted on resize. When something has to be cut, the less important part goes first (the title before the time, goal prefix or outcome), with an ellipsis exactly at the edge.
- Dialogs and detail views wrap and scroll and never cut text.
- The view model keeps full text, and only rendering decides what fits.

## 9. Say each thing once

Repetition buries the information. Goal detail repeated the goal's full title in every recent event, and showed "Status: Verified" beside "Stage: Verified" and "Role: -".

Check: inside a view about one thing, don't repeat what the header already says. Collapse fields that carry the same value, and omit fields with nothing to say.

## 10. Standard navigation everywhere

I shouldn't have to learn a new key scheme per pane ("what if I want to get to the top or bottom?").

Check: every scrollable list or text supports Up/Down, PgUp/PgDn and Home/End, and they move the selection so Enter acts on what's shown. Esc backs out one level. The hint line and `?` help list the keys that work in the current view, and only those.

## 11. Plans are live structure

Plans written as prose go stale the moment work lands. Epic plans needed hand rewrites to stay true. If a screen shows plans or progress, compute it from the goals, backlog and decisions it describes, so it updates as items are worked.

Check: nothing on a human surface is hand-maintained text that restates state the system already knows.

## 12. Fast to open, honest about slow parts

A console I avoid opening because it's slow is a console I don't use ("The initial console launch is seeming quite slow"). Load what's needed for the first screen and fetch the rest on demand, with the loading state from principle 4.

Check: measure time to first usable screen against the live board, and report it in the goal's evidence when a change touches startup or loading.

## How to use this

- **Briefs** for a human surface name the principles they touch and turn each into a headless-view test assertion.
- **Reviewers** check the diff against each named principle and against the checks above.
- **Before telling the owner a console change is ready**, the operator drives it against the live board (`.orchestrator/operator-tools/LiveConsoleDriveProbe.cs.txt`) and looks at the actual text. Tests on fixtures prove the mechanism; only the live board shows whether it reads well.
- **When I report a new problem**, add it to the principle it breaks, with the quote. Add a new principle only if none fits.
