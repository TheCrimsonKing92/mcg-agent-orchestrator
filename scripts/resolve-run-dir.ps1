# Resolves (and lazily populates) a per-build ISOLATED copy of the orchestrator binary, and writes its
# directory to stdout. The launcher runs `dotnet <run-dir>\App.dll` so a live run holds its own copy
# instead of the in-tree output -- leaving the in-tree binary free to rebuild while something runs, so
# builds and real runs stop interfering. Content-addressed by the complete app output: identical builds reuse
# one copy, while any changed dependency/config/head marker gets a fresh one. Copies unused for a day are pruned (a live copy's dll is locked, so
# it survives the prune). The copy is valid only when the native SQLite asset is present too; otherwise the
# launcher fails before running an orchestrator command that would later hit DllNotFoundException.
param([Parameter(Mandatory = $true)][string]$Dll)

$ErrorActionPreference = 'Stop'

function Test-NativeSqliteAssetPresent {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $rootAsset = Get-ChildItem -LiteralPath $Path -File -Filter 'e_sqlite3.*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $rootAsset) {
        return $true
    }

    $runtimePath = Join-Path $Path 'runtimes'
    if (-not (Test-Path -LiteralPath $runtimePath)) {
        return $false
    }

    $runtimeAsset = Get-ChildItem -LiteralPath $runtimePath -Recurse -File -Filter 'e_sqlite3.*' -ErrorAction SilentlyContinue | Select-Object -First 1
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

function Get-OutputContentHashPrefix {
    param([Parameter(Mandatory = $true)][string]$Path)

    $payload = [System.Text.StringBuilder]::new()
    $rootPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $rootPath.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal)) {
        $rootPath += [System.IO.Path]::DirectorySeparatorChar
    }
    foreach ($file in (Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName)) {
        if (-not $file.FullName.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Cannot hash file outside output directory: $($file.FullName)"
        }
        # Windows PowerShell runs on .NET Framework, which does not expose Path.GetRelativePath.
        $relativePath = $file.FullName.Substring($rootPath.Length).Replace('\', '/')
        # Use the framework crypto API because the supported Windows PowerShell host does not
        # consistently expose Get-FileHash.
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $stream = $null
        try {
            $stream = [System.IO.File]::OpenRead($file.FullName)
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
    }

    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $hashBytes = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($payload.ToString()))
        return ([System.BitConverter]::ToString($hashBytes) -replace '-', '').Substring(0, 16)
    }
    finally {
        $sha1.Dispose()
    }
}

$out  = Split-Path $Dll
$leaf = Split-Path $Dll -Leaf
$hash = Get-OutputContentHashPrefix -Path $out
$base = Join-Path $env:TEMP 'mcg-run'
$run  = Join-Path $base $hash

Assert-NativeSqliteAssetPresent -Path $out -Context 'app build output'

if ((-not (Test-Path -LiteralPath (Join-Path $run $leaf))) -or (-not (Test-NativeSqliteAssetPresent -Path $run))) {
    if (Test-Path -LiteralPath $run) {
        Remove-Item -LiteralPath $run -Recurse -Force
    }

    [void](New-Item -ItemType Directory -Force -Path $run)
    # Pipe each top-level item to Copy-Item -Recurse to copy subdirs (e.g. runtimes/ native libs) correctly.
    Get-ChildItem -LiteralPath $out | Copy-Item -Destination $run -Recurse -Force
}

Assert-NativeSqliteAssetPresent -Path $run -Context 'isolated run directory'

# Best-effort prune of old, unused run copies. A copy a process is actively running has its dll locked,
# so its directory survives the delete; only abandoned copies are removed. The window only has to outlast
# a single conductor lifetime -- an abandoned copy is reproducible from the build hash naming it -- and at
# one bounce per landing a longer window costs gigabytes.
if (Test-Path -LiteralPath $base) {
    foreach ($dir in (Get-ChildItem -LiteralPath $base -Directory)) {
        if ($dir.LastWriteTime -lt (Get-Date).AddDays(-1)) {
            Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Output $run
