# CLAUDE

<!-- HARNESS-COUNTERPART-CONTRACT:BEGIN -->
Owns: Claude Code harness operating guidance.
Counterpart: AGENTS.md owns shared repository discipline plus Codex harness operating guidance.
Rule: Agents editing shared-discipline content must update BOTH AGENTS.md and CLAUDE.md counterpart-contract blocks and shared-anchor lists, or move the content to docs/operator-runbook.md or another shared home.
Operator procedure home: docs/operator-runbook.md owns harness-neutral operate/observe/recover procedures, including conductor stop and relaunch semantics; counterpart files point there instead of duplicating them.
Role capability home: docs/role-capability-matrix.md owns the enforcement-sourced worker capability and acceptance-criterion routing matrix; counterpart files point there instead of duplicating it.
Shared anchors:
- output-discipline
- retry-and-loop-control
- repository-rules
- architecture-and-design-discipline
- specification-discipline
- diagnosis-discipline
- dashboard-dogfood-boundary
- operating-the-goal-loop
- safety
- evidence
<!-- HARNESS-COUNTERPART-CONTRACT:END -->

Attention is scarce. Preserve it.

Claude Code auto-reads this file. Shared repository discipline stays in [AGENTS.md](AGENTS.md), at the anchors listed in the counterpart contract above; do not duplicate those sections here.

> **Operating the orchestrator - driving, observing, or recovering goals? Start with [`docs/operator-runbook.md`](docs/operator-runbook.md).** It is the harness-neutral canonical conductor guide, including the stuck-goal playbook and state/store map.
> **Test-design discipline?** Use the shared [`test-design-discipline`](docs/test-design-discipline.md) guidance and Reviewer checklist requirement.
> **Writing acceptance criteria?** Assign each criterion to a capable evidence owner using the shared [`role-capability-matrix`](docs/role-capability-matrix.md) before creating the goal.
> **Assigning a verdict, terminal state, circuit trip, escalation, or retry-vs-fail choice?** Use [`dispositive-decision-discipline`](docs/dispositive-decision-discipline.md). One check: *could I write the justification for this outcome from what is in scope right here?* If not, the discriminating evidence was discarded upstream and the decision is a guess. Five instances of this shipped in a single day.

## Claude Harness Guidance

Prefer Claude Code's dedicated Read, Grep, and Glob tools for source inspection. Use shell commands for repo helpers, runtime checks, and commands whose output is the actual evidence.

Run long-lived work as background tasks with completion notifications when possible. Do not pace a loop with repeated sleep/poll commands when a persistent monitor can wake on the event.

Issue single bare shell commands so Claude permission allowlist prefixes match predictably. Avoid chaining, env-prefix wrappers, and incidental pipes in ordinary commands. When the task truly is a live monitor, use one deliberate persistent Monitor command rather than repeated ad-hoc polling.

For conductor loop monitoring, prefer a persistent Monitor on the stable structured stream, starting at the current end: `tail -n 0 -F .orchestrator/logs/conduct-events.log | grep --line-buffered -E "<event patterns>"`. It is JSON lines with `eventKind`, goal ids, phase changes, and long-gate heartbeats, and it survives rotation. Do not poll goal state or process lists while relevant heartbeats continue; per-batch loop output is fallback evidence only. Use a higher-level subscriber only after confirming that it honors the current cursor plus goal/event filters.

Use the runbook's manual-bounce procedure after loop-affecting landings and its `.conduct-stop` section for detach semantics, PID/lock validation, and relaunch. After ANY manual worktree-state repair (stash pop, privileged rebase finish), the repair is complete only when the next tick's dispatch actually forms — a dirty goal worktree blocks all dispatch with only a generic "no ready batch" hold, so commit restored work on the goal branch rather than leaving it unstaged.

To reopen an AcceptanceFailed goal whose verdict genuinely stands (flake or environment-caused gate failure) without a paid worker round, leave the loop running and submit `retry <goal> <last-task#> "<reason>" --mechanical`, then `progress <goal> <task#> completed "<justification>"`, then `verify-manual <goal> <task#> passed "<evidence>"`. These verbs always append typed records to `operator-intents.db`; the tick is the sole `state.db` writer and each submitting surface can poll the recorded outcome. If the loop is down, the intents remain pending until a conductor starts; the CLI never applies them directly.

Use dashboard cancel/refresh controls or exact known process ids for stuck workers. Never run broad process cleanup.

## Orchestrator Notes

Both Claude and Codex operators should use `docs/operator-runbook.md` for the operate/observe/recover flow. Claude-specific mechanics belong here; shared discipline and architecture/spec/evidence rules belong in `AGENTS.md`.
