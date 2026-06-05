param(
    [Parameter(Mandatory = $true)]
    [string]$ScriptPath,
    [string]$Url = "http://localhost:5087/",
    [int]$Port = 9222
)

$ErrorActionPreference = "Stop"

$resolvedScript = Resolve-Path -LiteralPath $ScriptPath
$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")

if (-not $resolvedScript.Path.StartsWith($repoRoot.Path, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ScriptPath must be under $($repoRoot.Path)."
}

$expression = Get-Content -Raw -LiteralPath $resolvedScript.Path
& (Join-Path $PSScriptRoot "Invoke-DashboardBrowser.ps1") -Expression $expression -Url $Url -Port $Port
