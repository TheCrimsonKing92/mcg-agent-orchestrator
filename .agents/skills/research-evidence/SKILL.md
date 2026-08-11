---
name: research-evidence
description: Research repository questions with anchored, classified evidence and stop when authoritative current evidence invalidates the premise.
---

# Research Evidence

Use this procedure for Researcher tasks.

1. State the named evidence question before searching.
2. Anchor every material statement to an exact source, artifact, symbol, line, command receipt, or primary-source link.
3. Label every material statement `Fact`, `Inference`, or `Unknown`. For each unknown, name the evidence that would settle it and the required source; distinguish inaccessible evidence from evidence that was never recorded.
4. If authoritative current evidence disproves the brief's premise, stop with a `premise-invalid` disposition through the centrally emitted `AgentOutputDirectives` worker-result contract, citing the fact and evidence instead of inventing implementation work. Otherwise, report the remaining unknowns without treating them as contradictions.

Follow `AGENTS.md`, `docs/role-capability-matrix.md`, `docs/test-design-discipline.md`, and `docs/dispositive-decision-discipline.md` as canonical authority. Reference their contracts; do not reproduce or replace them.
