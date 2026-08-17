# Infrastructure assembly decomposition

This document records decisions and slice state that cannot be derived from the project
graph. Project references and source ownership remain authoritative in the checked-in
project files and architecture tests.

## Baseline receipt

- basis SHA: `fb84bdffe7ceb0ba115cb1430792d36aabd5149b`
- manifest: `config/acceptance-manifest.json` at that SHA, 29 named entries
- host: `SEVENTHSON`
- corpus: `.orchestrator/acceptance-gate-attempts/**/*.trx`
- method: sum `UnitTestResult` durations per receipt, group by lane, and take the mean
  across the operator-supplied gate window
- source: operator-provided retry-2 pre-extraction receipt for goal `8fc5413a`

The post-extraction comparison must use the same host and method. This slice makes no
velocity claim; the operator owns that comparison after live corpus accumulation.

This checked-in receipt is metadata-only: it does not contain the measured per-lane/gate
values, a production publish inventory, or an unrelated-build hash/timestamp comparison.
Those omissions are not executable from a subscription Tester lane. Before any performance
or isolation claim, the operator must attach a same-host receipt containing the baseline and
post-extraction numeric lane/gate values, corpus receipt identifiers and sample counts, the
build/run-directory Providers assembly count and single-file publish bundle inventory, and
the Providers output `Get-FileHash` plus `LastWriteTimeUtc` before and after an unrelated-module
build. Until then the slice may be
reviewed for source/build correctness, but no velocity or production-isolation claim is made.

## Slice status

- Provider configuration/runtime: implementation candidate. The assembly owns provider
  adapters, Ollama defaults, provider smoke behavior, and agent-catalog serialization;
  acceptance evidence and landing remain pending.
- Persistence foundation: pending fresh dependency research, including relocation of
  `CohortAcceptanceStore` and the cited-evidence contract to their owning invariants.
- Process primitives: pending the dispatch-runner/diagnostic and WMIC-parser ownership
  corrections identified by research.
- Worktree/build/acceptance: pending.
- Worker runtime: pending.
- OperatorComms/Discord: pending Persistence and real hosted round-trip evidence.
- Diagnostics: pending provider, persistence, and worker seams.

The pending order is provisional. Each node requires a new five-role slice and must be
revised if the freshly compiled dependency graph contradicts it. Landing Providers does
not complete the decomposition epic.
