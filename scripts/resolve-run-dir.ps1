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
    $files = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in (Get-ChildItem -LiteralPath $Path -Force -Recurse -File | Sort-Object FullName)) {
        if (-not $file.FullName.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Cannot hash file outside output directory: $($file.FullName)"
        }

        $relativePath = $file.FullName.Substring($rootPath.Length).Replace('\', '/')
        if ($ExcludeRunCacheMarker -and $relativePath.Equals($script:RunCacheMarkerName, [System.StringComparison]::Ordinal)) {
            continue
        }
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
        $files.Add($relativePath, $fileHash)
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
        Files = $files
    }
}

function Repair-PublishedRunDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)]$SourceSnapshot,
        [Parameter(Mandatory = $true)][string]$AppLeaf
    )

    try {
        if (-not [IO.Path]::GetFileName($Path).Equals($SourceSnapshot.Digest, [StringComparison]::Ordinal) -or
            -not (Test-Path -LiteralPath $Path -PathType Container)) {
            return $false
        }

        $sourceNow = Get-OutputContentSnapshot -Path $SourcePath
        if (-not $sourceNow.Digest.Equals($SourceSnapshot.Digest, [StringComparison]::Ordinal) -or
            $sourceNow.FileCount -ne $SourceSnapshot.FileCount) {
            return $false
        }

        $markerPath = Join-Path $Path $script:RunCacheMarkerName
        $markerMissing = -not (Test-Path -LiteralPath $markerPath -PathType Leaf)
        if (-not $markerMissing) {
            $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            if ([int]$marker.schemaVersion -ne $script:RunCacheSchemaVersion -or
                -not ([string]$marker.digest).Equals($SourceSnapshot.Digest, [StringComparison]::Ordinal) -or
                [int]$marker.fileCount -ne $SourceSnapshot.FileCount) {
                return $false
            }
        }

        $published = Get-OutputContentSnapshot -Path $Path -ExcludeRunCacheMarker
        foreach ($entry in $published.Files.GetEnumerator()) {
            if (-not $SourceSnapshot.Files.ContainsKey($entry.Key) -or
                -not $SourceSnapshot.Files[$entry.Key].Equals($entry.Value, [StringComparison]::Ordinal)) {
                return $false
            }
        }
        if ($published.FileCount -eq $SourceSnapshot.FileCount -and -not $markerMissing) {
            return $false
        }

        foreach ($entry in $SourceSnapshot.Files.GetEnumerator()) {
            if ($published.Files.ContainsKey($entry.Key)) {
                continue
            }
            $relative = $entry.Key.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $sourceFile = Join-Path $SourcePath $relative
            $targetFile = Join-Path $Path $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetFile))
            try {
                [IO.File]::Copy($sourceFile, $targetFile, $false)
            }
            catch [IO.IOException] {
                if (-not (Test-Path -LiteralPath $targetFile -PathType Leaf) -or
                    -not (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash.Equals(
                        $entry.Value, [StringComparison]::OrdinalIgnoreCase)) {
                    throw
                }
            }
        }

        if ($markerMissing) {
            $seal = [ordered]@{
                schemaVersion = $script:RunCacheSchemaVersion
                digest = $SourceSnapshot.Digest
                fileCount = $SourceSnapshot.FileCount
                sealedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            } | ConvertTo-Json -Compress
            $bytes = [Text.Encoding]::UTF8.GetBytes($seal)
            try {
                $stream = [IO.File]::Open($markerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $stream.Write($bytes, 0, $bytes.Length) }
                finally { $stream.Dispose() }
            }
            catch [IO.IOException] {
                if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw }
            }
        }

        $sourceAfter = Get-OutputContentSnapshot -Path $SourcePath
        return $sourceAfter.Digest.Equals($SourceSnapshot.Digest, [StringComparison]::Ordinal) -and
            $sourceAfter.FileCount -eq $SourceSnapshot.FileCount -and
            (Test-PublishedRunDirectory -Path $Path -ExpectedDigest $SourceSnapshot.Digest -AppLeaf $AppLeaf)
    }
    catch {
        return $false
    }
}

function Remove-UnusedRunDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$CurrentDigest,
        [Parameter(Mandatory = $true)][string]$AppLeaf
    )

    foreach ($candidate in (Get-ChildItem -LiteralPath $BasePath -Directory -Force -ErrorAction SilentlyContinue)) {
        if (($candidate.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $candidate.Name -notmatch '^[0-9A-Fa-f]{64}$' -or
            $candidate.Name.Equals($CurrentDigest, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $appDll = Join-Path $candidate.FullName $AppLeaf
        $probe = $null
        $trash = $null
        $deleting = $false
        try {
            if (Test-Path -LiteralPath $appDll -PathType Leaf) {
                $probe = [IO.File]::Open($appDll, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            }
            # Rename is the boundary: if any child is held by a running process, Windows
            # refuses the move and the original directory remains wholly untouched.
            if ($null -ne $probe) { $probe.Dispose(); $probe = $null }
            $trash = Join-Path $BasePath ".trash-$PID-$([Guid]::NewGuid().ToString('N'))"
            [IO.Directory]::Move($candidate.FullName, $trash)
            # A launcher may have opened this DLL between the first probe and the move.
            # Check again before deleting any file and put the whole directory back if busy.
            $retiredAppDll = Join-Path $trash $AppLeaf
            if (Test-Path -LiteralPath $retiredAppDll -PathType Leaf) {
                $probe = [IO.File]::Open($retiredAppDll, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $probe.Dispose()
                $probe = $null
            }
            $deleting = $true
            Remove-Item -LiteralPath $trash -Recurse -Force -ErrorAction Stop
        }
        catch {
            # Retirement is best effort; it must never prevent launching the validated closure.
            if ($null -ne $trash -and -not $deleting -and
                (Test-Path -LiteralPath $trash -PathType Container) -and
                -not (Test-Path -LiteralPath $candidate.FullName)) {
                try { [IO.Directory]::Move($trash, $candidate.FullName) }
                catch { }
            }
        }
        finally {
            if ($null -ne $probe) { $probe.Dispose() }
        }
    }

    # An interrupted retirement can leave a renamed directory. Its creating resolver
    # owns it while alive; a later resolver may reclaim it only after that PID exits.
    foreach ($retired in (Get-ChildItem -LiteralPath $BasePath -Directory -Force -Filter '.trash-*' -ErrorAction SilentlyContinue)) {
        if (($retired.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $retired.Name -notmatch '^\.trash-([0-9]+)-[0-9A-Fa-f]{32}$') {
            continue
        }
        $ownerPid = 0
        if (-not [int]::TryParse($Matches[1], [ref]$ownerPid)) { continue }
        if (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) {
            continue
        }

        $probe = $null
        $claimed = $null
        $deleting = $false
        try {
            $retiredAppDll = Join-Path $retired.FullName $AppLeaf
            if (Test-Path -LiteralPath $retiredAppDll -PathType Leaf) {
                $probe = [IO.File]::Open($retiredAppDll, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $probe.Dispose()
                $probe = $null
            }
            $claimed = Join-Path $BasePath ".trash-$PID-$([Guid]::NewGuid().ToString('N'))"
            [IO.Directory]::Move($retired.FullName, $claimed)
            $claimedAppDll = Join-Path $claimed $AppLeaf
            if (Test-Path -LiteralPath $claimedAppDll -PathType Leaf) {
                $probe = [IO.File]::Open($claimedAppDll, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $probe.Dispose()
                $probe = $null
            }
            $deleting = $true
            Remove-Item -LiteralPath $claimed -Recurse -Force -ErrorAction Stop
        }
        catch {
            if ($null -ne $claimed -and -not $deleting -and
                (Test-Path -LiteralPath $claimed -PathType Container) -and
                -not (Test-Path -LiteralPath $retired.FullName)) {
                try { [IO.Directory]::Move($claimed, $retired.FullName) }
                catch { }
            }
        }
        finally {
            if ($null -ne $probe) { $probe.Dispose() }
        }
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
            if (Repair-PublishedRunDirectory -Path $run -SourcePath $out -SourceSnapshot $before -AppLeaf $leaf) {
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

Remove-UnusedRunDirectories -BasePath $base -CurrentDigest (Split-Path $resolvedRun -Leaf) -AppLeaf $leaf
Write-Output $resolvedRun
