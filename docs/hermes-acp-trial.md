# Hermes ACP worker-harness trial

Hermes ACP is registered as a typed, non-self-committing capability for an opt-in trial. It is not a default worker profile, has no landing authority, and is not authorized for live execution by this change.

## Immutable candidate

- Release: `v2026.8.27`
- Commit: `fcebd62163497e77e5de00d26d2ed86cb4ef8761`
- Policy: `config/trials/hermes-acp-v2026.8.27.json`

Both release and commit must appear in locally captured version output before a model call. A tag-only, branch, mutable package range, mismatched executable, or hidden model/provider fallback fails preflight.

## Authority and confinement

`WorkerProviderCatalog` is the capability authority. `hermes-acp` resolves to `ProviderKind.HermesAcp` with `CanSelfCommit=false` and `CanSelfVerify=false`, but `WorkerProfileCatalog.Default()` deliberately contains no runnable Hermes profile. Any later live trial therefore needs a separately authorized persisted profile.

Every Hermes role uses the orchestrator's whole-process Windows sandbox. Each dispatch receives a fresh `HERMES_HOME` below its sandbox root. Configured MCP is suppressed with the documented environment setting; memory, plugins, hooks, user config, and rules must be disabled by the checked adapter's documented `--safe-mode` argument. These settings are defense in depth; Hermes approvals are not treated as a security boundary. The owned Windows job remains responsible for child-process containment and teardown.

The checked adapter reads the existing prompt file as bytes, verifies its SHA-256, and keeps prompt content off argv. It prepares a no-window, redirected-stdio `hermes --safe-mode acp` server and requires terminal prompt/model/provider/usage evidence plus the ordinary role-specific `WORKER_RESULT`. ACP progress belongs on stderr. The adapter rejects missing usage, identity mismatch, policy violation, an unexpected child, or malformed/missing `WORKER_RESULT`; it never synthesizes usage and never falls back to one-shot mode.

The pinned upstream surface does not document enforceable delegation or code-execution disable switches, nor ACP terminal usage equivalent to one-shot `--usage-file`. Those are unresolved live preconditions. Until a separately reviewed wrapper proves both from ACP protocol receipts, the disposition remains `TrialOnly` / not adopted.

## Authorized evaluation sequence

Only after separate install/auth/live-run authorization:

1. Verify the exact release and commit, executable path, same underlying model/provider for both arms, dedicated state root, worktree, and protected shared `.git` paths.
2. Run an allowed in-worktree write and an otherwise-identical outside-write negative control. A failure before the write request is inconclusive.
3. Run 20 prompt/output probes, 10 start/cancel/restart cycles, the spaces/non-ASCII path case, and eight paired tasks across read-only, simple implementation, and medium defect work.
4. Retain raw per-arm `result.json`, `comparison.json`, usage, identity, cancellation, and teardown receipts in the operator-selected artifact directory. Record hashes in the SQLite dogfood log; do not use `.scratch` as durable storage.
5. Feed normalized evidence to `HermesTrialDecisionEngine`. Incomplete evidence yields `TrialOnly`; an immediate rejection yields `Rejected`; passing evidence yields only `EligibleForOperatorAdoption`. It never enables a profile or adopts a default.
6. Route the changed candidate and receipts through normal Tester, Acceptance, and cross-family Reviewer gates. A separate authorized goal is required for any default profile, role assignment, installation, authentication, or adoption.

## Stop conditions

Stop immediately for containment escape, protected/shared `.git` modification, prompt mismatch or truncation, false completion, uncontrolled activity, missing usage, hidden fallback, GUI/firewall prompt, timeout, orphan, or unresolved child. Do not relax a threshold on retry. Missing evidence is not clean evidence.
