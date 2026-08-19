# Local LLM + Grok worker path

Pipeline composition while OpenAI/Anthropic are dark:

| Role | What (backend) | How (harness) |
|---|---|---|
| Planner / Researcher / Developer | xAI `grok-4.6` | `grok-cli` (`--permission-mode plan` or `bypassPermissions`) |
| Tester / Reviewer / Ideation | LlamaCpp `qwen3.6-35b-a3b` at `http://127.0.0.1:8080/v1` | `qwen-code-cli` (`--approval-mode yolo` for Tester, `plan` for Reviewer/Ideation) |

Start the local server before LlamaCpp dispatch:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Start-LlamaServer.ps1
```

Default listen: `http://127.0.0.1:8080/v1` with `-c 16384`. `qwen-code-cli` must pass `--bare`: a default qwen-code startup at repo root is ~25728 tokens and 400s against 8k/16k; `--bare` drops that to ~8671, which fits 16k. `llama-server-cli` remains an optional HTTP-only smoke profile.

# Local LLM bench receipts

Comparable `llama-bench` runs for MoE models on the operator workstation. Models stay outside the repo (`%USERPROFILE%\models`). Receipts are JSON under `receipts/`.

## Default host profile (SEVENTHSON, RTX 3080 10 GB)

Measured 2026-08-19 on this 10 GB card. Do not copy these onto a different quant without a new sweep.

Operational flags (`Measure-LocalLlmBench.ps1` / `Start-LlamaServer.ps1` can pass these):

```text
IQ3_XXS: -ngl 99 -ncmoe 17 -t 20 -fa on -ctk q8_0 -ctv q8_0
IQ4_NL:  -ngl 41 -ot "blk\.14\.ffn_down_exps=CPU;blk\.(1[5-9]|[2-3][0-9]|40)\.ffn_(up|down|gate)_exps=CPU" -t 20 -fa on -ctk q8_0 -ctv q8_0
```

`Start-LlamaServer.ps1` defaults to that IQ4 fit-params profile (no `-ncmoe`). Pass `-OverrideTensor '' -GpuLayers 99 -CpuMoe 26` for the ncmoe peak. `Measure-LocalLlmBench.ps1 -OverrideTensor` omits `-ncmoe` and turns commas into semicolons (llama-bench treats comma `-ot` as a sweep). Both scripts accept `-PrintArgs`. Applying the new server default requires an operator restart of the live llama-server; this change does not bounce it.

## Speed envelope (SEVENTHSON, 2026-08-19)

Winning **operational** IQ3: `-ncmoe 17 -t 20 -fa on -ctk/v q8_0` — pp512 **195–199**, tg64 **63**, d4096 tg **66**. First cliff: `-ncmoe 16` (9723 MiB, 100% GPU, no table; killed).

Winning **operational** IQ4: `-ncmoe 26 -t 20 -fa on -ctk/v q8_0` — pp512 **102–106**, tg64 **43–45**, d4096 tg **48**. First legal-to-illegal step: `-ncmoe 22` (pp512 collapses to ~11); `-ncmoe 18` still thrashes. `-ncmoe 30` was just the first thing that did not die, not the peak.

Threads: do **not** split to `-t 12`. IQ3 tg peaks at 20; pp is a few percent better at 16/24. IQ4 wants 20 for both.

KV: q8_0 is the speed default. q4_0 does not buy tg and loses a little pp. f16 still fits at d0 and is a wash. Flash-attn **off cannot create a context** on this Qwen3.6 GGUF (tried ncmoe 17/18/24 IQ3 and 26 IQ4) — FA on is mandatory, not just faster.

`llama-fit-params` hypothesis **confirmed** and beats `-ncmoe` on prompt: IQ3 `-ngl 41` + last-layer expert `-ot` → pp512 **230** / tg64 **64**, holds at d4096 (tg **66**). IQ4 same shape → pp512 **123** / tg64 **46**. `Start-LlamaServer.ps1` defaults to that IQ4 `-ot`; `Measure-LocalLlmBench.ps1 -OverrideTensor` passes one semicolon-joined `-ot` (commas are a llama-bench sweep).

Speculative: **blocked**. `llama-bench` has no draft/`-md` flags. No draft GGUF on disk. These GGUFs are not the Unsloth MTP variant. Qwen3-0.6B vocab (151936) ≠ 248320. Command that would measure via server: `llama-server -m <target> -md <Qwen3.5-0.8B.gguf> --spec-type draft-simple --spec-draft-n-max 3` plus the winning ngl/ncmoe/fa/kv flags. External 3090 matrix on this class found ngram and draft-simple net-negative.

What is exhausted: ncmoe around the VRAM cliff, threads 8–24, KV q4/q8/f16, FA on/off, batch 512–2048 and ubatch 256–1024, pp128/512/2048 and tg64/256, d4096 on the finalists, and fit-params `-ot`. Leftover is only speculative (needs a vocab-matched draft or an MTP GGUF).

## Rerun

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Measure-LocalLlmBench.ps1 `
    -ModelPath "$env:USERPROFILE\models\Qwen3.6-35B-A3B-UD-IQ3_XXS.gguf" `
    -Label qwen36-35b-iq3xxs `
    -Depths 0,2048,4096
```

Quote the list if the shell splits on commas. The script also accepts spaces.

Empty-context compare (what the first live sweeps used):

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Measure-LocalLlmBench.ps1 `
    -ModelPath "$env:USERPROFILE\models\<gguf>" `
    -Label <label> `
    -PromptTokens 128 512 `
    -Depths 0
```

A receipt is comparable to another only when `command` (ngl/ncmoe/threads/fa/kv/prompt/gen/depths/reps) and `llama.version` match, or the mismatch is the independent variable.

## Receipt fields

`schemaVersion` 1. Host GPU/RAM snapshot, llama.cpp path+version, model path/size, exact argv, parsed `t/s` rows, and raw stdout.
