# Excess worker rounds, W28–W32

Backlog item: `abf6f8a2cd614f06bd49c2174a2503b8`

Frozen event cutoff: `2026-08-07T22:43:03.4206169Z`

Finding date: 2026-08-10

## Finding

The explicit reconstruction confirms the endpoint: median excess rounds rose from 1 in W28 to 4 in W32. It does **not** support one cohort-wide causal decomposition of that three-round change.

One enabling boundary and one tail mechanism are supported, at different strengths:

1. Commit `f2182b05` (July 8) made automatic selection of Reviewer-bearing pipelines possible—routed risk selects Developer+Reviewer and implicit scope selects the five-role pipeline—and `42449341` changed the inference inputs later that day. The observed Reviewer-bearing share rose from 28/63 to 57/85, which is consistent with that boundary. It is not a proven cause of the shift: selected-pipeline coverage is 0/275, and no archived-objective replay or per-goal routing receipt connects the source change to the observed goals. Excess also rose inside matched Reviewer-bearing role sets, so composition is not sufficient regardless.
2. Automatic Reviewer `needs-work` routing (`1b0b4307`, July 14) creates real Reviewer-to-upstream repair cycles. The bounded `c4a6fb14` chain proves that these can form a long tail while catching substantive defects. The available historical records do not establish their cohort-wide median contribution.

The data therefore do not justify compensating retry, circuit-breaker, scheduler, acceptance, role, model, or review changes. In particular, productive Reviewer rounds must not be optimized away.

## Reproducible definitions and inputs

- **Inclusion:** a goal has a line-start `WATCH_TRANSITION` in the validated operator-log manifest, has a timestamped goal-operation journal activation, and that activation falls in W28–W32.
- **Role round:** one line-start `WATCH_TRANSITION` receipt. Dispatch receipts are a different measure and are never substituted.
- **Distinct role set/order:** respectively, the sorted set and first-observed order of roles in included transition receipts.
- **Excess rounds:** role rounds minus distinct observed roles.
- **Activation:** earliest timestamped operation in the goal journal.
- **Week:** activation's ISO week after conversion to `America/Chicago`.
- **Landing:** first `Integrate goal/<prefix>` commit on `main`.
- **Exclusion:** missing journal or activation data excludes the goal. Missing landing, provider, task, criterion, or path data stays null and is excluded only from that field's denominator; it is never zero-filled.

The reconstructed manifest contains exactly 1,791 `operator-*.out.log` files whose filesystem write time is no later than the cutoff. Its normalized `path|sha256|lastWriteUtc` digest is `9a1264742da9091ec0477ccdcb70c11b541382886d9dd2366b690cb72faba4f2`. This is an explicit new selection rule, not the lost original cohort rule. Those logs name 325 goal prefixes; the journal manifest binds the 318 matching journals (seven are missing) at normalized digest `422885d0aa400142011b9ac060444942d096541f06f0d8ecdb5865b2342db6f2`. The other declared identities are dogfood database SHA-256 `f9f6d9e8cb7bed9af660bb94ba7d6ac5c6f9d6cf468a6c5d1781080ac926cba1` and Git history revision `4fc816b898f2a3496bf5b5f2fb6478c00d613908`.

The helper joined 275 W28–W32 goals. Seven transition-bearing prefixes lacked a journal; 43 activated outside the requested weeks. Output coverage was:

| Field | Covered / 275 | Interpretation |
|---|---:|---|
| Transition rounds, role order/set, per-round/final/max `files=` counts | 275 | Counts only; not unique paths or semantic scope |
| Activation | 275 | Required for inclusion |
| Main integration landing and non-negative E2E duration | 222 | Unlanded remains missing |
| Dogfood provider/model by role | 167 | Partial; no universal provider inference |
| Focused-evidence activity | 84 | Positive journal activity |
| Acceptance gate attempts | 251 | Positive journal activity |
| Selected pipeline, complexity, risk, criterion count/ownership | 0 | Historical task snapshot not supplied |
| Changed paths/scope | 0 | Transition receipts retain counts, not paths |

The missing task evidence is classified as:

`disposition=undecidable; would-settle=a frozen W28–W32 task snapshot containing selected pipeline, complexity/risk, criteria and owners, per-round provider/model, and changed paths for the reconstructed goal ids; required-source=historical orchestrator task/state store; unavailable-because=that store is outside this goal's authorized inputs`.

The original headline cohort is separately classified as:

`disposition=undecidable; would-settle=the original sorted 1,791-log manifest plus exact goal join and exclusion query that yields weekly n=45/84/48/42/49; required-source=the original measurement producer or frozen analysis receipt; unavailable-because=the manifest and command were never recorded`.

## Command ledger

Run from the repository root. The manifest and outputs are temporary and must not be committed.

```powershell
$cutoff = [datetimeoffset]'2026-08-07T22:43:03.4206169Z'
$storeRoot = 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator'
$analysisRoot = Join-Path $env:TEMP 'mcg-abf6f8a2'
$manifest = Join-Path $analysisRoot 'operator-manifest.csv'
$journalManifest = Join-Path $analysisRoot 'journal-manifest.csv'

New-Item -ItemType Directory -Path $analysisRoot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $storeRoot 'logs') -Filter 'operator-*.out.log' -File |
    Where-Object { $_.LastWriteTimeUtc -le $cutoff.UtcDateTime } |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            lastWriteUtc = $_.LastWriteTimeUtc.ToString('o')
        }
    } | Export-Csv -LiteralPath $manifest -NoTypeInformation -Encoding utf8NoBOM

$prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @(Import-Csv -LiteralPath $manifest)) {
    foreach ($line in [IO.File]::ReadLines($entry.path)) {
        if ($line -match '^WATCH_TRANSITION goal=(?<goal>[0-9a-f]{8,32}) ') {
            [void]$prefixes.Add($matches.goal.Substring(0, 8))
        }
    }
}

Get-ChildItem -LiteralPath (Join-Path $storeRoot 'goal-operations') -Filter '*.jsonl' -File |
    Where-Object { $_.BaseName.Length -ge 8 -and $prefixes.Contains($_.BaseName.Substring(0, 8)) } |
    Sort-Object Name |
    ForEach-Object {
        [pscustomobject]@{
            path = $_.Name
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
    } | Export-Csv -LiteralPath $journalManifest -NoTypeInformation -Encoding utf8NoBOM

pwsh -NoProfile -File .\scripts\Analyze-ExcessWorkerRounds.ps1 `
    -OperatorLogManifest $manifest `
    -OperatorLogManifestDigestSha256 9a1264742da9091ec0477ccdcb70c11b541382886d9dd2366b690cb72faba4f2 `
    -JournalRoot (Join-Path $storeRoot 'goal-operations') `
    -JournalManifest $journalManifest `
    -JournalManifestDigestSha256 422885d0aa400142011b9ac060444942d096541f06f0d8ecdb5865b2342db6f2 `
    -DogfoodDbPath (Join-Path $storeRoot 'dogfood-log.db') `
    -DogfoodDbSha256 f9f6d9e8cb7bed9af660bb94ba7d6ac5c6f9d6cf468a6c5d1781080ac926cba1 `
    -RepositoryRoot . `
    -RepositoryRevision 4fc816b898f2a3496bf5b5f2fb6478c00d613908 `
    -StartWeek 2026-W28 -EndWeek 2026-W32 `
    -TimeZoneId America/Chicago `
    -CutoffUtc ($cutoff.ToString('o')) `
    -OutputDirectory (Join-Path $analysisRoot 'run1')

# Repeat with run2, then compare both files byte-for-byte.
Get-FileHash (Join-Path $analysisRoot 'run1\excess-worker-rounds.csv')
Get-FileHash (Join-Path $analysisRoot 'run1\excess-worker-rounds-summary.json')
```

The original pre-fix verified output digests were:

| Output | SHA-256 | Repeat |
|---|---|---|
| `excess-worker-rounds.csv` | `3d6e0eb8c96727ffb9e8903c3aff34a922fece8df20ca523575f878ea7e0f704` | byte-identical |
| `excess-worker-rounds-summary.json` | `145e1f427bf94c2475132c0e50c7b72d12052d3bcb0d42f3ea1409459fa2a30b` | byte-identical |

### Post-fix operator rerun

The operator reran the corrected analyzer twice at `2026-08-11T15:38:46.5830999+00:00` against candidate `f0db9e1c8dd06d2ea321ee04f542f4d3e2e65515`. The conductor was stopped while a GUID temp snapshot copied only the 1,791 declared pre-cutoff logs, 318 matching journals, and dogfood database. Manifest entries used stable relative paths. Both analyzer passes consumed the same frozen snapshot; all raw logs, journals, database copies, manifests, and generated tables were deleted afterward.

| Post-fix input/output | Receipt |
|---|---|
| Cutoff / repository revision | `2026-08-07T22:43:03.4206169Z` / `4fc816b898f2a3496bf5b5f2fb6478c00d613908` |
| Operator manifest | 1,791 rows; SHA-256 `1856b9381b1786c611a75e3309b3351c0b05a5bd6b773ed19c0141c788c8d456` |
| Journal manifest | 318 rows; SHA-256 `422885d0aa400142011b9ac060444942d096541f06f0d8ecdb5865b2342db6f2` |
| Dogfood database / WAL | SHA-256 `f9f6d9e8cb7bed9af660bb94ba7d6ac5c6f9d6cf468a6c5d1781080ac926cba1`; no WAL content |
| `excess-worker-rounds.csv` | 275 rows; SHA-256 `ff9f2e1360d5e4f481dd02ca1be3a3785095321816046bafa6d309f13fd17283`; repeat byte-identical |
| `excess-worker-rounds-summary.json` | SHA-256 `505170ed5694988192595e2c0aba8c7d30a335543198e88da06aab37897a392f`; repeat byte-identical |

The post-fix weekly receipts remain W28 `63 / 4 / 1`, W29 `85 / 6 / 2`, W30 `41 / 8 / 4`, W31 `44 / 5 / 3`, and W32 `42 / 6 / 4` for goals / median rounds / median excess. Thus the corrected cutoff and typed-category logic preserve the report's `1 → 4` endpoint and the checked-in tables. The temporary operator harness SHA-256 was `e1c4ad713e6176fa8ba933f011cba0150d33faee1d24bdd7ae6ce876cf60e544`; the raw JSON receipt SHA-256 was `a94feb80c82a9d35f15b3073b81045e556a222d0ed4b31dc814483091abce8fe`. These hashes identify the executed harness/receipt, while the bounded facts above are authoritative after scratch cleanup.

The helper is read-only with respect to logs, journals, dogfood, Git, backlog, and orchestrator state. It writes only the requested output directory. Operator logs and journals are manifest-only inputs; later undeclared files are ignored. A changed declared file, manifest digest, dogfood DB, optional task snapshot, or Git revision fails closed before output, as do missing/duplicate files, post-cutoff operator rows, ambiguous journal prefixes, invalid weeks, and failed Git/SQLite reads. A non-empty `dogfood-log.db-wal` is also rejected unless its SHA-256 is supplied with `-DogfoodWalSha256`, and both database hashes are rechecked after the query. This means a later ledger rerun is either byte-identical or reports a named input-integrity mismatch; it never silently updates the finding from ambient state.

## Metric reproduction and sensitivity

| Activation week | Goals | Median rounds | Median excess | Bootstrap stability interval for median excess |
|---|---:|---:|---:|---:|
| W28 | 63 | 4 | 1 | 0–1 |
| W29 | 85 | 6 | 2 | 1–3 |
| W30 | 41 | 8 | 4 | 3–7 |
| W31 | 44 | 5 | 3 | 2–4 |
| W32 | 42 | 6 | 4 | 2–6 |

The intervals are deterministic bootstrap stability diagnostics for this observed cohort, not population confidence intervals.

The stored table (45/84/48/42/49 goals) is not exactly reproducible because its join/exclusion rule was not retained. The explicit reconstruction nevertheless preserves the 1→4 endpoint. Its landed E2E comparison is 152 rework goals at a 324.046-minute median versus 70 no-rework goals at 103.188 minutes, a 3.14× ratio. Thus “roughly 4×” is directionally stable but is not an exact result under this cohort.

## Matched comparisons

Raw medians alone confound pipeline composition. Exact observed-role-set strata give:

| Observed role set | W28 n / median excess | W29 n / median excess |
|---|---:|---:|
| Developer | 15 / 0 | 11 / 0 |
| Developer + Reviewer | 10 / 1.5 | 21 / 3 |
| Planner + Researcher + Developer + Tester | 11 / 0 | 9 / 0 |
| Five roles | 15 / 2 | 35 / 8 |

Reviewer-bearing mix increased, but both matched Reviewer-bearing strata also worsened. This rejects composition as a sufficient explanation.

Reweighting later goals to W28's observed-role-set weights gives a bounded sensitivity check:

| Week | Observed median | W28-role-set-standardized median | Observed minus standardized | Covered W28 weight |
|---|---:|---:|---:|---:|
| W29 | 2 | 0 | 2 | 84.13% |
| W30 | 4 | 2 | 2 | 57.14% |
| W31 | 3 | 0 | 3 | 65.08% |
| W32 | 4 | 7 | -3 | 41.27% |

This says composition is associated with up to two median points at the W29 boundary under 84% common-support coverage. It is not a causal allocation: the outcome within the reweighted strata had already changed. Later-week support is too incomplete for a stable endpoint decomposition, and the negative W32 value is a warning against interpreting it as “rounds explained.” Supported cohort-wide contribution to the W28→W32 +3 therefore remains **undetermined**, not zero.

## Causal tests and boundary receipts

| Hypothesis | Evidence and discriminator | Disposition |
|---|---|---|
| Goal-composition shift | `f2182b05` enables automatic Reviewer-bearing routing; `42449341` changes inference inputs; observed Reviewer-bearing mix rises, but selected-pipeline coverage is 0/275 and no per-goal routing replay exists | Source mechanism and observed shift are consistent; causal attribution is undetermined; composition-only explanation rejected by matched strata |
| Reviewer newly became active | W28 already contains 28 Reviewer-bearing goals; Reviewer was already a configured role | Rejected |
| Provider/model change universally caused the rise | Provider/model coverage is 167/275; the extreme `c4a6fb14` chain used OpenAI `gpt-5.5` for all roles despite `058c4461`'s Haiku routing | Universal claim rejected; partial effects undetermined |
| `ReviewerWorkerResultBlockers` change | Source timing and weekly overlap do not discriminate formatting-only from semantic failures | Cause undetermined; replay the same archived outputs across pre/post parser revisions and require an unchanged-HEAD formatting-only control |
| Impossible RED/GREEN or negative-control evidence | Criterion text and ownership have 0/275 historical coverage; `c4a6fb14` contains two role-policy deferrals but is one case | Cohort share undetermined; supply the frozen task/criterion snapshot and match exact text, owner, capability, deferral, and HEAD |
| Criterion/scope inference | `f2182b05` and `42449341` provide a routing mechanism, but no archived objective was replayed and joined to its observed outcome | Causal role in the composition shift and excess magnitude both undetermined until that replay exists |
| Automatic Reviewer repair | `1b0b4307` routes `needs-work` upstream; the exact sample below shows finding → changed Developer commit → later pass | Supported tail mechanism; cohort contribution undetermined |
| `ProgressiveReviewGlance` | It emits `GLANCE`, was introduced July 19 after W29 began, and runs only on an already failing round | Rejected as W28→W29 cause |

Current source anchors are `GoalObjectivePlanner.SelectPipeline` in `src/Mcg.AgentOrchestrator.App/Orchestration/GoalObjectivePlanner.cs`, `ConductorDriver.TryBuildVerifyingFindingAutoRetry` in `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs`, `WorkerResultBlockers` in `src/Mcg.AgentOrchestrator.Core/Application/ReviewerWorkerResultBlockers.cs`, and `ProgressiveReviewGlanceCoordinator` in `src/Mcg.AgentOrchestrator.App/Orchestration/ProgressiveReviewGlance.cs`. Effective source commits are `f2182b05` and `42449341` (July 8), `058c4461` (July 9), `1b0b4307` (July 14), `0c492951` and `f39f2232` (July 16), and `4e2a0875`/`0b7a67bc` (July 19).

## Bounded mechanism trace

`c4a6fb148e594fa999a7e44da1a26c70` is the deterministic extreme-tail sample retained because its evidence demonstrates the repair mechanism at chain level while exposing the limit of per-round classification.

- Transition anchors run from `operator-conduct-loop-batch63-20260716162123.out.log:9164` to `operator-conduct-loop-batch75-20260718095458.out.log:501`.
- It has 49 completed `WATCH_TRANSITION` receipts, five observed roles, and 44 excess rounds: Planner 2, Researcher 2, Developer 12, Tester 15, Reviewer 18.
- The separate dispatch census has 51 receipts: Planner 2, Researcher 2, Developer 12, Tester 18, Reviewer 17. The mismatch is expected because a dispatch receipt is not a completed transition.
- Six Reviewer results were `needs-work` and ten were `pass`. Exact needs-work anchors include `c4a6fb14-89adc4ed-20260717014244.out.log:16` (missing lane/reason plumbing, duplicate scorecard handling, typed retry marker) and `c4a6fb14-89adc4ed-20260718034000.out.log:15` (Developer-only mechanical selection, retry-kind plumbing, lost selection-reason receipt).
- Three Reviewer `needs-work` results occurred at unchanged HEAD `e8ead874` with substantially repeated blockers before the later `4e7490a` change. Their findings were substantive, but the individual unchanged-head re-review rounds cannot be separated into productive repair versus avoidable churn from the retained evidence. The later changed Developer commit followed by Reviewer pass supports the repair mechanism at the chain level only.
- Two Tester rounds deferred evidence because worker policy prohibited the requested self-verification; two other failed dispositions retained substantive blockers despite green tests. These are positive examples of evidence/role mismatch and “tests green is not done,” but one goal cannot establish cohort prevalence.
- Dogfood evidence identifies OpenAI `gpt-5.5` for every role. This falsifies a universal Haiku explanation for the representative spiral.

Historical criterion ownership is unavailable, so the trace stops short of claiming that a specific criterion-policy change caused any round. No timing-only claim is made.

## Category accounting and uncertainty

The available sources do not support a mutually exclusive cohort-wide census of productive defect-catching, no-op, impossible-evidence, provider/preflight, formatting-contract, and unchanged-head rounds. The analyzer emits only `provider/preflight`, whose authoritative journal producer is `WorkerProfileDispatcher.BuildReadyBlockedDiagnostic`/`ReadyBlockedDiagnostic` formatted by `ConductorDriver.DescribeEmptyBatch` and recorded as failed `conductor:dispatch` detail. Journals contain 9/275 such positive receipts, but they do not bind each completed transition to a final semantic disposition or HEAD change. Incidental words in acceptance-test names are excluded. Treating the other 266 as zero would violate the missing-data rule.

The production contract inventory explains why the other categories are withheld. Criterion/role-capability failures are emitted as task progress (`ProgressKind.TaskFailed`, `TaskRetried`, or finding-evidence state) by `AgentOrchestratorKernel`; malformed structured outcomes are emitted as `TaskFailed` by `AgentTaskRunner`; Reviewer `needs-work` is recorded in task verification/progress and routed through `ConductorDriver.TryBuildVerifyingFindingAutoRetry`. None is a `GoalOperationJournal` producer available to the frozen journal manifest. `WATCH_TRANSITION files=` counts contain no candidate HEAD, while acceptance journal `BranchHeadSha` belongs to a separate gate and is not bound to a completed worker transition. Therefore `impossible-evidence`, `formatting-contract`, `reviewer-finding`, and `unchanged-head` are explicitly marked unavailable in `failureFindingCategoryCoverage`; similarly named `reason=`/`category=` detail text is not classified.

| Category | Authoritative producer/source | Positive measured evidence | Defensible contribution to +3 median |
|---|---|---|---|
| Productive Reviewer repair | Bounded worker output/dispatch trace for `c4a6fb14`; not journal-classified | Six needs-work results with substantive findings; three repeated at unchanged HEAD before a later changed commit and pass | Chain-level tail mechanism supported; per-round split and cohort median points undetermined |
| Impossible evidence | Task progress/state (`AgentOrchestratorKernel`), unavailable in frozen journal inputs | Two policy-prohibited deferrals in the bounded `c4a6fb14` trace only; analyzer withholds the cohort category | Cohort median points undetermined |
| Provider/preflight | `ReadyBlockedDiagnostic` → `ConductorDriver.DescribeEmptyBatch` → failed `conductor:dispatch` journal detail | 9/275 journals contain a failed dispatch with a production-shaped provider/preflight reason; provider/model mapping exists for 167/275 | Causal contribution undetermined |
| Formatting contract | Task failure/progress (`AgentTaskRunner` and `AgentOrchestratorKernel`), unavailable in frozen journal inputs | Analyzer withholds the category; no cohort-wide transition-to-output binding | Undetermined |
| Reviewer finding | Task verification/progress and `ConductorDriver.TryBuildVerifyingFindingAutoRetry`, unavailable in frozen journal inputs | Analyzer withholds the category; bounded trace evidence remains separate | Undetermined |
| Unchanged-head/no-op | No producer binds `WATCH_TRANSITION` to candidate HEAD | Analyzer withholds the category; no cohort-wide transition-to-HEAD binding | Undetermined |
| Other no-op | No authoritative historical classifier | No authoritative historical classification | Undetermined |

These rows overlap and must not be summed. A factor earns a causal median-point estimate only after changed exposure, matched outcome movement, an exact mechanism receipt, and a counterfactual recomputation all agree. Only composition has a bounded reweighting estimate, and its incomplete support prevents an endpoint attribution.

## Ranked recommendation

1. **Close this backlog item as investigated with no compensating architecture.** The durable result is the reproduced endpoint, the routing boundary consistent with the observed composition shift, the chain-level productive-repair mechanism, the rejected hypotheses, and the explicit limits on attribution.
2. **At the goal boundary, check the canonical backlog before adding one prospective-measurement follow-up.** If uncovered, the narrow follow-up is to freeze the log manifest plus task/criterion/provider/HEAD snapshot and emit a per-transition typed category. It is measurement work, not retry or review-policy work.
3. **Do not weaken Reviewer or cap repair rounds from this evidence.** First run the proposed frozen census and the exact replay/control experiments above. If productive and avoidable rounds can then be separated, optimize only the proven avoidable category.

No pipeline or product behavior was changed by this investigation.
