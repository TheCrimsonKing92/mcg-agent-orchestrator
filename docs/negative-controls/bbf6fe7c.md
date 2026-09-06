# Hermes executable identity preflight negative control

- Goal: `bbf6fe7c`
- Test: `HermesAcpTrialTests.LifecycleAcceptsNativeVersionOnlyWithGitProvenance`
- RED configuration: the lifecycle still used `HermesAcpAdapter.ValidateVersionOutput`; the fixture supplied the pinned candidate's native `Hermes Agent v0.20.6 (2026.8.27)` output without a SHA and no Git provenance collaborator existed.
- RED command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'DisplayName~LifecycleAcceptsNativeVersionOnlyWithGitProvenance'`
- RED receipt: 1 run, 1 failed, 0 passed. The failure was `InvalidOperationException: Hermes executable is not pinned to v2026.8.27 (5fc308a70719a83cccdbba4c0e39c23f5a8239d5)` at `HermesAcpAdapter.ValidateVersionOutput`.
- GREEN configuration: the same native output is accepted only after the fake process supplies an OS-image path inside a real temporary Git checkout whose runtime-read HEAD, annotated tag object, peeled commit, and clean tracked status match an injected fixture pin.
- GREEN command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'DisplayName~HermesExecutableIdentity|DisplayName~HermesAcp|DisplayName~ChildOutputDrainAdoptionSaturation'`
- GREEN receipt: 57 run, 57 passed, 0 failed; managed runner exit 0. The broader focused set proves the verifier's negative cases, lifecycle preflight ordering, typed failure receipts, command wiring, and unchanged Hermes role/confinement/usage/teardown controls.
