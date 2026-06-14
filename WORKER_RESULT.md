## WORKER_RESULT

task_id: d07c40135e214124b9ab07ce856f3372
status: success
commit: 797c1de

### Summary

Generalized orchestrator planners/brokers beyond the self-hosted .NET assumption by introducing a `TargetToolchainDetector` and wiring it through all three emission sites.

### Changes

**New file: `src/Mcg.AgentOrchestrator.Infrastructure/Workers/TargetToolchain.cs`**
- `Toolchain` enum: `Dotnet, Go, Node, Python, Unknown`
- `TargetToolchainDetector.Detect(workingDirectory)` — priority-ordered marker detection: `go.mod` → Go; `package.json` → Node; `*.sln`/`*.csproj` (root or up to 2 levels deep) → Dotnet; `pyproject.toml`/`requirements.txt` → Python; else Unknown
- `GetSourceExtensions(toolchain)` — toolchain-specific extension arrays; Unknown uses a comprehensive catch-all so unrecognized repos remain fully indexed
- `GetBrokerCommandHint(toolchain, goalPrefix, attemptName)` — per-toolchain suggested command string
- `GetBrokerBuildTestNote(toolchain)` — per-toolchain usage note for workflow-brokers.md

**Modified: `src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerContextArtifacts.cs`**
- `EnumerateSourceFiles` — removed static `.NET`-biased extension list; now calls `TargetToolchainDetector.Detect()` + `GetSourceExtensions()` so Go repos index `.go` files, etc.
- `BuildDeterministicVerification` — emits `## Isolated .NET Verification` with `Invoke-IsolatedDotnet.ps1` for Dotnet; `## Go Verification` with `go build/test` for Go; Node/Python analogues; generic `## Verification` for Unknown
- `BuildWorkflowBrokers` — uses `GetBrokerCommandHint`/`GetBrokerBuildTestNote` instead of hardcoded dotnet strings

**Modified: `src/Mcg.AgentOrchestrator.App/Orchestration/GoalObjectivePlanner.cs`**
- `BuildRequiredTools` — delegates to new `InferBuildTestTool()` which uses text-signal detection on tokens and file-scope paths: `.cs`/`dotnet`/`sln` → `Invoke-IsolatedDotnet.ps1`; `.go`/`golang` → `go build / go test`; `.ts`/`npm`/`yarn` → npm/yarn; `.py`/`python`/`pytest` → python/pytest; fallback → generic string
- No orchestrator-local helper paths leak into generic or non-.NET objectives

**New file: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ToolchainDetectionTests.cs`**
- 18 new tests covering: detection from each marker file, priority ordering, extension list contents, source survey Go filtering, deterministic-verification per toolchain, workflow-brokers per toolchain, GoalObjectivePlanner required-tools (no leak, go signal, cs signal)

**Modified: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs`**
- Added `TestRepo.sln` marker to `WorkerProfileDispatcherWritesContextArtifactsWithRepoGuidanceAndFullerPriorEvidence` fixture so existing `.NET Verification` assertions continue to pass

### Verification

```
.\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix "5f88085a" -AttemptName "developer-d07c4013" test Mcg.AgentOrchestrator.sln --verbosity minimal
```

Results:
- Core.Tests: Passed 273, Failed 0, Skipped 0
- Infrastructure.Tests: Passed 635, Failed 0, Skipped 0
- **Total: 908 passed, 0 failed**

### Blockers

none
