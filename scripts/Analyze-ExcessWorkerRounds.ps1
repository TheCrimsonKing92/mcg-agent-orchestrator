<#
.SYNOPSIS
Builds a bounded, read-only excess-worker-round dataset from an explicit frozen manifest.

.DESCRIPTION
The script never discovers operator logs or journals. Every input file must be named in a
validated manifest or have an expected SHA-256. Missing files, changed hashes, post-cutoff
operator logs, ambiguous journal joins, and malformed week bounds fail closed. Git history
is read only through an explicit immutable revision.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OperatorLogManifest,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$OperatorLogManifestDigestSha256,
    [Parameter(Mandatory)][string]$JournalRoot,
    [Parameter(Mandatory)][string]$JournalManifest,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$JournalManifestDigestSha256,
    [Parameter(Mandatory)][string]$DogfoodDbPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$DogfoodDbSha256,
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$DogfoodWalSha256,
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$RepositoryRevision,
    [Parameter(Mandatory)][ValidatePattern('^\d{4}-W\d{2}$')][string]$StartWeek,
    [Parameter(Mandatory)][ValidatePattern('^\d{4}-W\d{2}$')][string]$EndWeek,
    [Parameter(Mandatory)][string]$TimeZoneId,
    [Parameter(Mandatory)][datetimeoffset]$CutoffUtc,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$TaskMetadataSnapshot,
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$TaskMetadataSnapshotSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-RequiredPath([string]$Path, [string]$Name, [switch]$Container) {
    if (-not (Test-Path -LiteralPath $Path -PathType $(if ($Container) { 'Container' } else { 'Leaf' }))) {
        throw "$Name does not exist: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-WeekOrdinal([string]$Week) {
    $parts = $Week -split '-W'
    $year = [int]$parts[0]
    $number = [int]$parts[1]
    if ($number -lt 1 -or $number -gt [System.Globalization.ISOWeek]::GetWeeksInYear($year)) {
        throw "Invalid ISO week: $Week"
    }
    return ($year * 100) + $number
}

function Get-IsoWeek([datetimeoffset]$Utc, [TimeZoneInfo]$Zone) {
    $local = [TimeZoneInfo]::ConvertTime($Utc, $Zone)
    $year = [System.Globalization.ISOWeek]::GetYear($local.DateTime)
    $week = [System.Globalization.ISOWeek]::GetWeekOfYear($local.DateTime)
    return ('{0}-W{1:D2}' -f $year, $week)
}

function Get-Median($Values) {
    $items = @($Values | Sort-Object)
    if ($items.Count -eq 0) { return $null }
    if (($items.Count % 2) -eq 1) { return [double]$items[[int](($items.Count - 1) / 2)] }
    return [Math]::Round(([double]$items[$items.Count / 2 - 1] + [double]$items[$items.Count / 2]) / 2, 3)
}

function Get-Percentile($Values, [double]$Probability) {
    $items = @($Values | Sort-Object)
    if ($items.Count -eq 0) { return $null }
    $index = [Math]::Floor(($items.Count - 1) * $Probability)
    return [double]$items[$index]
}

function Get-WeightedMedian($Pairs) {
    $items = @($Pairs | Sort-Object Value)
    if ($items.Count -eq 0) { return $null }
    $total = ($items | Measure-Object -Property Weight -Sum).Sum
    $running = 0.0
    foreach ($item in $items) {
        $running += $item.Weight
        if ($running -ge ($total / 2.0)) { return [double]$item.Value }
    }
    return [double]$items[-1].Value
}

function Get-BootstrapMedianInterval($Values, [int]$Seed) {
    $items = @($Values)
    if ($items.Count -eq 0) { return [ordered]@{ low = $null; high = $null } }
    $random = [Random]::new($Seed)
    $medians = [System.Collections.Generic.List[double]]::new()
    for ($sample = 0; $sample -lt 2000; $sample++) {
        $draw = for ($i = 0; $i -lt $items.Count; $i++) { $items[$random.Next($items.Count)] }
        $medians.Add((Get-Median $draw))
    }
    return [ordered]@{
        low = Get-Percentile $medians 0.025
        high = Get-Percentile $medians 0.975
    }
}

function Get-ManifestDigest($Rows) {
    $normalized = @($Rows | ForEach-Object {
        '{0}|{1}|{2}' -f $_.path, $_.sha256.ToLowerInvariant(), $_.lastWriteUtc
    }) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-JournalManifestDigest($Rows) {
    $normalized = @($Rows | ForEach-Object {
        '{0}|{1}' -f $_.path, $_.sha256.ToLowerInvariant()
    }) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-ExpectedFileHash([string]$Path, [string]$Expected, [string]$Name) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "$Name hash mismatch: expected $($Expected.ToLowerInvariant()), actual $actual"
    }
    return $actual
}

function Assert-DogfoodWalState([string]$Path, [string]$Expected, [string]$Phase) {
    $hasContent = (Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-Item -LiteralPath $Path).Length -gt 0
    if ([string]::IsNullOrWhiteSpace($Expected)) {
        if ($hasContent) {
            throw "Dogfood WAL has content $Phase but DogfoodWalSha256 was not supplied: $Path"
        }
        return $null
    }
    if (-not $hasContent) {
        throw "Dogfood WAL declared by DogfoodWalSha256 is missing or empty ${Phase}: $Path"
    }
    return Assert-ExpectedFileHash $Path $Expected 'Dogfood WAL'
}

function Get-OptionalPropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-JournalFacts([string]$Path, [datetimeoffset]$Cutoff) {
    $first = $null
    $focused = 0
    $gates = 0
    $categories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $lineCount = 0
    foreach ($line in [IO.File]::ReadLines($Path)) {
        $lineCount++
        try { $entry = $line | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Journal contains malformed JSON at ${Path}:$lineCount" }
        $atValue = Get-OptionalPropertyValue $entry 'at'
        $operation = [string](Get-OptionalPropertyValue $entry 'operation')
        $status = [string](Get-OptionalPropertyValue $entry 'status')
        $detail = [string](Get-OptionalPropertyValue $entry 'detail')
        if ($null -eq $atValue) { continue }
        $at = [datetimeoffset]$atValue
        if ($at.ToUniversalTime() -gt $Cutoff.ToUniversalTime()) { continue }
        if ($null -eq $first -or $at -lt $first) { $first = $at }

        if ($operation -eq 'conductor:finding-evidence' -and $status -eq 'Begin' -and
            $detail -match '(?i)Running focused (?:finding|reviewer) evidence') { $focused++ }
        if ($operation -eq 'conductor:acceptance' -and $status -eq 'Begin') { $gates++ }
        $isFailedDispatch = $operation -in @('conductor:dispatch', 'conductor:dispatch-start') -and $status -eq 'Failed'
        if ($isFailedDispatch -and
            $detail -match '(?i)(?:^|[; ])(?:reason|outcome(?:_rule|Rule)?)=(?:preflight-blocked|preflight-failure|provider-(?:authentication|connectivity|model-rejection|neutral-progress-stall|rate-limit|sandbox-launch-1312)|subscription-limit|silent-launch-failure|rate-limited)(?:[; :,]|$)') {
            [void]$categories.Add('provider/preflight')
        }
    }
    return [pscustomobject]@{
        Activation = $first
        FocusedEvidenceRounds = $focused
        GateAttempts = $gates
        Categories = @($categories | Sort-Object)
        LineCount = $lineCount
    }
}

function Read-DogfoodFacts([string]$DatabasePath) {
    $result = @{}
    $sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if ($null -eq $sqlite) { throw 'sqlite3 is required to read the explicit dogfood database input.' }
    $query = 'select goal_id,summary,model_fit from dogfood_log order by goal_id;'
    $csv = @('goal_id,summary,model_fit') + @(& $sqlite.Source -readonly -csv $DatabasePath $query)
    if ($LASTEXITCODE -ne 0) { throw "sqlite3 could not read dogfood database: $DatabasePath" }
    if ($csv.Count -eq 0) { return $result }
    foreach ($entry in @($csv | ConvertFrom-Csv)) {
        $models = [ordered]@{}
        foreach ($role in @('Planner', 'Researcher', 'Developer', 'Tester', 'Reviewer')) {
            if ($entry.summary -match ("{0} task via (?<lane>.+?) \(exit" -f $role)) {
                $models[$role] = $matches.lane.Trim()
            }
        }
        $result[$entry.goal_id] = [pscustomobject]@{
            ProviderModels = (($models.GetEnumerator() | ForEach-Object { '{0}={1}' -f $_.Key, $_.Value }) -join ';')
            ModelFit = $entry.model_fit
        }
    }
    return $result
}

$manifestPath = Resolve-RequiredPath $OperatorLogManifest 'OperatorLogManifest'
$journalPath = Resolve-RequiredPath $JournalRoot 'JournalRoot' -Container
$journalManifestPath = Resolve-RequiredPath $JournalManifest 'JournalManifest'
$dogfoodPath = Resolve-RequiredPath $DogfoodDbPath 'DogfoodDbPath'
$repoPath = Resolve-RequiredPath $RepositoryRoot 'RepositoryRoot' -Container
$zone = [TimeZoneInfo]::FindSystemTimeZoneById($TimeZoneId)
$startOrdinal = Get-WeekOrdinal $StartWeek
$endOrdinal = Get-WeekOrdinal $EndWeek
if ($endOrdinal -lt $startOrdinal) { throw 'EndWeek must not precede StartWeek.' }

$manifest = @(Import-Csv -LiteralPath $manifestPath)
if ($manifest.Count -eq 0) { throw 'Operator log manifest is empty.' }
$requiredColumns = @('path', 'sha256', 'lastWriteUtc')
foreach ($column in $requiredColumns) {
    if ($manifest[0].PSObject.Properties.Name -notcontains $column) { throw "Manifest is missing required column '$column'." }
}
$manifest = @($manifest | Sort-Object path)
$manifestDigest = Get-ManifestDigest $manifest
if ($manifestDigest -ne $OperatorLogManifestDigestSha256.ToLowerInvariant()) {
    throw "Operator manifest digest mismatch: expected $($OperatorLogManifestDigestSha256.ToLowerInvariant()), actual $manifestDigest"
}
$manifestDirectory = Split-Path -Parent $manifestPath
$seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$validated = [Collections.Generic.List[object]]::new()
foreach ($entry in $manifest) {
    $candidate = if ([IO.Path]::IsPathRooted($entry.path)) { $entry.path } else { Join-Path $manifestDirectory $entry.path }
    $resolved = Resolve-RequiredPath $candidate 'Manifest entry'
    if (-not $seenPaths.Add($resolved)) { throw "Manifest contains duplicate path: $resolved" }
    $recordedWrite = [datetimeoffset]$entry.lastWriteUtc
    if ($recordedWrite.ToUniversalTime() -gt $CutoffUtc.ToUniversalTime()) { throw "Manifest contains a post-cutoff file: $resolved" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash.ToLowerInvariant()
    if ($actual -ne $entry.sha256.ToLowerInvariant()) { throw "Manifest hash mismatch: $resolved" }
    $validated.Add([pscustomobject]@{ Path = $resolved; SortAt = $recordedWrite.ToUniversalTime(); Manifest = $entry })
}

$journalManifestRows = @(Import-Csv -LiteralPath $journalManifestPath)
if ($journalManifestRows.Count -eq 0) { throw 'Journal manifest is empty.' }
foreach ($column in @('path', 'sha256')) {
    if ($journalManifestRows[0].PSObject.Properties.Name -notcontains $column) { throw "Journal manifest is missing required column '$column'." }
}
$journalManifestRows = @($journalManifestRows | Sort-Object path)
$journalManifestDigest = Get-JournalManifestDigest $journalManifestRows
if ($journalManifestDigest -ne $JournalManifestDigestSha256.ToLowerInvariant()) {
    throw "Journal manifest digest mismatch: expected $($JournalManifestDigestSha256.ToLowerInvariant()), actual $journalManifestDigest"
}
$journalRootPrefix = $journalPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$seenJournals = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$validatedJournals = [Collections.Generic.List[string]]::new()
foreach ($entry in $journalManifestRows) {
    $candidate = if ([IO.Path]::IsPathRooted($entry.path)) { $entry.path } else { Join-Path $journalPath $entry.path }
    $resolved = Resolve-RequiredPath $candidate 'Journal manifest entry'
    if (-not $resolved.StartsWith($journalRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Journal manifest entry is outside JournalRoot: $resolved"
    }
    if (-not $seenJournals.Add($resolved)) { throw "Journal manifest contains duplicate path: $resolved" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash.ToLowerInvariant()
    if ($actual -ne $entry.sha256.ToLowerInvariant()) { throw "Journal manifest hash mismatch: $resolved" }
    $validatedJournals.Add($resolved)
}

$dogfoodDigest = Assert-ExpectedFileHash $dogfoodPath $DogfoodDbSha256 'Dogfood database'
$dogfoodWalPath = "${dogfoodPath}-wal"
$dogfoodWalDigest = Assert-DogfoodWalState $dogfoodWalPath $DogfoodWalSha256 'before read'
$resolvedRevision = @(& git -C $repoPath rev-parse --verify $RepositoryRevision)
if ($LASTEXITCODE -ne 0 -or $resolvedRevision.Count -ne 1 -or $resolvedRevision[0] -notmatch '^[0-9a-f]{40}$') {
    throw "Git revision could not be resolved to one commit: $RepositoryRevision"
}
$resolvedRevision = $resolvedRevision[0].ToLowerInvariant()
$revisionType = @(& git -C $repoPath cat-file -t $resolvedRevision)
if ($LASTEXITCODE -ne 0 -or $revisionType.Count -ne 1 -or $revisionType[0] -ne 'commit') {
    throw "Git revision does not identify a commit: $RepositoryRevision"
}

$roundsByPrefix = @{}
foreach ($item in @($validated | Sort-Object SortAt, Path)) {
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($item.Path)) {
        $lineNumber++
        if ($line -notmatch '^WATCH_TRANSITION goal=(?<goal>[0-9a-f]{8,32}) (?<role>Planner|Researcher|Developer|Tester|Reviewer)=') { continue }
        $prefix = $matches.goal.Substring(0, 8).ToLowerInvariant()
        $role = $matches.role
        $files = $null
        if ($line -match ' files=(?<files>\d+)') { $files = [int]$matches.files }
        if (-not $roundsByPrefix.ContainsKey($prefix)) { $roundsByPrefix[$prefix] = [Collections.Generic.List[object]]::new() }
        $roundsByPrefix[$prefix].Add([pscustomobject]@{
            Role = $role
            Files = $files
            Anchor = ('{0}:{1}' -f ([IO.Path]::GetFileName($item.Path)), $lineNumber)
            SortAt = $item.SortAt
        })
    }
}

$journalByPrefix = @{}
foreach ($journal in $validatedJournals) {
    $baseName = [IO.Path]::GetFileNameWithoutExtension($journal)
    if ($baseName.Length -lt 8) { continue }
    $prefix = $baseName.Substring(0, 8).ToLowerInvariant()
    if ($journalByPrefix.ContainsKey($prefix)) { throw "Ambiguous journal prefix '$prefix'." }
    $journalByPrefix[$prefix] = $journal
}

$landings = @{}
$gitLines = @(& git -C $repoPath log $resolvedRevision --reverse '--format=%aI%x09%s' '--grep=^Integrate goal/')
if ($LASTEXITCODE -ne 0) { throw "git landing query failed for $repoPath" }
foreach ($line in $gitLines) {
    $parts = $line -split "`t", 2
    if ($parts.Count -eq 2 -and $parts[1] -match '^Integrate goal/(?<goal>[0-9a-f]{8,32})') {
        $prefix = $matches.goal.Substring(0, 8).ToLowerInvariant()
        if (-not $landings.ContainsKey($prefix)) { $landings[$prefix] = ([datetimeoffset]$parts[0]).ToUniversalTime() }
    }
}

$dogfood = Read-DogfoodFacts $dogfoodPath
$metadata = @{}
$metadataDigest = $null
$metadataPath = $null
if (-not [string]::IsNullOrWhiteSpace($TaskMetadataSnapshot)) {
    if ([string]::IsNullOrWhiteSpace($TaskMetadataSnapshotSha256)) {
        throw 'TaskMetadataSnapshotSha256 is required when TaskMetadataSnapshot is supplied.'
    }
    $metadataPath = Resolve-RequiredPath $TaskMetadataSnapshot 'TaskMetadataSnapshot'
    $metadataDigest = Assert-ExpectedFileHash $metadataPath $TaskMetadataSnapshotSha256 'Task metadata snapshot'
    foreach ($entry in @(Import-Csv -LiteralPath $metadataPath)) {
        if (-not $entry.goal) { throw 'Task metadata snapshot contains a row without goal.' }
        $metadata[$entry.goal.ToLowerInvariant()] = $entry
    }
}

$rows = [Collections.Generic.List[object]]::new()
$excludedMissingJournal = 0
$excludedMissingActivation = 0
$excludedOutsideWeeks = 0
foreach ($prefix in @($roundsByPrefix.Keys | Sort-Object)) {
    if (-not $journalByPrefix.ContainsKey($prefix)) { $excludedMissingJournal++; continue }
    $journalFile = $journalByPrefix[$prefix]
    $journal = Get-JournalFacts $journalFile $CutoffUtc
    if ($null -eq $journal.Activation) { $excludedMissingActivation++; continue }
    $week = Get-IsoWeek $journal.Activation $zone
    $ordinal = Get-WeekOrdinal $week
    if ($ordinal -lt $startOrdinal -or $ordinal -gt $endOrdinal) { $excludedOutsideWeeks++; continue }

    $goalId = [IO.Path]::GetFileNameWithoutExtension($journalFile).ToLowerInvariant()
    $rounds = @($roundsByPrefix[$prefix] | Sort-Object SortAt, Anchor)
    $roles = @($rounds | ForEach-Object Role)
    $distinctRoles = @($roles | Select-Object -Unique)
    $roleCounts = @($roles | Group-Object | Sort-Object Name | ForEach-Object { '{0}={1}' -f $_.Name, $_.Count }) -join ';'
    $fileValues = @($rounds | Where-Object { $null -ne $_.Files } | ForEach-Object Files)
    $dogfoodEntry = if ($dogfood.ContainsKey($goalId)) { $dogfood[$goalId] } else { $null }
    $meta = if ($metadata.ContainsKey($goalId)) { $metadata[$goalId] } elseif ($metadata.ContainsKey($prefix)) { $metadata[$prefix] } else { $null }
    $landing = if ($landings.ContainsKey($prefix)) { $landings[$prefix] } else { $null }
    $landedMinutes = if ($null -ne $landing -and $landing -ge $journal.Activation.ToUniversalTime()) {
        [Math]::Round(($landing - $journal.Activation.ToUniversalTime()).TotalMinutes, 3)
    } else { $null }

    $rows.Add([pscustomobject][ordered]@{
        goal = $goalId
        activationUtc = $journal.Activation.ToUniversalTime().ToString('o')
        activationWeek = $week
        landed = ($null -ne $landing)
        landingUtc = if ($null -ne $landing) { $landing.ToString('o') } else { $null }
        landedE2EMinutes = $landedMinutes
        observedRoleOrder = ($distinctRoles -join '>')
        observedRoleSet = (@($distinctRoles | Sort-Object) -join '+')
        selectedPipeline = if ($null -ne $meta) { $meta.selectedPipeline } else { $null }
        roleRounds = $rounds.Count
        distinctRoleCount = $distinctRoles.Count
        excessRounds = $rounds.Count - $distinctRoles.Count
        roleRoundCounts = $roleCounts
        roundFiles = (@($rounds | ForEach-Object { if ($null -eq $_.Files) { '?' } else { $_.Files } }) -join ';')
        finalFilesCount = if ($fileValues.Count -gt 0) { $fileValues[-1] } else { $null }
        maxFilesCount = if ($fileValues.Count -gt 0) { ($fileValues | Measure-Object -Maximum).Maximum } else { $null }
        changedPathScope = if ($null -ne $meta) { $meta.changedPathScope } else { $null }
        complexity = if ($null -ne $meta) { $meta.complexity } else { $null }
        risk = if ($null -ne $meta) { $meta.risk } else { $null }
        criterionCount = if ($null -ne $meta) { $meta.criterionCount } else { $null }
        criterionOwners = if ($null -ne $meta) { $meta.criterionOwners } else { $null }
        providerModelsByRole = if ($null -ne $dogfoodEntry) { $dogfoodEntry.ProviderModels } else { $null }
        focusedEvidenceRounds = $journal.FocusedEvidenceRounds
        gateAttempts = $journal.GateAttempts
        failureFindingCategories = ($journal.Categories -join ';')
        failureFindingCategoryCoverage = 'provider/preflight=journal-positive-receipt;impossible-evidence=withheld-no-journal-producer;formatting-contract=withheld-no-journal-producer;reviewer-finding=withheld-no-journal-producer;unchanged-head=withheld-no-transition-head-binding'
        transitionAnchors = (@($rounds | ForEach-Object Anchor) -join ';')
        journalSource = [IO.Path]::GetFileName($journalFile)
        metadataMissingReason = if ($null -eq $meta) { 'historical-task-snapshot-not-supplied' } else { $null }
        providerMissingReason = if ($null -eq $dogfoodEntry) { 'no-dogfood-entry' } else { $null }
        changedPathMissingReason = if ($null -eq $meta -or [string]::IsNullOrWhiteSpace($meta.changedPathScope)) { 'receipts-contain-counts-not-paths' } else { $null }
    })
}

# Revalidate mutable files after all reads so a check-then-use change cannot be
# mislabeled with the declared input identities.
foreach ($item in $validated) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $item.Path).Hash.ToLowerInvariant()
    if ($actual -ne $item.Manifest.sha256.ToLowerInvariant()) { throw "Manifest hash changed while reading: $($item.Path)" }
}
foreach ($entry in $journalManifestRows) {
    $candidate = if ([IO.Path]::IsPathRooted($entry.path)) { $entry.path } else { Join-Path $journalPath $entry.path }
    $resolved = Resolve-RequiredPath $candidate 'Journal manifest entry'
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash.ToLowerInvariant()
    if ($actual -ne $entry.sha256.ToLowerInvariant()) { throw "Journal manifest hash changed while reading: $resolved" }
}
[void](Assert-ExpectedFileHash $dogfoodPath $DogfoodDbSha256 'Dogfood database after read')
[void](Assert-DogfoodWalState $dogfoodWalPath $DogfoodWalSha256 'after read')
if ($null -ne $metadataPath) {
    [void](Assert-ExpectedFileHash $metadataPath $TaskMetadataSnapshotSha256 'Task metadata snapshot after read')
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$csvPath = Join-Path $OutputDirectory 'excess-worker-rounds.csv'
$summaryPath = Join-Path $OutputDirectory 'excess-worker-rounds-summary.json'
$rows | Sort-Object activationUtc, goal | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8NoBOM

$weekly = foreach ($group in @($rows | Group-Object activationWeek | Sort-Object Name)) {
    $interval = Get-BootstrapMedianInterval @($group.Group | ForEach-Object excessRounds) (61406 + [int]($group.Name -replace '^\d{4}-W', ''))
    [ordered]@{
        week = $group.Name
        goals = $group.Count
        medianRounds = Get-Median @($group.Group | ForEach-Object roleRounds)
        medianExcess = Get-Median @($group.Group | ForEach-Object excessRounds)
        excessMedianBootstrapLow = $interval.low
        excessMedianBootstrapHigh = $interval.high
    }
}

$strata = foreach ($group in @($rows | Group-Object { '{0}|{1}' -f $_.activationWeek, $_.observedRoleOrder } | Sort-Object Name)) {
    $parts = $group.Name -split '\|', 2
    [ordered]@{
        week = $parts[0]
        observedRoleOrder = $parts[1]
        goals = $group.Count
        medianExcess = Get-Median @($group.Group | ForEach-Object excessRounds)
    }
}

$roleSetStrata = foreach ($group in @($rows | Group-Object { '{0}|{1}' -f $_.activationWeek, $_.observedRoleSet } | Sort-Object Name)) {
    $parts = $group.Name -split '\|', 2
    [ordered]@{
        week = $parts[0]
        observedRoleSet = $parts[1]
        goals = $group.Count
        medianExcess = Get-Median @($group.Group | ForEach-Object excessRounds)
    }
}

$baselineRows = @($rows | Where-Object activationWeek -eq $StartWeek)
$baselineGroups = @($baselineRows | Group-Object observedRoleSet)
$standardization = foreach ($weekGroup in @($rows | Group-Object activationWeek | Sort-Object Name)) {
    $pairs = [Collections.Generic.List[object]]::new()
    $coveredWeight = 0.0
    foreach ($baselineGroup in $baselineGroups) {
        $target = @($weekGroup.Group | Where-Object observedRoleSet -eq $baselineGroup.Name)
        if ($target.Count -eq 0) { continue }
        $stratumWeight = $baselineGroup.Count / [double]$baselineRows.Count
        $coveredWeight += $stratumWeight
        $rowWeight = $stratumWeight / $target.Count
        foreach ($row in $target) { $pairs.Add([pscustomobject]@{ Value = [double]$row.excessRounds; Weight = $rowWeight }) }
    }
    $observedMedian = Get-Median @($weekGroup.Group | ForEach-Object excessRounds)
    $standardizedMedian = Get-WeightedMedian $pairs
    [ordered]@{
        week = $weekGroup.Name
        goals = $weekGroup.Count
        observedMedianExcess = $observedMedian
        w28RoleSetStandardizedMedianExcess = $standardizedMedian
        observedMinusStandardizedMedianPoints = if ($null -ne $standardizedMedian) { $observedMedian - $standardizedMedian } else { $null }
        coveredW28RoleSetWeight = [Math]::Round($coveredWeight, 4)
    }
}

$coverageFields = @('landingUtc', 'landedE2EMinutes', 'selectedPipeline', 'complexity', 'risk', 'criterionCount', 'criterionOwners', 'providerModelsByRole', 'changedPathScope')
$coverage = [ordered]@{}
foreach ($field in $coverageFields) {
    $covered = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.$field) }).Count
    $coverage[$field] = [ordered]@{ covered = $covered; total = $rows.Count }
}

$landedWithDuration = @($rows | Where-Object { $null -ne $_.landedE2EMinutes })
$landedNoRework = @($landedWithDuration | Where-Object excessRounds -eq 0)
$landedAnyRework = @($landedWithDuration | Where-Object excessRounds -gt 0)
$noReworkMedian = Get-Median @($landedNoRework | ForEach-Object landedE2EMinutes)
$anyReworkMedian = Get-Median @($landedAnyRework | ForEach-Object landedE2EMinutes)

$summary = [ordered]@{
    schemaVersion = 1
    generatedFrom = [ordered]@{
        manifestCount = $manifest.Count
        manifestDigestSha256 = $manifestDigest
        manifest = [IO.Path]::GetFileName($manifestPath)
        cutoffUtc = $CutoffUtc.UtcDateTime.ToString('o')
        journalManifestCount = $journalManifestRows.Count
        journalManifestDigestSha256 = $journalManifestDigest
        journalManifest = [IO.Path]::GetFileName($journalManifestPath)
        dogfoodDbSha256 = $dogfoodDigest
        dogfoodWalSha256 = $dogfoodWalDigest
        repositoryRevision = $resolvedRevision
        taskMetadataSnapshotSha256 = $metadataDigest
        timeZone = $TimeZoneId
        startWeek = $StartWeek
        endWeek = $EndWeek
    }
    definitions = [ordered]@{
        inclusion = 'goal has a line-start WATCH_TRANSITION in the validated manifest, a timestamped journal activation, and activation week is in range'
        roleRound = 'one line-start WATCH_TRANSITION receipt'
        excessRounds = 'roleRounds minus distinct observed roles'
        week = 'ISO week of first timestamped journal operation after timezone conversion'
        landing = 'first Integrate goal/<prefix> commit reachable from the declared repository revision'
        missingData = 'null and excluded from field-specific denominators; never zero-filled'
    }
    exclusions = [ordered]@{
        missingJournal = $excludedMissingJournal
        missingActivation = $excludedMissingActivation
        outsideWeekRange = $excludedOutsideWeeks
    }
    weekly = @($weekly)
    matchedObservedRoleOrderStrata = @($strata)
    matchedObservedRoleSetStrata = @($roleSetStrata)
    w28RoleSetStandardization = @($standardization)
    landedE2EByRework = [ordered]@{
        noReworkGoals = $landedNoRework.Count
        noReworkMedianMinutes = $noReworkMedian
        anyReworkGoals = $landedAnyRework.Count
        anyReworkMedianMinutes = $anyReworkMedian
        medianRatioAnyToNone = if ($null -ne $noReworkMedian -and $noReworkMedian -gt 0) { [Math]::Round($anyReworkMedian / $noReworkMedian, 3) } else { $null }
    }
    coverage = $coverage
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8NoBOM

[pscustomobject]@{
    Dataset = $csvPath
    Summary = $summaryPath
    Goals = $rows.Count
    ManifestCount = $manifest.Count
    ManifestDigestSha256 = $summary.generatedFrom.manifestDigestSha256
} | ConvertTo-Json -Compress
