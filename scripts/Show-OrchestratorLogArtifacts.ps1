param(
    [Parameter(Mandatory = $true)]
    [string]$GoalPrefix,

    [string]$TaskPrefix = "",

    [int]$TailLines = 0,

    [switch]$All
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$logsRoot = Join-Path $repoRoot ".orchestrator\logs"

if (-not (Test-Path -LiteralPath $logsRoot)) {
    Write-Output "No orchestrator logs directory found: $logsRoot"
    exit 0
}

$goal = $GoalPrefix.Trim()
$task = $TaskPrefix.Trim()
if ([string]::IsNullOrWhiteSpace($goal)) {
    throw "GoalPrefix cannot be empty."
}

$filter = if ([string]::IsNullOrWhiteSpace($task)) {
    "$goal-*"
}
else {
    "$goal-$task-*"
}

$files = @(Get-ChildItem -LiteralPath $logsRoot -File -Filter $filter -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime, Name)

if ($files.Count -eq 0) {
    Write-Output "No log artifacts matched '$filter'."
    exit 0
}

function Get-DispatchRunKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $suffixes = @(
        ".heartbeat.json",
        ".dispatch.json",
        ".start-gate",
        ".exit.txt",
        ".out.log",
        ".err.log"
    )

    foreach ($suffix in $suffixes) {
        if ($FileName.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $FileName.Substring(0, $FileName.Length - $suffix.Length)
        }
    }

    return [System.IO.Path]::GetFileNameWithoutExtension($FileName)
}

if (-not $All) {
    $latestGroup = $files |
        Group-Object { Get-DispatchRunKey -FileName $_.Name } |
        Sort-Object `
            @{ Expression = { ($_.Group | Sort-Object LastWriteTime, Name | Select-Object -Last 1).LastWriteTime } },
            @{ Expression = { ($_.Group | Sort-Object LastWriteTime, Name | Select-Object -Last 1).Name } } |
        Select-Object -Last 1

    $files = @($latestGroup.Group | Sort-Object LastWriteTime, Name)
}

foreach ($file in $files) {
    $relative = ".orchestrator\logs\$($file.Name)"
    Write-Output "$relative`t$($file.Length) bytes`t$($file.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
}

if ($TailLines -gt 0) {
    foreach ($file in $files) {
        if ($file.Extension -notin @(".log", ".txt", ".json")) {
            continue
        }

        Write-Output ""
        Write-Output "==> .orchestrator\logs\$($file.Name) <=="
        Get-Content -LiteralPath $file.FullName -Tail $TailLines -ErrorAction SilentlyContinue
    }
}
