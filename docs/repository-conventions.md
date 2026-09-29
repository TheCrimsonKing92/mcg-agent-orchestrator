<!-- shared-discipline:repository-conventions -->
# Repository conventions that protect test tooling

These conventions apply to hand-written test source under `tests/`. Existing exceptions are frozen in
`RepositoryLayoutConventions`; new exceptions are not accepted, and each inventory entry must be removed as
soon as its file becomes compliant. Generated sources and `bin`, `obj`, scratch, artifact, `TestResults`, and
Playwright-report trees are outside the guard.

## One top-level public type per test file

A test source file must not declare more than one top-level public type. Nested private helper types are fine.
Multiple public declarations make filename-derived changed-test filters and focused-evidence sibling discovery
ambiguous: neither tool can reliably determine every class affected by changing that one file.

Put each public test class, record, struct, interface, enum, or delegate in its own file. Name the file for that
type. When decomposing an existing test file, a new one-class file needs no grandfather entry, so a compliant
split is easier than preserving the old layout.

## Dot-free test filenames

Do not put a dot in a test filename before the `.cs` extension. The test-impact planner currently derives a
filter such as `FullyQualifiedName~Parent.Suffix` from `Parent.Suffix.cs`; C# type names cannot contain that dot,
so the filter silently selects no test even when the file declares `ParentSuffix`.

Use a dot-free filename whose stem is the public test type name, such as `ParentSuffix.cs`. If a companion file
contains another public type, split it into the correctly named one-class file instead of encoding the
relationship with a dotted filename.

## Explicit repository roots

Tests and source helpers must not discover the repository from `Environment.CurrentDirectory`,
`AppContext.BaseDirectory`, or another process-ambient location when the owning seam can supply a root.
Ambient discovery breaks isolated gate execution and can make acceptance inspect the checkout running the
process instead of the goal worktree being verified.

Thread the repository or worktree root as an explicit argument. Tests that derive a root from `[CallerFilePath]`
must first use `VerifiedRepositoryRoot`. The helper accepts `MCG_ORCHESTRATOR_REPOSITORY_ROOT` when it names
a repository with a `.git` file or directory, `Mcg.AgentOrchestrator.sln`, and `tests`; otherwise each caller
keeps its source-path fallback. The variable names the worktree under verification. Base-build cache hits can
restore test assemblies compiled in another worktree, so a compile-time path alone can point at the wrong
checkout or one that has been removed.

The repository layout convention guard mechanically rejects test-source patterns that combine
`Environment.CurrentDirectory` or `AppContext.BaseDirectory` with repository-root discovery. It deliberately
does not reject ambient paths used for copied fixtures, build outputs, or other non-repository filesystem
access. Existing repository-root violators are grandfathered in the same shrink-only inventory as the other
test-source conventions and should be removed by their owning goals.
