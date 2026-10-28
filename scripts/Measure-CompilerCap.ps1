[CmdletBinding(DefaultParameterSetName = 'Native')]
param(
    [switch]$Run,
    [Parameter(ParameterSetName = 'Native')][string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(ParameterSetName = 'Native')][string]$ExpectedCommit,
    [Parameter(ParameterSetName = 'Native')][string]$ExpectedSdk,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(1, 3600)][int]$SetupTimeoutSeconds = 900,
    [ValidateRange(1, 3600)][int]$BuildTimeoutSeconds = 900,
    [Parameter(Mandatory, ParameterSetName = 'Fixture')][string]$FixtureFile,
    [Parameter(ParameterSetName = 'Native')][string]$QuietWindowDeclaration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CompilerCapExperiment.psm1') -Force
try {
    $options = @{
        Run = $Run.IsPresent; RepositoryRoot = $RepositoryRoot; ExpectedCommit = $ExpectedCommit
        ExpectedSdk = $ExpectedSdk; Configuration = $Configuration; FixtureFile = $FixtureFile
        SetupTimeoutSeconds = $SetupTimeoutSeconds; BuildTimeoutSeconds = $BuildTimeoutSeconds
        QuietWindowDeclaration = $QuietWindowDeclaration
    }
    $result = Invoke-CompilerCapExperiment @options
    if (-not $Run) { $result.Plan | ConvertTo-Json -Depth 32; exit 0 }
    Write-Output "COMPILER_CAP status=$($result.Status) report=$($result.ReportPath) (exploratory; no policy applied)"
    foreach ($comparison in $result.Comparisons) {
        Write-Output "$($comparison.Arm): $($comparison.Verdict) reasons=$($comparison.Reasons -join ',')"
    }
    if ($result.Status -ne 'completed') { exit 1 }
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
