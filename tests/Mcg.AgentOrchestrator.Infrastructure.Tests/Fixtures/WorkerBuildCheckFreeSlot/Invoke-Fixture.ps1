param(
    [ValidateSet("HashedSlotBusy", "HashedSlotFree")]
    [string]$Scenario = "HashedSlotBusy"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CapturedProcess {
    param(
        [string]$FileName,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        # Hang-only guard: the child must exit, never a performance assertion.
        [int]$TimeoutSeconds = 120
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) { $startInfo.ArgumentList.Add($argument) }
    foreach ($name in @("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR")) {
        $null = $startInfo.Environment.Remove($name)
    }
    foreach ($name in $Environment.Keys) { $startInfo.Environment[$name] = $Environment[$name] }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Process did not start: $FileName" }
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $null = $stdoutTask.GetAwaiter().GetResult()
            $null = $stderrTask.GetAwaiter().GetResult()
            throw "Child exit event did not arrive within the hang guard: $FileName"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdoutTask.GetAwaiter().GetResult()
            Stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$helperPath = Join-Path $repositoryRoot "scripts\Invoke-WorkerBuildCheck.ps1"
$tempBase = [System.IO.Path]::GetTempPath()
if ($tempBase -match '[\\/]\.orchestrator-worktrees[\\/]') {
    $tempBase = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Temp"
}
$temporaryRoot = [System.IO.Path]::GetFullPath((Join-Path $tempBase ("mcg-worker-free-slot-fixture-" + [Guid]::NewGuid().ToString("N"))))
if ($temporaryRoot -match '[\\/]\.orchestrator-worktrees[\\/]') {
    throw "Fixture work directory must resolve to the manual goal prefix."
}
$workDirectory = Join-Path $temporaryRoot "work"
$shimDirectory = Join-Path $temporaryRoot "shim"
$isolatedRoot = Join-Path $temporaryRoot "isolated"
$captureDirectory = Join-Path $temporaryRoot "capture"
$slotZeroLockPath = Join-Path $isolatedRoot "build-slots\build-0.lock"
$shellPath = (Get-Process -Id $PID).Path
$busyLock = $null
$busyLockHeld = $false

try {
    New-Item -ItemType Directory -Force -Path $workDirectory, $shimDirectory, $captureDirectory, (Split-Path -Parent $slotZeroLockPath) | Out-Null
    $project = Join-Path $workDirectory "One.csproj"
    Set-Content -LiteralPath $project -Value "<Project />"
    # Only this throwaway fixture repository is committed; the goal worktree is untouched.
    foreach ($gitArguments in @(
        ,@("init", "--initial-branch=fixture")
        ,@("add", "One.csproj")
        ,@("-c", "user.name=Build Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "fixture")
    )) {
        $git = Invoke-CapturedProcess -FileName "git" -ArgumentList $gitArguments -WorkingDirectory $workDirectory
        if ($git.ExitCode -ne 0) { throw "Fixture git setup failed: $($git.Stdout)$($git.Stderr)" }
    }

    $shimScript = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$rawArguments = $env:DOTNET_SHIM_RAW_ARGUMENTS
$artifactsMatch = [regex]::Match($rawArguments, '(?i)(?:^|\s)"?--artifacts-path"?\s+(?:"(?<path>[^"]+)"|(?<path>\S+))')
if (-not $artifactsMatch.Success) { throw "Shim did not receive --artifacts-path: $rawArguments" }
$artifactsPath = $artifactsMatch.Groups["path"].Value
[System.IO.File]::WriteAllText((Join-Path $env:MCG_FREE_SLOT_CAPTURE "artifacts-path.txt"), $artifactsPath)
# This snapshot executes inside dotnet build, while the helper still holds its slot.
foreach ($holder in @(Get-ChildItem -LiteralPath (Join-Path $env:MCG_DOTNET_ISOLATED_ROOT "build-slots") -Filter "*.lock.owner.json" -File)) {
    Copy-Item -LiteralPath $holder.FullName -Destination (Join-Path $env:MCG_FREE_SLOT_CAPTURE $holder.Name)
}
$fileLoggerMatch = [regex]::Match($rawArguments, '(?i)(?:^|\s)"?-flp:LogFile=(?<path>.+?);Verbosity=normal;Encoding=UTF-8;Append=false"?(?:\s|$)')
if (-not $fileLoggerMatch.Success) { throw "Shim did not receive the file logger argument: $rawArguments" }
[System.IO.File]::WriteAllText($fileLoggerMatch.Groups["path"].Value, "Build succeeded.", [System.Text.UTF8Encoding]::new($false))
exit 0
'@
    Set-Content -LiteralPath (Join-Path $shimDirectory "dotnet-shim.ps1") -Value $shimScript
    $shimCommand = @'
@echo off
set "DOTNET_SHIM_RAW_ARGUMENTS=%*"
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0dotnet-shim.ps1"
exit /b %ERRORLEVEL%
'@
    Set-Content -LiteralPath (Join-Path $shimDirectory "dotnet.cmd") -Value $shimCommand

    if ($Scenario -eq "HashedSlotBusy") {
        # The fixture is a separate process from the helper; its handle is not inherited.
        $busyLock = [System.IO.File]::Open($slotZeroLockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        $busyLock.Lock(0, 1)
        $busyLockHeld = $true
    }

    $helper = Invoke-CapturedProcess -FileName $shellPath -WorkingDirectory $workDirectory -ArgumentList @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $helperPath, $project
    ) -Environment @{
        PATH = $shimDirectory + [System.IO.Path]::PathSeparator + $env:PATH
        MCG_DOTNET_ISOLATED_ROOT = $isolatedRoot
        MCG_FREE_SLOT_CAPTURE = $captureDirectory
    }
    Write-Output $helper.Stdout
    if (-not [string]::IsNullOrWhiteSpace($helper.Stderr)) { Write-Output $helper.Stderr }

    $slot = if ($Scenario -eq "HashedSlotBusy") { "build-1" } else { "build-0" }
    $holderPath = Join-Path $isolatedRoot "build-slots\$slot.lock.owner.json"
    $capturedHolderPath = Join-Path $captureDirectory "$slot.lock.owner.json"
    $receiptPath = Join-Path $isolatedRoot "goals\manual\artifacts\worker-build-receipt.json"
    $artifactsCapturePath = Join-Path $captureDirectory "artifacts-path.txt"
    $lockReacquired = $false
    if ($Scenario -eq "HashedSlotFree") {
        $probe = [System.IO.File]::Open($slotZeroLockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        try {
            $probe.Lock(0, 1)
            $lockReacquired = $true
            $probe.Unlock(0, 1)
        }
        finally { $probe.Dispose() }
    }

    $observed = [ordered]@{
        helperExitCode = $helper.ExitCode
        artifactsPath = if (Test-Path -LiteralPath $artifactsCapturePath) { [System.IO.File]::ReadAllText($artifactsCapturePath) } else { $null }
        holder = if (Test-Path -LiteralPath $capturedHolderPath) { Get-Content -LiteralPath $capturedHolderPath -Raw | ConvertFrom-Json } else { $null }
        holderRemoved = -not (Test-Path -LiteralPath $holderPath)
        lockReacquired = $lockReacquired
        canonicalReceiptExists = Test-Path -LiteralPath $receiptPath -PathType Leaf
        receipt = if (Test-Path -LiteralPath $receiptPath -PathType Leaf) { Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } else { $null }
    }
    Write-Output ("OBSERVED fixture: " + ($observed | ConvertTo-Json -Depth 5 -Compress))
}
finally {
    if ($null -ne $busyLock) {
        if ($busyLockHeld) { $busyLock.Unlock(0, 1) }
        $busyLock.Dispose()
    }
    # Verify the absolute cleanup target is the fixture's unique root under its temp base.
    $cleanupPath = [System.IO.Path]::GetFullPath($temporaryRoot)
    $expectedParent = [System.IO.Path]::GetFullPath($tempBase).TrimEnd('\', '/')
    if ((Split-Path -Parent $cleanupPath) -ne $expectedParent -or
        (Split-Path -Leaf $cleanupPath) -notmatch '^mcg-worker-free-slot-fixture-[a-f0-9]{32}$') {
        throw "Fixture cleanup target is outside its temporary root: $cleanupPath"
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force -ErrorAction SilentlyContinue
}
