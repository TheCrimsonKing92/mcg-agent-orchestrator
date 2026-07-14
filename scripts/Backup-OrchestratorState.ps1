<#
.SYNOPSIS
  Create a consistent backup of the local orchestrator state directory.

.DESCRIPTION
  SQLite databases are captured with sqlite3's .backup command instead of a live
  file copy. Non-database state is copied into the same staging tree and archived
  to a timestamped zip outside the repository by default.
#>
param(
    [string]$StateRoot,
    [string]$BackupRoot,
    [int]$KeepDaily = 7,
    [int]$KeepWeekly = 4,
    [switch]$Install,
    [string]$TaskName = "Mcg Orchestrator State Backup",
    [string]$DailyAt = "03:20",
    [switch]$VerifyRestore,
    [string]$RestoreScratchRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Find-Sqlite3 {
    $command = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "sqlite3 was not found on PATH. Install SQLite CLI so this script can use the SQLite backup API."
    }

    return $command.Source
}

function Quote-SqliteIdentifier {
    param([Parameter(Mandatory = $true)][string]$Name)
    return '"' + $Name.Replace('"', '""') + '"'
}

function Invoke-Sqlite {
    param(
        [Parameter(Mandatory = $true)][string]$SqlitePath,
        [Parameter(Mandatory = $true)][string]$DatabasePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & $SqlitePath $DatabasePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 failed for $DatabasePath with exit code $LASTEXITCODE."
    }

    return @($output)
}

function Backup-SqliteDatabase {
    param(
        [Parameter(Mandatory = $true)][string]$SqlitePath,
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $destinationDirectory = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $backupCommand = ".backup '$($DestinationPath.Replace("'", "''"))'"
    Invoke-Sqlite -SqlitePath $SqlitePath -DatabasePath $SourcePath -Arguments @(".timeout 5000", $backupCommand) | Out-Null
}

function Get-TableRowCounts {
    param(
        [Parameter(Mandatory = $true)][string]$SqlitePath,
        [Parameter(Mandatory = $true)][string]$DatabasePath
    )

    $tables = Invoke-Sqlite -SqlitePath $SqlitePath -DatabasePath $DatabasePath -Arguments @(
        "-readonly",
        "-noheader",
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
    )

    $counts = [ordered]@{}
    foreach ($table in $tables) {
        if ([string]::IsNullOrWhiteSpace($table)) {
            continue
        }

        $quotedName = Quote-SqliteIdentifier -Name $table
        $count = Invoke-Sqlite -SqlitePath $SqlitePath -DatabasePath $DatabasePath -Arguments @(
            "-readonly",
            "-noheader",
            "SELECT COUNT(*) FROM $quotedName;"
        )
        $counts[$table] = [int64]$count[0]
    }

    return $counts
}

function Copy-IfPresent {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return
    }

    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function New-OrchestratorStateBackup {
    param(
        [Parameter(Mandatory = $true)][string]$StateRootPath,
        [Parameter(Mandatory = $true)][string]$BackupRootPath,
        [Parameter(Mandatory = $true)][string]$SqlitePath,
        [Parameter(Mandatory = $true)][string]$ScratchRootPath,
        [switch]$Verify
    )

    if (-not (Test-Path -LiteralPath $StateRootPath -PathType Container)) {
        throw "State root not found: $StateRootPath"
    }

    New-Item -ItemType Directory -Force -Path $BackupRootPath | Out-Null
    New-Item -ItemType Directory -Force -Path $ScratchRootPath | Out-Null

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stagingRoot = Join-Path $ScratchRootPath "mcg-orchestrator-state-$timestamp"
    $archivePath = Join-Path $BackupRootPath "mcg-orchestrator-state-$timestamp.zip"
    $payloadRoot = Join-Path $stagingRoot ".orchestrator"
    New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null

    $manifest = [ordered]@{
        createdAt = (Get-Date).ToString("o")
        stateRoot = $StateRootPath
        sqliteBackup = "sqlite3 .backup"
        databases = [ordered]@{}
        files = @()
        directories = @()
    }

    $databaseFiles = Get-ChildItem -LiteralPath $StateRootPath -Filter "*.db" -File | Sort-Object Name
    foreach ($databaseFile in $databaseFiles) {
        $destination = Join-Path $payloadRoot $databaseFile.Name
        Backup-SqliteDatabase -SqlitePath $SqlitePath -SourcePath $databaseFile.FullName -DestinationPath $destination
        $manifest.databases[$databaseFile.Name] = [ordered]@{
            bytes = (Get-Item -LiteralPath $destination).Length
            rowCounts = Get-TableRowCounts -SqlitePath $SqlitePath -DatabasePath $destination
        }
    }

    $fileNames = @(
        "agents.json",
        "workers.json",
        "landing-escalations.json",
        "model-functions.json",
        "operator-channel.json",
        "operator-decisions.json",
        "operator-inbox-acks.json",
        "semantic-acceptance.jsonl",
        "spec-refiner-precedents.json",
        "terminal-goal-sweep-cache.json"
    )

    foreach ($fileName in $fileNames) {
        $source = Join-Path $StateRootPath $fileName
        $destination = Join-Path $payloadRoot $fileName
        Copy-IfPresent -Source $source -Destination $destination
        if (Test-Path -LiteralPath $destination) {
            $manifest.files += $fileName
        }
    }

    $directoryNames = @("events", "goal-events", "goal-operations", "logs", "prompts", "rollback")
    foreach ($directoryName in $directoryNames) {
        $source = Join-Path $StateRootPath $directoryName
        $destination = Join-Path $payloadRoot $directoryName
        Copy-IfPresent -Source $source -Destination $destination
        if (Test-Path -LiteralPath $destination) {
            $manifest.directories += $directoryName
        }
    }

    $manifestPath = Join-Path $stagingRoot "manifest.json"
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    $stagedItems = @(Get-ChildItem -LiteralPath $stagingRoot -Force | ForEach-Object { $_.FullName })
    Compress-Archive -LiteralPath $stagedItems -DestinationPath $archivePath -CompressionLevel Optimal

    $receipt = [ordered]@{
        archive = $archivePath
        manifest = $manifestPath
        databases = @($databaseFiles | ForEach-Object { $_.Name })
        verifiedRestore = $false
        restoredTo = $null
    }

    if ($Verify) {
        $restoreRoot = Join-Path $ScratchRootPath "restore-$timestamp"
        Expand-Archive -LiteralPath $archivePath -DestinationPath $restoreRoot -Force
        $restoredPayload = Join-Path $restoreRoot ".orchestrator"
        foreach ($databaseName in $manifest.databases.Keys) {
            $restoredDatabase = Join-Path $restoredPayload $databaseName
            $restoredCounts = Get-TableRowCounts -SqlitePath $SqlitePath -DatabasePath $restoredDatabase
            foreach ($tableName in $manifest.databases[$databaseName].rowCounts.Keys) {
                if ($restoredCounts[$tableName] -ne $manifest.databases[$databaseName].rowCounts[$tableName]) {
                    throw "Restore verification failed for $databaseName table $tableName."
                }
            }
        }

        $receipt.verifiedRestore = $true
        $receipt.restoredTo = $restoreRoot
    }

    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    return $receipt
}

function Invoke-Retention {
    param(
        [Parameter(Mandatory = $true)][string]$BackupRootPath,
        [int]$Daily,
        [int]$Weekly
    )

    $archives = @(Get-ChildItem -LiteralPath $BackupRootPath -Filter "mcg-orchestrator-state-*.zip" -File | Sort-Object LastWriteTime -Descending)
    if ($archives.Count -eq 0) {
        return @()
    }

    $keep = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($archive in ($archives | Group-Object { $_.LastWriteTime.Date } | Sort-Object { $_.Group[0].LastWriteTime } -Descending | Select-Object -First $Daily)) {
        $keep.Add($archive.Group[0].FullName) | Out-Null
    }

    $calendar = [System.Globalization.CultureInfo]::InvariantCulture.Calendar
    foreach ($archive in ($archives | Group-Object {
            $week = $calendar.GetWeekOfYear($_.LastWriteTime, [System.Globalization.CalendarWeekRule]::FirstFourDayWeek, [DayOfWeek]::Monday)
            "{0:D4}-W{1:D2}" -f $_.LastWriteTime.Year, $week
        } | Sort-Object { $_.Group[0].LastWriteTime } -Descending | Select-Object -First $Weekly)) {
        $keep.Add($archive.Group[0].FullName) | Out-Null
    }

    $removed = @()
    foreach ($archive in $archives) {
        if (-not $keep.Contains($archive.FullName)) {
            Remove-Item -LiteralPath $archive.FullName -Force
            $removed += $archive.FullName
        }
    }

    return $removed
}

function Install-BackupTask {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$StateRootPath,
        [Parameter(Mandatory = $true)][string]$BackupRootPath,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$At,
        [int]$Daily,
        [int]$Weekly
    )

    $runAt = [DateTime]::ParseExact($At, "HH:mm", [System.Globalization.CultureInfo]::InvariantCulture)
    $powerShellPath = (Get-Process -Id $PID).Path
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $ScriptPath),
        "-StateRoot", ('"{0}"' -f $StateRootPath),
        "-BackupRoot", ('"{0}"' -f $BackupRootPath),
        "-KeepDaily", $Daily,
        "-KeepWeekly", $Weekly,
        "-VerifyRestore"
    ) -join " "

    $action = New-ScheduledTaskAction -Execute $powerShellPath -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -Daily -At $runAt
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask -TaskName $Name -Action $action -Trigger $trigger -Settings $settings -Description "Back up mcg orchestrator state with SQLite-consistent snapshots." -Force | Out-Null
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $StateRoot = Join-Path $repoRoot ".orchestrator"
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $env:USERPROFILE "backups\mcg-orchestrator"
}

if ([string]::IsNullOrWhiteSpace($RestoreScratchRoot)) {
    $RestoreScratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) "mcg-orchestrator-state-backup"
}

$stateRootPath = Resolve-FullPath -Path $StateRoot
$backupRootPath = Resolve-FullPath -Path $BackupRoot
$scratchRootPath = Resolve-FullPath -Path $RestoreScratchRoot

if ($Install) {
    Install-BackupTask -ScriptPath $PSCommandPath -StateRootPath $stateRootPath -BackupRootPath $backupRootPath -Name $TaskName -At $DailyAt -Daily $KeepDaily -Weekly $KeepWeekly
    Write-Output "installed task=$TaskName dailyAt=$DailyAt stateRoot=$stateRootPath backupRoot=$backupRootPath"
    return
}

$sqlitePath = Find-Sqlite3
$receipt = New-OrchestratorStateBackup -StateRootPath $stateRootPath -BackupRootPath $backupRootPath -SqlitePath $sqlitePath -ScratchRootPath $scratchRootPath -Verify:$VerifyRestore
$removed = @(Invoke-Retention -BackupRootPath $backupRootPath -Daily $KeepDaily -Weekly $KeepWeekly)

Write-Output ("archive={0}" -f $receipt.archive)
Write-Output ("databases={0}" -f (($receipt.databases | Sort-Object) -join ","))
Write-Output ("verifiedRestore={0}" -f $receipt.verifiedRestore)
if ($receipt.restoredTo) {
    Write-Output ("restoredTo={0}" -f $receipt.restoredTo)
}
Write-Output ("retentionRemoved={0}" -f $removed.Count)
