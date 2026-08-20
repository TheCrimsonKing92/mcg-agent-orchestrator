# Orchestrator Handoff

**Updated:** 2026-08-19 by Grok. Live status, not durable policy — verify before mutating.

## HOW TO READ THIS FILE — folklore warning

This file accretes by session. **Anything below the current session's sections is a point-in-time
record, not current truth.** On 2026-08-07 an audit found that a section marked `⚠ CRITICAL` described
a code gap that had since been closed, and a list headed "highest-value open items" had **4 of its 5
items already Done**. An operator followed both as live guidance for a full day.

Rules for using and maintaining this file:

- **Verify before acting on any directive older than the top section.** Goal states move
  (`status <goal>`), backlog items close (`backlog-show <id>`), and code gets fixed. All three are one
  command away.
- **A struck-through heading with a re-checked date means the claim was audited.** Absence of one means
  nobody has checked, not that it is still true.
- **When you invalidate a claim, mark it in place** — do not delete the reasoning. The receipts and the
  dead ends are the durable value; the conclusions expire.
- **Durable lessons belong in `### Operating lessons worth keeping`; live state belongs in the top
  section.** Everything else is history.

## RESUME HERE — 2026-08-20 09:35 UTC, the fourteen reds are FIXED and landed; board draining

**RESOLVED at `c0ad1c47`.** Main no longer carries the fourteen red tests that made the acceptance gate
unpassable for every goal. A combined candidate carrying all fourteen fixes gated with **zero failures
across every lane** and landed. `af19fd2a` (`6241ed3f`, gate-latency velocity) landed behind it.

**How it was resolved, because the mechanism matters.** The two fix goals DEADLOCKED each other:
`4fa6af44` fixed six Planner-contract reds, `c30eb2fe` fixed eight local-provider reds, and any candidate
touching `src/Mcg.AgentOrchestrator.Infrastructure/` runs the whole Infrastructure suite
(`RepositoryTestImpactPlanner.cs:194`), so each one's gate met the other's reds and failed. Proven, not
inferred: `4fa6af44`'s gate showed all six of its own fixed, zero introduced, and still failed on exactly
`c30eb2fe`'s eight. The exit was to merge `goal/4fa6af44` into `goal/c30eb2fe` so ONE gate run could see
all fourteen fixes at once. **That merge was manual and only necessary because the gate cannot distinguish
"you broke this" from "this was already broken" — backlog `92f531b4`, which carries the full proof.**

Root causes worth keeping:
- Six Planner reds: `MarkdownHeadingNormalizer`'s `(?m)(?<=\S)(#{1,6}[ \t]+\S)` — the first `#` of a
  start-of-line `## Heading` satisfied the lookbehind, so the replace demoted it to `# Heading`. Fixed to
  `(?<=[^\s#])`. One product bug behind all five failures, found by a grok Tester whose dispatch was then
  rejected on output shape.
- Eight local-provider reds: stale Ollama expectations after the LlamaCpp switch, plus one real bug —
  `OrchestratorHealthInspector.IsApiRouteUsable` treated `Mode == "LocalBridge"` as an API route.

**Still open and worth knowing before you drive:**

| Goal | State |
|---|---|
| `21b284a0` | **held deliberately.** Its criterion demands the focused-evidence path that `9a9c7e8e` breaks; it burned 32 attempts. The Developer fix is done. Read the note on task 4 before removing the hold. |
| `fe37d616` | `GoalAcceptanceVerifier` seam-4 extraction, recovered and current. **This is the only genuine test of whether the post-landing canary fires** — it is the sole engine-touching candidate. |
| `9f64cd98` | squashed from 12 commits (with merges) to **1 linear commit** to stop a rebase treadmill that recurred every time main moved. |
| `13b3be0d`, `ab933e32` | draining normally against a green base. |

`4fa6af44`'s root cause is worth keeping: `MarkdownHeadingNormalizer`'s `(?m)(?<=\S)(#{1,6}[ \t]+\S)`
demoted a start-of-line `## Heading` to `# Heading`, because the first `#` satisfied the lookbehind.
One product bug behind all five reds. Fixed in `c85b9bdc`, confirmed by a focused receipt: 26 tests
executed, passed.

`c30eb2fe`'s eight cluster around local provider routing and are **hypothesised** (not diagnosed) to be
stale Ollama expectations after the LlamaCpp switch. Verify before fixing.

**How that was nearly missed, and the lesson.** The first read of a failing gate said "worker profiles:
failed: 1". That came from grepping two of roughly twenty lane receipts. Always sweep every lane:

```
grep -oh 'testName="[^"]*"[^>]*outcome="Failed"' <attempt-dir>/<attempt-id>.*.trx | grep -o 'testName="[^"]*"' | sort -u
```

**Roster: codex is back on Developer** (`openai-developer`, `gpt-5.6-sol`, `codex-cli`, medium). Verify
which harness actually ran by the prompt filename suffix (`-codex-cli.md` / `-grok-cli.md`) or the
`Dispatched to ...` reason — **not** by `agents` output or `status`, both of which lied all evening. See
backlog `fd4a5ed5`: a roster change and a `reassign-agent` can both report success and never reach
dispatch.

**Five defects filed tonight, all orchestrator-side, all cost real rounds:**

| Id | Defect |
|---|---|
| `9a9c7e8e` | a rejected focused-evidence request is replayed verbatim forever; burned 16 retries on `21b284a0`. Trigger is **multi-selection** requests only — a single selection works |
| `2861b909` | operator text in `recover`/`retry --text-file` is recorded, reports `Applied`, and never reaches the prompt when the retry counter is not yet engaged |
| `fd4a5ed5` | `agent`/`agent-add` leave the subscription alias stale, and task agent assignments revert silently |
| `9fbce159` | the Planner contract rejects `File.cs:442-479`; a line range discards the whole plan. Mitigated for future Planners by `045930f8` |
| `7009ffbd` | the eight local-provider reds above |

**Operating notes that saved or cost time tonight** — all now in `docs/operator-runbook.md`:
verify a conductor rebuild with `App.dll.git-head` vs `git rev-parse HEAD` (run-dir hash proves nothing;
handoff rebuilds are intermittent); never grep a .NET assembly for a string you added (UTF-16, gives
false negatives on strings that are demonstrably present); put worker guidance in the **brief**, not a
recover note; and front-load numbers in operator notes because truncation eats the middle.

Prior section follows.

## RESUME HERE — 2026-08-20 00:38 UTC, loop RELAUNCHED (was wound down after a LlamaCpp Tester 400)

**UPDATE 2026-08-19/20 — board ran unattended, roster changed, loop relaunched.** While Claude and
Codex were rate-limited the board kept working: **58 commits landed, 14 of them goal integrations**.
Main is now `093a389b`. The loop was deliberately stopped and has been relaunched (conductor pid
1096804, lock claimed 00:37:58Z).

**Worker roster now:**

| Role | Model | Harness |
|---|---|---|
| Planner | Anthropic `claude-opus-5` | `claude-cli` |
| Researcher | xAI `grok-4.6` | `grok-cli` |
| Developer | xAI `grok-4.6` | `grok-cli` |
| Tester | xAI `grok-4.6` (id `xai-tester`) | `grok-cli` |
| Reviewer | Anthropic `claude-opus-5` | `claude-cli` |
| Ideation | LlamaCpp `qwen3.6-35b-a3b` | `qwen-code-cli` |

Planner was deliberately moved to Anthropic because its output feeds Developer, Tester **and**
Reviewer — a weak plan is the one defect that compounds through every downstream role. Changes were
made with the `agent <role> <provider> <model>` verb, not by hand-editing `agents.json`.

**CODEX QUOTA RESETS 22:29 CDT 2026-08-19 (03:29 UTC 2026-08-20) — reintegrate codex for Developer
then.**

**Local inference (qwen-code + llama-server): works for Planner-class roles, Tester failed 6 of 6 and
has never once succeeded.** Root cause measured rather than guessed:

- A Tester dispatch baseline is **~10,906 tokens**, of which **~8,671 is qwen-code's own startup**
  even with `--bare` (confirmed by api_response telemetry and independently by the README).
- Tool-result accumulation pushed real requests to **33,060** and **40,603** against a **32,768**
  context. One missed by only 292 tokens.
- The orchestrator owns only ~2,400 of those tokens, so **shrinking prompts cannot fix this** — the
  bloat is qwen-code's own conversation growth, which the orchestrator does not manage.
- All 6 dispatches were the same worst-case task (build a csproj and run MTP tests). **The normal
  deferring Tester has never been tried**, and would have ~21k headroom.

**Recommended path: `evidence_request`.** The Tester emits
`evidence_request:{selections:[{test_project,test_class}]}` and the conductor runs the tests
instead. Already implemented and proven on the Reviewer path (`mode=focused
reason=explicit-focused-mapping`). It removes both failure classes at once — no shell composition, so
no POSIX-on-Windows or MTP-filter errors, and zero test output in the model's context.

Fixed this session in commit `74857b14`: worker prompts now state the host is Windows (the prompt had
**zero** platform mentions, and the model reached for POSIX `printf` three times), and the Tester
requirements now name `Invoke-TestSummary.ps1` plus the required `FullyQualifiedName~Class` filter
syntax. Verified 66/66 on `TaskBrief` tests.

Open: `scripts/Start-LlamaServer.ps1` still defaults to `-c 16384`, which cannot fit a Tester after an
8,671-token startup — under review. Prompt reprocessing also dominates latency: a real dispatch took
`duration_ms 142923` for 10,906 input tokens, so the constant startup prefix is a KV-cache-reuse
candidate.


The orchestrator is deliberately stopped at a clean quiet boundary. Repo-root `.conduct-stop` is
present; `LOOP_STOP tick=29 rechecks=144 reason=stop-while-idle` was recorded at 23:49:36Z; former
conductor PID `834896` (host `857764`, cmd `767572`) is gone; lock file gone; inventory found no
orchestrator lock-holders and the build lock FREE. **Do not remove the marker or relaunch until the
operator resumes.** No goal-intake command was run after this stop.

Main is `093a389b` — charge qwen-code startup tokens to the worker profile, not LlamaCpp. Live llama-server
was last observed at `127.0.0.1:8080` with `-c 32768 -ngl 99 -ncmoe 26` on `qwen3.6-35b-a3b`; verify
before a LlamaCpp dispatch. Anthropic Reviewer is `claude-opus-5`. Usage was reset this session.

Landed this session (already on the incumbent that just stopped): `d3240da1` grok heading normalizer;
`ca5014f1` / `28982f8d` qwen-code prompt on stdin (not argv / not `Get-Content`); `5ca93490` always
`--filter-not-trait Category=HostIntegration` on unattended MTP; `3e572472` then `093a389b` LlamaCpp
window 32768 minus qwen-code `--bare` tax 8700 on the **profile**, not the provider.

### Parked goals — do not unpark as a set

| goal | state | do |
|---|---|---|
| `e8351d7c` | Parked. Developer completed (`dfd73332`, one comment). Tester Failed. | Unpark only after the 32k brief/system-prompt change below. Retry Tester **once**. |
| `6241ed3f` | Parked. Product `8d09516d` (typed drain policy) independently looks good. Last FullSuite failed the same 11 tests twice. | Do not land. Diagnose the 11 (lifecycle ×2, process-spawning budget test, Remainder 8) separately. Do not re-dispatch its Tester or Developer. |
| `ab933e32` | Parked. Researcher Failed heading contract twice (`…111635…`, `…211122…`). | Do **not** cancel. Do **not** third-retry the heading axis via feedback. |
| `b54b2d92` | Parked. Duplicate of the drain-policy work. | Do **not** recover. |
| `9f64cd98` | still AcceptanceFailed from the 08-18 handoff | Do **not** create a second retry. |
| `d6569ca1` | Cancelled/abandoned smoke | Leave it. |

### LlamaCpp Tester — launch is validated; completion is not

`e8351d7c` was the true file-touching Tester run (not `verify-manual`). Dispatch was
`qwen --bare --approval-mode yolo --input-format text` against the live 32768 server. Brief was 9644
bytes. Receipts:

| probe | prompt tokens | result |
|---|---|---|
| empty dir, `--bare`, “say ok” | **8017** | works |
| worktree + real Tester brief, `--bare --max-tool-calls 0` | **10906** | first request fits |
| live Tester, tools allowed | **40603** | `400 request exceeds 32768` |

Hook-up that is done: Windows argv, stdin, `--bare`, LlamaCpp 32768, profile tax 8700. The 400 is
**later-request tool growth**. The shared brief still says “read digest.md first” and to use
source-survey; those files plus `AGENTS.md` (~30k bytes) are what blew the session. **Do not “fix”
this with `--exclude-tools read_file`** — filesystem access is the intended model. Next change:
stop instructing a 32k Tester to ingest digest / source-survey / AGENTS; keep `read_file` and
`run_shell_command`. Then unpark `e8351d7c` and retry Tester once. Pass only on: process exit, MTP
counts ≠ 0, no 400, no `verify-manual`.

`--pipeline five-role` through `Invoke-OrchestratorCommand.ps1` never reaches the CLI:
`CmdletBinding` binds `--pipeline` to `-PipelineVariable`. Three intakes (`44a6563a`, `8d88ade3`,
`e8351d7c`) created automatic Developer+Reviewer (`isOverride:false`). The Tester task was added
with `add-task --goal e8351d7c Tester --text-file … --before-role Reviewer`. Until that wrapper is
fixed, force five-role via `mcg-orchestrator.cmd` directly, or `add-task`.

### At resume

1. Do the Tester brief/system-prompt change (or an equivalent that keeps FS tools) **before**
   unparking `e8351d7c`.
2. Leave `6241ed3f` / `ab933e32` / `b54b2d92` parked unless the operator names one.
3. Delete `.conduct-stop`, then
   `.\scripts\Invoke-RepoScript.ps1 scripts\Start-OrchestratorCommand.ps1 -Name <batch> conduct --loop --watch --policy Permissive --poll-seconds 15 --max-duration 3600`
   with no `-AppDll`.

## ~~RESUME HERE — 2026-08-18 05:50 UTC, deliberately wound down after usage exhaustion~~ — **SUPERSEDED by the 2026-08-19 23:49 UTC wind-down above** (re-checked 2026-08-19: conductor was relaunched and stopped again; several of the “no implementation goals” children below were later created — `ab933e32` from `76cd5e80`, `6241ed3f`/`b54b2d92` from `5c4c854d`. Treat the parked-goal table in the 23:49 section as live, not this intake list.)

The orchestrator is deliberately stopped at a clean quiet boundary. Repo-root `.conduct-stop` is
present; `LOOP_STOP tick=102 reason=stop-while-idle` was recorded at 05:49:29Z; former conductor PID
`26564` is gone; the final supported inventory found no orchestrator lock holders and the build lock
FREE. **Do not remove the marker or relaunch until the operator resumes usage.** No goal-intake command
was run after this stop.

Main is `9b0ab00d` after `d244f1b8` — make evidence-mutation lease release retryable and
non-dispositive — passed all 17 partitions and landed. Its full gate ran 23m51.175s. The now-landed
fix preserves every accepted result, including Passed-with-advisory, through lease cleanup. A supported
`acceptance-retry` has already moved `13b3be0d` — finish the cleanup-lane fixture reduction — from
AcceptanceFailed to Verified; the next current-HEAD conductor should run one fresh integrated gate
(the old main-keyed partition cache is invalid).

`9f64cd98` — give durable refinement one execution owner and keep readiness pure — failed its second
full gate in 21m19.335s on exactly three candidate regressions after all 17 shards ran. A precise
Developer task-1 retry is durably Pending as operator intent `8a78d17fa83b491781aa48d616a555d9`:
fresh-load intake state after a concurrent reservation winner commits, migrate the bare AdvanceLoop
fixture before reading `state_outbox`, and assert the kernel-owned goal after intentional refinement
pending. It will apply when a conductor resumes; do not create a second retry.

Four detailed dedicated backlog children were filed and related, but **no implementation goals were
created**:

- `76cd5e80` — fix `goals --board` lifecycle/attention disagreement and its serial per-worktree Git
  N+1. Fresh live dogfood was 34.3s for 62 rows; source launches status + ahead/behind serially per
  registered worktree. Reuse the landed `GoalGitFactIndex` capture-once pattern.
- `5c4c854d` — inject a typed inherited-output drain policy while retaining the 12s/2s production
  defaults; two accepted tests consume about 25s combined.
- `03d8c426` — direct-launch a fingerprint-validated current SQLite helper artifact instead of
  compiling it on every supported read; this is a dedicated child because umbrella `48bc3608` has a
  bad legacy Full owner.
- `80ca5e16` — build one immutable fingerprinted real self-relaunch successor while keeping fresh
  state/lease/log roots for all three scenarios; accepted tests total 85.36s.

At resume, while the marker still owns the quiet window, intake the highest-payoff disjoint set:
FULL `b51bc690` — replace five native-MTP real project builds (325.36s in the latest accepted receipt)
with one Core production-path run plus structural/contract coverage; Slice `74802406` — one trusted
main prebuild plus generation-keyed structural-discovery snapshots and bounded discovery fan-out
(250.781s measured serial tail); then the four children above as capacity permits. Slice `345175c8`
— replace two redundant terminal-sweep full-repository early-exit cases — is also ready and disjoint
from those production seams. Use `--pipeline auto`, explicit Full/Slice coverage, and keyed requests;
do not add brittle elapsed-time pass thresholds. Then remove `.conduct-stop` and relaunch Permissive
from current HEAD with `Start-OrchestratorCommand.ps1` and no `-AppDll`, so the landed ae72/1856/48/d244
fixes are finally armed. All transient `.scratch` inputs from this wind-down were deleted.

The targeted repeated-cold-work audit confirmed this is a useful exemplar: prepare immutable inputs
once, bounded-fan-out independent reads, aggregate deterministically. It also found a lower-priority
21.19s double `GoalAcceptanceVerifier.RunAsync` safety-valve test and confirmed persistence/SSE/external
delivery loops generally carry real transaction, ordering, or stream dependencies; do not blanket
parallelize every `foreach await`.

The architectural reframe is already durable as backlog `95d6bd6d` — make the driving agent
replaceable and non-authoritative while preserving disposable portfolio intelligence. Prefer that
framing over “work the operator out of the loop.”

## ~~RESUME HERE — 2026-08-18 04:13 UTC, Codex takeover, max concurrency under Permissive~~ — **SUPERSEDED by the 05:50 UTC wind-down section above**

Main is at `22aaca10` after `48bbd193` — replace the 54.69-second UTF-8 full-suite discovery with a
minimal MTP probe — passed and landed. Its accepted target duration is **0.5331634 s**, **99.03%
lower / ~102.6x faster** than baseline; the full gate was green across 22 TRXs / 17 shards in
21m03.695s, with Process spawning still longest at 685.617s. The preceding `1856ee3e` — compact
read-only `goals --board` — also passed and landed. Its
full gate was green across 22 TRXs / 17 infrastructure shards in 21m35.138s. Post-landing live-store
dogfood `goals --board --all` exited 0 in 9.1s with `shown=64 omitted=0` and rendered Active,
Verifying, Verified, AcceptanceFailed, and Parked rows. It also exposed a likely projection defect:
the board still rendered cleaned-up `1856ee3e` and `ae72beac` as Verified/accepting; triage is active.
The preceding `ae72beac` landing's full gate was green across 22 TRXs / 17 infrastructure shards in
26m06.761s; Process spawning remained the floor at 740.274s, so do not claim an overall wall-time win
from this high-variance sample. The running **Permissive** conductor renewed to continuity child pid
`26564` at 05:00 UTC but deliberately reused the incumbent executable, so it still predates both
landings; relaunch current HEAD at the next all-quiet boundary to arm the fixes. PowerShell
7 resolution remains fixed and no `.conduct-stop` is present. Other recent landed velocity changes
include `089ad32e` (skip .NET shards for proven empty/strict-docs candidates), `e64cc9f9`
(PostLandingCanary fixture build removal), and `a23b0945` (Remainder resource-key correction).

`48bbd193` — replace a 54.69-second UTF-8 full-suite discovery with the minimal MTP probe — is
CleanedUp on main and measured **0.5332 s** in its accepted full gate: **99.03% removed**. Its first
gate's sole failure was an
unrelated Remainder apparatus precondition: a Git `rev-parse HEAD` helper returned null in 78 ms and
discarded the causal stderr/exception; the same test passed in five recent gates and the automatic
recovery worker reproduced it green 1/1 in 2.221 s. Candidate `eddfe28d` remained clean and unchanged;
both tasks were manually reverified without a candidate change. Do not invent a candidate fix for
the opaque Git failure; a dedicated diagnostic-hardening brief is prepared for the next boundary.

`9f64cd98` — make durable refinement the sole execution owner and keep readiness pure — failed its
first full gate after 27m29.997s with 55 candidate-path failures across seven shards: repair state was
acquired but executor launch was deferred outside the app host, plus missing `state_outbox` setup.
Corrected candidate `de280649` now proves pending -> one outbox row -> coordinator ProcessAsync ->
reload -> dispatch (clarification slice 8/8 plus both prior regressions); independent review passed
and a fresh full gate is live concurrently with `d244f1b8`. `d244f1b8` — make evidence-lease
release retryable and unable to overwrite a green result — is in a live full gate at corrected candidate
`e775a415`: typed lease-acquisition evidence replaces Reason prose, every Passed result including
advisory-unmet is retained, build is green, and the exact focused slice passed 6/6. `13b3be0d`
remains AcceptanceFailed pending that fix landing; its old green partition cache is diagnostic only
because main has advanced and cache identity includes main SHA.

`1856ee3e` — compact read-only `goals --board` — is CleanedUp on main. Its
first gate found a disabled-collection invariant; the first fix would have serialized Cli + lifecycle
+ cleanup into a measured **~879.7-second** exclusive chain. The corrected candidate `9e144070`
instead removes the board fixture's unjustified cleanup collection and reverts only the new Cli key;
fresh focused suites passed 20/20, 17/17, 107/107, and 3/3. After landing, run live
`goals --board --all` and retain its post-landing operator evidence.

Anthropic now reports the explicit monthly spend limit on every Reviewer dispatch. Reviewer bindings
and roles remain unchanged; substantive independent operator review bridges only the affected current
candidates until Wednesday. Backlog `9d4cc989` records that the explicit budget message is currently
misclassified as `unknown-failure` and immediately retried. Intake backlogs `ad054da3`, `ec4faf81`,
and `aa06b228` retain the typed-inbox, slice-consumption, and ignored-pipeline defects. The next test
intake at the next all-quiet boundary is: (1) FULL `b51bc690`, collapse five serial native-MTP
project executions while retaining one real Core production-entry run (**338.66 s** observed); (2)
the structural-discovery-cache slice of `74802406`: accepted gate decomposition found a **5m03.986s**
post-shard serial tail, with **250.781s** in six serial trusted-project builds/discoveries; use one
generation prebuild, typed immutable snapshots, bounded fan-out, and fail-closed invalidation; (3) a
dedicated FULL child implementing the SQLite-tool current-artifact slice of umbrella `48bc3608`
(**15.61 s** test plus operator-read tax; legacy cancelled predecessor incorrectly owns Full
coverage, so do not `goal-replace` it); and (4) a dedicated child for the two inherited-output
12-second drain waits (**25.73 s**). `345175c8` remains the subsequent slice for the **42.56 s**
terminal-sweep fixture; its `CliPersistentStateRunner` collision cleared when the board goal landed.

Crash conclusion and dump configuration are in `### Crash analysis — final bounded conclusion`
below. The partial screen supports `CLOCK_WATCHDOG_TIMEOUT (0x101)`; the CPER proves a prior-session
Intel product-`0x037` PMC-family crash log surfaced at boot, but public Intel/OEM collateral does not
map its opaque reason fields, so component-level attribution remains unknown.

## ~~RESUME HERE — 2026-08-18 00:55 UTC, Codex takeover, max concurrency under Permissive~~ — **SUPERSEDED by the 04:05 UTC section above**

Main is at `bccb6254` after `8ab0a29d` landed. The conductor is running
**Permissive** under continuity-child pid `32184` (supervised by pid `22236`) with
`--poll-seconds 15 --max-duration 5400`; no `.conduct-stop` is present. It was launched before the
latest lease-backoff and MTP landings, so bounce it through the runbook at the next quiet gate
boundary to arm current HEAD; do not repair SQLite. At the latest status read, `4b9d69fb` was
Verifying; `1856ee3e`, `f9e4f0f0`, `9f64cd98`, `a23b0945`, `13b3be0d`, `e64cc9f9`, and new
velocity goal `089ad32e` were Active. Observe through `conduct-events.log` first. Do not replace
this with a Conservative/manual workflow.

### Live velocity work

- **`b985206d`** is Completed and landed at main commit `e9f1895e`; it removes the two deterministic
  ~60-second RunEventStore test waits. Candidate `bb7ce759` cleared the normal Anthropic Reviewer and
  passed full acceptance at 21:35:26 UTC. The retry added a real
  production-default cadence/store integration test, closing the first review's coverage blocker;
  the Tester independently passed the three-project isolated build. The full-gate TRX measured the
  targets at **0.021 s and 2.189 s**, down from about **60.1 s each** (~118 s of test work removed),
  and the production-default coverage test passed in 0.023 s.
- **`3b21ed78`** — stop retrying a held acceptance/evidence lease every tick and report its
  owner/expiry — is Completed and landed at `ea601c71`. Its normal Anthropic Reviewer completed;
  no discretionary extra cross-family review was added.
- **`1856ee3e`** — add a compact, read-only `goals --board` takeover/supervision command — is Active,
  full coverage of backlog `c9c1e8eb`. Fresh built-app execution proved the current candidate rejects
  `goals --board --all` before routing because the validated flag set omits `--board`; five blocking
  evidence/coverage gaps also remain. The conductor incorrectly launched a third Tester on unchanged
  SHA `79ef600d`; the operator cancelled it and an exact six-finding Developer retry intent was Applied.
  Backlog `b1c83566` records that same-SHA Tester/upstream-owner routing defect.
- **`4b9d69fb`** — replace coarse `ownership:tests` reservations with deterministic per-test-project
  keys — is Verifying in a live acceptance attempt, a slice of backlog `c49290c3`; Developer, Tester,
  and Reviewer completed. Its first Planner output was substantively complete but falsely rejected because an expected resource-key literal was parsed as a nonexistent target
  citation. It preserves shared-infrastructure and unknown-layout
  fail-closed behavior.
- **`f9e4f0f0`** — stop Tester prose from falsely requiring file changes when structured verification
  evidence is complete — is Active with Developer reopened after an environmental acceptance failure,
  a slice of backlog `c279e0b7`. Its 560-test Process-spawning shard ran 7m54.865s and failed only
  because low-integrity testhost could not launch `pwsh`; backlog `1a7c7ac7` now has this second
  independent receipt. The Reviewer correctly found that
  only the deferred fixture is a discriminating RED arm and that the per-goal negative-control receipt
  was missing; the Developer added the receipt and corrected that claim. A second review then hit the
  documented hard cycle of requiring Acceptance-owned execution before review can finish, so criterion
  10 alone was waived with that exact reason; the narrow Reviewer retry completed. No-change
  Developers remain blocked.
- **`8bc81cae`** — avoid redundant workspace-boundary integrity relabeling and parse the mandatory-
  label entry precisely — passed acceptance and landed at `45a755d9`, a slice of backlog `eaf7f191`.
  Unknown/non-Medium state still sets the label and fails closed.
- **`8ab0a29d`** — enable one-step MTP `dotnet test` — passed operator re-gate 1/3 and landed at
  main commit `bccb6254` after the prior full gate ran 1,000 infrastructure tests and failed only when
  `LaneTimingMeasurementScriptTests.MissingManifestLane_FailsAndNamesLane` could not launch `pwsh`
  from the low-integrity temp directory (`Win32Exception: Access is denied`). The candidate's
  Developer and normal Reviewer are complete; this was recorded as environmental, not a worker retry.
  Its persisted lifecycle still reads Verified immediately after landing, matching the known
  terminalization/sweep defect rather than an unmerged branch.
  Rebase proved the acceptance failure was branch-owned: its own commit reintroduced
  `CreateShortWindowsRoot`, whose synthetic `LOCALAPPDATA` parent was never created before atomic
  claim attempts. The retry fixes that exact defect and keeps the concurrent distinct-root control.
- **`1ef781e5`** — the standalone early-apphost/temp-root repair — is Cancelled because a fresh
  current-main control passed and the failing helper was owned by `8ab0a29d` itself. Backlog
  `da19b0b4` was superseded to `1afa6964`; do not revive this duplicate lane.
- **`9f64cd98`** — give the durable refinement coordinator sole execution ownership and make readiness
  a pure query — is Active with Developer complete at `28fd9a3a` and normal Reviewer live, full coverage of backlog
  `c87ed4d1`. Its exact scopes were rechecked against the live board diff before intake. It immediately
  reproduced its target: ticks 160 and 163 treated `SPEC_REFINEMENT_PENDING` with
  `executor_started=true` as a subscription spawn failure, escalated, and launched no-op executor
  rechecks while the original durable refinement remained in progress; no attention item is open.
- **`a23b0945`** — release `Remainder` and `Remainder balance B` from borrowed exclusive-resource
  keys — is Active with Developer complete and Reviewer Failed pending exact triage, a slice of backlog `13b65761`. The passed `8bc81cae` receipt
  shows roughly 149 seconds of unrelated tests held behind those keys. This is the obvious manifest
  correction; no new pre-change timing exercise is required. Its refiner correctly found two omitted
  `GoalAcceptanceVerifier` classes (`AcceptanceOutputCaptureTests` and
  `HermeticVerificationEnvironmentTests`); the operator answered yes to route/exclude them under the
  same exhaustive rule, and the goal returned to Active.
- **`13b3be0d`** — salvage the unlanded cleanup-lane reduced-repository fixture — is Active with a
  clean candidate at `9de855f0`; focused evidence passed 199/199 and Reviewer verdict was pass, but
  automatic attestation rejected two explicitly operator-owned criteria. Four reversible RED controls
  are prepared in detached worktree `.scratch/13b3be0d-negative-controls` and must run after live gates
  release the relevant resources, then be documented before `verify-manual` closes Reviewer task 2.
  Full coverage of new backlog `9b82af60`. It reapplies detached commits
  `a035f923`/`3e72bb0d`/`37306ce1` on current main with corrected evidence ownership and one shared
  immutable template helper. Expected source-level reduction is 6–18 seconds; post-landing timing is
  nonblocking.
- **`e64cc9f9`** — remove the full-repository build and live-checkout write from the measured
  38.02-second PostLandingCanary test — is Active with Developer responding to a failed focused-evidence
  round, full coverage of backlog `2918058d`.
  Production binary resolution remains unchanged; the test gets a scoped prebuilt-binary seam and a
  disposable minimal repository while retaining the real worktree/process/JSON/TRX path.
- **`089ad32e`** — skip dotnet shards for successfully known-empty or strict `docs/**` candidates while
  retaining non-dotnet checks and ordinary landing semantics — is Active with Developer live, full
  coverage of backlog `c6750b35`. Unknown/code/mixed paths remain fail-closed/full-gate; cohort TRX
  evidence is not weakened. Scope extraction again falsely claimed forbidden/example paths, already
  covered by backlog `e9c98cc5`.
- **`26e9d1ef`** is Cancelled/retired after testing the apparently obvious first slice of backlog `f1b5e4aa`: remove only
  `GoalWorktreeTestsRebaseMerge` from the process-wide cleanup collection. Pre-review evidence passed
  **31/31 tests in 165.908 s**, but the normal Reviewer correctly rejected it: the deleted attribute
  also supplies `IsolatedDotnetRootFixture`, and 19 tests reach the acceptance CLI's real build-slot
  grid. The isolated filter cannot prove concurrent safety. Its retry instead expanded into fake slot
  injection across 17 contexts; the operator cancelled it, discarded only that uncommitted divergence,
  and ran `abandon-goal`. This rejection is annotated on `f1b5e4aa`.
  `GoalWorktreeTestsOrphanEphemeralSweep` remains excluded because it mutates the static
  `GoalWorktrees.SandboxAclHelper` seam.
The runnable board is at worker capacity. `b15cb04c` (stop-while-idle) remains parked because its
frozen criterion requires the wrong behavior; recut it from backlog `2f13d826`. `e5c18520`
(background acceptance ownership across handoff) remains parked pending semantic integration against
the now-landed `3b21ed78` conductor/lease changes. `a893a9e6` and empty duplicate `275c97fd`
retain durable `Retired` dispositions; `a893a9e6` work remains at tag
  `salvage/a893a9e6-cleanup-lane-tests`; its useful work is now recut as `13b3be0d`.
Backlog `c87ed4d1` (make the durable refinement coordinator the sole refinement owner) was intaken as
goal `9f64cd98` when `8bc81cae` freed a lane; its corrected brief remains in the latest backlog annotation.

### Takeover dogfood findings

- Architecture backlog **`95d6bd6d`** replaces the crude “work the operator/driving agent out of the
  loop” framing with the stronger target: retain disposable portfolio-level intelligence, but make
  every driver replaceable and non-authoritative. A fresh driver reconstructs decision facts from
  typed durable state; typed idempotent proposals cross into a deterministic kernel that alone owns
  mutation, authorization, lifecycle, acceptance, and landing; driver failure loses time, not truth
  or control; humans receive compressed decision-complete escalations. `HANDOFF.md`, chat history, and
  agent personality are convenience context, never required authority or state.
- New backlog **`e9c98cc5`** records that scope extraction treated the explicit prohibition “do not
  edit `config/acceptance-manifest.json`” as a positive claimed file and reported a false exact-file
  collision between `13b3be0d` and `a23b0945`. New backlog **`dd9bb673`** records that invalid goal
  option validation persisted a failed intake before rejecting `--backlog-coverage partial`, burning
  the idempotency key and forcing a v2 request. The same scope defect then claimed the whole
  `docs/negative-controls/` directory for goal-specific evidence and falsely serialized `e64cc9f9`
  against `13b3be0d`.
- New backlog **`1a7c7ac7`** records the low-integrity `pwsh` launch failure that blocked
  `8ab0a29d` after 1,000 tests. New backlog **`bfa98686`** records the associated status-surface defect:
  `next --full` projected that `AcceptanceFailed` goal as Accept/high-confidence/no-blockers with no
  immediate action while the conductor called the same state unhandled.
- New backlog **`4731eac5`** records a regression of closed CLI-ergonomics backlog `61fd36c7`:
  `attention show` printed clarification id `2b55912c`, but both documented `attention answer` forms
  rejected it; only the separate top-level `answer 2b55912c ...` command succeeded.
- Backlog **`fe7e4012`** records the proven green-gate loss mechanism, now reproduced by
  `13b3be0d`: `RunParallelLandingAcceptance` computes GREEN while holding an evidence-mutation lease,
  then the lease-disposal SQLite `DELETE` can throw `database is locked` and replace that green return
  with a fault while leaving the lease stale. Later one-second attempts are merely early-held, not
  fresh green gates. Make release lock-safe and preserve the completed result; do not blame source-
  backlog replacement or rerun an uncached full gate.

- Goal-board usability is active goal **`1856ee3e`** (full coverage of backlog `c9c1e8eb`).
  `Get-OrchestratorSnapshot.ps1` again timed out without output during this intake, reinforcing the
  need for the compact read-only board command.
- New backlog **`50bb6fb8`** records that standalone `goal-recovery` is advertised as inspection
  but runs `TerminalGoalSweep` and terminalized `3b21ed78` without a repair confirmation. It now also
  carries the receipt that the live sweep re-terminalized already-landed `8bc81cae` with identical
  evidence on at least nine ticks, contributing repeated 6–14-second sweeps. Existing
  backlogs `c87ed4d1` and `3eb43a8a` now carry the tick-20 receipt where a pending-refinement
  readiness check held the serial per-goal walk for 176.374 s; `ec331244` carries both the 11-second
  per-command control-plane receipt and three filtered backlog reads that each took 19–20 seconds
  regardless of whether they returned 1, 25, or 41 rows.
- Existing backlog **`464b4b27`** now records the live contradiction where clarification-blocked
  goals remained Active, `readiness` said start allowed, and `next` advertised runnable tasks while
  only `attention show` exposed the real gate. Four clarifications were answered and both affected
  Researchers resumed through typed retry intents.
- New backlog **`ee2fd57f`** records the false Planner-contract rejection of an expected resource-key
  literal as a nonexistent source target. The exact plan was reissued through a typed Planner retry.
- New backlog **`75073507`** records that sanctioned isolated build/test helpers emitted roughly
  1,869–2,117 lines even at quiet verbosity (and about 358 lines on the focused summary path), hiding
  the useful verdict and adding avoidable operator latency. The fix must bound default output while
  retaining full diagnostics in a raw artifact.
- Existing backlog **`8674be25`** now has the `b985206d` receipt: a stale 30-minute evidence lease
  repeatedly blocked forward progress while reporting a generic concurrent-acceptance message; the
  first targeted Permissive tick after exact expiry advanced immediately.
- Existing backlog **`2f13d826`** now has a second `stop-while-idle` receipt. After `b985206d`
  became Verified, pid `21132` emitted no ticks for more than 10 minutes despite `next --full`
  reporting Accept/high-confidence/no-blockers and no child workers. The canonical stop file produced
  `LOOP_STOP tick=100 reason=stop-while-idle`; a clean Permissive relaunch then landed the goal.
- Operator priority is implementation velocity: use measurement to choose the obvious serial
  fraction, then change it. Do not create another audit when the measured target and mechanism are
  already known.
- Anthropic budget is constrained until Wednesday, so skip additional discretionary cross-family
  review until rollover. This does **not** change the Reviewer role or catalog. The exact original
  Anthropic Opus Reviewer and `b985206d` Reviewer binding were restored after a mistaken temporary
  reassignment.

### Crash analysis — final bounded conclusion

System WHEA EventRecordID `28747` is a valid 3,552-byte CPER (`BOOT`, `PreviousError`, Fatal), supplied
through ACPI BERT from the prior session. Its three firmware-record-reference sections decode as Intel
Crash Log `PMC`, `PMC_TRACE`, and `PMC_RST`; collection completed before reset. There are no standard
processor/MCA, memory, or PCIe sections in this CPER, but absence does not rule those causes out. The
operator's partial-screen photograph appears to show **`CLOCK_WATCHDOG_TIMEOUT (0x101)`**, which means
Windows detected a secondary processor that stopped processing clock interrupts. `Kernel-Power 41`
recorded bugcheck zero and Windows produced no bugcheck `MEMORY.DMP`, but WER did preserve a separate
`LiveKernelEvent 124/7` WHEA dump. Here `124/7` means a WHEA **BOOT error source** and corroborates the
next-boot CPER; it does not prove that the photographed stop was `0x124` or recover the lost `0x101`
state. Its queued copy is under
`C:\ProgramData\Microsoft\Windows\WER\ReportQueue\Kernel_124_6ae9f61acf7cd854dee99924782464fe55cf456c_00000000_1883ed1d-ef6e-48c4-ad49-860d2a1e5553`;
the original was `C:\Windows\LiveKernelReports\WHEA\WHEA-20260817-1502.dmp`. This session cannot read
the ACL-protected queued copy without another elevation prompt, which the operator explicitly declined.

Intel's complete reachable public crashlog history (all tags and pull refs), all six official release
ZIPs, and the relevant Intel PMT/Linux, EDK2, coreboot, and MSI public material contain no product
`0x037` definition or semantic mapping for reason `0x00020010`/PMC_RST `0x1802`. Official `iclg`
therefore decodes only the common headers; a full decode requires matching Intel/OEM collateral via
`iclg -c`. The extracted raw record is preserved in the ignored artifact store at
`artifacts\crash-forensics\2026-08-17\SeventhSon-evt-2026-08-17-20-02.crashlog`
(SHA-256 `EF11DCA3F172D40FDEE59C09BB12B2AC40A27CA7A1DE241B18ECCA80ED142909`), with an MSI/Intel-ready
support note beside it in `README.md`. The bounded conclusion is
**processor/platform forward progress was lost**; CPU silicon, BIOS/microcode, CPU voltage/clock
behavior, motherboard delivery, and firmware/software deadlock remain possible. Primary PSU loss is
less supported after the 0x101 photo. The strongest reversible trigger is the mixed four-DIMM 3200
configuration: 2x16 GB Corsair plus 2x8 GB Team Group. It is not proven, and prior APIC-0 internal-
parity WHEA reports predate the RAM upgrade. Run an XMP-only A/B first (all four DIMMs unchanged), then
one matched kit at JEDEC if both arms fail. Preserve the crash photo and restart once before testing so
the configured Automatic dump can capture the hung processor index and stack on a recurrence. Configuration is now
`CrashDumpEnabled=7` plus `AlwaysKeepMemoryDump=1`, with the system-managed pagefile on C:; a restart is
still required before those settings take effect. Board/firmware: MSI PRO Z690-A WIFI DDR4 (`MS-7D25`),
AMI BIOS `1.C0` dated 2023-05-16. Do not flash from this note; verify the exact current MSI release first.

## ~~RESUME HERE — 2026-08-17 20:10 UTC, recovered from a host crash, loop running~~ — **SUPERSEDED, see the 21:19 UTC section above**

**Host crashed mid-session and was recovered.** Main is at `36dfd0fb` (Integrate goal/27b2d4d3).
Conductor relaunched as **pid 24508**, holds `conduct-loop.lock`, ticking normally — `b76879ab`
restarted acceptance at 20:08:17.

Crash assessment, all verified rather than assumed: main working tree clean, no `.conduct-stop`, goal
branches intact, queued operator intents survived (the tick is the only applier). The old lock still
named dead pid `73244`; stale-lock takeover worked once `tasklist` confirmed **no `dotnet.exe`
processes at all** — that phrasing is the unambiguous form, unlike `Get-Process` exit codes.

Relaunch that worked:

    pwsh -NoProfile -File scripts/Start-OrchestratorCommand.ps1 -Name conduct-loop conduct --loop --max-duration 5400 --watch

The launcher's `$Arguments` is `ValueFromRemainingArguments`, so trailing positional args bind fine —
the older `-Arguments` note below still works but is not required. The launcher rebuilds and takes
**over two minutes**; do not treat a slow return as a hang.

### Landed 2026-08-17 — eleven goals

`b1656a93` worker-context artifact prep (the day's biggest unblocker, freed four goals) · `38a4449e`
lane variance ~2× · `951b4cb7` pass 1e fixed-waiting · `c3b6852a` lane-estimate refresh · `65a89883`
same-goal reconciliation · `323a40a8` temp-root flake (**did not actually fix it — see `da19b0b4`**) ·
`e7a49b08` qualified lane-timing tooling · `c23ca078` pass 1d · `06cbde82` gate-makespan measurement ·
`27b2d4d3` dispatch evidence disagreement (**incomplete — see `c279e0b7`**) · plus hand-landed
`c365fd32` artifact-preparation fallback.

### In flight

- **`b76879ab`** pass 1f — in acceptance, re-running after the crash. Measured **218.2 s serial vs a
  335.4 s control**, under its 380 s ceiling. Two criteria were waived by exact text (circular
  post-landing window, and an operator-owned RED arm); criteria 1 and 3 still stand as the anti-vacuity
  guards.
- **`b985206d`** cut top-cost tests — at Reviewer. Re-scoped by remeasurement to the only two targets
  still expensive: `RunEventMaintenanceCadence_self_defers_when_database_writer_is_busy` and
  `SqliteRunEventStore_maintenance_defers_when_database_write_lock_is_active`, both pinned at
  **60.07–60.24 s with a 0.15 s spread** — a hardcoded timeout, not work.

### Deliberately held — do NOT revive without reading why

- **`b15cb04c`** — premise refuted. Its frozen criterion 1 *mandates implementing a bug*. Needs a
  re-cut; corrected premise is on backlog `2f13d826`. The `27b2d4d3` landing does **not** unblock it.
- **`8ab0a29d`** — AcceptanceFailed. Do not reopen until `da19b0b4` closes with a repeat-run control. I
  reopened it once on an unvalidated landing and burned a full acceptance cycle.
- **`e5c18520`** — Developer dispatch blocked by a 3-file merge conflict with main. Only one conflict
  hunk in `ConductorBatchLoop.cs`, but resolution is *semantic*: main's side came from its own split-out
  child `65a89883`, which landed. Needs a guarded window — validating a conductor-loop merge requires a
  build.
- **`a893a9e6`** — salvaged and abandoned. Work preserved at tag `salvage/a893a9e6-cleanup-lane-tests`
  (6 files, 156 insertions; verify with `git diff --stat main...<tag>`, **not** `git show`).
- **`275c97fd`** — duplicate of `b985206d`, empty branch, refinement-deadlocked.

### Operator-owned work outstanding, all needing a quiet gate window

RED-arm execution for the declared negative controls (tracked `143cbe9f`); the five-run load validation
for `323a40a8` (now a prerequisite for `da19b0b4`); and the `e5c18520` merge. Receipt *scanning* needs
no window — that misclassification is why `06cbde82` sat blocked for hours.

### Defects filed 2026-08-17

`845349a5` 20 000-char decision cap silently makes a complete worker result "unparseable" · `e8729993`
eight more caps that feed decisions, incl. a scope guard that **strips its own omission marker** and a
16-PID cap that mislabels lock holders as foreign · `c279e0b7` `27b2d4d3` incomplete: a prose regex
still blocks verification-only Testers and matches test-audit goals by construction · `e2c366ba`
unresolvable base commit lets a no-change Developer reach Completed · `4dc7d959` CLI silently ignores
unknown flags · `3fe73923` `Get-TestCostRanking.ps1` aborts on the first malformed TRX · `da19b0b4`
temp-root flake survived its fix · `ab281eb9` reviewer misroute (**premise wrong — read its
annotation**) · `cab46813` 529s escalate instead of retrying · `c090ae39` / `9510f2a9` inline gates and
unbounded canary wait · `eee0d174` one-shot `goal-salvage` command · `27239f10` evidence reuse
(scope-corrected).

### Goal disposal is a minefield — three traps hit today

1. `cancel-goal` with open tasks reports `Cancelled` then **silently reverts to Active**.
2. Cancelling those tasks to make it stick instead makes the task set terminal and **promotes the goal
   toward landing**. Both `275c97fd` and `a893a9e6` advanced to Verified/Verifying this way.
3. `goal-replace` refuses a `Cancelled` predecessor, and cancelling strands the source-backlog claim —
   so `Failed` is usually the right terminal state.

`eee0d174` proposes collapsing this into one command. Until then, check `Source backlog:` before
cancelling: no claim means cancelling is safe.

### Cohorts: answered definitively, do not re-investigate

Across **all 70** `conduct-events*.log` files (2026-07-13 → 2026-08-17): **20 006 speculative plans, 0
with members; 1 974 production `ACCEPTANCE_COHORT`, all `outcome=unpaired`.** No coverage gap — the
machinery landed 2026-08-13 and logs precede it by a month. `LifecycleNotReady` is the largest
exclusion **every single day**, not `SerializedResourceOverlap`, so removing the resource veto attacks
the *second* constraint. Full design analysis and the narrowest traced pilot are on `c771916148`.

## ~~RESUME HERE — orchestrator deliberately wound down 2026-08-07 01:13 UTC~~ — **SUPERSEDED, see the 2026-08-17 section above** (re-checked 2026-08-17: the RAM upgrade completed, the host has since crashed and been recovered, and `71d5ab45` is 43 commits behind)

The loop was stopped gracefully at operator request (`LOOP_STOP tick=2 reason=stop-while-idle`), with
**zero workers and zero gates in flight**. No stale `conduct-loop.lock` was left behind; `.conduct-stop`
was removed after the stop, so a launch is not blocked. Nothing is mid-flight and nothing needs repair
before restarting.

To restart:

    .\scripts\Start-OrchestratorCommand.ps1 -Name conduct-loop -Arguments conduct,--loop,--watch,--policy,Permissive,--poll-seconds,30,--max-duration,5400

Note the parameter is **`-Arguments`**, not `-CommandArgs` — the latter is silently passed through as a
literal and the loop dies with `Unknown command '-CommandArgs'`.

The launcher rebuilds; `--max-duration` handoffs do NOT. Four goals landed on 2026-08-06, so the first
launch after a restart picks them up. Main is at `71d5ab45`.

**RAM UPGRADE IN PROGRESS.** The wind-down above was performed specifically so the host could be
powered off for it. Kit: 2×16GB 3200MHz Corsair Vengeance LPX, with the existing modules to be brought
to 3200 as well.

After the host comes back, before driving anything:

1. **Verify XMP actually applied** — this is the step most likely to be silently wrong:

       Get-CimInstance Win32_PhysicalMemory | Select-Object DeviceLocator,Capacity,ConfiguredClockSpeed,Speed

   Expect `ConfiguredClockSpeed = 3200` on **all** modules. If any report 2400, the profile did not take
   and the comparison against baseline is meaningless.

2. **Do one warm-up pass before measuring.** A cold file cache after boot will confound any
   before/after comparison and make the upgrade look worse than it is.

3. **Re-run the baseline and diff** — `scripts\Invoke-HostUpgradePrep.ps1` with **no switches**
   (capture-only; the switches mutate system settings), then compare against
   `Desktop\mcg-upgrade-baseline\` (56k counter rows, elevated capture, SSD wear = 0).

4. **Watch the pagefile ceiling.** It is system-managed by operator decision and its ceiling scales
   with installed RAM (~3×), so it will grow after the upgrade. Disk was at ~181 GB free
   (18.1%) at wind-down; that headroom is comfortable but the pagefile will consume some of it.

5. **Windows Update is paused until 2026-08-13.** Re-pause before any unattended drive after that date.

Restart the orchestrator only after the above. Nothing in the orchestrator state needs repair — it was
stopped gracefully with nothing in flight.

**Do NOT spend paid rounds on `e5c18520`.** Still parked, and the reason is now known precisely — see
`ed114610` below. Its Planner rounds are rejected by the Planner output contract for a **citation
phrasing rule**, not a defect in the plan. The exact diagnostic and a one-line operator unblock are
recorded on that backlog item.

## SESSION RESULT — 2026-08-06 (Claude): FOUR LANDINGS, ALL SELF-INFLICTED DEFECTS

Main advanced `c924098c` -> `f3a03105` -> `fc334734` -> `968cc79f` -> `71d5ab45`. Every goal that
landed fixes a defect in the orchestrator's own operation, and each was **filed, intaken, built,
reviewed, gated and landed within the same day**.

| goal | commit | fixes |
|---|---|---|
| `4b57adc0` | `f3a03105` | clarification **supersede** path — a wrong answer was previously immutable |
| `6b2085ed` | `fc334734` | duplicate-human-input guard failing clean rounds (~10 rounds lost) |
| `e67dd0f2` | `968cc79f` | worktree dirt — **PARTIAL, see below** |
| `d3543fa6` | `71d5ab45` | reconcile sweep that named a remedy and never ran it |

`f3ab7713` (zero-backoff runaway loops, backlog `c838cd95`) is **deliberately left `Failed`** — a
zero-cost pause, since a Failed goal dispatches nothing. Branch preserved at tag
`salvage/f3ab7713-20260806` (`299c4f4e`). It is now genuinely unblockable: the guard that failed ten of
its rounds landed in `fc334734`. Four real Reviewer findings remain open on it, including an
executed-confirmed regression in an untouched main test
(`ParallelAttempt_ExternalOldProductionSeam_FastExitRed`, `ConductorBatchLoopTests.cs:2565`).

### Known-live defects that still need an operator — read before an unattended run

1. **`fcf86dec` — worktree dirt. THE FIX IS INCOMPLETE.** `968cc79f` redirects
   `PSModuleAnalysisCachePath` for **worker dispatch** only. Confirmed post-landing: `d3543fa6` rebased
   onto main, ran its gate **with the fix present** (verified at `DispatchProcessHost.cs:294`), and the
   gate **still** deposited `Microsoft/Windows/PowerShell/ModuleAnalysisCache` into the worktree. The
   **acceptance gate's own** PowerShell is uncovered. Cleared manually **ten times** on 2026-08-06.
   It blocks dispatch at one end (generic `no ready batch`) and the pre-merge rebase at the other, so
   **no goal lands unattended** while it is live.
2. **`e402fbfe` — blocked-recheck starves queued work. Reproduced three times, one remedy.** A goal
   sits `Verified` with a passing gate, clean worktree and a free permit while the loop logs
   `SWEEP_BLOCKER … command="acceptance <goal>"` every ~33s without acting. **Bounce the loop** — it
   lands within a minute. Do **not** hand-run `acceptance`: doing so at 23:38Z raced a live permit
   holder and failed with `worker-process-registration-failed; stage=victim-identity-read`.
   `d3543fa6`'s fix may automate this for `completed-branch-unmerged`; untested as of the wind-down.
3. **`cf620520` — review-finding anchor normalization.** Rejects **byte-identical** raw locations as
   MOVED because prior and submitted anchors normalize differently; the error text says "Raw locations
   render identically" and rejects anyway. Killed one Reviewer round after 582s of unsatisfiable
   repairs and cost another an entire round of log archaeology. Escape hatch: resolve the finding and
   open a new `stable_id`.
4. **`5e9aabf30` — LANDED but verify.** Guard failed rounds reporting `blockers: none`. Operator
   workaround while unfixed was `supersede <goal> <REQUEST-id>` then `recover` — note **request** id,
   not clarification id, and it only clears one round at a time.

### Backlog filed 2026-08-06

| id | defect |
|---|---|
| `5e9aabf30` | duplicate-input guard fails clean rounds (LANDED as `fc334734`) |
| `fcf86dec` | PowerShell cache dirties worktree — blocks dispatch AND landing (PARTIAL) |
| `e402fbfe` | blocked-recheck never dispatches already-scheduled gates |
| `cf620520` | review-finding identity rejects byte-identical anchors |
| `7fb91cc2` | reviewer evidence request rejected in the form the orchestrator itself emits |
| `ed114610` | Planner contract rejects silently — diagnostic recovered, see below |
| `a22953ad` | workers inconsistently believe they may not run tests |
| `866eb692` | operator isolation for slot commands and heavy writes |
| `ed433dcc` | sweep names a remedy then logs it forever (LANDED as `71d5ab45`) |
| `45104c09`, `395ae032` | blocker routing / re-asked clarifications |

**`ed114610` is the one to read first if you touch `e5c18520`.** Its Planner rounds fail because
`ValidateCitedPaths` requires the literal token `file` after `new`: line 51 wrote
`— new file to create.` (exempt) and line 59 wrote `— new focused store/classification tests.` (not
exempt, path does not exist → reject). Five dispatches and ~750k tokens were spent on a phrasing rule.
The diagnostic was never surfaced anywhere an operator could read it.

### Operating lessons worth keeping

- **`Invoke-OrchestratorCommand.ps1` eats `--pipeline`.** It is `CmdletBinding`; `--pipeline` binds to
  `-PipelineVariable` and never reaches the app. `goal --pipeline five-role` through that wrapper
  creates automatic Developer+Reviewer. Use `.\mcg-orchestrator.cmd` or `add-task`. Receipt 2026-08-19:
  three intakes, all `isOverride:false`.
- **LlamaCpp / qwen-code 400 at 32k is usually the second request.** `--bare` first request is ~8–11k
  (8700 harness + brief). The 40k 400 is tool results after “read digest.md first” / source-survey /
  `AGENTS.md`. Do not disable `read_file` to paper over it. Receipt: `e8351d7c` Tester 2026-08-19.
- **qwen-code rewrites `.qwen/settings.json` (+ `.orig`) in the worktree.** Discard, never commit;
  dirty worktree blocks dispatch/rebase. Do not discard while a qwen process is still live.
- **`park-goal` is the isolation tool** (2026-08-19). The older “Failed is a zero-cost pause; no park
  verb needed” note below is stale for Active goals you must keep off the next Permissive tick.
- **`?? Microsoft/` first.** On `Assigned_tasks_exist_but_no_ready_batch`, run
  `git -C .orchestrator-worktrees/<goal> status --short` **before** `readiness`, which reports
  `RequireOperatorConfirmation` as a standing red herring. That cost two hours on 2026-08-06.
- **Read the worker's OWN `blockers:` line**, not the guard's message. Upstream `exact-blocker` text is
  quoted into every downstream prompt, so a naive grep attributes the Researcher's blocker to the
  Developer. That misattribution drove most of a day's wrong diagnosis.
- **A `Failed` goal is a zero-cost pause.** It never dispatches. To stop a non-converging goal, simply
  do not `recover` it — no park verb needed. Tag the branch head first.
  *(2026-08-19: `park-goal` now exists; use it to keep an **Active** goal off the next tick. Failed
  still will not dispatch if you leave it Failed.)*
- **`recover` races dispatch (~25s).** A follow-up `retry` is rejected "already running". Put worker
  guidance **inside the recover note** — it becomes the delivered `TaskRetried` note.
- **Event silence is not a hang.** `BLOCKED_RECHECK_SLEEP` writes nothing to `conduct-events.log` and
  does not advance the tick counter. Check the loop's stdout before concluding anything.
- **A time-based heartbeat is required.** The event monitor only fires on things that happen; the
  expensive failures on 2026-08-06 were all *absences*. A ~17-minute poll caught them.

### Host work completed 2026-08-05

Disk went from **50.3 GB free (5.0%)** to **181.1 GB free (18.1%)** — **+130.8 GB**:

| reclaimed | how |
|---|---|
| 77.5 GB | eleven stale orchestrator temp roots (`mcg-orchestrator-tests` alone was 13.67M items / 75 min) |
| 35.6 GB | `Temp\Low\mcg-tests` retained failed runs (1.85M files) |
| 27.1 GB | two runaway zero-backoff log files (see `c838cd95`) |
| ~6.3 GB | `hiberfil.sys` (operator disabled hibernation) |

Applied: NTFS last-access updates disabled; Defender exclusions corrected (the existing one pointed at
the STALE `Local\Temp\mcg-dotnet-isolated`; the live root is `LocalLow\mcg-dotnet-isolated`, now
excluded along with `Temp\Low\mcg-tests`). Pagefile left system-managed by operator decision — note
its ceiling scales with installed RAM (~3x), so watch it after the upgrade.

**The age guard saved two roots.** `mcg-dotnet-isolated` and `mcg-short-tests` were on the delete list
from a WizTree snapshot but had been written to hours later; the 6-hour guard skipped them. Never
purge from a stale list without a freshness check.

Baseline evidence for the before/after lives in `Desktop\mcg-upgrade-baseline\` (56k counter rows,
elevated capture with SSD wear = 0, and a checklist). SSD wear being zero means the paging era did not
damage the drive.

### Backlog filed 2026-08-05 (10 items, all with receipts)

| id | defect |
|---|---|
| `4269ec05` | claude-cli workers emit an untrusted-workspace warning on stdout (seeding omits `.claude.json`) |
| `beabe425` | semantic-acceptance CLI judges return nothing on ~2/3 of invocations |
| `76e1a763` | dispatch runs the worker in the MAIN CHECKOUT when a goal worktree is missing |
| `8d42f4b9` | goal-scoped conductor terminally REJECTS operator intents for other goals |
| `4c822fb0` | transient `victim-identity-read` failure kills a live worker tree at registration |
| `fd021595` | successful Planner rounds recorded Failed; rate-limit matcher matches a bare `429` |
| `1ab322e9` | generated evidence trees have no retention policy |
| `d1052b13` | `--artifacts-path` with stripped separators wrote build output into the repo root |
| `c838cd95` | **zero-backoff retry/recheck loops wrote 27 GB of stdout — disk-filling hazard** |
| `96c12640` | per-tick operations leak a temp working directory each (~31k orphaned, grows with UPTIME) |

`c838cd95` is the one to read first: a `TICK_WRITE_RETRYING` loop on a SQLite lock wrote 23.7 GB in
9.5 minutes (~41 MB/s) with `attempt=1` never incrementing, so whatever cap bounds it is inert.

## SESSION RESULT — 2026-08-05 20:00 UTC (Claude): SIX LANDINGS

Board went from fully stopped to six goals landed. Main advanced
`fa2b83df` -> `1414d8ef` -> `56af3c1c` -> `7ab5b2e6` -> `739262fa` -> `e7c19056` -> `c924098c`.

| goal | commit | note |
|---|---|---|
| `485363d4` | `1414d8ef` | refresh-dispatch output flood |
| `6bc464ca` | `56af3c1c` | Invoke-TestSummary timeout / MTP apphost |
| `7eb3f3da` | `7ab5b2e6` | **KEYSTONE** — owned-process-group attachment fix |
| `10075221` | `739262fa` | was blocked on the attachment fault |
| `a8e836f0` | `e7c19056` | was blocked on the attachment fault |
| `658501ce` | `c924098c` | was blocked on the attachment fault |

**The sequencing is now proven, not argued.** All three AcceptanceFailed goals shared one root cause
(`stage=owned-process-group-attachment`, `Win32Exception`). They were deliberately HELD rather than
retried, the keystone was proven with an operator receipt and landed, the loop was bounced so the fix
actually armed — and then all three passed acceptance on their FIRST attempt. Do not blind-retry
goals sharing a fault signature: fix the apparatus, arm it, then release them.

**The arming step is not optional.** `--max-duration` handoffs reuse the PREBUILT exe, so a landed
loop-affecting fix does NOT arm until a deliberate launcher bounce. Verify with the running binary's
`Mcg.AgentOrchestrator.App.dll.git-head` marker equal to `git rev-parse main` — confirmed here as
`7ab5b2e6` before releasing the three goals.

## Session update — 2026-08-05 17:30 UTC (Claude)

### Anthropic is back in the Reviewer seat

`.orchestrator/agents.json` now lists `anthropic-reviewer-opus-5` (claude-cli, `claude-opus-5`) as the
FIRST Reviewer with `openai-reviewer` retained behind it as fallback, and the `anthropic-tester-opus-5`
alternate has been restored. Edited directly — the `agent` CLI verb's UpsertRole drops a role's
alternates, and routing is ARRAY ORDER.

**Rollout semantics:** tasks pin an agent id at intake, so in-flight goals keep codex reviewers
(`6bc464ca`'s reviewer ran `gpt-5.6-sol` xhigh after the change). NEW goals get the anthropic reviewer
via `SelectAgentForTask`. To force existing tasks over, remove `openai-reviewer` — `ResolveAssignedAgent`
(`WorkerProfileDispatcher.cs:2003`) auto-repairs a missing pin to the role's first agent.

### The claude-cli worker lane is validated, not theoretical

Live Low-IL probes, seeding exactly as `SeedClaudeEnvironment` does. The lane was never exercised in
production after `7fb0a3be` landed, so this was measured before trusting it:

| condition | result |
|---|---|
| Low IL, seeded credentials, no `ANTHROPIC_API_KEY` | exit 0, authenticated, correct output in 3s |
| `--permission-mode plan` (what every non-Developer/Tester role gets) | Read OK, Bash `git --version` OK |
| `--permission-mode bypassPermissions` (Developer/Tester) | Read OK, Bash OK |
| `--permission-mode acceptEdits` | edits OK, **Bash DENIED**, trust record or not |

`BuildDispatchVariables` (`WorkerProfileDispatcher.cs:2102`) only ever emits `bypassPermissions` or
`plan`, so a claude Reviewer can diff. **Never "fix" a claude worker by moving it to `acceptEdits`** —
it silently strips shell access and still exits 0.

New backlog `4269ec05`: seeding omits `.claude.json`, so every goal worktree is untrusted and Claude
prepends an "Ignoring 178 permissions.allow entries" line to STDOUT on every dispatch. Not a capability
blocker; it is output-contract contamination in a repo that has burned goals on contract false negatives.

### All three AcceptanceFailed goals are ONE defect — do not blind-retry them

`a8e836f0` attempt `20260805031303685` failed with
`worker-process-registration-failed: stage=owned-process-group-attachment; exception=Win32Exception`,
byte-identical in shape to `10075221`'s newest attempt (`20260805010714486`, pid 1520). So
`10075221`, `658501ce` and `a8e836f0` are all victims of the apparatus `7eb3f3da` fixes. Landing
`7eb3f3da` unblocks three goals; retrying any of them first burns attempts on the known-broken path.

The failure is INTERMITTENT, not deterministic — `485363d4`'s 16:16 gate ran all 18 shards with real
child PIDs. Do not describe the apparatus as globally broken.

### `7eb3f3da` RECEIPT DELIVERED — all eight criteria satisfied

Ran at 17:55-18:06Z. Attempt `10075221-0-20260805175523615-dd9e2551b91141b38d91a497d8e7dc60`,
executed by scoped conductor pid 28236 launched from the CANDIDATE build via
`Start-OrchestratorCommand.ps1 -AppDll <candidate>` (the launcher takes `-AppDll`; that is how you
make the gate run the candidate).

| criterion | evidence |
|---|---|
| exactly one new attempt | 4 -> 5 on disk |
| candidate activation | gate child pid 15308 running the candidate DLL as `__acceptance-gate-attempt` |
| real child PIDs | `child_pid=28100`, `output_bytes=151032`, live heartbeat |
| nonzero-test TRX | 18 shard TRX, `core-tests.trx` 1,145,233 bytes |
| ordinary terminal result | `kind=accepted`, `faultKind=null` — NOT `kind=fault` |
| exactly one lease release | 1 ACQUIRE / 1 HANDOFF / 1 RELEASE on `permit=build-0` |
| zero paid dispatches | zero `TaskDispatched` during the gate |
| no leak | pids 15308 + 28100 `missing`; read-only `spawn_registry` query showed no leaked row |

**The apparatus defect is fixed by the candidate.** Prior attempts died at
`stage=owned-process-group-attachment` with `Win32Exception`; this one ran 18 shards with a real
registered child and reached an ordinary terminal outcome. Acceptance itself is `passed:false` on
`infrastructure tests: Remainder` (exitCode 2, real test failures) — per the Reviewer's own finding a
normal test failure still validates registration. `10075221` is NOT acceptable and must not be
re-retried on that basis. Operator regate budget consumed: `2/3`.

Reviewer retry queued as intent `35732adb` on task 5 (`5e04810f`) carrying the full receipt
(`scratchpad/7eb3f3da-receipt-delivered.md`). It applies when an UNSCOPED conductor next runs.

Note `sqlite3 -readonly` IS available at
`~/AppData/Local/Microsoft/WinGet/Packages/SQLite.SQLite_*/sqlite3` — use it for registry proofs
BEFORE any sweeping CLI command, since `SweepStartupOrphans` would otherwise repair and conceal a
leaked row. `spawn_registry` has no `status` column; active rows are `released_at is null`.

### `7eb3f3da` WILL NEED A CONFLICTING REBASE BEFORE IT CAN LAND

The candidate `0dbae4ee` is **28 commits behind main**, including `7366919b Integrate goal/049c54f3`
— the graceful-stop/continuity work, which edits the SAME functions in the SAME file the candidate
restructures (`WorkerProcessJobs.cs`: `TryRegisterCore`, `RegisteredJob`).

`git diff main 0dbae4ee` shows main-only code the candidate does not have:
`TryDetachForGracefulStop`, `WasGracefullyDetached`, the `SpawnRegistryLifecycle.GracefullyDetached`
branch in `SweepStartupOrphans`, and `SpawnProcessIdentity? Identity` on `RegisteredJob`. The
candidate does not delete these — it predates them — but a rebase must reconcile both sides, and it
will NOT be a clean auto-rebase like `6bc464ca`'s was.

Expect a Developer round for the rebase after the Reviewer clears the receipt. Do not hand-merge
this one: both sides deliberately restructured process registration.

Also expect the first post-rebase gate to fail structural coverage if the rebase is skipped —
`6bc464ca` attempt 1 failed exactly that way (`candidate=3138, minimum=3139, deleted=0`) purely for
being behind main, and passed on attempt 2 after rebasing.

### A SECOND registration failure stage exists: `victim-identity-read`

At 18:23Z, under the candidate conductor, a live Developer round on `10075221` was killed by
`worker-process-registration-failed: pid=30732; stage=victim-identity-read;
cleanup=attached-process-tree-termination-requested`.

Whole-history stage counts: **12** `owned-process-group-attachment`, **1** `victim-identity-read`
(this one). Different stage, and it fired on a WORKER dispatch, not a gate.

**It is NOT candidate-introduced** — `git show main:...WorkerProcessJobs.cs` contains
`victim-identity-read`, so the path is on main too. It had simply never been observed before.

Mechanism, `WorkerProcessJobs.cs:338-348`: at registration, if `readVictimIdentity(process)` returns
null the code builds a registration failure and calls
`ReadAccountingAndDispose(group, kill: true, ...)` — it KILLS the newly spawned tree. So a transient
inability to read a just-started process's identity destroys a live paid worker round. The Developer
had already produced two file edits when it was killed.

This does not invalidate the receipt: the receipt covers the gate path at
`owned-process-group-attachment`, which demonstrably worked for 18 shards. Worth filing separately as
a fail-closed-too-hard defect once the discriminator for the null identity read is known — do NOT
guess at the cause.

### Original constraint notes (still true)

Candidate is built and ready: worktree clean at `0dbae4ee`, Release build 0 errors, marker written
(`Mcg.AgentOrchestrator.App.dll.git-head` = `0dbae4ee...`). Baseline captured for the diff: 10075221 has
**4** attempts on disk, branch `f832d84c`, main now `fa2b83df`.

Reviewer state: all six code findings **resolved**, 41/41 focused tests, build clean. The ONLY open
blocker is `7eb3f3da-live-10075221-receipt`, which is genuinely operator-owned.

**Constraint:** `acceptance-retry <goal-prefix> <reason> --confirm-acceptance-retry` takes NO
candidate-binary argument, and the gate attempt re-invokes the conductor's OWN binary as
`__acceptance-gate-attempt`. So the receipt requires the candidate build to BE the running conductor.
The general loop must be down for it.

**`.conduct-stop` does not drain.** `ConductorBatchLoop.cs:255-263` stops at the next tick and calls
`DetachNonTerminalEligibleGoals` — in-flight paid workers are detached, not awaited. Never create it
while a worker or gate is mid-round, or you strand the round and create stale dispatches.

### Landed this session

| goal | result |
|---|---|
| `485363d4` | refresh-dispatch output flood; landed as `1414d8ef`, backlog `ccac061a` closed |

### Live reproduction: two gates held the SAME build permit

At 17:33Z, `485363d4` acquired `permit=build-1` (holderPid 26772) while `6bc464ca` still held
`build-1` (holderPid 34264) with shards actively running and **no intervening
`ACCEPTANCE_LEASE_RELEASE`** — while `build-0` sat idle. Whole-log balance: build-0 is 11 acquire /
11 release (clean); build-1 is 17 acquire / 17 handoff / **14** release. Two of the three outstanding
were the live gates, leaving one historical unreleased build-1 lease.

This is the `e5c18520` "reuses occupied logical slot" defect caught live. It supersedes the
2026-08-04 annotation that concluded "capacity 2 is delivering 2, there is no throughput defect
here" — that observation was real but does not generalise; allocation is not reliably exclusive.

UNVERIFIED: if a permit maps to a shared build-artifacts path, two holders would contend and produce
exactly the unexplained `CS2012` that failed `485363d4`'s 16:16 attempt. Confirm the permit-to-path
mapping before asserting that link.

Receipt: `scratchpad/e5c18520-live-slot-reuse-receipt.md`.

### INCIDENT: a worker was dispatched into the MAIN CHECKOUT — backlog `76e1a763`

After `recover e5c18520`, the conductor dispatched a full-access codex worker with
`--cd 'C:\Users\miles\vcs\mcg-agent-orchestrator\'` — the main checkout — because a sweep had already
removed that goal's worktree (`terminal-worktree-cleanup`, the goal was terminal) and dispatch
silently substituted the repository root instead of recreating it or failing closed. The dispatch
record shows `"workingDirectory"` = the repo root and `"worktreeHeadSha"` = main's HEAD. The conductor
then counted the operator's uncommitted files as the goal's diff:
`files=6 [.claude/settings.json, HANDOFF.md, scripts/Analyze-GoalVelocity.ps1, +3 more]`.

Contained: stopped by exact pid (`Stop-RepoProcess.ps1 -Id 7920 -CommandContains __dispatch-run`),
whole codex tree confirmed gone, `git status` on main byte-identical to before. Nothing was written.

**That was role luck, not safety.** It was a Planner, so `sandboxWorktreeWritable:false`. Developer
and Tester are write-capable — the same fallback with a Developer puts a writable full-access worker
in the main checkout with a commit path already pointed at it.

**Repair, in this order:** `workspace create e5c18520` (calls `Worktrees.Ensure` — the sanctioned
recreate, `CliCommandHandlers.Goals.Workspace.cs:50-52`), THEN `recover e5c18520`. Doing `recover`
first is exactly what produced the incident. Both are state-writing verbs — run them only when no
gate is mid-run.

### `e5c18520` is wedged on a FALSE Failed

Planner task 2 (`316cfc5a`) is recorded Failed, and tasks 3-6 all block on "predecessor 316cfc5a is
Failed, not Completed". But its newest dispatch (`e5c18520-316cfc5a-20260805161023`) SUCCEEDED:
`blockers: none`, `confidence: high`, a complete durable plan with matching
`MCG_DURABLE_PLANNER_PLAN:BEGIN`/`END`, named TEST-VERIFIABLE targets, explicit stop conditions, and
seams inspected at HEAD `fa2b83df` (current main). The stale Planner replay flipped a good round to
Failed.

Correcting it is a record-to-artifact reconciliation, not fabricated evidence. Prepared text:
`scratchpad/e5c18520-task2-progress.md` and `...-task2-verify.md`, to be applied as
`progress e5c18520 2 completed` + `verify-manual e5c18520 2 passed`.

### New backlog this session

| backlog | defect |
|---|---|
| `4269ec05` | claude-cli workers emit an untrusted-workspace warning on stdout; seeding omits `.claude.json` |
| `beabe425` | semantic-acceptance CLI judges return no output on ~2/3 of invocations |

On `beabe425`: valid rates across 457 evaluations are ollama 45%, claude-haiku 31%, codex-spark 31%.
Two distinct failure modes — the local judge returns unparseable output, the CLI judges return
nothing at all. Ruled out for the CLI judges by live probe: model alias, the exact substituted
command shape (`--permission-mode 'default'`), and prompt-over-stdin delivery. The discriminator is
NOT identified; do not fix on a guess. Advisory only — it never gates a merge.

### Loop state

Running: pid 14272, `policy=Permissive`, `--max-duration 5400`, launched 17:21 via
`Start-OrchestratorCommand.ps1 -Name conduct-loop-anthropic-reviewer-1` (rebuilt). It reconciled
`485363d4`'s stale lease on startup and applied both queued `e5c18520` intents
(`applied-pending-commit`). `McgOrchestratorAutoResume` is **Disabled**, so nothing relaunches
automatically — relaunch is manual.

`e5c18520` remains wedged: its corrective Planner `9ff76eaf` and all downstream tasks require Planner
`316cfc5a` to be Completed, and that task is Failed. The two applied intents did not clear it.

Unregistered worktree dirs `049c54f3` (empty) and `d5b985e8` (no `.git`, branch deleted, files from
07-30) are sweep debt; `6bc464ca`'s dirty worktree IS registered and is safe from the orphan sweep.

## Current handoff — 2026-08-05

### Safe operating state

- **The conductor is deliberately stopped.** `.conduct-stop` is present. Repo-helper checks after the final
  worker and gate exited found no conduct loop, dispatch host, or acceptance process. Leave the stop file in
  place until the scoped `7eb3f3da` candidate validation below is ready to run.
- **Main:** `fa2b83df Integrate goal/654f6666`.
- **Do not clean the main checkout.** `.claude/settings.json`, this file, and the four untracked velocity
  analysis scripts are user-owned work. `.conduct-stop` is the one intentional operator file added by this
  session.
- Use `scripts\Get-RepoProcessInfo.ps1` for process inspection. Do not return to raw `Get-CimInstance`
  commands or broad process-name cleanup.

### Landed in the 2026-08-05 session

| goal | result |
|---|---|
| `049c54f3` | graceful conductor stop / max-duration continuity; landed as `7366919b` |
| `ebf7c8a8` | Planner output-contract false negatives; landed as `f6b39e65` |
| `654f6666` | all known Reviewer identity mismatches; final head `61ec97b5`, landed as `fa2b83df`; backlog closed and dogfood receipt recorded |

### Work that must be resumed, not recreated

| goal | exact state | next action |
|---|---|---|
| `7eb3f3da` | Clean candidate head `0dbae4ee`; Tester passed 41/41 and independent Codex review passed. Pipeline Reviewer found no code defect and withheld only the operator-owned live receipt. | Run exactly one scoped candidate-binary retry of `10075221`; do **not** start a general `conduct --loop` first. See the procedure below. |
| `485363d4` | Clean head `8003dc1b`; pipeline and independent review passed. Attempt `485363d4-0-20260805161657209-bb009624dd184f6dbd7205437cae76fe` reached a normal `kind=accepted`, `faultKind=null` terminal result but failed its trusted-main baseline build with `CS2012` on `main-coverage-baseline\obj\Mcg.AgentOrchestrator.Infrastructure\debug\Mcg.AgentOrchestrator.Infrastructure.dll`. The lock owner/mechanism was not established. | Reconcile the terminal attempt, then retry acceptance only after the exact lock condition is clear. Do not record a contention theory as fact. Result: `.orchestrator/acceptance-gate-attempts/485363d4cba647dab8e3c53d190033c5/485363d4-0-20260805161657209-bb009624dd184f6dbd7205437cae76fe.result.json`. |
| `6bc464ca` | Corrective Developer exited 0 with build clean, 22/22 focused tests, root-exit RED/GREEN, and no blockers. Its five-file, 518-insertion/52-deletion diff is still **uncommitted** because the conductor was stopped before reconciliation. | Preserve the worktree. Let the conductor reconcile the completed dispatch, then run fresh Tester and Reviewer rounds. Worker receipt: `.orchestrator/logs/6bc464ca-d8ff9bd9-20260805160701.out.log`. |
| `e5c18520` | Source-confirmed acceptance-slot defect is per-tick durable slot reconstruction, not handoff-only and not physical build-permit contention. Corrective Planner task `9ff76eaf` is Assigned. The stale Planner replay returned task `316cfc5a` to Failed. | Pending intents `37e657475abe413884f33cc4de905f11` (progress Completed) and `3bfd169c4c984e0dbe530e4ee6fddc7d` (manual verification Passed) were queued while the loop was down so they apply together before scheduling. Confirm both Applied and that `9ff76eaf`—not another stale Planner retry—starts next. |
| `658501ce` | Code/review complete, but acceptance failed in the owned-process registration apparatus. | Do not spend another acceptance retry before the `7eb3f3da` live receipt validates the candidate registration path. |

### First resume: scoped `7eb3f3da` candidate validation

Independent review found `0dbae4ee` safe for the operator-owned retry. Keep this scoped so unrelated goals do
not start and `.orchestrator/last-drive.json` is not replaced:

1. Keep `.conduct-stop` present while verifying that `.orchestrator-worktrees/7eb3f3da` is clean at exact head
   `0dbae4ee`, building the Release app, and writing its `Mcg.AgentOrchestrator.App.dll.git-head` marker with
   `scripts\Update-AppDllGitHeadMarker.ps1`.
2. Capture the existing `10075221` attempt set, branch/main heads, and `TaskDispatched` count. Submit
   **exactly one** `acceptance-retry 10075221 ... --confirm-acceptance-retry` using that candidate DLL. Never
   resubmit merely because observation or launcher setup failed.
3. Remove `.conduct-stop` and start the candidate through `scripts\Start-OrchestratorCommand.ps1` with
   `conduct 10075221 --watch --policy Permissive --poll-seconds 15 --max-duration 5400`—not `conduct --loop`.
   Invoke helpers from the main checkout so the candidate uses the main `.orchestrator` stores.
4. Require one new attempt, real `PHASE_PROGRESS` child PIDs, nonzero-test TRX files, an ordinary terminal
   Passed/Failed result, exactly one acceptance-lease release, zero new `TaskDispatched` events, no surviving
   recorded PID/tree, and no active durable spawn-registry entry.
5. **Before any post-terminal orchestrator CLI command**, inspect `spawn_registry` directly. CLI startup calls
   `SweepStartupOrphans()` and could otherwise repair—and conceal—a leaked row. The checked-in SQLite helper
   currently lacks a generic read-only query; use `sqlite3 -readonly` if available or add a minimal read-only
   helper before performing this proof.
6. A normal test failure validates process registration but does not make `10075221` acceptable. Preserve the
   result and stop; do not blind-retry. Once the receipt is sound, finish the `7eb3f3da` Reviewer/operator gate
   before returning to the normal multi-goal loop.

### New durable follow-ups from this session

| backlog | defect |
|---|---|
| `a996907886ee48ce9ffaea3acfffb8dd` | verification-only Tester passes are falsely failed when they intentionally produce no file changes |
| `fb377790cf9c4372b703342554cdbe2f` | a continuity successor can exceed the global worker cap while predecessor-owned dispatches remain alive |
| `ebcb72a04085442e9b88675a0b394098` | Planner retries drop operator feedback and regenerate the original brief; progress/verify can race redispatch |

Everything below this point is the prior 2026-08-03/04 investigation archive. It contains valuable receipts,
but headings such as “RIGHT NOW” are historical and must not override the current snapshot above.

## Historical repository state (2026-08-03)

Main is at `b4b5fc52 Integrate goal/64d6959e`. **Ten goals landed 2026-08-02/03**, eight of them the
orchestrator repairing itself:

| goal | what it fixed |
|---|---|
| `fcf8669a` | merged goals auto-terminalize instead of escalating forever |
| `5146fab4` | (gate-chain follow-up) |
| `5f59b0d6` | planner output contract false-failed correct plans |
| `2dca50be` | pre-review evidence COLLAPSES to per-lane checks instead of rejecting >4 targets |
| `0b81147a` | auto-requeue keys on cancel PROVENANCE, not status — conductor reaps stay recoverable |
| `355d1972` | `NormalizeRegion` strips line ranges, so line drift stops burning reviewer rounds |
| `7e357b4a` | reviewer evidence-only blockers run on demand instead of reopening a paid Developer |
| `81f41994` | human-input requests persist and are answerable by id |
| `cd8ebb40` | **gate capacity 1 → 2**: a goal whose gate is RUNNING no longer counts as the oldest waiter |
| `64d6959e` | cross-tick test harness — invariants spanning ticks are testable at all now |
| `3f7b875d` | tamper-guard regex now sees bare `[Xunit.Fact]` — no more phantom "test removed" reports |
| `63d8f8e0` | **operators can amend/waive an acceptance criterion** (`29f725b1`) — closes the `0e593b09` trap that caused 3 fabrication attempts and destroyed `b3c2a121` |
| `10bd7223` | **max-duration handoff no longer allocates a visible console** (`2d38a4c5`) — cause CONFIRMED as incumbent console *attachment*, not window presence |
| `f00622a9` | **human-input requests dedupe instead of minting a new id per dispatch** (`ce403277`) — closes `8de4b409`; unblocks parked `e7fd7951` |
| `6173571e` | **backlog is enumerable** (`787f1fc3`) — filed items are discoverable, so the title list below stops being load-bearing |
| `ac066a17` | **burn-cpu window gone** (`972c09ae`) — hidden-console inherit + `-NoNewWindow` + asserted; teardown no longer scans machine-global process lists |
| `6cd812af` | **stale in-memory goal aggregate fixed** (`59d5e939`) — out-of-process stops now reach the running loop; closes the real mechanism behind weeks of "stop verbs don't stick" folklore |
| `fb13475c` | **glance scope guard no longer keyed on a 5-phrase prose allowlist** (`79a1d905`) — structural comparison + typed downgrade event; stops the glance killing correctly-scoped workers |
| `adca4b85` | **stuck-hold watchdog** (`cda49217`) — same-blocker-forever now surfaces; crash-safe boundary so the watchdog can never take down the loop it observes |
| `ce8597ad` | **pre-review cardinality deadlock fixed** (`d190290e`) — coverage instead of equality when collapse engages; also adds the `add-task` goal-prefix operator lever |
| `fa009044` | **refinement checks role FEASIBILITY** (`c75a5e2e`) — infeasible criteria get flagged at refinement instead of producing fabricated evidence |

## Historical operating state (2026-08-03)

- **Conductor:** operator-launched Medium-IL loop, pid 5072, self-renewing on `--max-duration` handoffs.
  Launch with `scripts\Start-OrchestratorCommand.ps1`. Note the returned pid is the WRAPPER;
  `.orchestrator/conduct-loop.lock` holds the real loop pid.
- **`McgOrchestratorAutoResume` scheduled task is DISABLED** (by operator approval 2026-08-03 00:33). It
  launched the conductor at LOW integrity, which cannot create goal workspaces — and worse, a Low-IL tick
  ESCALATES the goals it touches, so it converted healthy goals into escalated ones every ~10 minutes.
  Re-enable conditions are on backlog `ee6ee46b`; do not re-enable before both are met.
- **In flight as of 17:51 — all four past Developer.** `424f2d45` claude-cli empty-output flake: Reviewer ✓
  files=0, then **gate failed 749/750** on `ClassifySilentLaunchFailureIgnoresBookkeepingStderr`
  (expected `LaunchFailure`, got `EmptyOutputFlake`). Real defect, auto-retried and fixed in 1m36s
  (`c77e7835`): `HasZeroByteStandardError` filtered bookkeeping stderr at the top via
  `HasSubstantiveStandardError`, then fell back to a RAW `StandardError.Length == 0` at the bottom,
  re-introducing the sensitivity it had just removed — so bookkeeping-only stderr disqualified a genuine
  silent launch failure and fell through to the flake branch. Now `return true`. Back at Reviewer ·
  `20912699` glance-operator-rulings: **LANDED 18:29 as `1303223e Integrate goal/20912699`.** Gate passed
  `passed:true`, zero unmet criteria, full 18-shard run at `991a80ec`. My hand-resolved rebase is validated by
  EXECUTED TESTS, not just review; the enum union and the doc merge both hold · `809634e8` canary slot-busy: Reviewer (`15e7fc0b`, 4 files) ·
  `badac7c6` clarification-id collision: Reviewer sent it back, **Developer round 2** (`86fb61ff`).
## SESSION STATE 2026-08-03 21:40 — 28 LANDINGS, 9 DEFECTS FILED

**Loop generation `conduct-loop-armed-three-fixes`, pid 39100, launched 21:33 WITH REBUILD.** This is the
first generation that ARMS all three of tonight's landed fixes: doc-collision exclusion (`dd3b44fe`), canary
slot-busy → `Deferred` (`809634e8`), async pre-review evidence (`8e8afe96`). Earlier generations ran the
prebuilt exe and did NOT have them.

**LANES as of 22:40 — five, all defects found tonight by measurement:**
`c4d02669` Reviewer (outcome-class poisoning + token drift) · `09912b1f` Developer (SAFETY circuit
fail-open) · `1332b2ea` Developer round 3 (Developer focused-test execution) · `d3829298` scope-correction
retry `2dce9999` pending (conductor continuity) · `f9bc03c9` PARKED on `3a7afb95`.

**LANDED TONIGHT (5), every one a defect that was actively slowing the board:**
`dd3b44fe` shared-doc collision · `809634e8` canary slot-busy + `EmptyReceipt` demotion · `8e8afe96`
31-minute tick freezes · `ef46e924` stuck escalations needing a full bounce · `1046bed1` canary test whose
misfire rate ROSE with throughput.

**`BuildLockBlockedException` ROOT CAUSE — nested `USERPROFILE` redirect, NOT dying processes.** Goal
`1332b2ea` failed twice on `BuildLockBlockedException` at
`Temp/Low/mcg-tests/mcg-hvp/AppData/LocalLow/mcg-dotnet-isolated/runs/...`. Its worker diagnosed it at
`2063b3a9`: nested hermetic acceptance processes redirect `USERPROFILE`, so
`GetFolderPath(LocalApplicationData)` resolves BENEATH `mcg-hvp` even when the parent preserved the real
`LOCALAPPDATA` — the build environment lands in a nested isolated root whose locks do not coordinate with the
real machine-user Low-IL slot grid. Note the giveaway in the path: a second `AppData/LocalLow` nested inside
a test root.
**MY ERROR, recorded so it is not repeated:** I checked whether the run's pid was alive AFTER the run had
already failed, found it gone, and filed "build processes are dying mid-gate" as a blocking defect
(`a939117e`, corrected by `3b4adf69`). A normally-exited process is also gone — "pid is dead" does not
discriminate between exited and died. I also told the operator gates were "effectively blocked" on that
basis, which was wrong. What SURVIVES from that filing: lock acquisition should check holder liveness so a
dead holder cannot block later runs.

## ~~⚠ CRITICAL~~ — OPERATOR CLI COMMANDS KILLED LIVE WORKERS — **GAP APPEARS CLOSED, VERIFY BY RECEIPT** (re-checked 2026-08-07)

> **Read this box before the section below.** The described gap — the sweep killing a live victim
> without checking whether its owning conductor was alive — is **not present in current code**.
> `WorkerProcessJobs.cs:77-82` now evaluates owner liveness first and retains unless the owner is
> `DeadOrRecycled`, with a `retain-unknown-victim` path at `:85-96` and a `GracefullyDetached` branch
> at `:42-75`.
>
> This was verified by reading the code, **not** by receipt. Four reaping receipts in `state.db`
> established the original defect, so confirm the fix the same way before relying on it.
>
> It remains a live, unproven candidate for the unexplained `root_exit_code=1 / child_exit_code=0`
> Developer failures on `3a7afb95`, which occurred while an operator was running recovery verbs. Check
> `state.db` reaping receipts against those timestamps.
>
> **An operator followed this section as current guidance through all of 2026-08-06.** Tracked in the
> command-safety program, backlog `c2ed4680`. The historical account below is preserved as the
> reasoning record.

**`WorkerProcessJobs.cs:30` treats every live shared-registry entry as an orphan and kills it (lines 39-58)
WITHOUT checking whether its owning conductor is alive.** `Program.cs:447` runs that sweep on
lifecycle-owning App startups and `Program.cs:397` enables destructive startup cleanup for nearly EVERY
stateful command — the exemption list at `CliPersistentStateRunner.cs:419` is narrow.

**Age: ~6 weeks.** `SweepStartupOrphans` landed 2026-06-22 in `3f654221` and never checked owner liveness.
`MatchesLiveProcess` (line 41) confirms the VICTIM is alive — that is the entry condition for killing it.

**CORRECTION to the first version of this note:** `backlog-add` is EXEMPT (`SkipsKernelState` line 433;
`RequiresKernelBacklogState` line 452 for `--depends-on`) and cannot have caused the 01:19:30 reaping — I
asserted that from a timeline without checking whether the verb reaches the sweep. Trigger for that instance
is UNIDENTIFIED; the mechanism is confirmed. `backlog-intake` and `attention` are in neither list and DO sweep.
`authorityTransferRequested` returns at Program.cs:461 before sweeping, so max-duration handoffs are safe.
Sweeping verbs = everything not exempted: `recover`, `retry`, `progress`, `verify-manual`, `goal`,
`backlog-intake`, `attention`, `pending` — i.e. precisely the RECOVERY verbs, reached only when live work is
already fragile. Prior regression of this same class: `bc642e02` (2026-07-30) re-enabled the sweep for
read-only `backlog-list`; the deny-list guards a destructive DEFAULT, so every newly added CLI verb is unsafe
until someone remembers to exempt it.

CONFIRMED by `.orchestrator/state.db` reaping receipts, not inference:
| PID | goal | `startup-reaped` at | the command I ran |
|---|---|---|---|
| 37108 | `b3ada0bb` | 01:19:30.715Z | `backlog-add` |
| 35200 | `1332b2ea` | 01:19:31.045Z | same sweep, 330ms later |
| 35712 | `b3ada0bb` | 02:20:09.288Z | `backlog-intake --create-goal` |
A fourth (Reviewer `b7b88f90`, 02:29) coincides with `attention show`/`answer`. Victims spanned BOTH
providers (codex Developer, claude Reviewer) and both roles — consistent with an indiscriminate sweep, and
it rules out any provider-specific theory.

**Why it is worse than "a worker died":** killing the host skips the ONLY exit-artifact writer
(`DispatchProcessHost.cs:1364`, end of a `finally`); the tree is in a kill-on-close job
(`OwnedProcessGroup.cs:19`) so all descendants vanish and the heartbeat freezes at `state: running`. The
reconciler then MANUFACTURES exit 1 (`BackgroundDispatchRunner.cs:683-684` → written at 1253-1255), and
`DispatchRecoveryPolicy.cs:53` / `DispatchStateSurface.cs:203` track only artifact EXISTENCE, not
native-vs-synthetic provenance — so a fabricated failure is indistinguishable downstream. **This is the
mechanism behind the long-standing "dispatch manufactures failures for successful workers" note.**
Careful: success is NOT proven for these — `b3ada0bb`'s stderr ends mid-diff, `1332b2ea`'s during context
ingestion. Honest verdict is "interrupted after useful work". Recovery must PRESERVE work without ASSERTING
success (quarantined WIP snapshot, not a result-bearing commit).

**OPERATOR RULE UNTIL FIXED: do NOT run any App CLI command while workers are live** — including
`backlog-add` and `attention show`. Batch operator actions into worker-free windows, or accept that each
command may destroy a paid round. HANDOFF/file edits are safe (no sweep).
**NOT YET FILED as a backlog item** — filing requires `backlog-add`, which would kill live workers. File it
from `scratchpad/backlog-startup-sweep-kills-workers.txt` during the next worker-free window.
Fixes, in order: (1) sweep must check owner liveness; (2) invert the exemption list so commands opt IN to
destructive cleanup; (3) record the SWEEPER's pid/argv in the receipt (today it names only the victim, which
is why this needed a PID-targeted `state.db` query to find); (4) distinguish synthetic from native exit
artifacts (overlaps `f254171c` item 1 / goal `b3ada0bb`).

**FILED 2026-08-03/04 (this drive):**
- `7a81b5f6` startup sweep kills live workers. Annotated twice: sol's adversarial review, and the operator
  UNIX DEFERRAL scope decision. Design conclusion: the command-name list is the flaw in EITHER polarity;
  orphanhood must be decided from recorded owner-liveness evidence, not from who is asking. With Unix
  deferred, reclaim has NO constituency (Windows KILL_ON_JOB_CLOSE already reclaims), so the scoped fix is
  `startup-live-deferred` inside `SweepStartupOrphans` at the capability seam. STILL IN SCOPE regardless:
  `MarkReleased` keys on PID not entry id (`SpawnRegistry.cs:79`), and `GoalWorktreeOrphanSweepScheduler.SweepNow`
  at `Program.cs:465` is a SECOND destructive action on every stateful command.
- `8b2e0873` gate cancellation escalates with an untyped reason. Six causes (null record, Active, Parked,
  AcceptanceFailed, Cancelled, Superseded, Failed) collapse to one `Func<bool>`, then a message naming no
  disposition, then a 40-char clip. Cost ~39 min of dead lane time on `65e85ad6` tonight.
- `51926605` test cleanup swapped ACL-aware helper for `Directory.Delete` + `catch { }` and dropped
  `[Collection]`; gate's own log proves these trees resist deletion ("Directory deletion failed after ACL reset").
- `54b0a707` Reviewer rounds consumed by "finding-identity contract repair"; n=2 across goals, and on
  `b3ada0bb` one emitted `pass` WITHOUT re-reviewing a changed HEAD - which is how `51926605` reached a gate.

**SAFE vs SWEEPING CLI VERBS (verified in source, corrects the earlier blanket rule).**
`RunsStartupCleanup = !SkipsKernelState && !RequiresKernelBacklogState` (`Program.cs:402`).
SAFE (never sweep): `backlog-add` (without `--depends-on`), `backlog-update/annotate/close/supersede/link/reopen/view`,
`backlog-list/show/depends`, `gate-status`, `acceptance-engine`, `cleanup-status`, `repo-process-info`, `run-event`, `project`.
SWEEPING: `recover`, `retry`, `progress`, `verify-manual`, `goal`, `backlog-intake`, `attention`, `pending` -
i.e. precisely the RECOVERY verbs. Holding ALL verbs (my earlier rule) costs velocity for no safety gain.
CONSEQUENCE: dispatching `7a81b5f6` needs `backlog-intake`, a sweeping verb - the fix cannot be dispatched
without triggering the defect it fixes. The sweep is now a CONCURRENCY CEILING, not just a paid-round risk.

**LANDED #35: `d3829298`** - main `b32a6493 Integrate goal/d3829298`. Conductor continuity: durable
lifecycle records, supervisor restart/backoff/caps, AND both self-stop fixes (watch-mode blocked-recheck
budget at `ConductorBatchLoop.cs:620`, plus the one-shot `no-progress-no-watch` path at `:1115-1125`).
NOTE: handoffs spawn the PREBUILT exe, so this fix only armed when the loop was relaunched via
`Start-OrchestratorCommand.ps1` (which rebuilds) at 14:51Z. Loops started before that do NOT have it.

**GATE TRIAGE RECEIPTS (2026-08-04) - three goals, useful reference for what real vs apparatus looks like:**
- Apparatus signature: gate fails in 28-57s at "core tests" with `UnauthorizedAccessException` on
  `...mcg-hvp\AppData\Local\Temp\Low\...`. Fixed at host level (see temp-label note above); code fix filed
  as `302b92f6`.
- Real signature: gate runs 12+ min and returns assertion failures. After the host repair, `b3ada0bb`
  surfaced FIVE genuine failures that the apparatus noise had been hiding, then 3 of 5 were fixed by ONE
  line (removing `WorkTaskStatus.Completed` from the reconciler's early-return guard at
  `BackgroundDispatchRunner.cs:501`).
- `d3829298`'s passing gate ran 18 infrastructure shards in ~156 SECONDS (vs 12-19 min typical), which is the
  shared base-build cache working with a warm main sha.

**VELOCITY GOAL IN FLIGHT: `10075221`** = backlog `d87d26be` partition-level pass caching (re-run only failed
partitions on a gate re-roll; item estimates re-rolls 20-25 min -> 2-7 min). This was the ONLY one of three
velocity candidates that survived a staleness check - see the velocity note above.

**LANDED #34: `65e85ad6`** - main `a92b2989 Integrate goal/65e85ad6`. Note it passed with 2 files from
Developer and 0 from BOTH Tester and Reviewer (thin five-role goal; cf. unlanded `3a7afb95`).

**`b3ada0bb` IS TERMINALLY Failed BY A FORMAT BUG - ITS CODE WAS SOUND.** Root cause found and recorded in
`54b0a707`. `ReviewFindingLocation` is `(File, Region, Hunk)` and `ToString()` renders `{File}::{Region} [{Hunk}]`
only when Hunk is non-empty (`ReviewFindings.cs:132-141`), while `SameAnchor` compares File + Region and NEVER
Hunk (`:526-542`). So `Region="...predicate" + Hunk="L1934-L1946"` and `Region="...predicate [L1934-L1946]" +
Hunk=null` RENDER IDENTICALLY but are unequal anchors. `NormalizeRegion` (`:544-554`) should strip the trailing
range but its regex arms want `[<digit>` or whitespace-then-`l`; the real form `[L1934-L1946]` is bracketed AND
L-prefixed and matches NEITHER. Result: `ERR_REVIEW_FINDING_IDENTITY_MOVED` on genuinely-equal-looking values,
`MaxReviewFindingContractRepairsPerRound`=2 exhausted, goal Failed. The escalation message contains violation
code, both ids, both locations, open count AND the operator remedy - and the record kept 40 CHARACTERS of it
(that clip is `8b2e0873`). Full text is recoverable from the conductor stdout log, not from the escalation.
NOTE: I first hypothesised "line ranges are a volatile anchor that shifts under Developer edits" - WRONG,
Hunk is never compared. Retracted in the annotation.

RECOVERY SEQUENCE (from the truncated message, recovered from the loop log) - ALL THREE ARE SWEEPING VERBS:
`retry b3ada0bb <task#> "<reason>" --mechanical`, then `progress <task#> completed`, then
`verify-manual <task#> passed`. Re-escalates harmlessly at every loop start (re-derived from stored review
state, NO new dispatch, zero paid rounds), so there is no time pressure - wait for a genuinely worker-free
AND gate-free window.

**GATE TRX TRIAGE - FILTER ON `outcome='Failed'`, NOT `outcome != 'Passed'`.** `NotExecuted` means SKIPPED and
carries no ErrorInfo message, so a `!= Passed` filter reports skips as message-less "failures".
`LockAttribution_handle_probe_returns_results_for_real_held_file` and
`GoalAcceptanceVerifier_real_runner_smoke_is_opt_in` are opt-in/environment tests that are NotExecuted in
EVERY gate including passing ones. I mis-triaged them as environmental failures twice.

**RELAUNCH TRAP: `conduct --loop` DEFAULTS TO `Conservative`. ALWAYS PASS `--policy Permissive`.**
The flag is `--policy <Conservative|Permissive|Manual>` (`CliCommandHandlers.Goals.cs:1294`), default
`ConductorAutonomyPolicy.Default` = Conservative. Handoff relaunches inherit the incumbent's policy, so this
only bites on a MANUAL relaunch - which is exactly when an operator is already recovering from something.
Symptoms under Conservative, all of which look like unrelated defects:
- Every goal whose write-set touches a high-risk ownership area holds forever with
  `No tasks dispatched; all ready tasks require operator approval ... high-risk ownership area requires
  operator approval: Script scripts/<name>.ps1; reserved write-set resource: ownership:scripts`.
  `ParallelExecutionPlanner.cs:28` gates this on `approveHighRiskOwnership && !hasGeneratedPath`.
- Goals escalate to `Failed` at role boundaries with the generic
  `Goal_is_in_Failed_state;_operator_action`, indistinguishable from a real failure.
Confirmed live 2026-08-04: after relaunching without the flag, `d3829298` and `b3ada0bb` held indefinitely and
`9de9649d` (whose Planner had just SUCCEEDED with blockers=none, confidence=high) escalated to Failed.
The tick line is the tell - it prints the policy: `[Conservative]` vs `[Permissive]`. Check it after any
manual relaunch. Correct command:
`.\scripts\Start-OrchestratorCommand.ps1 -Name "<label>" conduct --loop --watch --policy Permissive --max-duration 5400`

**VELOCITY BACKLOG - VERIFY BEFORE INTAKE, TWO OF THREE WERE ALREADY DONE (checked 2026-08-04).**
There is a filed, sequenced gate-throughput program: `4a50857f` shared base-build, `d87d26be` partition
pass-caching, `a9d5e81d` diff-scoped suite selection, `5daadb79` parallel gates, `3e1a0ad7` merge train,
`201b9b3c` candidate-keyed verdicts.
- `396fd25d` (reuse gate receipts across acceptance+landing) is **STALE**: `65e85ad6` landed with exactly ONE
  gate attempt directory. Auto-promote already consumes attempt results; only the operator CLI path re-ran,
  and that is not the path in use.
- `4a50857f` (shared base-build layer) is **ALREADY IMPLEMENTED**: gate logs emit
  `BASE_BUILD_CACHE base-build-cache main_sha=... build_phase_ms=... projects=Core=miss,Infrastructure=changed,...
  built_projects=... evictions=none`.
- `d87d26be` (partition-level pass caching for re-rolls) is **LIVE and is the one to take**: `b3ada0bb`
  gate 1 failed ONE shard, gate 2 re-ran all 19. Item estimates re-rolls ~20-25 min -> ~2-7 min.

**TEST TEMP LEAK: 3,298 directories** under
`C:\Users\miles\AppData\Local\Temp\mcg-hvp\AppData\Local\Temp\Low\mcg-tests\`, dating to Aug 1. This is the
concrete cost of backlog `51926605` (ACL-aware `GoalWorktrees.DeleteDirectory` replaced by
`Directory.Delete` + `catch { }`): cleanup failures are now invisible. Related but NOT root-caused: a
`d3829298` gate failed in 28s with `UnauthorizedAccessException` under that tree in
`AgentHarnessDocsDriftTests`. The directory is empty and writable now and the tree carries no mandatory
label, so the Low-IL theory does NOT hold - cause unconfirmed. The auto-retry Developer went straight to
`tests/Mcg.AgentOrchestrator.Core.Tests/AssemblyTempRedirect.cs`, which is the right seam.

**WORKFLOW DEADLOCK ON TWO-ROLE GOALS (filed `c0171e99`).** The Developer lane is restricted to
`Invoke-WorkerBuildCheck.ps1` (no testhost). Only the Tester role executes tests, and two-role goals
(Developer+Reviewer) have no Tester - so a Reviewer asking for executed evidence deadlocks the goal, since the
gate cannot run until the Reviewer passes. Cheapest fix is making `not-verifiable` non-actionable by contract
so a Developer is never dispatched to clear one. Related: `1e178ba0` - rule (l) controls cannot produce a
receipt when RED is a HANG; give such tests an explicit iteration/deadline bound so RED is a bound-exceeded
assertion.

**ACCEPTANCE PROCESSES ARE IN THE SPAWN REGISTRY** (`GoalAcceptanceVerifier.cs:6168`, owner `acceptance:{dir}`),
so a sweeping verb during a GATE kills the gate's own shard processes - not just workers. A worker-free window
is NOT sufficient; it must also be gate-free.

**TWO WORKERS DIED WITHOUT EXIT ARTIFACTS IN THE SAME MINUTE (01:19-01:20)** — `b3ada0bb` and `1332b2ea`,
both `blocked_by_stale_dispatch`, reason `no live process, exit-absent, heartbeat present but process
liveness is absent`. Systemic, not isolated. NOT diagnosed — correlation only: free RAM was 2.4GB under load
earlier and recovered to 3.4GB once both died, with the conductor alone at 883MB. Host is 15.9GB total.
That is consistent with memory pressure killing workers but I did NOT establish causation. **Relevant to the
"why not 3 build permits" question: if workers already die under load at 2 permits, that is evidence against
raising it — but measure per-gate peak RSS before concluding.**
Recovery that worked, in order: (1) `recover <goal> "<reason>"` resets the task; (2) then CHECK
`git status` in the worktree — `b3ada0bb` was left holding **239+60 lines of uncommitted work** in the exact
files it targets, which blocks ALL dispatch behind the generic `Assigned_tasks_exist_but_no_ready_batch`
hold; (3) COMMIT that work on the goal branch (`f6c56831`), do not discard it. A stranded edit discarded
earlier in this project turned out to BE the fix, so look before dropping.

**CORRECTION — "only a deliberate relaunch clears a pre-landing rebase escalation" is TOO STRONG.** I said
that four times tonight based on `809634e8` and `ef46e924`, both of which needed relaunches. But `09912b1f`
cleared and went to gate after only a MAX-DURATION HANDOFF (00:36), with no deliberate bounce. What differed:
I had fixed that branch's HISTORY with a real `git rebase main` (resolving the add at the offending commit),
not just its final tree. I cannot cleanly attribute the clear to the handoff vs. the history fix vs. a tick
re-evaluation — so do NOT bounce on the assumption it is required. Fix the branch history, verify with
`workspace rebase` reporting fast-forward, then WAIT a generation before spending a relaunch.

**WHICH ESCALATION CLASSES `recover` ACTUALLY FIXES** (learned by trial tonight — the runbook should say
this): it WORKS for `dispatch-exit-reconciled` (goal `f9bc03c9`) and for `advance-fault` (goal `09912b1f`,
slot-busy) — both reset the affected task to dispatchable with completed work intact, no worker round lost.
It is a NO-OP for **pre-landing rebase conflicts** (`809634e8`, `ef46e924`) — only a conductor relaunch
clears those, and `recover` prints "nothing to recover" while the goal stays set aside.

**`3fcf1286` — build-slot shortage ESCALATES the goal, third site of the same defect.** `advance threw:
Stable dotnet build slots busy` turned a routine two-permit contention into an operator-attention escalation
of a blameless goal (its Developer round had just completed cleanly). Log also shows `pid=5576` — the
CONDUCTOR ITSELF — holding `slot-1` while requesting a lease for the goal. The same exception class was made
non-fatal TWICE today (`809634e8` canary, `8e8afe96` round 4 evidence) and neither prevented this third
instance, because both were local patches. That is the argument for the written rule in one sentence.

**`d3829298` round 2 introduced a DETERMINISTIC HANG — caught by the Reviewer, not by me.**
`ConductorBatchLoop.cs:606-646` `continue`s at :645 without incrementing `totalTicks`, so the max-iterations
guard at :254-256 can never fire; `BatchLoopPersistingRebaseConflictStaysSetAside` hangs forever rather than
failing. Note the irony to carry into review: the goal exists to stop the loop stopping when it should not,
and the first cut made it never stop when it should. Both directions of one boundary.

**`f9bc03c9` PARK IS HOLDING** (`park-goal <id> <reason> --confirm-goal-park`; dry-run by default, and it
reported `running dispatches to cancel: 0` before confirming). No re-escalation since 23:06, where it had
been firing every generation. PROVISIONAL evidence against the old "park reverts to Active" note — watch it
across a full generation before trusting.

**`Goal_is_in_Failed_state` HAS AT LEAST TWO DISTINCT CAUSES — do not treat it as one defect.**
- `f9bc03c9` (five-role): Planner exited **0**, root and child both, with a well-formed WORKER_RESULT and
  `blockers: none`. Goal Failed ~10s later, nothing in the journal. STILL UNEXPLAINED — filed `3a7afb95`.
- `d3829298` (TWO-role): Developer exited **1** while reporting `tests: pass - build 0 errors; focused tests
  GREEN`, `blockers: none`. That is the exit-1-after-success class, and sol's audit gives it a candidate
  mechanism — `BackgroundDispatchRunner.cs:2390` reads invalid exit-file text as exit code 1 (in
  `f254171c`). Worker also hit the ~124s codex command cap on two whole-class test attempts (`b9d93945`).
So `3a7afb95`'s "appears specific to the five-role workflow" framing is only safe for the EXIT-0 case. Check
the exit code before assuming which defect you are looking at. `recover` fixed both, but note it reset the
DEVELOPER task on `d3829298` rather than advancing to Reviewer, so that round re-runs.

**`merge-tree` IS THE WRONG PRE-LANDING CHECK — THE CONDUCTOR REBASES.** `git merge-tree --write-tree
--name-only main HEAD` tests whether the FINAL TREES merge. A rebase replays EVERY COMMIT, so a branch that
ADDS a file in one commit and DELETES it in a later one still conflicts at the add, even though its end state
is clean. On `09912b1f` I ran merge-tree, got a clean tree, declared the conflict resolved — and the
conductor escalated on `pre-landing_rebase_conflict` minutes later. **Verify with an actual
`git rebase main` in the worktree**, not merge-tree. Resolution for that shape: `checkout --ours` (main's
copy) at the add, then `rebase --skip` the now-moot doc commits — verify afterwards with
`diff --stat main...HEAD` that the goal's real work survived, and with `workspace rebase <goal>` reporting
"can already fast-forward".

**ADD/ADD ON A SHARED DOC CANNOT BE FIXED BY EDITING EITHER SIDE.** Two branches that independently ADD the
same path have no common ancestor for it, so `git merge-tree --write-tree --name-only main HEAD` keeps
reporting `CONFLICT (add/add)` no matter how the content is reconciled. Goal `09912b1f`'s worker merged the
two versions into a better 130-line document and the conflict persisted unchanged. The ONLY resolution is to
delete one side. Resolved mechanically at `07b9b39d` (branch deletes, main is canonical); verified with
`merge-tree` returning a clean tree, and `diff --stat main...HEAD` confirming the goal's own 10 files
survived. **Salvage before deleting** — that worker's version contained two things the canonical doc lacked
(persisted state vs read-time availability must not overwrite each other; retries must not turn exhaustion
into success). Preserved to `scratchpad/branch-discipline-doc.md` and filed as `6f400169`.

**AUTO-RETRY OUTRUNS OPERATOR GUIDANCE — plan around it.** The gap between `WATCH_TRANSITION` and the next
dispatch is ~10s. Reading a Reviewer finding, deciding, writing a considered retry and submitting it takes
longer, so the retry is REJECTED (`Task is already running` / `downstream task has a running process`) and
the round proceeds on the Reviewer's findings instead. Both guidance retries I attempted tonight lost that
race. Implications: (1) operator retries only land if you CANCEL the running task first, or catch the goal
while ESCALATED; (2) for everything else, rely on Reviewer findings — they independently reached the same
conclusion as my rejected retries in both cases; (3) reserve operator retries for when the Reviewer is WRONG,
or the decision is genuinely operator-owned (scope, premise, policy), and expect to cancel first.

**OPERATOR TRAP — "Operator intent queued" does NOT mean it will apply.** A `retry` on an upstream task is
REJECTED if a downstream task still has a running process
(`Cannot retry Developer task ... while downstream Reviewer task ... has a running process`). The submit
output still prints `status=Pending`, and the rejection is visible ONLY via
`operator-intent-status <id>`. I fired a retry mid-Reviewer-run, saw "queued", and it sat Rejected for 40
minutes while I reported the goal as "retry pending". **Poll the id the command hands you**, or submit only
when no downstream task is running.

**Operator decisions made on `09912b1f` (record these, they are policy):** unreadable circuit FAILS CLOSED
after a tightly-bounded retry, with an operator-visible item. Rationale is asymmetry — fail-open risks
unverified code landing past a verifier we have DETECTED is lying; fail-closed risks a cheap reversible
pause. Two constraints: the halt must be operator-visible (this circuit halted all landings for 29 min
unnoticed), and the retry budget must stay well under a second because `Read()` is a synchronous block and
`8e8afe96` landed tonight to take a blocking call OFF the tick thread. Also stated: a fail-closed policy that
reports `Unhealthy` for UNKNOWN state is the same defect sign-flipped — `Unavailable` must be its own value
with policy applied on top. Rejected: stale-cache fallback (a cache is most wrong exactly when it matters).

**Filed tonight, all from measured evidence:**
| id | defect | status |
|---|---|---|
| `aca3d2fc` | shared-doc collision | LANDED as `dd3b44fe` |
| `cfe34c0e` | canary slot-busy halts all landings | LANDED as `809634e8` |
| `1732dc79` | pre-review evidence blocks the tick | LANDED as `8e8afe96` |
| `7ab385bc` | 2 remaining synchronous evidence call sites | open |
| `bcbf92fc` | Developer cannot execute tests → `tests: deferred` everywhere | goal `1332b2ea` |
| `18ccc733` | CS2012 baseline build recorded as a verdict | goal `f9bc03c9` (blocked) |
| `f490241d` | conductor continuity unmeasurable, self-stops = 41% of dead time | open, HELD for `ef46e924` |
| `4fd42301` | canary test pins exact main HEAD | goal `1046bed1` |
| `3a7afb95` | five-role goal → Failed on clean Planner exit | open |

**`3a7afb95` has an operator trap worth knowing before you hit it:** `recover` REPORTS SUCCESS on that goal
("reset task 2 to dispatchable") and the goal fails again on the next Planner round. Following the runbook
loops, and each cycle burns a full paid Planner round. Two cycles confirmed. Park rather than recover until
the defect lands.

**DESIGN RULE WRITTEN AND ALREADY PAYING — `docs/dispositive-decision-discipline.md`** (pointers +
shared-anchor entries in BOTH `AGENTS.md` and `CLAUDE.md`). The rule: *a dispositive decision must be made in
the presence of the evidence that discriminates its alternatives, and must record that evidence.* The
enforceable check: **could I write the justification for this outcome from what is in scope right here?**
Two independent audits (sol + Fable, near-disjoint results) found **13 MORE violations** within an hour:
| id | finding | confidence |
|---|---|---|
| `e8b5cd1b` | **SAFETY: acceptance circuit FAILS OPEN** — unreadable state returns `Healthy`, a tripped circuit permits landings | verified by direct read |
| `893f8be9` | outcome-class token drift poisons the routing scorecard DURABLY (`provider-Sandbox1312` interpolated vs `provider-sandbox-1312` literal) | verified by direct read |
| `f0b431d9` | candidate mechanism for `438b18d3` stop-verbs-don't-stick | convergent, UNTRACED |
| `f254171c` | six remaining, incl. invalid exit-file → exit 1 (candidate mechanism for "dispatch manufactures failures") | mixed |
Fable's overall read: **the runtime conductor path is now well-hardened**; surviving violations cluster in
the reporting layer that re-derives cause from strings AFTER typed evidence already existed. `f254171c`
carries four REFERENCE PATTERNS where the rule is already applied correctly — use those as fix templates.

**MY WORST PATTERN TONIGHT — file paths in backlog bodies, and direct commits to main mid-flight.**
Three goals derailed by the same two habits, all within ~90 minutes, after I had already written the lesson
down once:
| goal | damage | cause |
|---|---|---|
| `d3829298` | worker added 226 lines to a THROWAWAY analysis script instead of touching the conductor | I named the script path in the backlog body |
| `09912b1f` | worker authored a competing copy of the discipline doc → `CONFLICT (add/add)`, branch could not land | I named the doc path in the backlog body, then committed my own to main |
| `c4d02669` | gate failed on a red main + worker made an off-topic drift-test change | I committed an anchor-list edit to main WITHOUT grepping what asserts it |
**Rules for me:** (1) describe provenance WITHOUT paths — "the rule this came from", never the filename;
(2) do NOT commit a file to main while any in-flight goal lists that path in scope; (3) before editing any
contract file (`AGENTS.md`, `CLAUDE.md`, shared-anchor lists), grep for what asserts it and RUN that test
before committing — `AgentHarnessDocsDriftTests` pins the anchor list to in-file `## Section` headings, and
shared-home docs like `test-design-discipline.md` are referenced by POINTER ONLY, never listed as anchors.
Containment was luck: only one lane happened to gate during the 20-minute red-main window while three sat in
worker rounds.

**OPERATOR LESSON, my error: do NOT name incidental tooling paths in a backlog body.** `f490241d`'s body
named `scripts/Extract-LoopUptime.ps1` while explaining why current data is untrustworthy. Scope inference
latched onto the filename, and goal `d3829298`'s Developer spent a full round adding 226 lines to that
THROWAWAY ANALYSIS SCRIPT instead of touching the conductor. Scope inference cannot distinguish "this is the
broken thing" from "this is how I noticed". Retry `2dce9999` sent with corrected scope + explicit do-not-touch
list for all four scratch scripts (`Extract-LoopUptime`, `Extract-GoalVelocity`, `Extract-GoalFactsV2`,
`Analyze-GoalVelocity` — none are product code).

**Recurring architectural flaw, now 5 instances in one day — the boundary between "the measuring apparatus
failed" and "the candidate failed" is drawn independently at each site and got it wrong every time: canary
slot-busy (verdict←apparatus), `8e8afe96` `-001` terminal outcomes (verdict←apparatus), CS2012 baseline
(verdict←apparatus), `EmptyReceipt`→`EnvironmentFault` (apparatus←verdict, the reverse), and the goal→Failed
transition in `3a7afb95` leaving no typed record. This is a design rule waiting to be written, not five bugs.

## VELOCITY ANALYSIS 2026-08-03 (me + sol + Fable, two rounds each, 756 goals / 7 weeks)

- **COUNTING RULE — `git log --grep="Integrate goal/" | count` IS NOT A LANDING COUNT.** 408 Integrate
  commits but only **372 distinct goals** (`| sort -u`). 36 are re-integrations: `a2f929ec` ×5, `a645091f`
  ×4, three more ×3. Inflation is ~9% overall and up to **33% on peak days** (Jun 25: 21 commits ≈ 14 goals).
  I quoted 21/day as landings AND defended it when challenged; both wrong. Any throughput metric built on raw
  commit counts is inflated, and early-era-vs-later comparisons are unsafe because re-integrations may
  cluster early (one `a2f929ec` message says "hand-landed past the acceptance fingerprint guard").
- **#1 LEVER, ranked first by BOTH independent analyses: conductor continuity.** Dead-time attribution over
  371 merged gaps — `all-done-or-escalated` self-stops precede **41.2% of all dead time** (14,369 min, 90
  gaps), `stop-file` 27.5%, one-shot/stub rows 23.7%, `max-duration` handoff only 7.3% (median gap 16.8 min
  but p90 195 min). The Jul 22–Aug 1 collapse contains three mega-gaps (3,971 / 5,073 / 2,609 min) each
  opened by such a stop. Steady-coverage eras ran 11.9–12.5 landings/day vs 3.3–4.8 when coverage collapsed.
  Filed as backlog **`f490241d`**; goal `ef46e924` targets one instance.
- **The collapse was a FLOW collapse, not slower processing.** Goals that landed in Jul 24–Aug 1 were the
  FASTEST of any July era (median wall 161 min vs 229 at peak). Intake fell ~14–23/day → 6.1/day and dispatch
  starts 13.1 → 5.7. Round count doubling in that era was an EFFECT (goals spanning many generations), not a
  cause. **Check uptime BEFORE concluding the pipeline got slower.**
- **Rejected by both analyses: synchronous evidence runs are NOT the aggregate throughput driver.** They
  barely existed before ~Jul 12 yet latency had already doubled; collapse-era tax ≈7% of tick capacity; and
  the era with the MOST evidence activity (Aug 2–3) had the BEST throughput. What survives is tail risk —
  one goal (`e7fd7951`) ran **159 evidence runs / 1,184 min** and never landed, with no per-goal cap. So
  `8e8afe96` is worth landing for board-wide blocking, but I oversold it as the top lever.
- Gate first-pass by era: **67.4% → 50.9% → 56.0%** (E3 → collapse → Aug 2–3). My earlier "~40%" pooled
  across the collapse and was wrong.
- Pre-dispatch queue wait is **negligible** — median 0.1–6.7 min, p90 ≤21 min, <3% of wall, stable across
  eras. That question is settled; stop investigating it.
- Partition re-execution: **3,312 executions, 15 reuses (0.5%), 0 forced reruns.** Median = p90 = **19
  partition runs per gate attempt**; ~89% of partition work re-verifies what already passed; upper bound on
  cross-attempt green re-runs ≈ **1,468 runs (44%)**. Cannot be priced in minutes — no partition durations.
- Build lock/slot blocking genuinely vanishes after ~Jul 20, **but the throughput effect was invisible** —
  the heaviest-blocked era was the throughput PEAK, so those were grind-holds, not stoppers.
- **MY EXTRACTION SCRIPTS HAVE KNOWN DEFECTS — do not reuse without fixing.**
  `scripts/Extract-LoopUptime.ps1`: includes one-shot/stub logs (51% of rows; filter `loopStarted=True`),
  and 45 rows carry a −5h offset (filename parsed local, mtime taken local). Recorded uptime is a LOWER
  bound. Proper source is `LOOP_START`/`LOOP_STOP` in `conduct-events.log`, but **that log rotates and
  retains <1 day** — which is why the analysis needed log archaeology at all.
  `scripts/Extract-GoalVelocity.ps1` / `Extract-GoalFactsV2.ps1`: landing match drops goals with no journal
  file, and v2 aggregates away per-event timestamps that were needed to date the lock/slot cessation.

- **`1732dc79` IS THE TOP DEFECT ON THE BOARD — reproduced live tonight, root-caused to one line.**
  `ConductorDriver.cs:480` blocks the tick thread on an already-async method:
  `RunFocusedEvidenceAsync(...).GetAwaiter().GetResult()`. It is UNCONDITIONAL — no config switch — so it
  cannot be mitigated operationally. Receipt in `goal-operations/badac7c6...jsonl`: focused reviewer evidence
  ran **17:55:45 → 18:27:03 = 31m18s synchronously inside the tick**. Corroborated independently by
  `conduct-events.log` holding **ZERO events of any kind** across 17:55:23 → 18:27:04, every goal resuming in
  the same millisecond. Collateral: `20912699` PASSED its gate at 18:08 and could not be reaped or landed
  until 18:27 — a finished, landable goal held 19 minutes behind an UNRELATED goal's handoff. It runs BEFORE
  Reviewer dispatch, so every Developer→Reviewer handoff pays it.
  **Diagnostic lesson:** per-goal events legitimately go quiet while a worker runs, so silence there is NOT a
  stall signal — I misread it as healthy. The signal that distinguishes a running worker from a frozen loop is
  the tick-emitted `eventKind:"acceptance"` poll; when those stop, the tick is blocked.
- **Loop handed off at 18:34** (`LOOP_STOP tick=104 reason=max-duration`), successor pid **38560** alive and
  ticking, `424f2d45`'s in-flight gate survived and kept completing shards. Handoff spawns the PREBUILT exe,
  so `20912699`'s landed code is NOT armed in this generation — and neither will `1732dc79` be when it lands.
  Bounce deliberately via the launcher after landing it.
- **INTAKED as goal `8e8afe96`** (2026-08-03 18:54, Developer+Reviewer, Complex). This is the `1732dc79`
  fix. Scope-collision advisory: 0 explicit conflicts across 24 compared goals.
- **`424f2d45` LANDED 18:46 as `1c474a25`** — 24 landings. Then the loop self-stopped
  `LOOP_STOP tick=27 reason=all-done-or-escalated`, because BOTH remaining goals escalated at once with
  `pre-landing_rebase_conflict (docs/test-design-discipline.md)`. NOTE: contrary to the old rule, this
  all-done stop left **no stale lock** — `conduct-loop.lock` was already gone and pid 38560 was dead.
- **I hand-resolved both conflicts (2026-08-03 ~18:50).** `badac7c6` → `8d105de7`, `809634e8` → `de3cbfe3`.
  Both were PURELY POSITIONAL: each goal appends an independent "Negative-control record for goal X"
  paragraph at the SAME anchor (right after `fa009044`'s, before "Motivating incidents:"), and main had just
  gained `424f2d45`'s. Resolution was a union in landing order — zero judgment content. Verified after each
  rebase that `diff --stat main...HEAD` shows ONLY that goal's own files (badac7c6 = 8 files, 809634e8 = 13),
  so neither branch absorbed foreign work. **Both branches changed, so both need a re-gate.**
  Filed the systemic cause as backlog **`aca3d2fc`** — the shared doc both serializes parallel acceptance
  (only-shared-path overlap) AND escalates landings (same-anchor insert). Fix has two halves: exclude
  `*.md`/`docs/**` from the overlap/reservation computation, and stop making one shared doc the write target
  for per-goal records.
- **Loop relaunched 18:54 via the launcher (REBUILDS): pid 38288**, name `conduct-loop-tickfix-lane`. This
  generation therefore ARMS `20912699` + `424f2d45`. Board on relaunch: `badac7c6` + `809634e8` rebased and
  awaiting re-gate, `8e8afe96` fresh.
- Superseded: backlog `1732dc79` (pre-review evidence blocks the tick) is now goal `8e8afe96`.
- **`badac7c6` LANDED 19:11 as `fb9fb42d` — 25 landings.** Its re-gate PASSED at tick 62, so my hand-resolved
  union rebase (`8d105de7`) is validated by executed tests. Post-landing sweep terminalized it cleanly.
- **`809634e8` escalated a SECOND time on the same doc**, exactly as predicted: `badac7c6` landing put another
  paragraph at the same anchor. Re-resolved by union rebase → `dfc73043`, still only its own 13 files.
  `workspace rebase 809634e8` now reports "can already fast-forward into main; no rebase needed".
  **BUT the escalation state persists independently of git state** — the goal reads `Status: Verifying` with
  both tasks Completed, yet the SWEEP phase still reports `set_aside=1` and prewalk `eligible=1`, so the loop
  will not re-gate it on its own. `recover` is a NO-OP here ("nothing to recover"). `attention show` lists no
  open item. The conductor's own guidance is `use 'workspace rebase' to resolve` then `Next: acceptance
  809634e8`. **Deliberately NOT running manual `acceptance` while `8e8afe96`'s Reviewer is about to hand off
  to a gate** — a slot-leased command overlapping a gate has wiped lane receipts before. Options when the
  board is quiet: run `acceptance 809634e8`, or graceful-stop + relaunch (a relaunch cleared exactly this
  escalation class earlier tonight at 18:54).
- **`8e8afe96` round 1 REJECTED by the Reviewer — and the Reviewer was right.** This is the depth we want:
  it found two blocking defects I did not, by tracing the new dispatch kind through parent/child
  reconciliation rather than reading the diff. Retry queued as intent `cd6817c4` (task 1).
  - **`-001` (ConductorDriver.cs ~2289-2328), blocking:** terminal-without-run outcomes (BlockedBuildSlot /
    BlockedBuildLock / ProcessDied / CorruptArtifacts / LaunchFailed / Cancelled / StaleCandidate) are
    synthesized as `FocusedEvidenceRunResult(Accepted:false)`, and the `!Accepted` branch consumes that as a
    REJECTED EVIDENCE REQUEST → Tester reopen or `PRE_REVIEW_MAPPING_NEEDS_INPUT` escalation. The acceptance
    path it copied treats the identical set as RETRYABLE (`ConductorBatchLoop.cs:2090-2095, 2447-2453`).
    Permit acquisition uses `TimeSpan.Zero` (`ConductorParallelAcceptanceAttempts.cs:873-889`), so
    BlockedBuildSlot is ROUTINE on the 2-permit board, and a restart-killed child yields ProcessDied on the
    same path — **it would escalate goals under exactly the conditions the goal exists to fix.**
  - **`-002` (ConductorParallelAcceptanceAttempts.cs ~664-699), blocking:** the child coordinator always gets
    `tryRunPreSlot: RunParallelLandingAcceptancePreSlot`, so a pre-review child runs the LANDING-ONLY
    already-merged short-circuit, journals a FALSE `Acceptance skipped:skip-already-merged`, and returns
    `Early(Done(Verified))` with no evidence. Gate on `Kind == GateDispatchKind`.
  - **`-003` is OPERATOR-OWNED AND MINE.** Criterion 4's rule (l) negative control has no execution receipt
    (Developer honestly reported `tests: deferred`). I must restore the synchronous
    `.GetAwaiter().GetResult()`, run `FullyQualifiedName~ConductorDriverTests`, capture actual RED, revert.
    **Do this only AFTER the -001/-002 fix lands** — running it now measures code about to change. I told the
    worker to state in WORKER_RESULT exactly which edit produces RED for each new test so I need not guess.
  - Scope calls I made: `-006` (missing terminal-lane coverage, lease path stubbed null) IN; `-005` (evidence
    coordinator outside the 2-slot admission budget while competing for the same permits) IN **only if it is
    wiring, not restructuring** — report rather than force; `-004` OUT, already backlog `7ab385bc`.
- **Loop relaunched 19:20, pid 28656**, name `conduct-loop-async-fix-round2`. Again NO stale lock after an
  all-done stop — that is twice now, so treat the old "always leaves one behind" rule as obsolete.
  The relaunch ALSO cleared `809634e8`'s stuck escalation (it re-gated at 19:21:40 without manual
  `acceptance`), and applied the queued retry intent. **A relaunch is the reliable clear for a stale
  landing-escalation; `recover` is not.**
  Caution when reading this generation: **redirected stdout is BLOCK-BUFFERED**, so
  `operator-conduct-loop-*.out.log` lags reality by minutes. I misread that lag as an 18-minute startup hang.
  Confirm liveness by the process's CPU climbing, not by the log's mtime.
- **`8e8afe96` round 2 (`96ac38fc`, 3 files) fixes BOTH blockers — verified by reading, not by claim:**
  `-002` gates the pre-slot on `attempt.Kind == GateDispatchKind`. `-001` routes
  `ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun` to `Held … retry on next conduct tick`,
  and `IsTerminalWithoutRunOutcome` (`ConductorParallelAcceptanceAttempts.cs:1700-1707`) covers EXACTLY the
  seven outcomes the Reviewer named. +157 test lines cover `-006`.
  **`-005` was neither implemented nor mentioned in WORKER_RESULT** — I asked for "do it if wiring, report if
  restructuring" and got neither. Re-raise it if the Reviewer does not.
- **Round 2 was NOT complete — `-001` has a residual, and my "genuinely fixed" verdict was overconfident.**
  I verified `IsTerminalWithoutRunOutcome` covers the seven named outcomes (it does) but never checked the
  OTHER entry path. `Failed` is deliberately absent from that predicate, and `FromArtifact`
  (`ConductorParallelAcceptanceAttempts.cs:1631`) maps a fault to a run with `FocusedEvidence == null`, so an
  infrastructure exception in the evidence child arrives as a **Completed** decision, hits
  `ConductorDriver.cs:2304-2311`, and is synthesized as `Accepted:false` → `MappingNeedsInput` + Tester
  reopen. Same defect class, different door. Remedy (Reviewer's): treat Completed-with-null-FocusedEvidence
  as retryable — `MarkReconciled` + `Held("…retry on next conduct tick…")` matching the 2289-2301 branch —
  plus a test driving a Fault evidence attempt asserting Held + null receipt, whose RED comes from removing
  that new guard. Auto-retry carried it to **Developer round 3**; no operator relay needed.
  **Lesson for verifying this class of fix: checking that a predicate's SET is complete is not the same as
  checking every path that reaches the consuming branch.**
- **`809634e8` gate failed 489/491 on its OWN new tests** (`Post-landing canary sink failure ...`, both
  `breakSqliteStore` cases, `exitCode 2`). Not from my rebase (that touched 2 doc lines). Auto-retry fixed it
  in 2m4s (`b4dae627`) by INVERTING an assertion: `Unhealthy` → `Healthy`.
  **I checked this rather than trusting it, because an inverted assertion is normally a stop-and-ask signal.
  It is legitimate.** The goal exists to stop a transient slot shortage tripping the circuit, so
  "canary failure stays non-blocking" IS the intended behavior. The safety net is preserved: the enum gained
  a distinct `Deferred` kind (`PostLandingCanaryState.cs:78-88`) for the slot-busy path, while the health
  read still returns `Unhealthy` for ANY `Failed` receipt (`PostLandingCanaryState.cs:472-486`). Slot-busy is
  non-fatal; a real canary failure still fails closed. Matches sol's recommendation from earlier tonight.
- **WATCH: both in-flight goals now modify `tests/…/ConductorDriverTests.cs` from the same base
  (`e7c3e60b`)** — `8e8afe96` appends tests near line 3141, `809634e8` edits ~1868. Far apart so it will
  probably auto-merge, but this is the first genuine CODE overlap tonight (all prior ones were the shared
  doc). If the second landing conflicts, resolve as a union the same way — the regions are independent.
- **`8e8afe96` gate FAILED 20:22 on INFRASTRUCTURE, not code.** Check `structural test coverage:
  infrastructure tests`, exitCode 1, `resultSummary: "trusted main baseline build failed"`, cause
  `CSC : error CS2012 ... file may be locked by 'csc' (32188)` in the isolated
  `main-coverage-baseline` artifacts dir. The goal's code was never evaluated. The conductor still recorded
  it as a failed verdict and auto-dispatched a paid Developer round. **Filed as backlog `18ccc733`** —
  classify baseline-build file-lock/IO faults as retryable infrastructure, same shape as the canary fix in
  `809634e8`. Recurring principle: an exception in the MEASUREMENT APPARATUS must never be recorded as a
  MEASUREMENT of the candidate. That is now three instances tonight (canary slot-busy, `8e8afe96` `-001`,
  this).
  **This also INVALIDATES my earlier flakiness estimate.** I concluded "gate failures are real defects, not
  flaky" from finding only 2 RED→GREEN `partitionVerdict` flips. A baseline BUILD failure never emits a
  partition verdict, so that whole class was invisible to the check. The ~40% first-pass rate is depressed by
  an unknown number of infrastructure failures — recount before trusting it.
- **`8e8afe96` round 4 (`cdff3ea8`) is legitimate, not phantom-chasing.** It correctly did NOT try to fix the
  CS2012 (gate infrastructure, not its code). It extended the retryable branch to catch
  `DotnetBuildSlotsBusyException`/`BuildLockBlockedException`/`OperationCanceledException` from
  `attemptDecision.Run?.Exception`, and split build-environment attempt naming into `pre-review-evidence-*`
  vs `parallel-acceptance-*` so lease telemetry attributes evidence children correctly. Its only blocker is
  the operator-owned rule (l) receipt. **Developer round 4 — the retry cap is CUMULATIVE, so the next
  Reviewer round must come back clean.**
- **OPERATOR-OWNED RULE (l) NEGATIVE CONTROL FOR `8e8afe96` — STILL OWED, deferred 5× on slot contention.**
  The Reviewer DOWNGRADED it at round 4 (`state:open, severity:advisory, category:test-evidence`) and passed
  the goal, so it can land with the control never having run. The obligation stands regardless: rule (l) says
  a test earns authority only by being shown capable of failing, and these tests' power is unproven.
  Hypothesis worth watching, NOT a claim: round 4 sat at the cumulative retry cap, where any blocker
  escalates instantly — that creates structural pressure to downgrade rather than block.
  **Run against MAIN, not the goal worktree** — the worktree is cleaned on landing, and main is where the
  tests will live. For each: apply the edit, run `FullyQualifiedName~ConductorDriverTests` through the slot
  runner, capture the ACTUAL red text, then REVERT.
  1. Restore unconditional pre-slot invocation (undo the `attempt.Kind == GateDispatchKind` gate in
     `ConductorParallelAcceptanceAttempts.cs` ~744) → `PreReviewAttempt_FocusedKind_SkipsLandingPreSlot`
     must fail its 0/1 invocation assertions.
  2. Remove the terminal-without-run hold (`ConductorDriver.cs` ~2287) →
     `PreReviewEvidence_ProcessDiesAfterRestart_RelaunchesWithoutReceipt` must fail its Held/null-receipt
     assertions.
  3. Restore the synchronous `.GetAwaiter().GetResult()` pre-review call →
     `PreReviewEvidence_InFlightRun_ReturnsThenReconcilesAfterRestart` must fail its first-walk
     Held/zero-run assertions.
  4. (added round 4) Remove the `DotnetBuildSlotsBusyException or BuildLockBlockedException or
     OperationCanceledException` arm from that same branch → its round-4 test must fail.
  **If ANY fails to go RED that is a defect in the test, not a formality — file it immediately** rather than
  letting the goal's evidence stand.
  **Do NOT run while any gate holds a build slot** — a slot-leased command mid-gate wipes lane receipts.
  **The deferral pattern IS the argument in backlog `bcbf92fc`:** an evidence obligation only an operator can
  discharge, in windows a busy board rarely provides, will keep slipping.
- **Filed backlog `7ab385bc`:** two focused-evidence call sites remain SYNCHRONOUS on the tick thread after
  `8e8afe96` — `ConductorDriver.cs` ~1197 (reviewer-issued evidence-on-demand) and ~1541 (conductor-derived
  substitution), both passing `CancellationToken.None`. `8e8afe96` correctly fixes only the pre-review path,
  which is the path tonight's 31-minute freeze actually came through (verified: the run sat between Developer
  completion and Reviewer dispatch). Do these AFTER `8e8afe96` lands so there is one mechanism.
  **Attribution trap:** "reviewer mapped project evidence" is a check name generated inside
  `RunFocusedEvidenceAsync` (`GoalAcceptanceVerifier.cs:1104/1140/2124`) and appears for EVERY caller — use
  the surrounding transition sequence to attribute a freeze, not that string. Brief written to `scratchpad/brief-1732dc79.txt` — grounded in the 478930/162961/123499 ms
  measurement, copies the acceptance gate's existing async shape rather than inventing a second one, carries
  the "do NOT build event-driven handoffs (66ms, measured)" negative result, and splits the perf measurement
  into an OPERATOR-OWNED post-landing criterion because the feasibility pass makes live-conductor/wall-clock
  observation infeasible for a worker role. Fire it with `goal --brief-file` once a slot frees.
  **Do not intake while a gate holds a build permit** — heavy write racing an active gate crashes the loop.
  All four fix defects MEASURED tonight; three of them cost operator time in the last four hours.
- **`20912699` carries a HAND-RESOLVED REBASE.** I resolved its pre-landing conflict manually rather than
  delegating: `ProgressKind` union (`PreReviewMappingEscalationSuppressed=28` from main, `OperatorTaskNote=29`,
  `OperatorGateSatisfied=30`) plus a `docs/test-design-discipline.md` section merge. Its re-gate then failed on
  three `Cli_note_*` tests. **Cause established (a): the tests were stale, not the merge.** `CliCommandHandlers`
  `.Tasks.cs:111,458` call `RecordOperatorTaskNote`, which writes the new kind, so tests asserting `TaskNote`
  could not pass. Fixed in `991a80ec` (4m20s). The split is deliberate and consistent: `TaskNote` = kernel/system
  note (still written from 7 sites in `Recording.cs`), `OperatorTaskNote` = operator-authored. "Any note"
  consumers (`TaskBriefs.cs:1237`, `TaskOutcomeClassification.cs:84`, `PromptContextFormatter.cs:257`) match
  BOTH; only the gate-source resolvers narrow to the operator kind — which is the point of the goal.
- **Correction to my earlier warning: `ProgressKind` ORDINALS ARE FREE.** I told the worker (and wrote here) to
  treat the numbers as persisted. They are not. `SqliteOrchestratorStateRepository.cs:2058-2061` registers a
  `JsonStringEnumConverter`, so kinds round-trip **by name**; the stores that do serialize enums numerically
  (`OperatorIntentStore`, `ProgressiveReviewSteeringStore`, `CollaborationItemStore`, `PortfolioStore`) carry no
  `ProgressKind` at all. The real constraint is on the NAME. Renumbering is safe — do not avoid a correct
  renumber on my say-so.
- **`10bd7223` is the standout.** Its Developer CONFIRMED the console-flash hypothesis rather than fabricating:
  it found `GetConsoleWindow()` insufficient, added `GetConsoleProcessList` and proved **attachment** is the
  causal state (unattached incumbent → successor allocated a visible window at +98ms; attached incumbents → no
  window). Post-fix: 3 handoffs, 0 windows, `CREATE_NEW_CONSOLE` positive control, visual capture, RED controls
  with exact compiler errors, 187/187. Flags pinned `0x01080600`, both forbidden flags absent, guards untouched.
- **`f00622a9` was wedged 2.5h and nothing surfaced it.** A progressive-review glance returned
  FundamentalMisdirection at 00:29:55 because the diff went outside `docs/test-design-discipline.md`, which it
  called "the authoritative trusted scope" — that is the CANNED refiner field (identical Includes list appears
  on `10bd7223` and on freshly filed backlog items; the brief itself says `Scope confidence: unknown`). The
  worker was killed after 336 CPU-seconds with 612 insertions left uncommitted, and every tick since held on
  "Reviewer blocked: predecessor is Cancelled, not Completed" — no requeue path exists for a glance cancel.
  Recovered by committing the stranded work as `f68f7444` and submitting retry intent `07d183d3`. Filed
  `ce36eff9`. **If a goal is quiet, check for this shape: Cancelled predecessor + Assigned dependent.**
- **`e7fd7951` is PARKED** pending `f00622a9`. Its work is committed (12 test files, zero production, plus
  `cb80328d`), scope adjudicated test-only, but every dispatch minted a NEW human-input request so answering
  could not clear it. Re-intake after `f00622a9` lands.
- **`b3c2a121` was ABANDONED TWICE** — premise disproven mid-flight, criteria unamendable. The first abandon
  (01:03) was UNDONE at 01:45 when a fresh loop generation replayed the identical 00:58 Reviewer escalation at
  **tick 1**. Expect the second abandon (02:56) to be undone the same way at the next handoff. Filed `c1fbf766`.
  This is NOT `438b18d3` — that goal has no open task, so its documented "cancel the tasks first" workaround
  does not apply.

## ~~The recurring theme, and where the leverage is~~ — **LIST IS STALE, 4 OF 5 LANDED** (re-checked 2026-08-07)

> Status re-checked against the backlog store on 2026-08-07. **Do not use this as a priority list.**
> The *theme* below is still true and worth reading — the pipeline handling mistakes badly is where the
> cost lives — but the items are not open work.

| item | status 2026-08-07 |
|---|---|
| `0e593b09` operators cannot amend/waive an acceptance criterion | **Done** |
| `8de4b409` every dispatch mints a new human-input request | **Done** |
| `de3dc015` refinement never checks whether the role CAN measure a criterion | **Done** |
| `6dc0d093` no window for an operator decision between failure and auto-retry | **OPEN — the only one** |
| `4dbac8df` landed goals never reach terminal status | **Done** |

`6dc0d093` is worth carrying forward: it is the same family as the 2026-08-06 finding that a `retry`
is rejected "already running" while a downstream task is live, which silently dropped four Tester
blockers (`45104c09`).

The original text, preserved for its reasoning:

1. `0e593b09` — **operators cannot amend or waive an acceptance criterion.** The re-rendered brief outranks any
   retry note, so one mis-specified criterion produced THREE fabrication attempts on `cd8ebb40` and forced
   `b3c2a121` to be destroyed rather than corrected. (Fix is in flight as `63d8f8e0`.)
2. `8de4b409` — **every dispatch mints a new human-input request** for the same question; `e7fd7951`
   accumulated five ids for one question. Answering is futile. (Fix in flight as `f00622a9`.)
3. `de3dc015` — refinement checks HOW a criterion is measured, never whether the executing role CAN measure
   it. An infeasible criterion produces fabricated evidence, not a blocked report.
4. `6dc0d093` — no window exists for an operator decision between a failure and the auto-retry. Four intents
   rejected as "already running" in one session; delivering one ruling required cancelling a paid round.
5. `4dbac8df` — landed goals never reach a terminal status, so the sweep re-reconciles them every tick.

## Two visible-window defects — both now RESOLVED, and by the same mechanism

Kept separate throughout on the operator's explicit instruction, which was correct — they were different code
paths with different symptoms. Both fixes converged on **inherit a hidden console from
`ProcessTreeGuiSuppression`**, which is now the established pattern for any spawn that must not paint a window.

- `038b87ae` → goal `10bd7223`, **LANDED `2d38a4c5`.** Max-duration handoff. Cause CONFIRMED by measurement as
  incumbent console **attachment**, not window presence — `GetConsoleWindow()` alone is an insufficient probe,
  since `CreateNoWindow` can report no window while the process stays attached to a windowless console. The
  guard tests at `ConductorBatchLoopTests.cs:3924-3925` were preserved untouched.
- `8d200a04` → goal `ac066a17`, **in Reviewer.** Dispatch-host grandchild test fixture. The `-NoNewWindow` fix
  the operator proposed was NOT used and would have been wrong — the wrapper has no console to share. Instead
  the wrapper launches through `ProcessTreeGuiSuppression.Start` so the grandchild inherits a hidden console.
  `ERROR_NO_DATA` root cause: the test host owned the anonymous-pipe read ends and the wrapper implicitly
  offered those handles to the grandchild, so cancellation, host exit, **or an operator killing the window**
  broke the launch. Now redirected to files. Script renamed `grandchild-reap-probe.ps1`, waits bounded at 30s
  naming the expired resource.

## INCIDENT 03:15-03:26 — post-handoff breakdown, loop bounced

At 03:15:41 the loop hit `--max-duration` and handed off (`LOOP_STOP tick=270`). Everything the NEW generation
launched then failed, and by 03:25:55 the loop self-stopped with `LOOP_STOP tick=31 reason=all-done-or-escalated`.

| dispatched | launched by | outcome |
|---|---|---|
| 03:14:30 claude Reviewer | old loop | ran 3.5 min, real result |
| 03:15:01 claude Reviewer | old loop | ran 8m18s, three real findings |
| 03:18:06 / 03:19:11 / 03:21:59 claude Reviewer | **new gen** | exit 1, **zero stdout AND stderr**, ~3s CPU |
| 03:23:53 codex Developer | **new gen** | escalated `blocked_by_stale_dispatch` 96s in **while healthy** |

The codex worker was demonstrably fine when killed: 132KB stderr, 19s CPU, 7 owned pids. The escalation came
from `ConductorDriver.cs:920-937`, which escalates any task whose status is `Failed` with a non-retryable
stale-recovery action — even when a live worker is producing output for it.

**Recovery performed:** loop had already self-stopped and left NO stale lock (contrary to the old
"always leaves one behind" note — that path now cleans up). pids 27340/28180/22532 all confirmed dead.
Relaunched via `scripts\Start-OrchestratorCommand.ps1` (rebuilds — required, since `3f7b875d` and `64d6959e`
landed loop-affecting code before the handoff). Wrapper pid 13804 at 03:27:12.

**RECOVERED 03:35.** All four goals dispatched and running again. The recovery had a non-obvious ordering
constraint worth knowing: with every goal escalated, a fresh loop ticks ONCE and self-stops with
`all-done-or-escalated`, so queued operator intents never apply (the CLI warns "intent queued but NO conduct
loop is running"). **Queue all recovery work FIRST, then start the loop**, so its first tick has something to
apply and it stays up. Sequence that worked: `retry` intents for both stale-dispatch goals → start loop →
answer 11 clarifications (7 on `10bd7223`, 4 on `6173571e`) → re-abandon `b3c2a121`.

**Ruled out, with evidence:** rate limiting / lane health (`f00622a9`'s opus-5 Reviewer succeeded on the same
lane at 03:15-03:23); prompt size (failing prompt was 48KB, while `0b81147a` succeeded at 67KB). The empty-output
signature is a FOURTH variant of the "Worker CLI non-launch root causes" item — exit **1** with `ownedCpuMs=3015`
and zero bytes on both streams, vs. that item's documented exit-**0**/`ownedCpuMs=0` and brief-stderr signatures.

## `backlog-list` WORKS NOW — the title list below is no longer load-bearing

`6173571e` landed (`787f1fc3`) and armed at the 10:20 rebuild. `backlog-list` enumerates everything with slug
id, `status=open|closed`, linked `goal=`, and title. The workaround era is over — stop maintaining the table
below as an index and just list.

**Gotcha:** `backlog-intake <id>` needs the **FULL** id, not a prefix. `4dbac8df` returns "No backlog items
matched the requested filter"; `4dbac8dfc50b446085f6737f322b2b3f` works. `backlog-close` DOES take a prefix.
Get full ids from `backlog-list`.

## Backlog items filed 2026-08-03

**`backlog-close <id-prefix> "<reason>"` exists** (undocumented in help). Closed as dead on 2026-08-03:
`5c36d640` (landed as `6173571e`), `795568d4` (obsolete — `10bd7223` shipped the real fix),
`c1fbf766` (superseded by `60d80486`), `ce36eff9` (superseded by `49d54f76`). Items already converted to
goals were left OPEN deliberately, so they survive if their goal fails.

`backlog-intake` with no filter returns just 5 legacy epics — filed items are reachable only by exact title
(`backlog-intake "<distinctive phrase>"`). Filed as `5c36d640`. Until it lands, keep appending here.

| id | title fragment to filter on |
|---|---|
| `ce36eff9` | "Progressive-review glance treats the canned boilerplate scope field" |
| `c1fbf766` | "Loop restart replays a stale Reviewer escalation" |
| `6eebf604` | "Test asserts a hardcoded token count" |
| `5c36d640` | "Backlog cannot be enumerated" |
| `128b8b60` | "No stuck-hold watchdog" |
| `795568d4` | ~~"Console suppression … still UNFIXED"~~ — **OBSOLETE, close it**; see below |

**`795568d4` is OBSOLETE — do not intake it.** I filed it when `10bd7223` returned INCONCLUSIVE and had shipped
only the diagnostic. The worker then went back, made the unattached state reachable by launching the apphost
with `DETACHED_PROCESS`, **CONFIRMED** the hypothesis, and shipped the real fix in `1758024b`. Nothing remains.
| `83fefff8` | "Two spec clarifications … identical short ids … PERMANENTLY unanswerable" |
| `0e021011` | "Progressive-review glance cannot see operator clarification answers or task notes" |
| `e410866b` | "Pre-review cardinality guard compares post-collapse count against pre-collapse count" — parks `80f4bd56` |
| `52eebc86` | "build-lease-cleanup is global … aborts on the first undeletable lease" |

**Recurring shape worth naming — `e410866b` and `52eebc86` are the same defect class:** the system diagnoses
correctly, prints a specific remedy, and provides no way to apply it. One says "no Tester task is available"
when `add-task` cannot target a goal; the other prints a per-goal `build-lease-cleanup` recommendation for a
verb that only runs globally and aborts on the first failure. Worth a sweep for other verbs recommended in
escalation text that cannot be aimed at the thing being escalated.
| `60d80486` | **"Long-lived conductor kernel keeps walking a STALE in-memory copy"** — supersedes `c1fbf766`'s mechanism |

**Clarification-id gotcha (`83fefff8`), you WILL hit this:** `attention answer <goal> <id>` resolves by prefix
against `ShortClarificationId` = the correlation key's topic segment truncated to **exactly 8 chars**
(`CliCommandHandlers.System.cs:159-163`). Two clarifications whose topics share an 8-char prefix are both
unanswerable — shorter matches two, longer matches none, and the error's advice to "use more characters from
`attention show`" is impossible because that command only ever prints those 8.
**It hit TWO goals in ninety minutes (`fb13475c`, `ac066a17`) and stopped the whole board** — both escalated to
AwaitingClarification with nothing dispatchable, so the loop self-stopped with `all-done-or-escalated`.
**ESCAPE HATCH, verified 05:21:** deliver the answers as a `note` on the task, then
`park-goal <g> "<reason>" --confirm-goal-park` (reports `Resolved attention items: N` — it cleared 4 on each
goal) followed immediately by `unpark-goal <g> "<reason>" --confirm-goal-unpark` (Parked → Active). `unpark-goal`
does a DRY RUN first and needs `--confirm-goal-unpark` to apply. The clarifications resolve as parked rather
than answered, which is why the note carrying the real answers must go first.
| `49d54f76` | "CORRECTS ce36eff9" — **read this instead of `ce36eff9`** |
| `e508a16b` | "ERR_REVIEW_FINDING_IDENTITY_MOVED is unsatisfiable" — see scope caveat below |

**`c1fbf766` — TRIGGER CONFIRMED BY MEASUREMENT, mechanism still unverified. I corrected this twice; the
second correction was wrong and is retracted.**

MEASURED (from `conduct-events.log`, LOOP_START vs `b3c2a121` escalations):

    01:45 LOOP_START -> 01:45 escalated Failed
    03:17 LOOP_START -> 03:17 escalated Failed
    03:28 LOOP_START -> 03:28 escalated Failed
    03:31 LOOP_START -> 03:31 escalated Failed

Four loop starts, four resurrections, each within the SAME MINUTE. Then **28+ minutes of continuous running
with ZERO resurrections**, while the goal sat Cancelled. Loop start is the trigger. That is the claim in
`c1fbf766` and it holds.

**RETRACTED:** my intermediate "correction" that `ReopenTerminalGoalWithNonTerminalTasks`
(`GoalLifecycle.cs:648`) explains it via the `[Failed]` task. Task 2 has been `[Failed]` CONTINUOUSLY the whole
time — so that path would fire on every sweep, not only at startup. It has not fired in 28 minutes. Refuted by
the same observation that confirmed the trigger. I pattern-matched to a memory instead of checking whether the
path actually fires.

**STILL NOT ESTABLISHED:** the precise mechanism. `c1fbf766` says "replays a stale escalation at tick 1"; what
is measured is only that loop start triggers re-escalation of a terminal goal. Whether that is a literal replay
of a stored escalation record or startup reconciliation re-deriving state is UNREAD. Do not implement against
the replay story — read the startup path first.

**SOLVED 05:15 by a second reader (codex/sol) — see backlog `60d80486`, which supersedes this item's mechanism.**
The kernel was walking a **STALE IN-MEMORY COPY**. Verified chain: prewalk excludes terminal goals
(`ConductorBatchLoop.cs:2786`), so escalating a Cancelled goal PROVES a stale aggregate. The per-tick reload
OMITS terminal goals (`CliPersistentStateRunner.cs:921`), so a goal cancelled from a separate CLI process never
becomes terminal inside the running loop. `Escalate` never sets `GoalStatus.Failed` — domain status stayed
Active while the projected lifecycle state was Failed, which is the three-way status disagreement. The
once-per-loop-run cadence came from the post-escalation set-aside at `ConductorBatchLoop.cs:872`, NOT from
startup. It stopped because the 03:38 `progress` intent — **the one that was REJECTED** — forced the terminal
snapshot into the reload and evicted the goal (`AgentOrchestratorKernel.cs:187`).
**The command that looked like it failed is the one that fixed it, and three `abandon` calls that looked like
they succeeded did nothing to the running loop.**

Provenance, kept short because the lesson outlives the incident: I offered TWO mechanisms from the event
timeline — "replays a stale escalation at tick 1" and "`ReopenTerminalGoalWithNonTerminalTasks` fires on the
`[Failed]` task" — and both were refuted, the second by the same handoff experiment that refuted my prediction.
`b3c2a121` is now stably Cancelled. **Reading a code path beat three rounds of reasoning from timestamps.**
Backlog `c1fbf766` was closed as superseded.

**Scope caveat on `e508a16b`:** its core claim holds — the identity error IS unsatisfiable once the Developer
moves the code, and the re-dispatch WAS uncapped. But its framing that all four rounds failed identically is
wrong. Three failed on `ERR_REVIEW_FINDING_IDENTITY_MOVED` (03:14:19, 03:17:56, 03:19:07) and three on the
empty-output exit 1 (03:18:50, 03:19:38, 03:22:43). Two interleaved defects, not one.

**`ce36eff9` is superseded by `49d54f76`.** The symptom and incident evidence in `ce36eff9` stand, but its
mechanism was written before reading the code and is wrong in two ways: a scope-downgrade guard
(`GuardUnsupportedScopeVerdict`, ProgressiveReviewGlance.cs:540-573) ALREADY exists and does what that item
asked for, and `RequeueInterruptedDispatch` ALREADY exists at ProgressiveReviewSteeringCoordinator.cs:184, so
"glance-cancelled tasks are never requeued" is false in general. The confirmed defect is that the guard only
engages when `LooksLikeScopeDeviation` (:575-580) matches one of **five hardcoded substrings**, and the actual
verdict text ("Scope is violated… the authoritative trusted scope is limited to…") matches none — same bug
class as the `3f7b875d` regex that just landed. Why the task was left Cancelled rather than requeued is still
OPEN; the line-166 early-return-before-requeue is a candidate, NOT a confirmed cause.

## Optional follow-up: one stranded edit preserved, deliberately NOT landed

`fb13475c` reached Verified then failed its pre-landing rebase on a dirty worktree — one uncommitted file,
`ProgressiveReviewSteeringTests.cs`. The edit was REAL, not junk: it wires `FakeCollaborationItemStore` into a
test and improves an assertion to dump `result.ProgressLines` on failure.

**I discarded it from the worktree on purpose.** The gate that passed and the Reviewer that approved both ran on
the COMMITTED branch head — this edit was in neither. Committing it would have landed ungated, unreviewed code
and broken the evidence chain between the gate verdict and what reaches main.

Saved as a patch before discarding, at
`…/scratchpad/fb13475c-stranded-edit.patch` (1619 bytes, session-scoped so it will not survive indefinitely).
Re-apply as a small follow-up if wanted — it is an improvement, not a fix, and the committed state gates green
without it.

**The general rule this establishes:** when a stranded edit blocks a pre-landing rebase, the question is not
"is this edit good" but "was it gated". Preserve it, discard it, land what was actually verified. That differs
from the `f00622a9` case earlier tonight, where the stranded work was the ONLY copy of a killed worker's output
and nothing had gated yet — there, committing was correct.

**BUT I GOT THE SECOND HALF WRONG, and the gate proved it at 09:19.** I called that edit "an improvement, not a
fix". It was the fix. The gate failed on exactly that test —
`ProgressiveReviewSteering_requeues_and_preserves_goal_worktree_when_restart_preparation_throws`,
`ProgressiveReviewSteeringTests.cs:932`, `Assert.True(result.MutatedTaskState)` False. The edit passed a
`FakeCollaborationItemStore` into `NewCoordinator`, whose `attentionStore` parameter is **optional and defaults
to null** (`:945`), so removing it compiled silently while the restart-failure path — which raises attention —
stopped completing.

**The discard was still correct; the CHARACTERIZATION was not.** Landing ungated code would have been worse.
What I should have done in the same breath was hand the Developer the edit's exact content instead of filing it
as an optional follow-up. Done now, at 09:21, with the diff inline and an instruction to verify the mechanism
rather than trust me.

**Rule to carry:** an optional parameter that defaults to null makes a deletion invisible to the compiler. When
a stranded edit only *adds arguments*, assume it is load-bearing until a test proves otherwise — and route it
to the worker immediately rather than parking it as a nicety.

## `fb13475c` is Verified-but-set-aside — waiting on a handoff, NOT broken

It passed review and gate, then failed its pre-landing rebase at 07:16 on a dirty worktree. I cleared the dirty
file at 07:17 (see the stranded-edit section) — but the goal then sat with **zero events for 45+ minutes**.

That is not a stall and not a new defect. Escalation SETS THE GOAL ASIDE for the remainder of that loop run
(`ConductorBatchLoop.cs:872`, per sol's trace), so the repaired worktree is never re-tried within the same
generation. `goal-recovery` confirms **"Findings: none"** — the goal is healthy. A new loop generation
re-includes it, so the natural `--max-duration` handoff clears it.

**Operator lesson:** repairing a goal worktree does NOT re-arm the goal. The repair only takes effect when a
later generation re-walks it. Don't bounce the loop to force this while workers are in flight; wait for the
handoff.

Also found: `goal-recovery fb13475c` reports its build lease ORPHANED (`ownerPid=12720 ownerAlive=False
canCleanup=True`). **`build-lease-cleanup` is GLOBAL with no goal-prefix parameter**, and it aborts on the first
lease it cannot delete — here `goal-80f4bd56`, whose artifacts are file-locked by a live pre-review evidence run
that continued after the goal was parked. So one locked lease blocks cleanup for every other goal. Retry once
that evidence run finishes.

## ~~`80f4bd56` is PARKED on a deadlock — do not unpark until `e410866b` lands~~ — **RESOLVED, DIRECTIVE VOID** (re-checked 2026-08-07)

> `80f4bd56` is **Cancelled**. `e410866b` is **Done**. There is nothing to unpark and nothing to wait
> for. Kept only for the park-does-not-survive-handoff finding at the end of this section, which has
> NOT been re-verified and may also be stale.

Its Developer work is COMPLETE and committed at `c48b3be5` on `goal/80f4bd56`. Nothing is wrong with the goal.

Two individually-correct fixes deadlock each other. `2dca50be` (landed today) makes pre-review evidence COLLAPSE
to per-lane project checks instead of rejecting >4 focused targets. The cardinality guard at
`ConductorDriver.cs:2253` asserts `evidence.Checks.Count == context.SelectedFocusedTests.Count` — a post-collapse
count against a pre-collapse count. Collapsing to `Infrastructure.Tests` yields one check per acceptance lane,
and the manifest has 18, so it reports `planned=1 actual=18` forever.

The fallback routes to a Tester (`:2267`), but backlog-intake goals are Developer + Reviewer only, so it
escalates every tick with no path out. **Neither a fresh Developer round nor a focused task-level verification
plan clears it** — I tried both; `planned` comes from the selection context, not from that text. And
`add-task <role> <desc>` is scoped to the CLI's current goal with no goal-prefix parameter, so an operator
cannot add the Tester the escalation asks for.

Filed as `e410866b`. Unpark and re-run after it lands.

**THE PARK DID NOT SURVIVE THE 08:47 HANDOFF — mechanism UNKNOWN.** `park-goal` reported success at 07:54 and
resolved 2 attention items; after the handoff the goal reads `Status: Active` again, and **no unpark event was
recorded** for it at all.

A plausible lead, explicitly NOT confirmed: `ParkGoal` (`GoalLifecycle.cs:649-651`) calls
`CompleteOpenHumanInputRequestsForGoal` as part of parking, which would satisfy the unpark condition in
`RefreshParkedGoalsWithResolvedHumanWaits` (`:677-698`) — no open requests, and input resolved after the park
decision — making parking self-defeating. **Evidence contradicts this:** that path appends
`"Goal unparked: resolved parked human wait."` and no such event exists in this goal's history. So either a
different path un-parked it silently, or the status is being recomputed without an event.

Do not act on the lead above. It is recorded so whoever fixes park starts from a checked dead end rather than
re-deriving it.

## BLOCKED DISPATCH IS INVISIBLE — the highest-value diagnostic learned tonight

**`status` shows `[Assigned]` for a task that is BLOCKED and cannot dispatch.** Nothing distinguishes
"about to run" from "stuck and going nowhere". Goal `5694679a` sat blocked for ~2 HOURS while reading
`1. [Assigned] Developer:` — and I reported it as making progress in five separate status updates.
Meanwhile **726 insertions of finished worker output sat uncommitted in its worktree, the only copy.**

**THE DIAGNOSTIC — maps a janitorial exception to its owning goal in seconds:**

    grep -rl "<task-id>" .orchestrator/goal-operations/

The journal line then carries the blocking reason verbatim:

    <task-id> provider=codex-cli reason=dirty-worktree: blocked: worktree has 14 uncommitted change(s)
    before dispatch

First instance cost me two hours. Second cost ninety seconds.

**THE SYMPTOM TO WATCH:** `LOOP_JANITORIAL_FAILED ... InvalidOperationException: Task '<id>' has no dispatch to
execute`, repeating every tick. That is blocked dispatch, NOT a corrupt or dangling record. It fired every ~13
seconds for two hours across two loop generations and produced no escalation and no goal-level signal.

**THE FIX, every time:** commit the stranded work on the goal branch (`git add -A` + commit), which cleans the
tree and lets dispatch form on the next tick. Verified on `5694679a` (`00b5096c`) and `ce8597ad` — the exception
stopped within a minute of each.

**This has now been needed FOUR times tonight** — `f00622a9`, `fb13475c`, `5694679a`, `ce8597ad`. Identical
operator action every time. Filed as scope item 5 of `e0dc2b2d`: auto-commit stranded output rather than
blocking indefinitely.

**Caveat — do NOT blindly commit.** If a GATE has already passed on the branch head, the stranded edit was not
gated and committing it lands unverified code; discard and preserve a patch instead. See the stranded-edit
section above for the two cases and how to tell them apart.

## RETRACTED: my "persisted dangling reference" theory about the janitorial exception

I twice theorised about this exception and was wrong both times. Recorded so nobody re-derives the dead ends.

- **Wrong theory 1:** eviction residue from `59d5e939` leaving in-memory operation references.
- **Wrong theory 2:** a PERSISTED queued operation referencing a deleted task — argued from the fact that it
  survived the 12:26:52 handoff and that both task ids kept alternating.

Theory 2 felt strong because surviving a process restart genuinely does rule out in-memory state. But the
premise was wrong: the reference was never dangling. Both tasks belonged to **live, active goals** whose
dispatch was blocked by a dirty worktree — so the blocking CONDITION survived the restart, not a cached
reference. The alternation was simply two different goals blocked at once.

Actual mechanism and the diagnostic are in the blocked-dispatch section above. `a0f5982b` is CLOSED and
superseded by `e0dc2b2d`.

**The transferable lesson:** "it survived a restart, therefore it is persisted state" is a valid inference about
STATE but says nothing about whether the thing is broken. A live, self-renewing CONDITION reproduces the same
signature. Check whether the referenced entity actually exists before theorising about why the reference is
stale — `grep -rl "<task-id>" .orchestrator/goal-operations/` answers that in seconds.

## THE TICK BLOCKS ON SYNCHRONOUS TEST RUNS — measured, and it kills the "event-driven handoff" idea

Filed as `1732dc79`. Two things every future measurement of this system needs.

**1. `WATCH_TRANSITION ... elapsed=` RESETS when a worker restarts.** Back-computing a round's start time by
subtracting it from the transition timestamp produces PHANTOM IDLE GAPS. I did that and published a wrong
"26% idle" figure. For loop cost use `PHASE_TIMING phase=per-goal-walk elapsed_ms`; treat
`WATCH_PROGRESS elapsed=` as a live liveness read only.

**2. Role handoffs are already instant. The loop is BUSY, not asleep.**

    11:10:39.526  tick=67 ... adca4b85 ... Executed
    11:10:39.592  WATCH_TRANSITION Developer -> next=Reviewer ... "Reviewer dispatched"
    11:10:39.593  WATCH_PROGRESS role=Reviewer elapsed=0s pid 24048 alive

**66 ms**, same tick. So backlog `cd753fd9` (event-driven handoffs, "every handoff costs up to a full tick") is
aimed at a cost that does not exist on this path. Do not build it without re-measuring first.

The real cost is pre-review evidence running a REAL TEST SUITE synchronously inside `per-goal-walk`. Across
~300 ticks of one generation, only THREE exceeded 10s — 478930 ms, 162961 ms, 123499 ms — but those three ate
**12.75 minutes, ~14% of the generation**, and the walk is serial so EVERY goal freezes. The acceptance gate is
already async (`acceptance_verification_running_in_background`); pre-review evidence is not. That asymmetry is
the fix.

Also note what a blocked tick costs beyond throughput: while blocked, the loop cannot apply operator intents or
notice stopped goals.

## Liveness lives in the STDOUT log, not the event log

`conduct-events.log` records **decisions**, not liveness. A healthy loop with workers running and nothing to
decide emits NOTHING there for many minutes. A monitor watching only that file cannot tell quiet from dead —
which is a real gap in the silence-breaker as built, and it produced one false alarm at 07:21.

Per-tick liveness is in the loop's own stdout log (`.orchestrator/logs/operator-<name>-<ts>.out.log`):

    PHASE_TIMING tick=26 phase=sweep elapsed_ms=3189 goals=16 ...
    PHASE_TIMING tick=26 phase=prewalk scoped=15 candidates=15 eligible=2 excluded_terminal=0 ...
    WATCH_PROGRESS goal=6cd812af role=Reviewer elapsed=5m16s liveness="alive" ...

To check liveness properly: read that tail, and/or `Get-Process -Id <lock pid>` and look at **CPU** — a loop
consuming CPU is working. The lock pid is line 1 of `.orchestrator/conduct-loop.lock`.

**TIMEZONE TRAP — bit me twice.** `ls` prints **local time (UTC-5)**; log timestamps and `date -u` are **UTC**.
Comparing an `ls` mtime against a UTC log timestamp makes a 12-second-old file look 5 hours stale, and makes a
healthy loop look dead. Either compare `ls` mtime to `date` (local), or compare log-line timestamps to
`date -u`. Never mix the two.

Note `scoped=15` of `goals=16`: the missing one is the goal evicted per `60d80486`, so that counter is a cheap
way to see eviction actually happening.

## Operator gotchas learned tonight

- **`WATCH_TRANSITION files=N` is the LAST ROUND's delta, not the branch total.** Use
  `git diff --stat main...<branch-head>`. It caused two false alarms.
- **Filter artifact directories by ATTEMPT ID.** Attempt folders accumulate siblings; I read a three-hour-old
  TRX and filed a wrong diagnosis from it (`7dfecf10`, since retracted).
- **A cancelled dispatch strands its partial edits**, which dirties the worktree and blocks the pre-landing
  rebase. Check `git status --porcelain` in the goal worktree after any cancel — and READ the diff before
  discarding, because one of them was a real fix worth committing.
- **`goal-mark-landed` is NOT broken** — it records the landing and defers terminalization to a sweep that
  never flips the status. I wrongly called it broken from a single `status` check.
