param(
    [string]$DashboardUrl = "http://localhost:5087/",
    [string]$Solution = "Mcg.AgentOrchestrator.sln",
    [int]$HealthTimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Join-Url {
    param([string]$Base, [string]$Path)
    if (-not $Base.EndsWith("/")) {
        $Base = "$Base/"
    }

    return "$Base$($Path.TrimStart('/'))"
}

function Invoke-DashboardJson {
    param([string]$Path, [string]$Method = "GET")
    $uri = Join-Url -Base $DashboardUrl -Path $Path
    $response = Invoke-WebRequest -UseBasicParsing -Method $Method -Uri $uri
    return $response.Content | ConvertFrom-Json
}

function Wait-ProcessExit {
    param([int[]]$ProcessIds, [int]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    foreach ($processId in $ProcessIds | Sort-Object -Unique) {
        while ((Get-Date) -lt $deadline) {
            $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($null -eq $process) {
                break
            }

            Start-Sleep -Milliseconds 250
        }

        if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
            throw "Process $processId did not exit within $TimeoutSeconds seconds."
        }
    }
}

function Stop-ExactProcesses {
    param([int[]]$ProcessIds)
    foreach ($processId in $ProcessIds | Sort-Object -Unique) {
        if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
            Stop-Process -Id $processId
        }
    }
}

function Start-RestartCommand {
    param([string]$RestartCommand)
    $match = [regex]::Match(
        $RestartCommand,
        "^(?<file>\S+)\s+(?<command>\S+)\s+(?<url>\S+)(?:\s+--refresh\s+(?<refresh>\d+))?(?<noOpen>\s+--no-open)?$")
    if (-not $match.Success) {
        throw "Restart command format is not supported: $RestartCommand"
    }

    $arguments = @($match.Groups["command"].Value, $match.Groups["url"].Value)
    if ($match.Groups["refresh"].Success) {
        $arguments += @("--refresh", $match.Groups["refresh"].Value)
    }

    if ($match.Groups["noOpen"].Success) {
        $arguments += "--no-open"
    }

    Start-Process -FilePath $match.Groups["file"].Value -ArgumentList $arguments -WindowStyle Hidden
}

function Wait-DashboardHealth {
    param([int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $healthUri = Join-Url -Base $DashboardUrl -Path "health"
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUri
            if ($response.StatusCode -eq 200 -and $response.Content -eq "ok") {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    throw "Dashboard did not become healthy at $healthUri within $TimeoutSeconds seconds."
}

function New-IsolatedDotnetArguments {
    $runRoot = Join-Path ([System.IO.Path]::GetTempPath()) "mcg-dotnet-isolated\runs\dashboard-$PID"
    $artifactsPath = Join-Path $runRoot "artifacts"
    New-Item -ItemType Directory -Force -Path $artifactsPath | Out-Null
    $maxCpuCount = 0
    if (-not ([int]::TryParse($env:MCG_BUILD_MAXCPUCOUNT, [ref]$maxCpuCount)) -or $maxCpuCount -le 1) {
        $maxCpuCount = [Math]::Max(2, [int]([Environment]::ProcessorCount / 4))
    }

    [pscustomobject]@{
        RunRoot = $runRoot
        ArtifactsPath = $artifactsPath
        Arguments = @(
            "--property:McgIsolatedArtifactsPath=$artifactsPath",
            "-maxcpucount:$maxCpuCount"
        )
    }
}

$plan = Invoke-DashboardJson -Path "api/system/build-test-cleanup"
$stopResult = Invoke-DashboardJson -Path $plan.StopCurrentUrl -Method "POST"

$siblingIds = @()
foreach ($sibling in @($plan.SiblingProcesses) + @($stopResult.SiblingProcesses)) {
    if ($null -ne $sibling.ProcessId) {
        $siblingIds += [int]$sibling.ProcessId
    }
}

Wait-ProcessExit -ProcessIds @([int]$plan.CurrentProcessId, [int]$stopResult.ProcessId)
Stop-ExactProcesses -ProcessIds $siblingIds
Wait-ProcessExit -ProcessIds $siblingIds

$remaining = Get-Process Mcg.AgentOrchestrator.App -ErrorAction SilentlyContinue
if ($remaining) {
    $remainingIds = ($remaining | Select-Object -ExpandProperty Id) -join ", "
    throw "Dashboard app processes are still running after exact cleanup: $remainingIds"
}

$env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = (Get-Location).Path

$buildIsolation = New-IsolatedDotnetArguments
$isolatedArguments = $buildIsolation.Arguments
$cycleSucceeded = $false

try {
    dotnet build $Solution --no-restore --verbosity minimal @isolatedArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $testIsolationArguments = @(
        "--property:McgIsolatedArtifactsPath=$($buildIsolation.ArtifactsPath)",
        "--property:BuildInParallel=false"
    )
    dotnet test --solution $Solution --no-build --verbosity minimal @testIsolationArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }

    Start-RestartCommand -RestartCommand $plan.RestartCommand
    Wait-DashboardHealth -TimeoutSeconds $HealthTimeoutSeconds

    $finalPlan = Invoke-DashboardJson -Path "api/system/build-test-cleanup"
    $cycleSucceeded = $true
    [pscustomobject]@{
        BuildSucceeded = $true
        TestSucceeded = $true
        RestartCommand = $plan.RestartCommand
        CurrentProcessId = $finalPlan.CurrentProcessId
        CurrentListeningPorts = $finalPlan.CurrentListeningPorts
        SiblingProcessCount = @($finalPlan.SiblingProcesses).Count
    }
}
finally {
    dotnet build-server shutdown *> $null
    if ($cycleSucceeded) {
        Remove-Item -LiteralPath $buildIsolation.RunRoot -Force -Recurse -ErrorAction SilentlyContinue
    }
}
