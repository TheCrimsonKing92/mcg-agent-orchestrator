---
name: verification-before-completion
description: Require fresh, inspected execution evidence before a Developer claims completion.
---

# Verification Before Completion

Before claiming completion:

1. Identify the command or observable state that proves each changed behavior and the original symptom.
2. Run the proof now; inspect its output, exit code, result counts, and repository diff or state change.
3. For action-required work, prove the action executed. Status text or an agent narrative without an observable diff/state change and command receipt is not evidence.
4. Report failures honestly. A killed, timed-out, or result-less command is inconclusive. Race or contention claims require the controlled comparison defined by `AGENTS.md`.
5. Walk the requested criteria individually before writing the completion claim.

Reuse repository authority rather than restating it: `docs/role-capability-matrix.md` for evidence ownership, `docs/test-design-discipline.md` for asserted execution and negative controls, `docs/dispositive-decision-discipline.md` for result classification, and `.agents/skills/orchestrator-worker-verification/SKILL.md` for worker-output trust.

Independently condensed from the `verification-before-completion` ideas in [obra/superpowers](https://github.com/obra/superpowers/blob/main/skills/verification-before-completion/SKILL.md), MIT licensed, copyright 2025 Jesse Vincent.
