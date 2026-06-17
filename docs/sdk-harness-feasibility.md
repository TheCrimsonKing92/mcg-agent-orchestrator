# Claude Agent SDK Harness — Feasibility Spike

**Goal (6f486244) / Task (8d770bf1)** — Developer spike.  
Can the Claude Agent SDK drive a worker on the SUBSCRIPTION OAuth profile (no paid per-token API) from this .NET app?

---

## (a) OAuth Subscription Profile Resolution

**Finding: Yes, confirmed.** The `@anthropic-ai/sdk` (TypeScript) and `anthropic` (Python) packages resolve credentials through the same chain as Claude Code:

```
ANTHROPIC_API_KEY → ANTHROPIC_AUTH_TOKEN → ant-auth-login profile (~/.config/anthropic/)
```

The CLI documentation states explicitly:  
> "Claude Code and the Claude Agent SDK honor the same profile resolution."

`ant auth login` writes a short-lived OAuth bearer token to `credentials/<profile>.json`.  
Subsequent bare `new Anthropic()` / `Anthropic()` calls pick it up automatically, sending it as `Authorization: Bearer <token>` with `anthropic-beta: oauth-2025-04-20`.  
No `ANTHROPIC_API_KEY` is needed.

**Important caveat — billing scope is unconfirmed for the API surface directly.**  
The current `claude-cli` profile runs the `claude` CLI subprocess (Claude Code product), whose usage is covered by the Pro/Max/Team subscription.  
Using the SDK directly with the same OAuth token calls the Anthropic Messages API or Managed Agents API under your OAuth-identified account. Whether those calls draw from the same subscription bucket (or trigger separate API billing) is **not documented in the SDK skill materials** and would need validation against Anthropic's billing dashboard or support.  
The credential resolution works; the billing outcome requires a live test or Anthropic support confirmation.

---

## (b) Event / Hook Surface

### Messages API + tool use (client-managed loop)

This is the "Claude API + tool use" tier — you host the compute and control the loop. It is available via the TypeScript SDK today.

**Streaming event types emitted by the SDK:**

| SDK event | Meaning |
|---|---|
| `content_block_start` (type `text`) | New text block begins |
| `content_block_delta` (type `text_delta`) | Streaming text token |
| `content_block_start` (type `thinking`) | Extended thinking block begins |
| `content_block_delta` (type `thinking_delta`) | Streaming thinking token |
| `content_block_start` (type `tool_use`) | Claude is calling a tool |
| `content_block_delta` (type `input_json_delta`) | Tool input being streamed |
| `content_block_stop` | Block complete |
| `message_delta` (stop_reason) | `end_turn`, `tool_use`, `max_tokens` |
| `message_stop` | Response complete |

**Hook analogue mapping:**

| Claude Code hook | Messages API equivalent |
|---|---|
| `PreToolUse` | Intercept `content_block_start` where `type === "tool_use"` before executing |
| `PostToolUse` | After executing the tool, before sending `tool_result` back |
| `Stop` | `stop_reason === "end_turn"` in `message_delta`; or the stream closes |

There is no built-in hook registry — the harness drives the loop, so every transition is a natural hook point.

**Changed-files + commit sha extraction:** the harness parses tool calls (`bash`, `write`, `edit`) to capture touched paths and reads `git rev-parse HEAD` at end-of-turn.

### Managed Agents API (server-managed loop, hosted container)

This is the higher-level surface (`/v1/agents`, `/v1/sessions`). Events arrive over SSE:

| Event type | Meaning |
|---|---|
| `agent.message` | Text output |
| `agent.thinking` | Thinking blocks |
| `agent.tool_use` | Built-in tool fired (bash, read, write…) |
| `agent.tool_result` | Tool result |
| `agent.custom_tool_use` | Your custom tool needed — session idles |
| `session.status_idle` / `running` / `terminated` | Session lifecycle |
| `span.model_request_start` / `_end` | Per-turn inference spans with usage |

The `always_ask` permission policy is the `PreToolUse` equivalent; `user.tool_confirmation` is your gate response.

**For the .NET sidecar use case, Managed Agents is heavier than necessary** — it provisions cloud containers, persists sessions, and requires pre-creating an `agent` object. The Messages API + tool use loop is the right tier.

---

## (c) Integration Shape and Smallest Next Step

### Why a Node sidecar

The orchestrator is .NET (C#). The Anthropic SDK for C# (`Anthropic` NuGet) exists and supports the Messages API, but:
- The OAuth profile credential resolution is not confirmed for the C# SDK (the documented profile chain covers Python and TypeScript)
- The TypeScript SDK is the reference implementation for Managed Agents and has the most complete OAuth path documentation

The realistic shape is therefore a **Node.js sidecar**:

```
.NET dispatcher
  │
  │  PowerShell: node scripts/sdk-dispatch.mjs --prompt <path> --workdir <dir>
  ▼
Node.js sidecar (sdk-dispatch.mjs)
  │  reads ANTHROPIC_AUTH_TOKEN from environment
  │  calls Anthropic Messages API with @anthropic-ai/sdk
  │  runs tool loop (bash/file tools)
  │
  │  stdout: NDJSON lines
  │    {"type":"progress","text":"..."}
  │    {"type":"tool_use","name":"bash","args":{...}}
  │    {"type":"changed_file","path":"..."}
  │    {"type":"done","commit_sha":"abc123","changed_files":["..."]}
  │
  ▼
.NET reads stdout line-by-line, maps to dispatch record
```

The .NET side already has the pattern for streaming process output (codex-cli and qwen-code-cli do similar things). The NDJSON contract is easy to parse with `System.Text.Json`.

### Smallest next step (if STEP 2 proceeds)

1. **`scripts/sdk-dispatch.mjs`** — Node.js script (~120 lines):
   - Reads `--prompt` path, `--workdir` path from argv
   - Creates `new Anthropic()` (picks up `ANTHROPIC_AUTH_TOKEN` automatically)
   - Calls `client.messages.stream()` with model + tools (bash, text editor)
   - Emits NDJSON lines to stdout for each tool call and on completion
   - Reads `git rev-parse HEAD` after last tool call to emit `commit_sha`

2. **New profile in `WorkerProfileCatalog.Default()`**:
   ```
   "sdk-node-cli": "node {scriptPath}/sdk-dispatch.mjs --prompt {promptPath} --workdir {workingDirectory}"
   ```
   Where `{scriptPath}` is a new placeholder resolving to the repo scripts directory.

3. **`SdkNdjsonEvent` record + `SdkHarnessParser` class** in Infrastructure/Workers:
   - Deserializes each stdout line to a typed event
   - Maps `changed_file` events to a `List<string>` and `done` to a dispatch record

4. **Test**: `SdkHarnessParserTests` — verifies that a sequence of known NDJSON lines maps to the correct changed-files list and commit sha, no real Node process needed.

---

## STEP 2 Recommendation: Deliver STEP 1 only

STEP 2 is feasible in concept but is **not clearly small** within a single spike:

- The Node sidecar needs `@anthropic-ai/sdk` installed (`package.json`, `node_modules`), which adds dependency management this repo does not currently have for scripts.
- The billing question in §(a) is unresolved. Implementing a new dispatch path before confirming it actually runs on the subscription (and not API credits) risks building something that silently bills at API rates.
- The `{scriptPath}` placeholder is a new kind of placeholder not in `BuildDispatchVariables()`; adding it correctly requires understanding how the orchestrator resolves the worktree root.
- The C# SDK (`Anthropic` NuGet) could be a simpler in-process path if its OAuth resolution is confirmed, which would make the sidecar unnecessary — but that's a second spike.

**The deliverable here is this document.** The findings confirm:
- OAuth profile resolution: works the same way as Claude Code (verify billing in a live test)
- Event surface: Messages API streaming provides all needed hook points (PreToolUse/PostToolUse/Stop analogues)
- Integration shape: Node sidecar with NDJSON is the concrete path; the smallest next step is a ~120-line `sdk-dispatch.mjs` guarded behind a new `sdk-node-cli` profile

A follow-on task can implement STEP 2 once the billing question is answered by a live smoke test (`node -e "new Anthropic().messages.create(...)"` with `ANTHROPIC_AUTH_TOKEN` set and checking the billing dashboard).
