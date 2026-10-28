# Explicit Crosswalk acceptance checks

Declare `{"name":"vitest selection","type":"crosswalk-test","project":"web","arguments":["adapter-owned-id"],"timeoutMinutes":2}` in the acceptance manifest. Omit `project` or use an empty string for the candidate worktree root. The directory must exist and remain inside that worktree after resolving links. Crosswalk is never selected implicitly by this check implementation.

`arguments` is an array of exact, ordinal, opaque IDs; an empty array selects all discovered IDs. Acceptance always lists before running. Blank, duplicate, unknown or ambiguous discovered IDs fail before test execution. IDs are passed unchanged using repeated `--id` arguments. Set `MCG_ORCHESTRATOR_CROSSWALK_PATH` to the executable file.

One timeout budget covers listing, running and receipt publication. Processes use the acceptance owner and its capture/drain lifecycle. Results use the existing `mcg-test-result/1` reader/writer. Passing requires exit zero, readable results, positive execution, executed coverage for every selection, and no failed/error records. Additional passing IDs are permitted; additional failures fail the check. Skipped tests cannot satisfy selected coverage.

Check receipts carry normalized paths, attempt ID, invocation ordinal, counts, failing IDs, stderr, exit status and typed `crosswalkDiagnostics` (the first is primary). Usable returned evidence survives a nonzero exit. Paths are allocated by the attempt, never accepted from external JSON. No TRX is created. The current TRX-required cohort consumer explicitly reports incompatibility; use standalone owned acceptance for this slice. Default .NET/Go routing and stack detection are unchanged.

## Operator proof, required before final acceptance

Status: prospective; no real Crosswalk/Vitest demonstration was performed by Developer. Acceptance owns regression execution. Operator owns the steps below, after the candidate is committed and reviewed. Keep this run outside the orchestrator's goal/landing state.

Prerequisites: .NET 10 SDK, Node/npm compatible with the locked Vitest fixture, git, and the real Crosswalk executable. Build `scripts/CrosswalkAcceptanceProbe/CrosswalkAcceptanceProbe.csproj` from the exact candidate with `scripts/Invoke-WorkerBuildCheck.ps1`. Use its retained build output directory; set `$probe` below to the resulting `CrosswalkAcceptanceProbe.dll`. This small operator host invokes `GoalAcceptanceVerifier.RunOwnedAsync`, so the proof enters manifest loading and production dispatch. It does not invoke `CrosswalkClient` directly or land a goal.

```powershell
$candidate = (Get-Location).Path # exact, clean candidate checkout
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('crosswalk-vitest-' + [guid]::NewGuid().ToString('N'))
$receipts = Join-Path $fixture '.orchestrator/operator-proof'
$probe = '<retained candidate build directory>/CrosswalkAcceptanceProbe.dll'
$env:MCG_ORCHESTRATOR_CROSSWALK_PATH = '<absolute real Crosswalk executable>'
Copy-Item -LiteralPath (Join-Path $candidate 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/CrosswalkVitest') -Destination $fixture -Recurse
Push-Location $fixture
npm ci --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'fixture dependency installation failed' }
git init -b main
git add .
git -c user.name=CrosswalkFixture -c user.email=crosswalk-fixture@example.invalid commit -m fixture
New-Item -ItemType Directory -Path $receipts -Force | Out-Null
& $env:MCG_ORCHESTRATOR_CROSSWALK_PATH list $fixture 1> (Join-Path $receipts 'discovery.json') 2> (Join-Path $receipts 'discovery.stderr')
$LASTEXITCODE | Set-Content (Join-Path $receipts 'discovery.exit')
$discovery = Get-Content -Raw (Join-Path $receipts 'discovery.json') | ConvertFrom-Json
$pass = @($discovery | Where-Object name -eq 'crosswalk passing selection')
$fail = @($discovery | Where-Object name -eq 'crosswalk known failing selection')
if ($pass.Count -ne 1 -or $fail.Count -ne 1) { throw 'inspect exact adapter discovery names; select one unmodified ID for each fixture test' }
dotnet $probe $candidate $fixture $receipts $pass[0].id 1> (Join-Path $receipts 'passing.stdout') 2> (Join-Path $receipts 'passing.stderr')
$LASTEXITCODE | Set-Content (Join-Path $receipts 'passing.exit')
dotnet $probe $candidate $fixture $receipts $fail[0].id 1> (Join-Path $receipts 'failing.stdout') 2> (Join-Path $receipts 'failing.stderr')
$LASTEXITCODE | Set-Content (Join-Path $receipts 'failing.exit')
Pop-Location
```

The direct List call supplies selectors only. It is not the demonstration. Each probe run performs its own List → Run via candidate acceptance, with a new execution owner. It writes a `*.operator.json` envelope and attempt-owned `*.ctrf.json` receipt. The envelope records candidate commit/tree/dirty paths, hashes of loaded Infrastructure/Execution assemblies and Crosswalk executable, selected ID, host exit code and the complete acceptance result. Preserve both envelopes, their referenced normalized files, capture/heartbeat files and the shell exit files as one directory. Attach the candidate build receipt too. Dirty candidate paths must be empty; assembly hashes must match that candidate build.

Inspect the passing envelope: host exit 0, selected check passed, invocation exit 0, positive executed count and an executed record whose exact `testId` matches the passing selection. Inspect the known negative: host exit 1, selected check failed, actual Crosswalk exit status retained, normalized failed identity equal to the failing selection, and failure message/trace available. A nonzero invocation remains a failure even with readable output. Neither a skipped check nor a missing receipt satisfies either arm.

If installed Crosswalk cannot represent the listed IDs or the required normalized records, stop and supply executable/version/hash, exact invocation, exit status, stdout/stderr and candidate envelope. Do not reinterpret IDs or change the external protocol speculatively. Respond to the pending operator obligation with these retained evidence paths; do not mark final acceptance complete until both arms have been inspected.

## Acceptance-owned regression selectors

`GoalAcceptanceVerifierCrosswalkTests`, `GoalAcceptanceVerifierCrosswalkEvidenceTests`, `GoalAcceptanceVerifierCrosswalkBoundaryTests`, `CrosswalkClientTests`, `ReceiptTestFailureReaderTests`, `NormalizedResultRetentionScenarioTests`, `ProjectStackResolverDecisionTests`, and `GoalAcceptanceVerifierTestsGoTestJsonReceipt`. Run the existing normalized reader/writer suites as required by changed dependency selection. Developer/Tester/Reviewer must not launch test hosts.
