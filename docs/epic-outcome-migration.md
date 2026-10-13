# Versioned epic outcomes

Delivered goals, source-scope completion, plan coverage and epic outcomes are separate facts. Existing descriptions, decisions and legacy `Bar` prose remain intact. Empty, missing and narrative-only bars are **unknown**. Portfolio setup adds empty versioned tables; it approves no policy and creates no proof.

## Supported mutation contract

`epic-outcome-define <epic> --text-file <definition.json> [--operator-actor <name>] [--actor-kind <human|agent>] [--idempotency-key <key>] [--json]`

```json
{
  "schemaVersion": 1,
  "expectedVersion": 0,
  "displayText": "Owner-approved, measurable service outcome",
  "requirements": [
    {
      "id": "service-observed",
      "statement": "Replace this example with the owner's approved measurable requirement",
      "sourceKind": "file-sha256",
      "authority": "owner-approved-observation"
    }
  ]
}
```

Initial version is zero. Each applied definition increments the version, including a replacement with unchanged wording. Requirement ids are unique ASCII letters/digits/dashes/underscores/dots, at most 128 characters. Statements, display text and authority are nonblank. The supported evidence kind is `file-sha256`.

`epic-outcome-record <epic> --text-file <evidence.json> [--operator-actor <name>] [--actor-kind <human|agent>] [--idempotency-key <key>] [--json]`

```json
{
  "schemaVersion": 1,
  "barVersion": 1,
  "requirementId": "service-observed",
  "expectedObservationSequence": 0,
  "met": false,
  "observedAt": "2026-10-13T12:00:00Z",
  "source": {
    "kind": "file-sha256",
    "locator": "observed-service-receipt.txt",
    "authority": "owner-approved-observation",
    "sha256": "<64 hexadecimal characters matching the actual receipt bytes>"
  },
  "supersedesReceiptIds": []
}
```

All fields shown are required except `supersedesReceiptIds`, which defaults to empty. Unknown fields, missing required fields, unsupported schema/source kinds, invalid versions/hashes/ids, duplicate requirement ids or flags, and missing input files refuse submission. Option flags use separate values; duplicate `--json` is invalid. Epic references resolve by the existing id/title contract; missing or ambiguous epics refuse. Run explicit portfolio setup before using older stores.

`met` is an attributed operator observation against the approved requirement. Readable bytes alone do not prove the requirement, and prose is never parsed into a verdict. The operator must check that the named authority actually produced the supporting receipt and that it demonstrates the approved requirement. Relative evidence paths resolve against the explicit workspace root. File paths are bound to their SHA-256 bytes; nonempty receipts are limited to 1 MiB. The portfolio retains the snapshot, source identity, version, requirement, actor, actor kind, channel, observation time and applied-request identity.

Submission reports a pending intent and its id. The conductor applies workspace intents; `operator-intent-status <id> [--wait]` distinguishes applied from rejected. Until application, every view retains the last applied state. Application requires the trusted channel's Mutate authorization or higher; actor text grants no permission and Steward's limited capability cannot mutate outcomes. At application, stale bar versions/sequences, unknown requirements, invalid supersession, mismatched authority/hash and missing/inaccessible evidence reject without changing portfolio history. Evidence accessibility/identity is checked again then, and by the shared read projection.

The same idempotency key and canonical payload/attribution returns the existing intent, including its current status. Changing that payload with the same key refuses. Replaying an already applied portfolio request appends no revision or observation. A new request key is required for a new observation or policy.

Observations bind the exact current bar version. Changing the definition or changing legacy Bar text invalidates prior proof without deleting observations. `epic-bar <epic> --text-file <path>` retains blank/clear syntax and applies the same trusted mutation authorization; identical normalized text is a no-op. Its revision has no structured requirements, so it remains unknown until a new explicit definition is applied.

Each requirement has its own observation sequence. Multiple unsuperseded observations remain **unknown / ambiguous-provenance**; the reader never chooses the most convenient receipt. To replace an observation, include its receipt id (the applied intent id) in `supersedesReceiptIds`. To resolve multiple active receipts, supersede all of them. Supersession may reference only currently active receipts of the same requirement and bar version.

## Shared reads

`epic-list [--since <Nh|Nd|timestamp>] [--json]`, `epic-show <epic> [--json]`, and `epic-plan <epic> [--json]` expose one shared outcome projection. JSON uses envelope schema `mcg.epic-progress`, version 1, with `payload.epics[]`: the existing counts, structured `outcome` state/version/reasons/requirements (including active receipt identities, source locators/hashes and attribution), and `plan` scope facts, decisions, unplanned members and combined next step. `goals --epic --json` continues to mean member goals.

Text preserves the existing scope summary and adds outcome state and outcome next step as separate fields. The console consumes the same projection snapshot in its list and detail. Window filters change delivery counts, not outcome proof. When scope is complete but outcome is unknown/unmet, the combined next step is `outcome evidence required` / `outcome unmet`. A proven bar does not hide reopened scope or unplanned active work, waive a requirement, or create automatic intake work.

Unknown reasons distinguish no definition, unevaluated requirements, stale versions, missing receipts, inaccessible evidence, receipt identity mismatch and ambiguous provenance. A valid unmet observation makes the outcome unmet while other unknown requirement reasons remain visible. Proven requires every current requirement to have one valid, attributable met observation.

## Owner decisions for the live eight epics

Run the read-only `epic-outcome-proposals --json` against the explicitly selected live workspace after its additive setup is approved. It emits a separate proposal for every actual epic: identity/title, current version, legacy Bar, observed outcome, required owner decisions and exact per-epic define/record/decision commands. Confirm the result contains the eight intended epics. The Developer package has no live epic census; their ids or policies must not be guessed.

For **each** of those eight proposals, the owner must:

1. Decide to retain unknown or approve explicit measurable requirements. Preserve narrative descriptions as context.
2. Name the permitted evidence authority and the real receipt that would prove or disprove each requirement.
3. Record the decision through `epic-decide <id> --text-file <owner-decision.txt> --by <owner>` or the existing native decision/progress store.
4. Only after that explicit approval, submit the definition at its observed `expectedVersion`, wait for application, and separately record real observations.

Unapproved or unobserved policies remain unknown. The proposal command writes no policy and creates no extra monitoring receipt files.

## Final-candidate isolated round trip (Operator / Acceptance)

These are required pre-landing observations, not Developer test receipts. Use the built final-candidate DLL, record its candidate SHA in native finding/progress evidence, and use a fresh temporary workspace. In a dedicated PowerShell process set `MCG_ORCHESTRATOR_REPOSITORY_ROOT` to that temporary root **before** launching any app command and clear inherited `MCG_ORCHESTRATOR_PROJECT` / `MCG_ORCHESTRATOR_TENANT` selections; keep the candidate checkout and the temporary store root distinct. The root setting controls all descendants in that isolated process and must never point to the live workspace. Confirm the temporary root has no registered project state override. Use the repository-safe launcher with `-AppDll <absolute-final-candidate-App.dll>` for every command below.

1. Run `conductor setup`; inspect its reported portfolio/backlog/state database paths and require every path to remain under the temporary root. Create an isolated epic with `epic-add`, a backlog slice with `backlog-add`, and a plan slice with `epic-plan-add`. Close the slice's backlog item. If exercising landed goal counts, also create/save the isolated completed member goal and its full source coverage through supported goal/backlog commands. Record the actual store paths.
2. Read `epic-list`, `epic-show`, `epic-plan`, their `--json` forms, and the owner console. Confirm unknown at version zero, truthful landed/scope counts, and remaining outcome work despite completed scope.
3. Submit the example definition with an owner-approved **test** requirement. Run `conduct --loop --max-iterations 1` in the empty isolated workspace to apply the pending intent; poll `operator-intent-status`. Confirm unknown/unevaluated at version one.
4. Create a real receipt under that temporary root. Calculate its hash with `Get-FileHash -Algorithm SHA256`. Submit an explicit unmet observation, apply one conductor iteration, and confirm unmet consistently across text/JSON/console.
5. Submit a met observation at the displayed sequence, superseding the unmet intent id. Apply and confirm proven in every view, with unchanged delivery/scope counts. Never fabricate real live outcome evidence from this isolated test receipt.
6. Apply a new definition at the expected current version. Confirm unknown/stale-version, retained prior observations and rejected old-version evidence. Exercise missing/inaccessible receipts, changed hashes, ambiguous observations, unauthorized application, and duplicate request/payload behavior using the focused contract tests.
7. Generate isolated proposals; confirm read-only behavior. Record candidate, commands, exits and discriminating observations in native evidence stores. Acceptance executes the current-candidate cross-family Grok source review covering outcome/scope distinction, version binding, additive migration and CLI/console reachability. Grok plan mode uses native read/search tools; no Python or terminal command tool.

After landing, separately read the live epic views for rollout confirmation. That read cannot substitute for the final-candidate isolated round trip or the owner decisions that authorize live policy migration.
