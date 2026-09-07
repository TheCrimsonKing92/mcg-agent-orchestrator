# Hermes ACP worker-harness trial

Hermes ACP is registered as a typed, non-self-committing capability for an opt-in trial. It is not a default worker profile, has no landing authority, and is not authorized for live execution by this change.

## Immutable candidate

- Release: `v2026.8.27`
- Annotated tag object: `fcebd62163497e77e5de00d26d2ed86cb4ef8761`
- Peeled commit: `5fc308a70719a83cccdbba4c0e39c23f5a8239d5`
- Policy: `config/trials/hermes-acp-v2026.8.27.json`

Before any model call, the lifecycle reads the operating-system image path of the launched `--version` child and derives its enclosing Git checkout from that path. It then requires the exact clean tracked `HEAD`, annotated tag object, and peeled commit above. Native version output is only a refusing consistency check for the release, Git install method, and derived install directory; self-reported text never selects or authorizes a checkout. The stateless verifier creates one receipt per lifecycle run; the lifecycle independently validates its pin, evidence, and freshness before ACP launch, and no persisted receipt is ever loaded to authorize a later run. Terminal validation binds to that same in-memory receipt; an independently replayed receipt outside the one-minute freshness window refuses. Untracked installation products such as `.venv` are allowed, while tracked modifications, stale receipts, a tag-object-only identity, mutable package range, mismatched executable, or hidden model/provider fallback fail preflight.

## Authority and confinement

`WorkerProviderCatalog` is the capability authority. `hermes-acp` resolves to `ProviderKind.HermesAcp` with `CanSelfCommit=false` and `CanSelfVerify=false`, but `WorkerProfileCatalog.Default()` deliberately contains no runnable Hermes profile. Any later live trial therefore needs a separately authorized persisted profile.

Every Hermes role uses the orchestrator's whole-process Windows sandbox. Each dispatch receives one fresh `HERMES_HOME` below its sandbox root, and the checked adapter preserves that dispatch-owned path rather than replacing it. Configured MCP is suppressed with the documented environment setting; memory, plugins, hooks, user config, and rules must be disabled by the checked adapter's documented `--safe-mode` argument. These settings are defense in depth; Hermes approvals are not treated as a security boundary. Hermes starts suspended in a no-breakaway owned Windows job before it can execute. Terminal receipts require both job-empty confirmation and an independent identity-based survivor inventory.

The checked adapter reads the existing prompt file as bytes, verifies its SHA-256, and keeps prompt content off argv. It prepares a no-window, redirected-stdio `hermes --safe-mode acp` server and requires terminal prompt/model/provider/usage evidence plus the ordinary role-specific `WORKER_RESULT`. ACP progress belongs on stderr. The adapter rejects missing usage, identity mismatch, policy violation, an unexpected child, or malformed/missing `WORKER_RESULT`; it never synthesizes usage and never falls back to one-shot mode.

The pinned upstream surface does not document enforceable delegation or code-execution disable switches, nor ACP terminal usage equivalent to one-shot `--usage-file`. Those are unresolved live preconditions. Until a separately reviewed wrapper proves both from ACP protocol receipts, the disposition remains `TrialOnly` / not adopted.

## Checked operator command

`hermes-acp-verify-identity --executable <absolute-path>` runs only the owned `--version` child and emits the typed executable-identity receipt, including the OS image path, Git identities, native stdout/stderr, exit code, and job teardown. It accepts optional `--working-directory`, `--hermes-home`, and `--receipt` paths. This check makes no ACP session, authentication, or model call and is the operator-owned real-installation acceptance step for the pinned candidate.

`hermes-acp-trial` is the checked one-shot ACP client owned by the App. It requires `--confirm-live-hermes-start` and an explicit `--role`, reads the prompt from `--prompt` or `MCG_TRIAL_BRIEF_PATH`, verifies `--prompt-sha256` or `MCG_TRIAL_BRIEF_SHA256`, starts `hermes --safe-mode acp`, and writes `hermes-acp-terminal-receipt.json` inside the supplied sandbox. Planner, Researcher, and Reviewer permission requests are denied. Developer and Tester may select only an `allow_once` option, and only when every reported file location is inside the assigned worktree. An outside, missing, malformed, execute, or `.git`-scoped request is refused without being misreported as a containment-policy violation; an unsupported ACP client method still records a terminal policy violation. The whole-process sandbox remains the security boundary. Only the final authoritative worker output is written to stdout; JSON-RPC progress and the receipt location go to stderr.

Invoke it only as a harness inside the existing `trial-compare` contained root; the command rejects a direct invocation or paths outside that root. A trial harness entry uses the checked launcher as `fileName`, with arguments `hermes-acp-trial --confirm-live-hermes-start --provider <provider> --model <model> --role <role>`; `trial-compare` supplies the prompt path/hash and starts the whole tree at Low integrity in a no-breakaway job. The comparator copies the typed terminal receipt into the durable per-arm receipt directory before destroying the trial root.

## Authorized evaluation sequence

Only after separate install/auth/live-run authorization:

1. Verify the exact release and commit, executable path, same underlying model/provider for both arms, dedicated state root, worktree, and protected shared `.git` paths.
2. Run an allowed in-worktree write and an otherwise-identical outside-write negative control. A failure before the write request is inconclusive.
3. Run 20 prompt/output probes, 10 start/cancel/restart cycles, the spaces/non-ASCII path case, and eight paired tasks across read-only, simple implementation, and medium defect work.
4. Retain raw per-arm `result.json`, `comparison.json`, usage, identity, cancellation, and teardown receipts in the operator-selected artifact directory. Record hashes in the SQLite dogfood log; do not use `.scratch` as durable storage.
5. Feed normalized evidence to `HermesTrialDecisionEngine`. It loads thresholds from the checked policy, derives parseability from the actual probe count, rejects count inconsistencies, and fails loudly if the policy is absent or malformed. Incomplete evidence yields `TrialOnly`; an immediate rejection yields `Rejected`; passing evidence yields only `EligibleForOperatorAdoption`. It never enables a profile or adopts a default.
6. Route the changed candidate and receipts through normal Tester and Acceptance gates, then record an Opus 5 cross-family Reviewer verdict under the current operator model policy. A separate authorized goal is required for any default profile, role assignment, installation, authentication, or adoption.

Automated tests prove command-to-lifecycle wiring, JSON-RPC framing, role- and worktree-scoped permission handling, continuous stdout draining, usage/model/provider validation, owned-job terminal accounting, identity-based child inventory, and typed receipt persistence. They do not prove that the pinned Hermes executable is installed, authenticated, compatible with the configured provider, or clean across an actual Hermes ACP teardown. The operator must run the authorized evaluation sequence above and retain those live receipts separately.

## Stop conditions

Stop immediately for containment escape, protected/shared `.git` modification, prompt mismatch or truncation, false completion, uncontrolled activity, missing usage, hidden fallback, GUI/firewall prompt, timeout, orphan, or unresolved child. Do not relax a threshold on retry. Missing evidence is not clean evidence.

## Durable comparison diagnostics

`trial-compare` copies each arm's stdout, stderr, and teardown receipt into that arm's durable receipt directory before the disposable root becomes unreachable. Output metadata binds those copies to the workload, arm, and launch attempt; records source and retained lengths and SHA-256 hashes; and marks encoding, truncation, and whether the source process had fully exited. Output retention is capped at 64 KiB per stream. Above that limit, the retained bytes contain bounded beginning and ending context separated by an explicit omission marker.

These files prove which bytes were retained for diagnosis after teardown. They do not authorize completion: `TrialHarnessComparison` validates `WORKER_RESULT` only from the original pre-teardown stdout stream, and never substitutes a truncated diagnostic, a digest, or a plausible terminal block from the retained copy. Legacy metadata-only receipts remain readable with `contentAvailable=false`. The durable teardown path in `result.json` points beside that result receipt; its source Low-integrity path remains separately recorded for custody tracing.

The operator-owned non-model control is `config/trials/evidence-retention-control.json`. From the repository root, run:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 trial-compare --spec config/trials/evidence-retention-control.json --receipts <isolated-receipts-directory> --timeout-seconds 120
```

After both owned jobs exit, verify each arm's `stdout.diagnostic.log`, `stderr.diagnostic.log`, `teardown-receipt.json`, and `result.json`; compare the retained hashes and fixed arm-specific text; and confirm the teardown receipt's `rootPath` no longer exists. This is a contained child round trip only: it makes no model, authentication, network, or tool call and does not authorize or modify the Hermes configuration. If the host cannot create the contained child, record the control as platform-limited rather than treating a fake-host test as the round trip.
