[CmdletBinding()]
param(
    [string] $ModelPath,

    [string] $LlamaCppDir = (Join-Path $env:USERPROFILE 'tools\llama-b10488'),
    [string] $Label = 'local-llm',
    [int] $GpuLayers = 99,
    [int] $CpuMoe = 18,
    [int] $Threads = 20,
    [ValidateSet('on', 'off', 'auto')]
    [string] $FlashAttn = 'on',
    [string] $CacheTypeK = 'q8_0',
    [string] $CacheTypeV = 'q8_0',
    [string] $PromptTokens = '128,512',
    [int] $GenTokens = 64,
    [string] $Depths = '0',
    [int] $Repetitions = 1,
    [switch] $Warmup,
    [string] $OverrideTensor,
    [switch] $PrintArgs,
    [string] $ReceiptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ReceiptRoot)) {
    $ReceiptRoot = Join-Path $repositoryRoot 'docs\dogfood\local-inference\receipts'
}

function ConvertTo-IntList {
    param([string] $Value)

    $parts = @()
    foreach ($token in @($Value -split '[,;\s]+')) {
        if ([string]::IsNullOrWhiteSpace($token)) {
            continue
        }

        $parts += [int]$token
    }

    return $parts
}

function Get-NvidiaQuery {
    param([string] $Query)

    $nvidia = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($null -eq $nvidia) {
        return $null
    }

    $raw = & $nvidia.Source --query-gpu=$Query --format=csv,noheader,nounits 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$raw)) {
        return $null
    }

    return ([string]$raw).Trim()
}

function ConvertFrom-LlamaBenchTable {
    param([string[]] $Lines)

    $header = $null
    $rows = @()
    foreach ($line in $Lines) {
        if ($line -notmatch '^\|') {
            continue
        }

        $cells = @()
        foreach ($cell in $line.Trim().Trim('|').Split('|')) {
            $cells += $cell.Trim()
        }
        if ($cells.Count -eq 0) {
            continue
        }

        if ($cells[0] -eq 'model') {
            $header = $cells
            continue
        }

        if ($null -eq $header -or $cells[0] -match '^-') {
            continue
        }

        $record = [ordered]@{}
        for ($i = 0; $i -lt $header.Count -and $i -lt $cells.Count; $i++) {
            $record[$header[$i]] = $cells[$i]
        }

        $rateText = [string]$record['t/s']
        $rate = $null
        if ($rateText -match '^([0-9]+(?:\.[0-9]+)?)') {
            $rate = [double]$Matches[1]
        }

        $rows += [pscustomobject]@{
            Test            = [string]$record['test']
            TokensPerSecond = $rate
            TokensPerSecondRaw = $rateText
            Ngl             = [string]$record['ngl']
            CpuMoe          = [string]$record['n_cpu_moe']
            Threads         = [string]$record['threads']
            TypeK           = [string]$record['type_k']
            TypeV           = [string]$record['type_v']
            FlashAttn       = [string]$record['fa']
            Backend         = [string]$record['backend']
            ModelSize       = [string]$record['size']
            Params          = [string]$record['params']
        }
    }

    return $rows
}

function New-LlamaBenchArgList {
    param(
        [string] $ResolvedModelPath
    )

    $built = @(
        '-m', $ResolvedModelPath,
        '-fa', $FlashAttn,
        '-ngl', "$GpuLayers",
        '-t', "$Threads",
        '-ctk', $CacheTypeK,
        '-ctv', $CacheTypeV,
        '-p', ($promptTokenList -join ','),
        '-n', "$GenTokens",
        '-d', ($depthList -join ','),
        '-r', "$Repetitions"
    )
    if ([string]::IsNullOrWhiteSpace($OverrideTensor)) {
        $built += @('-ncmoe', "$CpuMoe")
    }
    else {
        # llama-bench treats commas in -ot as a sweep. Join patterns with semicolons.
        $normalizedOverride = $OverrideTensor -replace ',', ';'
        $built += @('-ot', $normalizedOverride)
    }
    if (-not $Warmup) {
        $built += '--no-warmup'
    }

    return $built
}

$promptTokenList = @(ConvertTo-IntList $PromptTokens)
$depthList = @(ConvertTo-IntList $Depths)
if ($promptTokenList.Count -eq 0 -or $depthList.Count -eq 0) {
    throw 'PromptTokens and Depths must each contain at least one integer.'
}

if ($PrintArgs) {
    $printModelPath = $ModelPath
    if ([string]::IsNullOrWhiteSpace($printModelPath)) {
        $printModelPath = Join-Path $env:USERPROFILE 'models\Qwen3.6-35B-A3B-UD-IQ4_NL.gguf'
    }

    $printArgsList = @(New-LlamaBenchArgList -ResolvedModelPath $printModelPath)
    $printArgsList | ForEach-Object { Write-Output $_ }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ModelPath)) {
    throw 'ModelPath is required unless -PrintArgs is set.'
}

$modelFull = (Resolve-Path -LiteralPath $ModelPath).Path
$benchExe = Join-Path $LlamaCppDir 'llama-bench.exe'
$cliExe = Join-Path $LlamaCppDir 'llama-cli.exe'
if (-not (Test-Path -LiteralPath $benchExe)) {
    throw "llama-bench.exe not found at $benchExe"
}

$startedAt = [DateTimeOffset]::UtcNow
$versionLine = ''
if (Test-Path -LiteralPath $cliExe) {
    $versionLine = ((& $cliExe --version 2>&1) | Out-String).Trim()
}

$modelItem = Get-Item -LiteralPath $modelFull
$cs = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem

$benchArgs = @(New-LlamaBenchArgList -ResolvedModelPath $modelFull)

$rawOutput = & $benchExe @benchArgs 2>&1
$exitCode = $LASTEXITCODE
$rawLines = @(
    $rawOutput | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            [string]$_.Exception.Message
        }
        else {
            [string]$_
        }
    }
)
$rows = @(ConvertFrom-LlamaBenchTable -Lines $rawLines)
$finishedAt = [DateTimeOffset]::UtcNow

$receipt = [ordered]@{
    schemaVersion = 1
    label         = $Label
    startedAt     = $startedAt.ToString('o')
    finishedAt    = $finishedAt.ToString('o')
    elapsedSeconds = [Math]::Round(($finishedAt - $startedAt).TotalSeconds, 3)
    exitCode      = $exitCode
    host          = [ordered]@{
        name           = $cs.Name
        cpuLogical     = [int]$cs.NumberOfLogicalProcessors
        ramGb          = [Math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
        freeRamGb      = [Math]::Round($os.FreePhysicalMemory / 1MB, 1)
        gpuName        = Get-NvidiaQuery 'name'
        vramTotalMiB   = Get-NvidiaQuery 'memory.total'
        vramUsedMiB    = Get-NvidiaQuery 'memory.used'
        vramFreeMiB    = Get-NvidiaQuery 'memory.free'
    }
    llama         = [ordered]@{
        directory = $LlamaCppDir
        benchExe  = $benchExe
        version   = $versionLine
    }
    model         = [ordered]@{
        path              = $modelFull
        bytes             = [int64]$modelItem.Length
        lastWriteTimeUtc  = $modelItem.LastWriteTimeUtc.ToString('o')
    }
    command       = [ordered]@{
        gpuLayers    = $GpuLayers
        cpuMoe       = $CpuMoe
        threads      = $Threads
        flashAttn    = $FlashAttn
        cacheTypeK   = $CacheTypeK
        cacheTypeV   = $CacheTypeV
        promptTokens = @($promptTokenList)
        genTokens    = $GenTokens
        depths       = @($depthList)
        repetitions  = $Repetitions
        warmup       = [bool]$Warmup
        arguments    = @($benchArgs)
    }
    rows          = @($rows)
    rawOutput     = @($rawLines)
}

New-Item -ItemType Directory -Force -Path $ReceiptRoot | Out-Null
$stamp = $startedAt.ToString('yyyyMMddTHHmmssZ')
$receiptPath = Join-Path $ReceiptRoot "$stamp-$Label.json"
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiptPath -Encoding utf8

Write-Output "receipt=$receiptPath"
Write-Output "exit=$exitCode"
foreach ($row in $rows) {
    Write-Output ("{0}`t{1}" -f $row.Test, $row.TokensPerSecondRaw)
}

if ($exitCode -ne 0) {
    throw "llama-bench exited $exitCode"
}
