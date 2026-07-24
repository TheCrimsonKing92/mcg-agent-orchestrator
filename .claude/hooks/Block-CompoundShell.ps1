# PreToolUse hook: rejects Bash/PowerShell tool calls containing compound-command
# shapes that cannot match the permission allowlist prefix matcher and therefore
# always trip an interactive prompt (or historically did). Lone pipes are allowed:
# a few piped forms are legitimately allowlisted. Blocking here converts a
# user-facing permission prompt into an immediate self-correction.
$ErrorActionPreference = 'SilentlyContinue'
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $command = [string]$payload.tool_input.command
}
catch {
    exit 0
}
if ([string]::IsNullOrWhiteSpace($command)) { exit 0 }

$violations = New-Object System.Collections.Generic.List[string]
if ($command -match '\$\(')      { $violations.Add('command substitution $(...)') }
if ($command -match '[\x60](?![nrt0aeb])') { $violations.Add('backtick substitution/escape') }
if ($command -match '<<')        { $violations.Add('heredoc <<') }
if ($command -match '&&')        { $violations.Add('chaining &&') }
if ($command -match '\|\|')      { $violations.Add('chaining ||') }
if ($command -match ';')         { $violations.Add('statement chaining ;') }
if ($command -match '\|\s*\.?[\\/]?mcg-orchestrator') { $violations.Add('piping into the orchestrator CLI/REPL') }

if ($violations.Count -gt 0) {
    [Console]::Error.WriteLine("BLOCKED compound shell command [" + ($violations -join '; ') + "]. These shapes never match the permission allowlist and trip interactive prompts. Split into separate bare tool calls (one command each); use Read/Grep/Glob tools for file inspection; use a literal path learned in a prior call instead of substitution; there is no prompt-free way to drive the CLI REPL.")
    exit 2
}
exit 0
