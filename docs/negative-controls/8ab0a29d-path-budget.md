# Measured path budget for the shortened isolated root

The worst-case destination length asked for over three rounds, measured rather than estimated.
It confirms the `4251a42e` approach — keep `bin/{projectName}/{configuration}` and shorten the
root — has real margin.

## Method

Deepest relative artifact path under a built test output, taken across every file recursively:

    bin\Mcg.AgentOrchestrator.Infrastructure.Tests\Debug
      deepest relative = \Fixtures\PlannerOutputContract\485363d4-ba8e416a-20260805022806.out.txt
      length           = 72

The `runtimes` tree that `MSB3021` reported on is present in that output and is *shorter*:
`\runtimes\browser-wasm\nativeassets\net9.0\e_sqlite3.a` is 52. The 72-character fixture path is
the binding case, not the native copies.

## Worst case under the new scheme

    root       C:\Users\miles\AppData\LocalLow\mdi-<16 hex>        52
    separator  \bin\                                                5
    project    Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests   62
    artifact   deepest relative                                    72
    ------------------------------------------------------------------
    total                                                         191
    margin to 260                                                  69

Longest project name in the repository is used, with the deepest observed artifact, so this is the
worst case rather than a typical one.

## What this does and does not establish

It establishes that the shortened root leaves 69 characters of headroom, which is why the arms
that failed with `MSB3021 ... exceeds the OS max path limit` now fit. It does not pin the
invariant: nothing fails the build if a future change spends that margin — a longer project name, a
deeper fixture path, or a lease root that grows again.

The current assertions compare the property *expression* text:

    Xunit.Assert.Equal("$(McgIsolatedArtifactsPath)\\bin\\$(MSBuildProjectName)\\", ...)

which passes whether or not the resulting path fits. Per `test-design-discipline` rule (p), the
discriminating assertion is over the computed worst-case length, not the template that computes
it. A test that composes the longest project name with the deepest artifact path and asserts the
total stays under a stated budget would fail exactly when the margin is spent, which is the point
at which someone needs to know.

Related: backlog `a48e3df6` records the same expression-versus-value issue for the project key.
