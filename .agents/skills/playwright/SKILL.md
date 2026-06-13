---
name: playwright
description: Validate dashboard and web UI behavior with browser automation. Use for dashboard UI flows, prototype UI checks, screenshots, EventSource or refresh behavior, form submission, navigation, accessibility smoke checks, and end-to-end browser validation.
---

# Playwright

Use this skill when a task needs real browser evidence instead of source-only inspection.

## Workflow

- Prefer checked-in dashboard helpers over ad hoc browser scripts.
- Keep browser checks narrow: one flow, one viewport set, and exact assertions tied to the task.
- Capture screenshots only when they prove layout, rendering, or interaction behavior.
- Avoid leaving browser profiles, reports, screenshots, or scratch scripts in source control unless the task explicitly asks for artifacts.

## Verification

- Report the helper command or Playwright command, target URL, and result in `WORKER_RESULT tests`.
- If browser automation is blocked by a missing server, port, or dependency, report it in `WORKER_RESULT blockers` with the exact next command.
