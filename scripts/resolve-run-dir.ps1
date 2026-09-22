# Resolves (and lazily populates) an immutable, content-addressed copy of the orchestrator
# application closure. A launcher never observes a directory while it is being copied: each
# caller copies into its own sibling staging directory, verifies the complete payload, writes a
# seal, and atomically renames the directory into the published namespace.
param([Parameter(Mandatory = $true)][string]$Dll)

$ErrorActionPreference = 'Stop'
$script:RunCacheSchemaVersion = 2
$script:RunCacheMarkerName = '.mcg-run-closure.json'
$script:SourceSnapshotAttempts = 3

function Test-NativeSqliteAssetPresent {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }

    $rootAsset = Get-ChildItem -LiteralPath $Path -Force -File -Filter 'e_sqlite3.*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $rootAsset) {
        return $true
    }

    $runtimePath = Join-Path $Path 'runtimes'
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Container)) {
        return $false
    }

    $runtimeAsset = Get-ChildItem -LiteralPath $runtimePath -Force -Recurse -File -Filter 'e_sqlite3.*' -ErrorAction SilentlyContinue | Select-Object -First 1
    return $null -ne $runtimeAsset
}

function Assert-NativeSqliteAssetPresent {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if (Test-NativeSqliteAssetPresent -Path $Path) {
        return
    }

    throw "Native SQLite asset e_sqlite3 is missing from $Context ($Path). Rebuild the app with 'dotnet build src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj' and retry; if it is still missing, restore packages and inspect Microsoft.Data.Sqlite runtime assets."
}

function Get-OutputContentSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$ExcludeRunCacheMarker
    )

    $payload = [System.Text.StringBuilder]::new()
    $rootPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $rootPath.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal)) {
        $rootPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $fileCount = 0
    foreach ($file in (Get-ChildItem -LiteralPath $Path -Force -Recurse -File | Sort-Object FullName)) {
        if ($ExcludeRunCacheMarker -and $file.Name.Equals($script:RunCacheMarkerName, [System.StringComparison]::Ordinal)) {
            continue
        }
        if (-not $file.FullName.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Cannot hash file outside output directory: $($file.FullName)"
        }

        $relativePath = $file.FullName.Substring($rootPath.Length).Replace('\', '/')
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $stream = $null
        try {
            $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            $fileHash = [System.BitConverter]::ToString($sha256.ComputeHash($stream)) -replace '-', ''
        }
        finally {
            if ($null -ne $stream) {
                $stream.Dispose()
            }
            $sha256.Dispose()
        }

        [void]$payload.Append($relativePath)
        [void]$payload.Append(':')
        [void]$payload.Append($fileHash)
        [void]$payload.Append("`n")
        $fileCount++
    }

    $sha256Payload = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256Payload.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($payload.ToString()))
        $digest = [System.BitConverter]::ToString($hashBytes) -replace '-', ''
    }
    finally {
        $sha256Payload.Dispose()
    }

    return [pscustomobject]@{
        Digest = $digest
        FileCount = $fileCount
    }
}

function Test-PublishedRunDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedDigest,
        [Parameter(Mandatory = $true)][string]$AppLeaf
    )

    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            return $false
        }
        if (-not (Test-Path -LiteralPath (Join-Path $Path $AppLeaf) -PathType Leaf)) {
            return $false
        }
        if (-not (Test-NativeSqliteAssetPresent -Path $Path)) {
            return $false
        }

        $markerPath = Join-Path $Path $script:RunCacheMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            return $false
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ([int]$marker.schemaVersion -ne $script:RunCacheSchemaVersion -or
            -not ([string]$marker.digest).Equals($ExpectedDigest, [System.StringComparison]::Ordinal)) {
            return $false
        }

        $snapshot = Get-OutputContentSnapshot -Path $Path -ExcludeRunCacheMarker
        return $snapshot.Digest.Equals($ExpectedDigest, [System.StringComparison]::Ordinal) -and
            [int]$marker.fileCount -eq $snapshot.FileCount
    }
    catch {
        return $false
    }
}

$resolvedDll = [System.IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $resolvedDll -PathType Leaf)) {
    throw "Orchestrator application assembly does not exist: $resolvedDll"
}

$out = Split-Path $resolvedDll
$leaf = Split-Path $resolvedDll -Leaf
$base = Join-Path (Join-Path $env:TEMP 'mcg-run') "v$script:RunCacheSchemaVersion"
[void](New-Item -ItemType Directory -Force -Path $base)

$resolvedRun = $null
$lastSnapshotFailure = $null
for ($attempt = 1; $attempt -le $script:SourceSnapshotAttempts; $attempt++) {
    $staging = $null
    try {
        Assert-NativeSqliteAssetPresent -Path $out -Context 'app build output'
        $before = Get-OutputContentSnapshot -Path $out
        $run = Join-Path $base $before.Digest

        if (Test-Path -LiteralPath $run) {
            if (Test-PublishedRunDirectory -Path $run -ExpectedDigest $before.Digest -AppLeaf $leaf) {
                $resolvedRun = $run
                break
            }
            throw "Published orchestrator run directory is invalid and will not be modified or launched: $run"
        }

        $staging = Join-Path $base ".staging-$PID-$([Guid]::NewGuid().ToString('N'))"
        [void](New-Item -ItemType Directory -Path $staging -ErrorAction Stop)
        Get-ChildItem -LiteralPath $out -Force | Copy-Item -Destination $staging -Recurse -Force -ErrorAction Stop

        Assert-NativeSqliteAssetPresent -Path $staging -Context 'staged isolated run directory'
        $staged = Get-OutputContentSnapshot -Path $staging
        $after = Get-OutputContentSnapshot -Path $out
        if (-not $before.Digest.Equals($staged.Digest, [System.StringComparison]::Ordinal) -or
            -not $before.Digest.Equals($after.Digest, [System.StringComparison]::Ordinal) -or
            $before.FileCount -ne $staged.FileCount -or
            $before.FileCount -ne $after.FileCount) {
            $lastSnapshotFailure = "App output changed while it was being staged (attempt $attempt of $script:SourceSnapshotAttempts)."
            continue
        }

        $marker = [ordered]@{
            schemaVersion = $script:RunCacheSchemaVersion
            digest = $before.Digest
            fileCount = $before.FileCount
            sealedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $staging $script:RunCacheMarkerName),
            ($marker | ConvertTo-Json -Compress),
            [System.Text.Encoding]::UTF8)

        try {
            [System.IO.Directory]::Move($staging, $run)
            $staging = $null
        }
        catch [System.IO.IOException] {
            if (-not (Test-PublishedRunDirectory -Path $run -ExpectedDigest $before.Digest -AppLeaf $leaf)) {
                throw "Concurrent orchestrator run publication did not produce a valid closure at '$run'. $($_.Exception.Message)"
            }
        }

        if (-not (Test-PublishedRunDirectory -Path $run -ExpectedDigest $before.Digest -AppLeaf $leaf)) {
            throw "Published orchestrator run directory failed recursive closure validation: $run"
        }
        $resolvedRun = $run
        break
    }
    catch {
        $lastSnapshotFailure = $_.Exception.Message
        if ($attempt -eq $script:SourceSnapshotAttempts) {
            throw
        }
    }
    finally {
        if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ([string]::IsNullOrWhiteSpace($resolvedRun)) {
    throw "Could not seal a stable orchestrator application closure after $script:SourceSnapshotAttempts attempts. $lastSnapshotFailure"
}

Write-Output $resolvedRun
