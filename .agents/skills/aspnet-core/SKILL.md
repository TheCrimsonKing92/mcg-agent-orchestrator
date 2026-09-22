---
name: aspnet-core
description: Build, review, or modify ASP.NET Core and dashboard web surfaces in this repository. Use for Blazor, Razor, MVC, Minimal APIs, middleware, SignalR, authentication, authorization, endpoint routing, dashboard rendering, dashboard API DTOs, and focused web tests.
---

# ASP.NET Core

Use this skill for web-facing .NET changes in `src/Mcg.AgentOrchestrator.App`.

## Workflow

- Locate the endpoint, renderer, DTO, or parser before editing.
- Keep dashboard/API changes consistent between server DTOs, rendering, scripts, and tests.
- Prefer focused endpoint or rendering tests before broad project runs.
- Preserve existing route names, command shapes, and compact operator-facing output unless the task explicitly changes them.
- For dashboard JavaScript, add tests that assert durable behavior markers rather than brittle formatting.

## Verification

- Run `scripts/Invoke-TestSummary.ps1 -Target <test-project> -Filter 'FullyQualifiedName~<class>'` with the narrowest relevant class or method filter; do not run raw `dotnet test`, which launches the generated native test apphost on Windows.
- For browser behavior, prefer existing dashboard/browser helpers when available.
- Report the exact test filter and result in `WORKER_RESULT tests`.
