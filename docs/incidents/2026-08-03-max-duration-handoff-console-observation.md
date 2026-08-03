# Max-duration handoff console observation

Status: **INCONCLUSIVE — suppression change gated**

The reported visible-console flash was traced to the Windows max-duration handoff's direct
`CreateProcessW` path, not `Start-OrchestratorCommand.ps1`. Before changing that guarded spawn,
the incumbent's `GetConsoleWindow()` state was journaled immediately before `CreateProcessW` as
`incumbentConsole=present|absent`, adjacent to `spawnPath=windows-createprocess`.

## Pre-fix observations

The real `conduct --loop --daemon --watch --poll-seconds 1 --max-duration 3 --quiet` path ran in
isolated empty workspaces. Each incumbent was launched console-less. A WinEvent
`EVENT_OBJECT_SHOW` hook plus top-level-window enumeration sampled the complete handoff window.
No real goal state or paid worker was available to these runs.

| Run | Incumbent console | New visible top-level windows | Window-show events |
| --- | --- | ---: | ---: |
| obspre1 | absent | 0 | detector v1 (console-class polling only) |
| obspre2 | absent | 0 | detector v1 (console-class polling only) |
| obspre3 | absent | 0 | detector v1 (console-class polling only) |
| obspre4 | absent | 0 | 0 |

Runs `obspre5` through `obspre9` also reached the instrumented Windows spawn path, but their
combined observation command exceeded its outer 60-second receipt window; they are not counted
as window-observation evidence.

## Gate decision

The hypothesis is neither confirmed nor refuted. These observations establish the predicted
console-less incumbent state but did not reproduce the intermittent flash. They therefore do not
show that console absence correlates with the flash, while absence-without-flash does not disprove
the narrower claim that a flash can occur only in that state.

Per the goal's hypothesis gate, hidden-console acquisition was not added. The permanent diagnostic
remains so a naturally occurring flash can be paired with incumbent console state. A post-worker-
dispatch handoff and visual confirmation also remain outstanding.
