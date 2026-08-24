# OperatorComms assembly extraction receipt

Measured on 2026-08-24 from pre-edit HEAD `2c0dad4b668a` and the final candidate on the
same Windows host. This extraction does not claim acceptance-gate concurrency or throughput;
gate slots, not production assembly identity, control that concurrency.

## Boundary and package measurement

`Mcg.AgentOrchestrator.Infrastructure.OperatorComms` owns the 26 former `OperatorComms`
sources and references only Core plus `Discord.Net`, `BouncyCastle.Cryptography`, and
`Microsoft.Data.Sqlite`. Infrastructure has no reference to the new project; App references
both projects and owns the persistence composition.

`Discord.Net` and `BouncyCastle.Cryptography` moved out of the Infrastructure project and
are now confined to OperatorComms among production project declarations. `Microsoft.Data.Sqlite`
is not confined: both OperatorComms delivery storage and Infrastructure persistence use it.
The seam tax was three declaration groups moved to Core: collaboration-store contracts,
`OperatorIntentVerbs`, and `BacklogItemStatus`.

## Incremental rebuild measurement

Method: build the Release solution to a steady state, record each project primary DLL hash and
`LastWriteTimeUtc`, advance only `IOperatorChannel.cs`'s timestamp, rebuild the Release solution,
compare, and restore the source timestamp. `WriteOnlyWhenDifferent` leaves deterministic hashes
unchanged for recompiles, so timestamp and hash changes were both recorded.

- Before extraction, the rewritten primary outputs were Infrastructure plus
  `RealProcessShardProbe` and `Infrastructure.ProviderEnvironment.Tests`; all three timestamps
  changed and their hashes did not.
- After extraction, the rewritten primary outputs were OperatorComms,
  `Infrastructure.Tests`, `RealProcessShardProbe`, and
  `Infrastructure.ProviderEnvironment.Tests`. The umbrella test assembly's timestamp and hash
  changed; the other three timestamps changed while hashes did not.
- Infrastructure itself no longer rebuilds for an OperatorComms-only source change. The new
  friend-assembly consumer makes the umbrella test recompilation cost explicit.

These are observed incremental-build outputs, not a static dependency-graph projection.

The acceptance base-build cache registry previously omitted OperatorComms, so its output was
built only transitively through App and absent from cache receipts. The final candidate registers
OperatorComms immediately after Providers; an OperatorComms-only policy closure now caches and
reports the new assembly directly.

## Publish inventory measurement

Framework-dependent inventory used
`dotnet build src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj -c Release --output <dir>`
before and after. It changed from 34 to 35 files. The only added filename was
`Mcg.AgentOrchestrator.Infrastructure.OperatorComms.dll`; no filename was removed. Discord,
BouncyCastle, Sqlite, and the Infrastructure DLL remained present. OperatorComms embeds its
symbols and suppresses a standalone class-library dependency file so staged relaunch output
does not acquire incidental metadata files.

Single-file inventory used `scripts/publish-windows.ps1` with Release, `win-x64`, and isolated,
writable NuGet/cache roots before and after. External inventory remained 8 files with no filename
addition or removal. The executable changed from 146,057,447 to 146,078,641 bytes (+21,194).
Reading the .NET bundle manifest showed 366 entries before and 367 after; the only added entry
was `Mcg.AgentOrchestrator.Infrastructure.OperatorComms.dll`. Infrastructure, Discord,
BouncyCastle, and Sqlite bundle entries remained present.

## API and test-project cost

Regex-censusing the same 26 source files at HEAD and in the candidate found 104 declared types
both times: 101 public and 3 internal. The visibility census also compared the three declaration
groups relocated to Core: the collaboration-store interface and five records,
`OperatorIntentVerbs`, and `BacklogItemStatus` were public before and after the move. Across the
complete extraction surface, public promotions: **0**. New
`InternalsVisibleTo` grants: **1**, to `Mcg.AgentOrchestrator.Infrastructure.Tests`; no unused
friend grants were copied.

No test project was added. Operator communications coverage deliberately remains in the existing
Infrastructure test project, whose acceptance manifest already has an unfiltered umbrella check;
`config/acceptance-manifest.json` was therefore not changed.

The pre-existing dependency-map drift remains visible and was not silently broadened in this
slice: Infrastructure.Tests references `OrchestratorSqliteTools`, `IsolatedDotnetProbe`, and
`RealProcessShardProbe`, but those three entries are absent from the hand-maintained acceptance
dependency map.

This receipt proves the structural boundary and local publish shape. It does not prove a hosted
Discord round trip; that real integration check remains pending.
