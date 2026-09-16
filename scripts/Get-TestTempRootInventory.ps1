<#
.SYNOPSIS
  Produce a bounded, read-only inventory of mcg test-temp roots.

.DESCRIPTION
  Classifies only process-owned p{hex} roots with readable owner leases. It uses
  targeted process-id checks and an exclusive lease open; it never enumerates all
  machine processes and never deletes a directory or lease file.
#>
[CmdletBinding()]
param(
    [string[]]$Root = @(),
    [ValidateRange(1, 10000)]
    [int]$MaxCandidates = 256,
    [ValidateRange(1, 10000000)]
    [int]$MaxFiles = 250000,
    [switch]$IncludeRepoFallbackRoots,
    [switch]$NoDefaultRoots
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-OwnerIdentity {
    param([System.IO.Stream]$Stream, [int]$ExpectedProcessId)

    $Stream.Position = 0
    $reader = [System.IO.StreamReader]::new($Stream, [System.Text.Encoding]::UTF8, $true, 1024, $true)
    try {
        $fields = @{}
        foreach ($field in $reader.ReadToEnd().Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $parts = $field.Split('=', 2)
            if ($parts.Count -eq 2) {
                $fields[$parts[0]] = $parts[1]
            }
        }
        $parsedProcessId = 0
        $parsedStartedAt = [DateTimeOffset]::MinValue
        if (-not [int]::TryParse($fields['pid'], [ref]$parsedProcessId) -or
            $parsedProcessId -ne $ExpectedProcessId -or
            -not [DateTimeOffset]::TryParseExact(
                $fields['startedAt'],
                'O',
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$parsedStartedAt)) {
            return $null
        }
        return [pscustomobject]@{
            ProcessId = $parsedProcessId
            StartedAt = $parsedStartedAt.ToUniversalTime()
            Path = $fields['path']
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Get-CandidateDisposition {
    param([string]$SharedRoot, [System.IO.DirectoryInfo]$Directory)

    if ($Directory.Name -notmatch '^p([0-9a-fA-F]+)$') {
        return [pscustomobject]@{ Disposition = 'Ambiguous'; Reason = 'unowned-name'; ProcessId = $null }
    }
    try {
        $processId = [Convert]::ToInt32($Matches[1], 16)
    }
    catch {
        return [pscustomobject]@{ Disposition = 'Ambiguous'; Reason = 'invalid-process-id'; ProcessId = $null }
    }

    $leasePath = Join-Path $SharedRoot ('.{0}.owner.lock' -f $Directory.Name)
    try {
        $readLease = [System.IO.FileStream]::new(
            $leasePath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        try {
            $identity = Read-OwnerIdentity -Stream $readLease -ExpectedProcessId $processId
        }
        finally {
            $readLease.Dispose()
        }
    }
    catch {
        return [pscustomobject]@{ Disposition = 'Ambiguous'; Reason = 'missing-or-unreadable-lease'; ProcessId = $processId }
    }
    if ($null -eq $identity) {
        return [pscustomobject]@{ Disposition = 'Ambiguous'; Reason = 'malformed-or-mismatched-identity'; ProcessId = $processId }
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        try {
            try {
                $observedStartedAt = [DateTimeOffset]::new($process.StartTime.ToUniversalTime(), [TimeSpan]::Zero)
            }
            catch {
                return [pscustomobject]@{ Disposition = 'Ambiguous'; Reason = 'process-identity-inaccessible'; ProcessId = $processId }
            }
            if ([Math]::Abs(($identity.StartedAt - $observedStartedAt).TotalMilliseconds) -lt 1) {
                return [pscustomobject]@{ Disposition = 'Live'; Reason = 'matching-process-instance'; ProcessId = $processId }
            }
        }
        finally {
            $process.Dispose()
        }
    }

    try {
        $exclusiveLease = [System.IO.FileStream]::new(
            $leasePath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        try {
            $revalidated = Read-OwnerIdentity -Stream $exclusiveLease -ExpectedProcessId $processId
            if ($null -eq $revalidated -or
                $revalidated.ProcessId -ne $identity.ProcessId -or
                $revalidated.StartedAt -ne $identity.StartedAt -or
                $revalidated.Path -ne $identity.Path) {
                return [pscustomobject]@{ Disposition = 'Ambiguous'; Reason = 'identity-changed'; ProcessId = $processId }
            }
            return [pscustomobject]@{ Disposition = 'Reclaimable'; Reason = 'owner-gone-exclusive-lease'; ProcessId = $processId }
        }
        finally {
            $exclusiveLease.Dispose()
        }
    }
    catch {
        return [pscustomobject]@{ Disposition = 'LiveOrLocked'; Reason = 'exclusive-lease-unavailable'; ProcessId = $processId }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$roots = [System.Collections.Generic.List[string]]::new()
foreach ($configuredRoot in $Root) {
    if (-not [string]::IsNullOrWhiteSpace($configuredRoot)) {
        $roots.Add([System.IO.Path]::GetFullPath($configuredRoot))
    }
}
if (-not $NoDefaultRoots -and -not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $roots.Add((Join-Path $env:LOCALAPPDATA 'Temp\Low\mcg-tests'))
}
if ($IncludeRepoFallbackRoots) {
    foreach ($fallback in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests') -Directory -Filter '.test-tmp' -Recurse -ErrorAction SilentlyContinue) {
        $roots.Add($fallback.FullName)
    }
}

$uniqueRoots = @($roots | Sort-Object -Unique)
$rows = [System.Collections.Generic.List[object]]::new()
$candidateCount = 0
$filesMeasured = 0
$bytesMeasured = [long]0
$measurementComplete = $true
foreach ($sharedRoot in $uniqueRoots) {
    if (-not (Test-Path -LiteralPath $sharedRoot -PathType Container)) {
        continue
    }
    foreach ($directory in Get-ChildItem -LiteralPath $sharedRoot -Directory) {
        if ($candidateCount -ge $MaxCandidates) {
            $measurementComplete = $false
            break
        }
        $candidateCount++
        $disposition = Get-CandidateDisposition -SharedRoot $sharedRoot -Directory $directory
        $candidateFiles = 0
        $candidateBytes = [long]0
        foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File -Recurse -ErrorAction SilentlyContinue) {
            if ($filesMeasured -ge $MaxFiles) {
                $measurementComplete = $false
                break
            }
            $candidateFiles++
            $filesMeasured++
            $candidateBytes += $file.Length
            $bytesMeasured += $file.Length
        }
        $rows.Add([pscustomobject]@{
            Root = $sharedRoot
            Candidate = $directory.FullName
            ProcessId = $disposition.ProcessId
            Disposition = $disposition.Disposition
            Reason = $disposition.Reason
            FilesMeasured = $candidateFiles
            BytesMeasured = $candidateBytes
        })
    }
    if ($candidateCount -ge $MaxCandidates) {
        break
    }
}

$rows | Sort-Object Root, Candidate | Format-List Root, Candidate, ProcessId, Disposition, Reason, FilesMeasured, BytesMeasured
$reclaimable = @($rows | Where-Object Disposition -eq 'Reclaimable')
$reclaimableBytes = [long]0
foreach ($candidate in $reclaimable) {
    $reclaimableBytes += $candidate.BytesMeasured
}
[pscustomobject]@{
    InventoryRoots = $uniqueRoots.Count
    CandidatesInspected = $candidateCount
    FilesMeasured = $filesMeasured
    BytesMeasured = $bytesMeasured
    MeasurementComplete = $measurementComplete
    ReclaimableCount = $reclaimable.Count
    ReclaimableBytesMeasured = $reclaimableBytes
    MutationPerformed = $false
} | Format-List
