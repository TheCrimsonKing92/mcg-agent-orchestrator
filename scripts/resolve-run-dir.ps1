# Resolves (and lazily populates) a per-build ISOLATED copy of the orchestrator binary, and writes its
# directory to stdout. The launcher runs `dotnet <run-dir>\App.dll` so a live run holds its own copy
# instead of the in-tree output -- leaving the in-tree binary free to rebuild while something runs, so
# builds and real runs stop interfering. Content-addressed by the App.dll hash: identical builds reuse one
# copy, a new build gets a fresh one. Copies unused for 7 days are pruned (a live copy's dll is locked, so
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

function Get-Sha1Prefix {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha1 = [System.Security.Cryptography.SHA1]::Create()
        try {
            $hashBytes = $sha1.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hashBytes) -replace '-', '').Substring(0, 16)
        }
        finally {
            $sha1.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$out  = Split-Path $Dll
$leaf = Split-Path $Dll -Leaf
$hash = Get-Sha1Prefix -Path $Dll
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
# so its directory survives the delete; only abandoned copies are removed.
if (Test-Path -LiteralPath $base) {
    foreach ($dir in (Get-ChildItem -LiteralPath $base -Directory)) {
        if ($dir.LastWriteTime -lt (Get-Date).AddDays(-7)) {
            Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Output $run
