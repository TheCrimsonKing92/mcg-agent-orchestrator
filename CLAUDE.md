# CLAUDE

<!-- HARNESS-COUNTERPART-CONTRACT:BEGIN -->
Owns: Claude Code harness operating guidance.
Counterpart: AGENTS.md owns shared repository discipline plus Codex harness operating guidance.
Rule: Agents editing shared-discipline content must update BOTH AGENTS.md and CLAUDE.md counterpart-contract blocks and shared-anchor lists, or move the content to docs/operator-runbook.md or another shared home.
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

## Claude Harness Guidance

Prefer Claude Code's dedicated Read, Grep, and Glob tools for source inspection. Use shell commands for repo helpers, runtime checks, and commands whose output is the actual evidence.

Run long-lived work as background tasks with completion notifications when possible. Do not pace a loop with repeated sleep/poll commands when a persistent monitor can wake on the event.

Issue single bare shell commands so Claude permission allowlist prefixes match predictably. Avoid chaining, env-prefix wrappers, and incidental pipes in ordinary commands. When the task truly is a live monitor, use one deliberate persistent Monitor command rather than repeated ad-hoc polling.

For conductor loop monitoring, prefer a persistent Monitor on the stable structured stream, starting at the current end: `tail -n 0 -F .orchestrator/logs/conduct-events.log | grep --line-buffered -E "<event patterns>"`. It is JSON lines with `eventKind`, goal ids, phase changes, and long-gate heartbeats, and it survives rotation. Do not poll goal state or process lists while relevant heartbeats continue; per-batch loop output is fallback evidence only. Use a higher-level subscriber only after confirming that it honors the current cursor plus goal/event filters.

For graceful conductor stops, create `.conduct-stop`, wait for `LOOP_STOP` in `conduct-events.log`, then remove `.conduct-stop` before any new loop. Bounded loops self-renew on `--max-duration`; relaunch manually only after `LOOP_HANDOFF_FAILED`, deliberate stop/Ctrl-C, code/config changes, or an all-done stop followed by new goals.

Use dashboard cancel/refresh controls or exact known process ids for stuck workers. Never run broad process cleanup.

## Orchestrator Notes

Both Claude and Codex operators should use `docs/operator-runbook.md` for the operate/observe/recover flow. Claude-specific mechanics belong here; shared discipline and architecture/spec/evidence rules belong in `AGENTS.md`.
