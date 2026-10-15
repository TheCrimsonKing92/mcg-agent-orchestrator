<#
Exit codes:
- 1: generic launch failure
- 2: conduct-loop launch refused because stop authority is active
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Name = "command",
    [string]$AppDll,
    [string]$Pipeline,
    [string]$RepositoryRoot,
    [string]$ExpectedStateDirectory,
    [switch]$DisposableFixture,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Arguments = @($Arguments)
if ($PSBoundParameters.ContainsKey('Pipeline')) {
    # Keep the orchestrator's --pipeline flag from binding to PowerShell's
    # common -PipelineVariable parameter instead of reaching the child CLI.
    $Arguments += @('--pipeline', $Pipeline)
}

$driveJournalSchemaVersion = 1

function ConvertTo-SafeName {
    param([string]$Value)
    $safe = ($Value.Trim().ToLowerInvariant().ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_)) { $_ } else { '-' }
    }) -join ''
    $safe = $safe.Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "command"
    }

    return $safe
}

function ConvertTo-CommandLineArgument {
    param([string]$Value)

    if ($null -eq $Value) {
        return '""'
    }

    if ($Value.Length -gt 0 -and $Value.IndexOfAny([char[]]@(' ', "`t", "`n", "`r", '"')) -lt 0) {
        return $Value
    }

    $quoted = [System.Text.StringBuilder]::new()
    [void]$quoted.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$quoted.Append('\', ($backslashes * 2) + 1)
            [void]$quoted.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$quoted.Append('\', $backslashes)
            $backslashes = 0
        }

        [void]$quoted.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$quoted.Append('\', $backslashes * 2)
    }

    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function Get-ArgumentValue {
    param(
        [string[]]$Values,
        [string]$Name
    )

    for ($i = 0; $i -lt $Values.Count; $i++) {
        if ($Values[$i].StartsWith($Name + '=', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $Values[$i].Substring($Name.Length + 1)
        }
        if ($Values[$i].Equals($Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            if (($i + 1) -lt $Values.Count) {
                return $Values[$i + 1]
            }

            return $null
        }
    }

    return $null
}

function Test-HasArgument {
    param(
        [string[]]$Values,
        [string]$Name
    )

    foreach ($value in $Values) {
        if ($value.Equals($Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-IsConductLoop {
    param([string[]]$Values)

    return $Values.Count -ge 2 -and
        $Values[0].Equals("conduct", [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-HasArgument -Values $Values -Name "--loop")
}

function Write-LastDriveJournal {
    param(
        [string]$RepositoryRoot,
        [string]$JournalPath,
        [string]$BatchName,
        [string]$ApplicationDll,
        [string[]]$CommandArguments
    )

    if (-not (Test-IsConductLoop -Values $CommandArguments)) {
        return
    }

    $orchestratorRoot = Split-Path -Parent $JournalPath
    New-Item -ItemType Directory -Force -Path $orchestratorRoot | Out-Null

    $journal = [ordered]@{
        schemaVersion = $driveJournalSchemaVersion
        writtenAt = (Get-Date).ToUniversalTime().ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
        repoRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
        name = $BatchName
        appDll = if ([string]::IsNullOrWhiteSpace($ApplicationDll)) { $null } else { [System.IO.Path]::GetFullPath($ApplicationDll) }
        arguments = @($CommandArguments)
        policy = Get-ArgumentValue -Values $CommandArguments -Name "--policy"
        pollSeconds = Get-ArgumentValue -Values $CommandArguments -Name "--poll-seconds"
        watchInterval = Get-ArgumentValue -Values $CommandArguments -Name "--watch-interval"
        maxDuration = Get-ArgumentValue -Values $CommandArguments -Name "--max-duration"
        maxIterations = Get-ArgumentValue -Values $CommandArguments -Name "--max-iterations"
        watch = Test-HasArgument -Values $CommandArguments -Name "--watch"
        daemon = Test-HasArgument -Values $CommandArguments -Name "--daemon"
    }

    $json = $journal | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $JournalPath -Value $json -Encoding UTF8
}

function Write-LaunchFailure {
    param(
        [string]$Reason,
        [string]$StdoutPath,
        [string]$StderrPath,
        [string[]]$LaunchArgs,
        [string]$Kind,
        [string]$StopFilePath
    )

    $errorPayload = [ordered]@{}
    if (-not [string]::IsNullOrWhiteSpace($Kind)) {
        $errorPayload.kind = $Kind
    }

    $errorPayload.reason = $Reason

    if (-not [string]::IsNullOrWhiteSpace($StopFilePath)) {
        $errorPayload.stopFilePath = $StopFilePath
    }

    if (-not [string]::IsNullOrWhiteSpace($StdoutPath)) {
        $errorPayload.stdoutPath = $StdoutPath
    }

    if (-not [string]::IsNullOrWhiteSpace($StderrPath)) {
        $errorPayload.stderrPath = $StderrPath
    }

    if ($null -ne $LaunchArgs) {
        $errorPayload.args = @($LaunchArgs)
    }

    [Console]::Error.WriteLine(($errorPayload | ConvertTo-Json -Compress))
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $repoRoot "mcg-orchestrator.cmd"
$stdoutPath = $null
$stderrPath = $null
$processArguments = @()
$failureKind = $null
$failureStopFilePath = $null
$exitCode = 1

try {
    $Arguments = @($Arguments | ForEach-Object { [string]$_ })
    if ($Arguments.Count -eq 0) {
        throw "Usage: .\scripts\Start-OrchestratorCommand.ps1 [-Name <name>] <orchestrator-args...>"
    }

    $usesLauncher = [string]::IsNullOrWhiteSpace($AppDll)
    $hasBinding = $PSBoundParameters.ContainsKey('RepositoryRoot') -or $PSBoundParameters.ContainsKey('ExpectedStateDirectory') -or $DisposableFixture -or
        @($Arguments | Where-Object { $_ -match '^--(repository-root|expected-state-directory|disposable-fixture)(=|$)' }).Count -gt 0
    $bindingArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) { $bindingArgs += @('--repository-root', $RepositoryRoot) }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedStateDirectory)) { $bindingArgs += @('--expected-state-directory', $ExpectedStateDirectory) }
    if ($DisposableFixture) { $bindingArgs += '--disposable-fixture' }
    if ($hasBinding -and $usesLauncher) {
        throw 'Workspace binding requires an explicit prebuilt -AppDll; build and select the compatible runtime before retrying.'
    }
    $dotnetPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH", "Process")
    if ([string]::IsNullOrWhiteSpace($dotnetPath)) { $dotnetPath = "dotnet" }
    $verifiedStateDirectory = $null
    $verifiedExecutionDirectory = $repoRoot
    $verifiedStopFile = $null
    if ($hasBinding) {
        if (-not (Test-Path -LiteralPath $AppDll -PathType Leaf)) { throw "Build the app before a bound launch: $AppDll" }
        # Ask the actual app's owning resolver; do not duplicate registry/tenant/relocation rules.
        $selectionArgs = @()
        for ($i = 0; $i -lt $Arguments.Count; $i++) {
            if ($Arguments[$i] -match '^--(project|tenant|repository-root|expected-state-directory)=') { $selectionArgs += $Arguments[$i] }
            elseif ($Arguments[$i] -in @('--project', '--tenant', '--repository-root', '--expected-state-directory')) {
                $selectionArgs += $Arguments[$i]
                if (++$i -ge $Arguments.Count) { throw 'Missing workspace selector value.' }
                $selectionArgs += $Arguments[$i]
            }
            elseif ($Arguments[$i] -eq '--disposable-fixture') { $selectionArgs += $Arguments[$i] }
        }
        $bindingJson = & $dotnetPath $AppDll '__workspace-binding-check' '--json' @selectionArgs @bindingArgs
        if ($LASTEXITCODE -ne 0) { throw 'Workspace binding prelaunch check refused; no launcher artifacts written.' }
        $verified = ($bindingJson -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
        $requestedSelectors = @($selectionArgs) + @($bindingArgs)
        $requestedRoot = Get-ArgumentValue -Values $requestedSelectors -Name '--repository-root'
        $requestedState = Get-ArgumentValue -Values $requestedSelectors -Name '--expected-state-directory'
        if ([string]::IsNullOrWhiteSpace([string]$verified.repositoryRoot) -or
            [string]::IsNullOrWhiteSpace([string]$verified.stateDirectory) -or
            [string]::IsNullOrWhiteSpace([string]$verified.executionDirectory)) { throw 'Malformed workspace binding receipt.' }
        foreach ($expectation in @(@($requestedRoot, [string]$verified.repositoryRoot), @($requestedState, [string]$verified.stateDirectory))) {
            if (-not [string]::IsNullOrWhiteSpace($expectation[0]) -and
                -not [string]::Equals([System.IO.Path]::GetFullPath($expectation[0]).TrimEnd('\', '/'),
                    [System.IO.Path]::GetFullPath($expectation[1]).TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'Workspace binding receipt differs from the explicit expectation.'
            }
        }
        $verifiedStateDirectory = [string]$verified.stateDirectory
        $verifiedExecutionDirectory = [string]$verified.executionDirectory
        $verifiedStopFile = [string]$verified.stopFile
        $Arguments += $bindingArgs
    }
    $targetExecutable = if ($usesLauncher) {
        $launcher
    } else {
        [System.IO.Path]::GetFullPath($AppDll)
    }
    $processArguments = @($targetExecutable) + $Arguments

    if (Test-IsConductLoop -Values $Arguments) {
        $configuredRepositoryRoot = [Environment]::GetEnvironmentVariable(
            "MCG_ORCHESTRATOR_REPOSITORY_ROOT",
            "Process")
        $effectiveRepositoryRoot = if ($usesLauncher -or [string]::IsNullOrWhiteSpace($configuredRepositoryRoot)) {
            $repoRoot
        } else {
            [System.IO.Path]::GetFullPath($configuredRepositoryRoot)
        }
        $stopFilePath = if ($null -ne $verifiedStopFile) { $verifiedStopFile } else { [System.IO.Path]::GetFullPath((Join-Path $effectiveRepositoryRoot ".conduct-stop")) }
        if (Test-Path -LiteralPath $stopFilePath -ErrorAction Stop) {
            $failureKind = "stop-authority-refused"
            $failureStopFilePath = $stopFilePath
            $exitCode = 2
            throw "Conduct loop launch refused because active stop authority exists at '$stopFilePath'. Deliberately remove the stop file before retrying."
        }
    }

    $logsRoot = if ($null -ne $verifiedStateDirectory) { Join-Path $verifiedStateDirectory 'logs' } else { Join-Path $repoRoot ".orchestrator\logs" }
    New-Item -ItemType Directory -Force -Path $logsRoot | Out-Null

    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $safeName = ConvertTo-SafeName $Name
    $stdoutPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.out.log"))
    $stderrPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.err.log"))

    if (-not (Test-Path -LiteralPath $targetExecutable -PathType Leaf)) {
        throw "Launcher or App DLL not found: $targetExecutable"
    }

    $processFilePath = $dotnetPath
    $launchArguments = $processArguments
    if ($usesLauncher) {
        $processFilePath = $env:ComSpec
        if ([string]::IsNullOrWhiteSpace($processFilePath)) {
            $processFilePath = "cmd.exe"
        }

        $launchArguments = @("/d", "/c") + $processArguments
    }

    $processArgumentLine = ($launchArguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " "

    $journalPath = if ($null -ne $verifiedStateDirectory) { Join-Path $verifiedStateDirectory 'last-drive.json' } else { Join-Path $repoRoot ".orchestrator\last-drive.json" }
    Write-LastDriveJournal `
        -RepositoryRoot $verifiedExecutionDirectory `
        -JournalPath $journalPath `
        -BatchName $Name `
        -ApplicationDll $AppDll `
        -CommandArguments $Arguments

    $previousStdoutLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", "Process")
    $previousStderrLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", "Process")
    $previousBatchName = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", "Process")
    $startedAt = (Get-Date).ToUniversalTime().ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $stdoutPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $stderrPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", $Name, "Process")

    try {
        $process = Start-Process `
            -WindowStyle Hidden `
            -PassThru `
            -FilePath $processFilePath `
            -WorkingDirectory $verifiedExecutionDirectory `
            -ArgumentList $processArgumentLine `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath
    }
    finally {
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $previousStdoutLogPath, "Process")
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $previousStderrLogPath, "Process")
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", $previousBatchName, "Process")
    }

    $requestKey = $null
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        if ($Arguments[$index] -eq "--request-key" -and $index + 1 -lt $Arguments.Count) {
            $requestKey = $Arguments[$index + 1]
            break
        }
        if ($Arguments[$index].StartsWith("--request-key=", [System.StringComparison]::OrdinalIgnoreCase)) {
            $requestKey = $Arguments[$index].Substring("--request-key=".Length)
            break
        }
    }

    $receipt = [ordered]@{
        pid = [int]$process.Id
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        args = @($processArguments)
        startedAt = $startedAt
    }
    if (-not [string]::IsNullOrWhiteSpace($requestKey)) {
        $receipt["requestKey"] = $requestKey
    }
    [pscustomobject]$receipt | ConvertTo-Json -Compress
}
catch {
    Write-LaunchFailure `
        -Reason $_.Exception.Message `
        -StdoutPath $stdoutPath `
        -StderrPath $stderrPath `
        -LaunchArgs $processArguments `
        -Kind $failureKind `
        -StopFilePath $failureStopFilePath
    exit $exitCode
}
