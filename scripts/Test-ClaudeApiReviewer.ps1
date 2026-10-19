# Local process-boundary checks; no provider calls or real credentials.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('claude-api-reviewer-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$launcher = Join-Path $PSScriptRoot 'Invoke-ClaudeApiReviewer.ps1'
$fakeExe = Join-Path $testRoot 'fake-claude.exe'
$credential = Join-Path $testRoot 'credential.xml'
$prompt = Join-Path $testRoot 'prompt.txt'
$stderr = Join-Path $testRoot 'stderr.txt'
$stdout = Join-Path $testRoot 'stdout.txt'
$envNames = @('ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'CLAUDE_CODE_OAUTH_TOKEN', 'ANTHROPIC_BASE_URL',
    'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY')
$saved = @{}
try {
    foreach ($name in $envNames) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, 'parent-sentinel', 'Process')
    }
    # A native child checks what actually crossed the process boundary, not source text.
    Add-Type -OutputAssembly $fakeExe -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.IO;
public static class FakeClaude {
    public static int Main(string[] args) {
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") != "fixture-only-key") return 81;
        foreach (string name in new[] { "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_BASE_URL",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
            if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return 82;
        if (Environment.CurrentDirectory != Path.GetDirectoryName(typeof(FakeClaude).Assembly.Location)) return 83;
        string flags = String.Join(" ", args);
        if (flags.Contains("fixture-only-key") || flags.Contains("parent-sentinel")) return 84;
        foreach (string flag in new[] { "--bare", "--restricted", "--strict-mcp-config", "--no-session-persistence" })
            if (Array.IndexOf(args, flag) < 0) return 85;
        foreach (string[] pair in new[] {
            new[] { "--permission-mode", "dontAsk" }, new[] { "--permission-prompts", "none" },
            new[] { "--disallowed-tools", "Edit,Write,NotebookEdit" }, new[] { "--model", "claude-opus-5-5" },
            new[] { "--effort", "high" } }) {
            int i = Array.IndexOf(args, pair[0]);
            if (i < 0 || i + 1 >= args.Length || args[i + 1] != pair[1]) return 86;
        }
        int allowed = Array.IndexOf(args, "--allowed-tools");
        if (allowed < 0 || !args[allowed + 1].Contains("Bash(git diff *)") || args[allowed + 1].Contains("Bash(*)")) return 87;
        if (Console.In.ReadToEnd() != "review prompt: \u03bb\nsecond line") return 88;
        Console.WriteLine("CHILD_BOUNDARY_PASS");
        return 23; // Intentional nonzero: the launcher must preserve it.
    }
}
'@
    ConvertTo-SecureString 'fixture-only-key' -AsPlainText -Force | Export-Clixml -LiteralPath $credential
    [IO.File]::WriteAllText($prompt, "review prompt: $([char]0x03bb)`nsecond line", [Text.UTF8Encoding]::new($false))
    $workerArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $launcher,
        '-Role', 'Reviewer', '-CredentialPath', $credential, '-WorkingDirectory', $testRoot,
        '-PromptPath', $prompt, '-ClaudePath', $fakeExe)
    $ErrorActionPreference = 'Continue' # Windows PowerShell treats redirected native stderr as errors.
    & powershell.exe @workerArgs 1> $stdout 2> $stderr
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 23 -or [IO.File]::ReadAllText($stdout).Trim() -ne 'CHILD_BOUNDARY_PASS') {
        throw "Child boundary or exit propagation failed (exit $LASTEXITCODE)."
    }
    foreach ($name in $envNames) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process') -ne 'parent-sentinel') {
            throw "Parent environment changed: $name"
        }
    }
    $combined = [IO.File]::ReadAllText($stdout) + [IO.File]::ReadAllText($stderr)
    if ($combined.Contains('fixture-only-key') -or $combined.Contains('parent-sentinel')) { throw 'Credential leaked to output.' }
    $roleIndex = [Array]::IndexOf($workerArgs, '-Role') + 1
    $workerArgs[$roleIndex] = 'Developer'
    $ErrorActionPreference = 'Continue'
    & powershell.exe @workerArgs 1> $stdout 2> $stderr
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 1 -or [IO.File]::ReadAllText($stdout).Contains('CHILD_BOUNDARY_PASS')) { throw 'Non-reviewer role was not rejected.' }
    $workerArgs[$roleIndex] = 'Reviewer'
    [Security.SecureString]::new() | Export-Clixml -LiteralPath $credential
    $ErrorActionPreference = 'Continue'
    & powershell.exe @workerArgs 1> $stdout 2> $stderr
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 1 -or [IO.File]::ReadAllText($stdout).Contains('CHILD_BOUNDARY_PASS')) { throw 'Empty credential was not rejected.' }
    Write-Output 'PASS: child-only credential, auth isolation, read-only flags, UTF-8 stdin, working directory, exit propagation, role guard, empty-key rejection.'
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    foreach ($path in @($fakeExe, $credential, $prompt, $stderr, $stdout)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    Remove-Item -LiteralPath $testRoot -Force
}
