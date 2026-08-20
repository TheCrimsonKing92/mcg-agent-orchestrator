# Ownership reservation keys

`RepositoryOwnershipMap` classifies recognized Core and Infrastructure subsystems with keys shaped as
`shared-infrastructure:<project>/<subsystem>`. The classification remains high risk and serialized; only the
scope of the reservation changes.

| Project token | Recognized subsystems |
| --- | --- |
| `core` | `application`, `collaboration`, `conductor`, `domain`, `models`, `persistence`, `reports` |
| `infrastructure` | `diagnostics`, `operatorcomms`, `persistence`, `processes`, `verification`, `workers`, `workspaces` |
| `infrastructure.providers` | None |

The mapping fails closed. A project-root file, `Properties`, an unknown third path segment, or a malformed
third segment receives the coarse `shared-infrastructure` key. Adding a directory without adding it to the
allowlist therefore costs concurrency; it never invents a new reservation identity. Coarse paths conflict
with other coarse paths, but not with every recognized fine-grained key.

These ownership keys coordinate simultaneous goals. They are distinct from the per-invocation acceptance
shard keys described in [Acceptance gate resource isolation](acceptance-gate-resource-isolation.md).

Test-project reservations remain project-wide. Goals that touch the same test project can therefore remain
serialized even when their production paths use different subsystem keys.
