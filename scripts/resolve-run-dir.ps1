# Resolves (and lazily populates) a per-build ISOLATED copy of the orchestrator binary, and writes its
# directory to stdout. The launcher runs `dotnet <run-dir>\App.dll` so a live run holds its own copy
# instead of the in-tree output -- leaving the in-tree binary free to rebuild while something runs, so
# builds and real runs stop interfering. Content-addressed by the App.dll hash: identical builds reuse one
# copy, a new build gets a fresh one. Copies unused for 7 days are pruned (a live copy's dll is locked, so
# it survives the prune). On any failure, the launcher falls back to the in-tree binary.
param([Parameter(Mandatory = $true)][string]$Dll)

$ErrorActionPreference = 'SilentlyContinue'

$out  = Split-Path $Dll
$leaf = Split-Path $Dll -Leaf
$hash = (Get-FileHash -Algorithm SHA1 -LiteralPath $Dll).Hash.Substring(0, 16)
$base = Join-Path $env:TEMP 'mcg-run'
$run  = Join-Path $base $hash

if (-not (Test-Path -LiteralPath (Join-Path $run $leaf))) {
    [void](New-Item -ItemType Directory -Force -Path $run)
    # Pipe each top-level item to Copy-Item -Recurse to copy subdirs (e.g. runtimes/ native libs) correctly.
    Get-ChildItem -LiteralPath $out | Copy-Item -Destination $run -Recurse -Force
}

# Best-effort prune of old, unused run copies. A copy a process is actively running has its dll locked,
# so its directory survives the delete; only abandoned copies are removed.
foreach ($dir in (Get-ChildItem -LiteralPath $base -Directory)) {
    if ($dir.LastWriteTime -lt (Get-Date).AddDays(-7)) {
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force
    }
}

Write-Output $run
