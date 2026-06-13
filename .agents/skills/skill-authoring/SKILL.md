---
name: skill-authoring
description: Create, update, or review repo-scoped worker skills under .agents/skills. Use for SKILL.md files, skill routing rules, skill selection tests, worker skill catalogs, and evidence that workers used or ignored selected skills.
---

# Skill Authoring

Use this skill for repo-local worker skills and their routing.

## Workflow

- Keep each skill small, procedural, and specific to work another worker will repeat.
- Include only required `name` and `description` frontmatter plus concise task instructions.
- Avoid auxiliary files unless they directly support repeated worker execution.
- Update deterministic routing and tests whenever adding a new skill category.
- Ensure selected skills are available in `.agents/skills/<name>/SKILL.md` before dispatch preflight can require them.

## Verification

- Check that `selected-skills.md` lists the expected skill with `Status: available`.
- Check that worker prompts still request `WORKER_RESULT skills`.
- Report skill usage or non-usage in `WORKER_RESULT skills`.
