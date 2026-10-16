param(
    [Parameter(Mandatory = $true)][string]$Worktree,
    [Parameter(Mandatory = $true)][string]$Task,
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$InputFile
)

$ErrorActionPreference = 'Stop'
$toolRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("worker-result-submit-" + [Guid]::NewGuid().ToString('N'))
$toolBin = Join-Path $toolRoot 'bin'
$toolProject = Join-Path $PSScriptRoot 'WorkerResultSubmitTool\WorkerResultSubmitTool.csproj'
$buildLog = Join-Path $toolRoot 'bootstrap.txt'
$exitCode = 3
try {
    New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
    & dotnet build $toolProject --nologo --configuration Debug --verbosity minimal --artifacts-path $toolRoot --output $toolBin -maxcpucount:1 -p:BuildInParallel=false -tl:off *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine([System.IO.File]::ReadAllText($buildLog))
        [Console]::Error.WriteLine('REJECTED: submit tool build failed')
    } else {
        $toolDll = Join-Path $toolBin 'WorkerResultSubmitTool.dll'
        & dotnet $toolDll --worktree $Worktree --task $Task --path $Path --input $InputFile
        $exitCode = $LASTEXITCODE
    }
} catch {
    if (Test-Path -LiteralPath $buildLog -PathType Leaf) {
        [Console]::Error.WriteLine([System.IO.File]::ReadAllText($buildLog))
    }
    [Console]::Error.WriteLine($_.Exception.Message)
    [Console]::Error.WriteLine('REJECTED: submit tool build failed')
} finally {
    $resolvedToolRoot = [System.IO.Path]::GetFullPath($toolRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedToolRoot.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedToolRoot).StartsWith('worker-result-submit-') -and
        (Test-Path -LiteralPath $resolvedToolRoot)) {
        Remove-Item -LiteralPath $resolvedToolRoot -Recurse -Force
    }
}
exit $exitCode
