<#
.SYNOPSIS
Run a read-only Claude reviewer using a DPAPI-protected API key, independently of subscription login.
.DESCRIPTION
Register this as a custom worker profile with provider name AnthropicApi. That name is a
billing-route identity: subscription cooldowns remain on Anthropic. The credential never
appears in command arguments, prompts, receipts, or the parent environment. --bare makes
Claude authenticate exclusively with the API key and skips subscription/keychain lookup.
Repository policy must be embedded by the orchestrator (AutoLoadsRepositoryPolicy=false).
#>
[CmdletBinding(DefaultParameterSetName = 'Worker')]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Reviewer')][string]$Role,
    [Parameter(Mandatory = $true)][string]$CredentialPath,
    [Parameter(Mandatory = $true)][string]$WorkingDirectory,
    [Parameter(Mandatory = $true, ParameterSetName = 'Worker')][string]$PromptPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Probe')][switch]$Probe,
    [ValidatePattern('^claude-[a-z0-9._-]+$')][string]$Model = 'claude-opus-5-5',
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')][string]$ReasoningEffort = 'high',
    [string]$ClaudePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$key = $null
$child = $null
$startInfo = $null
$promptStream = $null

try {
    $workingRoot = (Resolve-Path -LiteralPath $WorkingDirectory -ErrorAction Stop).Path
    if (-not [IO.Directory]::Exists($workingRoot)) { throw 'Reviewer working directory is not a directory.' }
    $key = Import-Clixml -LiteralPath $CredentialPath
    if ($key -isnot [Security.SecureString] -or $key.Length -eq 0) {
        throw 'Reviewer API credential must be a nonempty encrypted SecureString.'
    }
    if ([string]::IsNullOrWhiteSpace($ClaudePath)) {
        $ClaudePath = (Get-Command claude.exe -CommandType Application -ErrorAction Stop).Source
    }
    $ClaudePath = (Resolve-Path -LiteralPath $ClaudePath -ErrorAction Stop).Path

    # Keep these read-only tools aligned with ProviderCommandBuilder.BuildClaude.
    $tools = 'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite'
    $allowed = 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite'
    # All interpolated CLI values are validated enums/identifiers or constants. Paths use
    # ProcessStartInfo properties and prompt bytes use stdin, never shell interpolation.
    $arguments = '-p --bare --model ' + $Model + ' --effort ' + $ReasoningEffort +
        ' --permission-mode dontAsk --permission-prompts none --restricted --strict-mcp-config' +
        ' --tools "' + $tools + '" --allowed-tools "' + $allowed + '"' +
        ' --disallowed-tools "Edit,Write,NotebookEdit" --no-session-persistence'
    if ($Probe) {
        $arguments += ' --max-turns 1 --max-budget-usd 0.25'
        $promptStream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('Reply with OK only.'))
    } else {
        $promptStream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $PromptPath -ErrorAction Stop).Path)
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ClaudePath
    $startInfo.Arguments = $arguments
    $startInfo.WorkingDirectory = $workingRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    # Output inherits the orchestrator's capture handles and streams without buffering.
    foreach ($name in @('ANTHROPIC_AUTH_TOKEN', 'CLAUDE_CODE_OAUTH_TOKEN', 'ANTHROPIC_BASE_URL',
        'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY')) {
        $startInfo.EnvironmentVariables.Remove($name)
    }
    $startInfo.EnvironmentVariables['ANTHROPIC_API_KEY'] = [Net.NetworkCredential]::new('', $key).Password
    [Console]::Error.WriteLine('CLAUDE_API_REVIEWER billing=AnthropicApi auth=api-key role=Reviewer model=' + $Model)
    $child = [Diagnostics.Process]::Start($startInfo)
    $startInfo.EnvironmentVariables.Remove('ANTHROPIC_API_KEY')
    $key.Dispose()
    $key = $null
    try {
        $promptStream.CopyTo($child.StandardInput.BaseStream)
        $child.StandardInput.Flush()
    } catch [IO.IOException] {
        # A refused request can exit before reading stdin; retain the child's real verdict.
    } finally {
        $child.StandardInput.Close()
        $promptStream.Dispose()
        $promptStream = $null
    }
    $child.WaitForExit()
    $childExitCode = $child.ExitCode
} finally {
    if ($startInfo) { $startInfo.EnvironmentVariables.Remove('ANTHROPIC_API_KEY') }
    if ($key) { $key.Dispose() }
    if ($promptStream) { $promptStream.Dispose() }
    if ($child) { $child.Dispose() }
}
exit $childExitCode
