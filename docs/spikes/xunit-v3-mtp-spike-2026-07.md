# xUnit v3 + Microsoft.Testing.Platform Spike

Date: 2026-07-17

Scope: temporarily convert `tests/Mcg.AgentOrchestrator.Core.Tests` to xUnit v3 + Microsoft.Testing.Platform (MTP), measure against the current VSTest path, revert the conversion, and land this report only.

## Recommendation

No-go for migrating `Infrastructure.Tests` under the current gate.

MTP is materially faster for `Core.Tests`, but the current gate cannot switch by package changes alone. The existing gate uses `dotnet test`, VSTest `--filter FullyQualifiedName...` expressions, VSTest TRX logger arguments, and `--blame-hang-timeout`. The MTP executable path needs a gate command adapter first.

Recommended next step: build a small Core-only gate adapter that invokes the project test executable from a stable slot, translates focused filters, emits TRX via `--report-trx`, and replaces hang detection with an explicit MTP timeout policy. Do not migrate `Infrastructure.Tests` until that adapter is proven.

## Environment

- OS: Windows, local worktree `C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\2c0971f7`
- SDK: `dotnet --version` -> `10.0.103`
- Baseline project: xUnit v2, `Microsoft.NET.Test.Sdk` 17.11.1, `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2
- Temporary MTP project: `xunit.v3.mtp-v2` 3.2.2, `Microsoft.Testing.Extensions.TrxReport` 2.3.2
- Note: the shared NuGet cache had permission failures. MTP restore/build used `NUGET_PACKAGES=<repo>\.scratch\xunit-v3-mtp-spike\nuget-packages`.

## Commands Run

Baseline setup:

```powershell
dotnet build-server shutdown
dotnet build .\tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --nologo --verbosity minimal -clp:ErrorsOnly
```

VSTest baseline, cold and warm:

```powershell
dotnet test .\tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --no-build --logger trx --results-directory .\.scratch\xunit-v3-mtp-spike\vstest-cold --verbosity minimal --blame-hang-timeout 120s --blame-hang-dump-type none
dotnet test .\tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --no-build --logger trx --results-directory .\.scratch\xunit-v3-mtp-spike\vstest-warm --verbosity minimal --blame-hang-timeout 120s --blame-hang-dump-type none
```

MTP setup:

```powershell
$env:NUGET_PACKAGES = (Resolve-Path .).Path + '\.scratch\xunit-v3-mtp-spike\nuget-packages'
dotnet build .\tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --nologo --verbosity minimal -clp:ErrorsOnly
```

MTP execution, cold and warm:

```powershell
.\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe --no-ansi --progress off --results-directory .\.scratch\xunit-v3-mtp-spike\mtp-cold --report-trx --report-trx-filename mtp-cold.trx --timeout 120s
.\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe --no-ansi --progress off --results-directory .\.scratch\xunit-v3-mtp-spike\mtp-warm --report-trx --report-trx-filename mtp-warm.trx --timeout 120s
```

Compatibility probes:

```powershell
.\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe --no-ansi --progress off --results-directory .\.scratch\xunit-v3-mtp-spike\compat\mtp-filter-class --report-trx --report-trx-filename filter-class.trx --filter-class GoalLifecycleTests
.\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe --no-ansi --progress off --results-directory .\.scratch\xunit-v3-mtp-spike\compat\mtp-filter-not-class --report-trx --report-trx-filename filter-not-class.trx --filter-not-class GoalLifecycleTests
.\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe --no-ansi --progress off --results-directory .\.scratch\xunit-v3-mtp-spike\compat\mtp-gate-filter-direct --report-trx --report-trx-filename gate-filter-direct.trx --filter 'FullyQualifiedName~GoalLifecycleTests'
dotnet test .\tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --no-build --filter 'FullyQualifiedName~GoalLifecycleTests' -- --no-ansi --progress off --results-directory .\.scratch\xunit-v3-mtp-spike\compat\dotnet-mtp-gate-filter --report-trx --report-trx-filename dotnet-mtp-gate-filter.trx
.\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe --no-ansi --info
.\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe --no-ansi --help
```

## Measurements

| Runner | Run | Wall | TRX/test-run elapsed | Total | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| VSTest `dotnet test` | cold | 2.432s | 1.437s | 513 | 513 | 0 | 0 |
| VSTest `dotnet test` | warm | 1.912s | 1.267s | 513 | 513 | 0 | 0 |
| MTP executable | cold | 0.693s | 0.332s | 513 | 513 | 0 | 0 |
| MTP executable | warm | 0.630s | 0.336s | 513 | 513 | 0 | 0 |

Observed improvement:

- Cold wall: MTP was 1.739s faster than VSTest.
- Warm wall: MTP was 1.282s faster than VSTest.
- TRX/test-run elapsed: MTP reported about 0.33s versus VSTest 1.27s to 1.44s.

## Temporary Conversion Deltas

Required csproj deltas for the working MTP executable:

```diff
+    <OutputType>Exe</OutputType>
+    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
...
-    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
-    <PackageReference Include="xunit" Version="2.9.2" />
-    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
-      <PrivateAssets>all</PrivateAssets>
-      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
-    </PackageReference>
+    <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.3.2" />
+    <PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" />
```

Attempted but not acceptable under SDK 10 gate shape:

```xml
<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
```

With SDK 10, `dotnet test` against the MTP-enabled project failed with:

```text
Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.
```

Package compatibility delta:

- `xunit.v3` 3.2.2 plus `Microsoft.Testing.Extensions.TrxReport` 2.3.2 failed at executable startup with `System.TypeLoadException` for `Microsoft.Testing.Platform.Extensions.TestHost.IDataConsumer`.
- Switching to `xunit.v3.mtp-v2` 3.2.2 fixed the MTP v1/v2 mismatch.

Code deltas:

- None. `Core.Tests` compiled and passed without test-source changes.
- No xUnit v3 assertion or attribute API changes were required in this project.
- Build warnings after isolated restore were only `NU1900` vulnerability-cache permission warnings from NuGet, not xUnit API warnings.

## Compatibility Questions

### 1. Filter Parity

Verdict: FAIL as-is.

Evidence:

- Native MTP/xUnit include filter worked: `--filter-class GoalLifecycleTests` ran 46 tests, all passed.
- Native MTP/xUnit exclude filter worked: `--filter-not-class GoalLifecycleTests` ran 467 tests, all passed.
- Existing gate-style direct filter failed: `--filter FullyQualifiedName~GoalLifecycleTests` exited 5 and produced no TRX.
- Existing `dotnet test --filter FullyQualifiedName~GoalLifecycleTests` against the MTP-enabled project exited 1 on SDK 10 before running tests because VSTest target execution is no longer supported for MTP.

Implication: the gate must translate its current VSTest filter grammar. Examples:

- `FullyQualifiedName~GoalLifecycleTests` -> `--filter-class GoalLifecycleTests` or a query-filter equivalent.
- `FullyQualifiedName!~GoalLifecycleTests` -> `--filter-not-class GoalLifecycleTests`.
- `Category!=HostIntegration` -> `--filter-not-trait Category=HostIntegration`.
- Existing compound `&` and `|` expressions need deterministic translation tests before Infrastructure migration.

### 2. TRX/Report Output

Verdict: PASS with bridge.

Evidence:

- Adding `Microsoft.Testing.Extensions.TrxReport` 2.3.2 exposed `--report-trx` and `--report-trx-filename`.
- MTP commands emitted `.trx` files with 513 `UnitTestResult` nodes and normal counters.
- The report file accepted a stable filename: `--report-trx-filename mtp-cold.trx`.

Implication: the gate must replace VSTest logger arguments:

- Current: `--logger "trx;LogFileName=<name>.trx" --results-directory <dir>`
- MTP executable: `--results-directory <dir> --report-trx --report-trx-filename <name>.trx`

### 3. Firewall and Executable Path

Verdict: PASS with a stable-slot executable path; FAIL if run from per-worktree `bin`.

Evidence:

- MTP execution was the project apphost: `tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.exe`.
- `--info` reported the test module as `...\tests\Mcg.AgentOrchestrator.Core.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Core.Tests.dll`.
- No `testhost.exe` process is part of the direct MTP command shape.

Implication: for unattended slots, the gate should build/run the per-project test executable from a bounded stable-slot artifacts path, not from each goal worktree's `bin` path. That keeps firewall rule cardinality bounded by slot/project executable paths rather than per-goal apphost paths.

### 4. Serialized Collections and AssemblyInfo Carryover

Verdict: PASS for `Core.Tests`.

Evidence:

- `rg` found no `AssemblyInfo`, `CollectionBehavior`, `DisableTestParallelization`, `CollectionDefinition`, or `xunit.runner.json` in `Core.Tests`.
- The converted project passed all 513 tests with xUnit v3 default collection parallelization.
- MTP help showed `--parallel collections` as the default and `--parallel none` as the explicit serialized option.

Implication: `Core.Tests` has no collection/assembly carryover work. Other projects must be checked individually before migration, especially any project with explicit collection behavior or host integration serialization.

### 5. Hang Detection

Verdict: FAIL as a drop-in replacement.

Evidence:

- Current gate arguments include `--blame-hang-timeout 120s --blame-hang-dump-type none`.
- MTP help exposes `--timeout <duration>` as a global run timeout and xUnit exposes `--long-running <seconds>` for long-running test detection.
- The measured MTP runs used `--timeout 120s` successfully.
- No equivalent to VSTest `--blame-hang-timeout` plus dump policy was verified.

Implication: replacing the current hang story needs an explicit gate design. The likely minimum is `--timeout 120s` plus `--long-running 120`, but that is not telemetry-equivalent to VSTest blame output and should be tested with a deliberate hanging fixture before migration.

## Pipeline Unit Estimate

- `Core.Tests`: 1 developer round. Package/properties only; no source API deltas.
- Gate MTP adapter before any broad migration: 2 to 3 developer rounds. Scope: command construction, stable-slot executable resolution, filter translation, TRX telemetry, timeout policy, and acceptance tests.
- `Infrastructure.Tests`: 3 to 4 developer rounds after the adapter exists. Scope is larger because the current gate partitions Infrastructure by many `FullyQualifiedName~/!~` filters and excludes `Category!=HostIntegration`.
- Additional test projects: 1 to 2 developer rounds each if they have no custom collection behavior; 2 to 3 if they use serialization, host processes, or custom report assumptions.

## Final State

The xUnit v3/MTP conversion was reverted. The intended landed diff is this report only.
