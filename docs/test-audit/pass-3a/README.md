# Test audit pass 3a: tests we should not be running

Snapshot: `385d1814d3e67b07bcb384dcf815a5c4a5c7d956`.

This pass is an analysis-only inventory. It changes no tests, production source, or acceptance manifest. Follow-up fixes must be sequenced with explicit file scopes.

## Priority and reconciliation

| Priority | Smell | Instances | False-green capable | Record |
| ---: | --- | ---: | --- | --- |
| 1 | Silent no-op pass | 78 | Yes; every row can report green without executing its claimed assertion | [01-silent-no-ops.md](01-silent-no-ops.md) |
| 2 | Source text standing in for behavior | 22 | 13 behavior proxies are marked yes and precede the 9 static-contract keeps | [02-source-text-assertions.md](02-source-text-assertions.md) |
| 3 | Literal companion beside a derived quantity | 11 | No; this is a brittle or under-specified population assertion | [03-derived-literal-companions.md](03-derived-literal-companions.md) |
| 4 | Test identifier over 80 characters | 128 | No; this signals coupled behaviors rather than a false pass by itself | [04-overlong-test-names.md](04-overlong-test-names.md) |
| 5 | Exclusive-resource lane membership | 3,030 | No; this is a standing throughput cost | [05-exclusive-resource-membership.md](05-exclusive-resource-membership.md) |

Rows are independently counted per smell, so a test appearing in more than one record remains in each applicable inventory. A `[Theory]` method is one source instance regardless of discovered cases. The stable key is repository-relative file, source line, and method identifier; display names are supplementary only.

## Dispositions

Every row has exactly one allowed disposition: `delete`, `decompose`, `rewrite assertion`, or `keep with justification`. No row in this pass is marked `delete`; therefore no detection-loss field is applicable. If a follow-up proposes deletion, it must name exactly what would go undetected.

For exclusive lanes, `decompose` means later removal or reassignment of lane membership, not deletion of the test. A keep requires current method-level source evidence for every exclusive key. Historical per-test isolation proof is `disposition=undecidable`: it would be settled by a controlled concurrent-versus-serialized receipt naming the resource and showing the verdict changes only with that resource; the required per-test empirical receipt was never recorded in the supplied source, manifest, or context. Missing proof defaults to `decompose`, not inherited justification.

## Search methods and exclusions

- Silent no-ops: Roslyn method-boundary inspection for direct Windows guard returns, then one-level call-site review for guarded helpers and review of environment-controlled returns. The `Skip=` case at `DotnetBuildEnvironmentManagerTests.cs:781` and the post-assertion return at `WorkerShellTests.cs:6` are documented exclusions.
- Source-text assertions: Roslyn search for checked-in `File.ReadAllText`/`File.ReadAllLines` reads, followed by assertion-flow review. Runtime fixture reads and assertions whose actual contract is static checked-in text are distinguished in the row disposition.
- Derived literals: Roslyn arithmetic nodes containing `Count` or `Length`, followed through derived locals. This finds the class that project-name search misses and avoids numeric-literal noise. Occurrence counts, boundary inputs, line calculations, and synthetic PIDs were reviewed and excluded because the literal is intrinsic to the calculation.
- Long names: Roslyn identifiers on all `[Fact]`/`[Theory]` methods in the three test projects. Distribution: 4,235 methods; minimum 18, p50 58, p90 74, p95 78, maximum 103; 128 exceed 80.
- Exclusive resources: Roslyn root Infrastructure test methods evaluated against the lane filters and exclusive-key declarations at `config/acceptance-manifest.json:36-114`, then direct method-source evidence checked per key. The root project exclusions `Cli/**`, `Fixtures/IsolatedDotnetProbe/**`, and `ProviderEnvironment/**` are omitted. The 3,357 included methods map to exactly one lane; 3,030 map to a lane with exclusive keys.
  - Case-insensitive direct-source probes were `GoalWorktree|worktree|cleanup hook|RemoveWorkspace|Landing|GitCommand` for cleanup hooks; `SetEnvironmentVariable|EnvironmentVariableScope|EnvironmentMutation|RestoreEnvironment|WithEnvironment` for environment mutation; `Process.Start|ProcessStartInfo|StartProcess|StartAndWait|WaitForExit|DispatchProcess|LaunchProcess` for process spawning; `InvokeIsolatedDotnet|dotnet build|dotnet test|BuildSlot|DotnetBuild|RunDotnet` for build slots; and `GoalAcceptanceVerifier|AcceptanceGateEngine|RunAcceptance|VerifyAcceptance` for the verifier. Job accounting deliberately requires the narrower `JobAccounting|JobObject|WorkerDispatchJob`; merely naming an acceptance verifier is not evidence of job-accounting use.

## Exclusive-lane reconciliation

| Lane | Count | Exclusive keys |
| --- | ---: | --- |
| Goal lifecycle commands | 335 | `xunit:GoalWorktreeCleanupHooks` |
| Goal worktree cleanup | 151 | `xunit:GoalWorktreeCleanupHooks` |
| Worker profiles | 200 | `xunit:EnvMutation` |
| Worker dispatch fixtures | 302 | `xunit:EnvMutation` |
| Process spawning | 479 | `xunit:ProcessSpawning` |
| Dotnet build slots | 336 | `xunit:DotnetBuildSlots` |
| Goal acceptance verifier | 46 | `xunit:GoalAcceptanceVerifier`, `xunit:JobAccounting` |
| Goal acceptance build slots | 137 | `xunit:JobAccounting` |
| Remainder balance B | 323 | `xunit:ProcessSpawning` |
| Remainder | 721 | `xunit:DotnetBuildSlots`, `xunit:GoalAcceptanceVerifier`, `xunit:ProcessSpawning` |
| **Total** | **3,030** | |
