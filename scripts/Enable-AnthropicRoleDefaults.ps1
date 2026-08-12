[CmdletBinding()]
param(
    [DateTimeOffset] $NotBeforeUtc = [DateTimeOffset]::MinValue,
    [string] $ReceiptPath = '.orchestrator/logs/anthropic-role-defaults-receipt.json',
    [switch] $Schedule,
    [switch] $ReplaceExistingSchedule
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agentsPath = Join-Path $repoRoot '.orchestrator/agents.json'
$modelFunctionsPath = Join-Path $repoRoot '.orchestrator/model-functions.json'
$workersPath = Join-Path $repoRoot '.orchestrator/workers.json'
$resolvedReceiptPath = if ([System.IO.Path]::IsPathRooted($ReceiptPath)) {
    [System.IO.Path]::GetFullPath($ReceiptPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ReceiptPath))
}
$repoPrefix = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedReceiptPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Receipt path must remain under the repository root: $resolvedReceiptPath"
}

function Write-ProgressLog([string] $Message) {
    $line = '{0} {1}' -f [DateTimeOffset]::UtcNow.ToString('O'), $Message
    Write-Output $line
}

if ($Schedule) {
    $scriptPath = [System.IO.Path]::GetFullPath($PSCommandPath)
    $existing = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ProcessId -ne $PID -and
        $null -ne $_.CommandLine -and
        $_.CommandLine.Contains($scriptPath, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($existing.Count -gt 0) {
        if (-not $ReplaceExistingSchedule) {
            Write-ProgressLog ('ANTHROPIC_ROLE_DEFAULTS_ALREADY_SCHEDULED pids={0} target={1}' -f
                (($existing.ProcessId | Sort-Object) -join ','),
                $NotBeforeUtc.ToUniversalTime().ToString('O'))
            return
        }

        foreach ($scheduledProcess in $existing) {
            Stop-Process -Id $scheduledProcess.ProcessId -ErrorAction Stop
            Wait-Process -Id $scheduledProcess.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
        }
        Write-ProgressLog ('ANTHROPIC_ROLE_DEFAULTS_REPLACED_SCHEDULE pids={0}' -f
            (($existing.ProcessId | Sort-Object) -join ','))
    }

    $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
    $logRoot = Join-Path $repoRoot '.orchestrator/logs'
    [System.IO.Directory]::CreateDirectory($logRoot) | Out-Null
    $stdoutPath = Join-Path $logRoot 'anthropic-role-defaults-scheduler.out.log'
    $stderrPath = Join-Path $logRoot 'anthropic-role-defaults-scheduler.err.log'
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $scriptPath,
        '-NotBeforeUtc', $NotBeforeUtc.ToUniversalTime().ToString('O'),
        '-ReceiptPath', $resolvedReceiptPath)
    $process = Start-Process `
        -FilePath $pwshPath `
        -ArgumentList $arguments `
        -WorkingDirectory $repoRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    Start-Sleep -Seconds 2
    if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
        throw 'Scheduled Anthropic role-default process exited before its wait loop was established.'
    }
    Write-ProgressLog ('ANTHROPIC_ROLE_DEFAULTS_SCHEDULED pid={0} target={1} stdout={2} stderr={3}' -f
        $process.Id,
        $NotBeforeUtc.ToUniversalTime().ToString('O'),
        $stdoutPath,
        $stderrPath)
    return
}

while ([DateTimeOffset]::UtcNow -lt $NotBeforeUtc) {
    $remaining = $NotBeforeUtc - [DateTimeOffset]::UtcNow
    $sleepSeconds = [Math]::Min(30, [Math]::Max(1, [Math]::Ceiling($remaining.TotalSeconds)))
    Start-Sleep -Seconds $sleepSeconds
}

foreach ($requiredPath in @($agentsPath, $modelFunctionsPath, $workersPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required orchestrator catalog is missing: $requiredPath"
    }
}

$claude = Get-Command claude -ErrorAction Stop
$workers = Get-Content -LiteralPath $workersPath -Raw | ConvertFrom-Json
$claudeProfiles = @($workers.Profiles | Where-Object { $_.Name -eq 'claude-cli' })
if ($claudeProfiles.Count -ne 1 -or
    -not ([string] $claudeProfiles[0].CommandTemplate).Contains('{subscriptionModelName}', [System.StringComparison]::Ordinal) -or
    -not ([string] $claudeProfiles[0].CommandTemplate).Contains('{permissionMode}', [System.StringComparison]::Ordinal)) {
    throw 'claude-cli must exist exactly once and retain model plus permission-mode placeholders.'
}

$agents = Get-Content -LiteralPath $agentsPath -Raw | ConvertFrom-Json
$modelFunctions = Get-Content -LiteralPath $modelFunctionsPath -Raw | ConvertFrom-Json
$cheapJudgesBefore = @($modelFunctions.Bindings | Where-Object {
    $_.Purpose -eq 'acceptance-judge' -and $_.Lane -eq 'CheapApi'
})
if ($cheapJudgesBefore.Count -ne 1) {
    throw 'The existing cheap acceptance-judge binding is missing or ambiguous.'
}
$cheapJudgeBeforeJson = $cheapJudgesBefore[0] | ConvertTo-Json -Depth 20 -Compress
$opusTemplate = @($agents.Agents | Where-Object {
    $_.Id.Value -eq 'anthropic-researcher-opus-5' -and
    $_.Subscription.WorkerProfileName -eq 'claude-cli' -and
    $_.Subscription.ModelAlias -eq 'claude-opus-5'
})
if ($opusTemplate.Count -ne 1) {
    throw 'The reviewed Anthropic Opus 5 agent template is missing or ambiguous.'
}

$reviewer = $opusTemplate[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$reviewer.Id.Value = 'anthropic-reviewer'
$reviewer.Name = 'Opus 5 Reviewer'
$reviewer.Role = 'Reviewer'
$reviewer.Model.ModelName = 'claude-opus-5'
$reviewer.Model.ReasoningEffort = 'high'
$reviewer.ComplexModel.ModelName = 'claude-opus-5'
$reviewer.ComplexModel.ReasoningEffort = 'high'
$reviewer.Status = 'Available'
$reviewer.Subscription.ReasoningEffort = 'high'

$remainingAgents = @($agents.Agents | Where-Object { $_.Id.Value -ne 'anthropic-reviewer' })
$firstReviewer = -1
for ($index = 0; $index -lt $remainingAgents.Count; $index++) {
    if ($remainingAgents[$index].Role -eq 'Reviewer') {
        $firstReviewer = $index
        break
    }
}
if ($firstReviewer -lt 0) {
    $agents.Agents = @($remainingAgents) + @($reviewer)
} else {
    $before = if ($firstReviewer -gt 0) { @($remainingAgents[0..($firstReviewer - 1)]) } else { @() }
    $after = @($remainingAgents[$firstReviewer..($remainingAgents.Count - 1)])
    $agents.Agents = @($before) + @($reviewer) + @($after)
}

$sonnetRefiner = [pscustomobject][ordered]@{
    Purpose = 'spec-refiner'
    Lane = 'Capable'
    Model = [pscustomobject][ordered]@{
        ProviderName = 'Anthropic'
        ModelName = 'sonnet'
        Capabilities = 'Text'
        SubscriptionMode = 'ApiKey'
        ReasoningEffort = $null
        MaxOutputTokens = $null
    }
    Name = 'sonnet'
    Subscription = [pscustomobject][ordered]@{
        WorkerProfileName = 'claude-cli'
        ModelAlias = 'sonnet'
        ReasoningEffort = $null
    }
}

$newBindings = [System.Collections.Generic.List[object]]::new()
$refinerInserted = $false
foreach ($binding in @($modelFunctions.Bindings)) {
    if ($binding.Purpose -eq 'spec-refiner') {
        if (-not $refinerInserted) {
            $newBindings.Add($sonnetRefiner)
            $refinerInserted = $true
        }
        continue
    }
    $newBindings.Add($binding)
}
if (-not $refinerInserted) { $newBindings.Add($sonnetRefiner) }
$modelFunctions.Bindings = @($newBindings)

$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupRoot = Join-Path $repoRoot ".orchestrator/config-switch-backups/anthropic-$stamp"
[System.IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$agentsBackup = Join-Path $backupRoot 'agents.json'
$modelFunctionsBackup = Join-Path $backupRoot 'model-functions.json'
Copy-Item -LiteralPath $agentsPath -Destination $agentsBackup -ErrorAction Stop
Copy-Item -LiteralPath $modelFunctionsPath -Destination $modelFunctionsBackup -ErrorAction Stop

$agentsTemp = "$agentsPath.$PID.tmp"
$modelFunctionsTemp = "$modelFunctionsPath.$PID.tmp"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($agentsTemp, (($agents | ConvertTo-Json -Depth 20) + [Environment]::NewLine), $utf8NoBom)
[System.IO.File]::WriteAllText($modelFunctionsTemp, (($modelFunctions | ConvertTo-Json -Depth 20) + [Environment]::NewLine), $utf8NoBom)

try {
    [System.IO.File]::Move($agentsTemp, $agentsPath, $true)
    [System.IO.File]::Move($modelFunctionsTemp, $modelFunctionsPath, $true)

    $writtenAgents = Get-Content -LiteralPath $agentsPath -Raw | ConvertFrom-Json
    $writtenFunctions = Get-Content -LiteralPath $modelFunctionsPath -Raw | ConvertFrom-Json
    $reviewers = @($writtenAgents.Agents | Where-Object { $_.Role -eq 'Reviewer' })
    $primaryReviewer = $reviewers | Select-Object -First 1
    $refiners = @($writtenFunctions.Bindings | Where-Object { $_.Purpose -eq 'spec-refiner' })
    $cheapJudges = @($writtenFunctions.Bindings | Where-Object {
        $_.Purpose -eq 'acceptance-judge' -and $_.Lane -eq 'CheapApi'
    })
    if ($primaryReviewer.Id.Value -ne 'anthropic-reviewer' -or
        $primaryReviewer.Model.ModelName -ne 'claude-opus-5' -or
        $primaryReviewer.ComplexModel.ModelName -ne 'claude-opus-5' -or
        $primaryReviewer.Subscription.ModelAlias -ne 'claude-opus-5' -or
        $refiners.Count -ne 1 -or
        $refiners[0].Subscription.ModelAlias -ne 'sonnet' -or
        $cheapJudges.Count -ne 1 -or
        ($cheapJudges[0] | ConvertTo-Json -Depth 20 -Compress) -cne $cheapJudgeBeforeJson) {
        throw 'Post-write Anthropic role-default validation failed.'
    }

    $receiptDirectory = Split-Path -Parent $resolvedReceiptPath
    [System.IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
    $receipt = [pscustomobject][ordered]@{
        schema = 'mcg-anthropic-role-defaults-v1'
        appliedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        notBeforeUtc = $NotBeforeUtc.ToUniversalTime().ToString('O')
        repositoryRoot = $repoRoot
        claudeExecutable = $claude.Source
        reviewer = 'Anthropic/claude-opus-5'
        specRefiner = 'Anthropic/sonnet alias (Sonnet 5 required)'
        cheapAcceptanceJudge = 'Unchanged'
        implementationRoleDefaults = 'OpenAI unchanged'
        anthropicImplementationAlternates = 'Offline unchanged'
        inFlightAssignments = 'Unchanged; only future role resolution uses the new defaults'
        backupDirectory = $backupRoot
        agentsSha256 = (Get-FileHash -LiteralPath $agentsPath -Algorithm SHA256).Hash
        modelFunctionsSha256 = (Get-FileHash -LiteralPath $modelFunctionsPath -Algorithm SHA256).Hash
    }
    [System.IO.File]::WriteAllText(
        $resolvedReceiptPath,
        (($receipt | ConvertTo-Json -Depth 10) + [Environment]::NewLine),
        $utf8NoBom)
    Write-ProgressLog "ANTHROPIC_ROLE_DEFAULTS_APPLIED receipt=$resolvedReceiptPath"
} catch {
    Copy-Item -LiteralPath $agentsBackup -Destination $agentsPath -Force -ErrorAction Stop
    Copy-Item -LiteralPath $modelFunctionsBackup -Destination $modelFunctionsPath -Force -ErrorAction Stop
    throw
} finally {
    Remove-Item -LiteralPath $agentsTemp -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $modelFunctionsTemp -Force -ErrorAction SilentlyContinue
}
