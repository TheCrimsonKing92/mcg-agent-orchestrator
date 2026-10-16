<#
.SYNOPSIS
  Record operator evidence that an exact legacy acceptance holder and all descendants settled.
.DESCRIPTION
  Inspect/settle the entire predecessor tree first. This command does not kill processes or infer
  descendant exit from root death. Admission independently revalidates the binding and root identity
  and publishes terminal metadata before freeing capacity. Evidence is retained outside .scratch.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$MetadataPath,
    [Parameter(Mandatory = $true)] [string]$EvidencePath,
    [Parameter(Mandatory = $true)] [switch]$ConfirmEntireTreeSettled,
    [Nullable[datetime]]$HostBootTime,
    [scriptblock]$ProcessStartTimeReader = { param($process) $process.StartTime }
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('HostBootTime')) {
    try { $HostBootTime = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime }
    catch { $HostBootTime = $null }
}
if (-not $ConfirmEntireTreeSettled) { throw 'Whole-tree settlement confirmation is required.' }
$metadataFile = (Resolve-Path -LiteralPath $MetadataPath).Path
$evidenceFile = (Resolve-Path -LiteralPath $EvidencePath).Path
if ([string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($evidenceFile))) { throw 'Settlement evidence is empty.' }
$metadata = [IO.File]::ReadAllText($metadataFile) | ConvertFrom-Json
function Read-Property([string]$Name) {
    $property = $metadata.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
if ((Read-Property 'HolderContractVersion') -eq 1) { throw 'Durable holders use job settlement, not legacy recovery.' }
if ([IO.Path]::GetFullPath((Read-Property 'MetadataPath')) -ne $metadataFile) { throw 'Canonical metadata path differs.' }
$holderId = [int](Read-Property 'OwnerProcessId')
if ($holderId -lt 0 -or ($holderId -eq 0 -and (Read-Property 'Outcome') -in @('Running', 0))) {
    throw 'A running unresolved launch cannot be recovered; settle the launcher first.'
}
$holderStartedAt = Read-Property 'OwnerProcessStartedAt'
$holderImage = Read-Property 'OwnerExecutablePath'
$attemptStartedAt = [DateTimeOffset](Read-Property 'StartedAt')
# Creation precedes launch. Only a recorded heartbeat or terminal observation can
# establish that this PID was already bound to the original holder by that time.
$holderObservedAt = Read-Property 'LastHeartbeatAt'
if ($null -eq $holderObservedAt) { $holderObservedAt = Read-Property 'ReconciledAt' }
if ($null -ne $holderObservedAt -and ([DateTimeOffset]$holderObservedAt -lt $attemptStartedAt -or
    [DateTimeOffset]$holderObservedAt -gt [DateTimeOffset]::UtcNow)) { $holderObservedAt = $null }
function Test-PredatesHostBoot {
    if ($null -eq $HostBootTime) { return $false }
    try {
        $boot = ([DateTimeOffset]$HostBootTime).ToUniversalTime()
        $started = Read-Property 'StartedAt'
        if ($null -eq $started -or [string]::IsNullOrWhiteSpace([string]$started) -or
            [DateTimeOffset]$started -ge $boot) { return $false }
        $heartbeat = Read-Property 'LastHeartbeatAt'
        if ($null -ne $heartbeat -and [DateTimeOffset]$heartbeat -ge $boot) { return $false }
        foreach ($field in @('HeartbeatPath', 'StdoutPath', 'StderrPath', 'ExitCodePath', 'ResultPath')) {
            $path = Read-Property $field
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            # Get-Item distinguishes a missing file from an unreadable activity artifact.
            try { $artifact = Get-Item -LiteralPath $path -Force -ErrorAction Stop }
            catch [System.Management.Automation.ItemNotFoundException] { continue }
            if ([DateTimeOffset]$artifact.LastWriteTimeUtc -ge $boot) { return $false }
        }
        return $true
    }
    catch { return $false }
}
function Assert-RecordedHolderGone {
    if (Test-PredatesHostBoot) { return }
    if ($holderId -eq 0) { return } # Explicit evidence must include absence of pending launchers.
    $process = $null
    try { $process = [Diagnostics.Process]::GetProcessById($holderId) }
    catch [ArgumentException] { return }
    try {
        if ($process.HasExited) { return }
        try {
            $processStart = & $ProcessStartTimeReader $process
            if ($null -eq $processStart) { throw 'Start time unavailable.' }
            $currentStart = ([DateTimeOffset]$processStart).ToUniversalTime()
        }
        catch { throw 'holder PID is owned by a process whose start time cannot be read' }
        if ($null -ne $holderStartedAt) {
            if ($currentStart -ne [DateTimeOffset]$holderStartedAt) { return }
            throw 'Recorded holder is live or its image is unverified; settlement refused.'
        }
        if ($null -eq $holderObservedAt -or [DateTimeOffset]$holderObservedAt -lt $attemptStartedAt -or
            [DateTimeOffset]$holderObservedAt -gt [DateTimeOffset]::UtcNow -or
            $currentStart -le [DateTimeOffset]$holderObservedAt) {
            throw 'Live PID with incomplete recorded identity cannot be recovered.'
        }
        # Different creation time proves PID reuse. Never stop the replacement process.
    }
    finally { if ($null -ne $process) { $process.Dispose() } }
}
Assert-RecordedHolderGone
$bootEvidence = if (Test-PredatesHostBoot) {
    'holder activity predates host boot ' + ([DateTimeOffset]$HostBootTime).ToUniversalTime().ToString('O')
} else { $null }
$goalId = Read-Property 'GoalId'
if (-not [string]::IsNullOrWhiteSpace($goalId)) {
    if ((Read-Property 'ExecutionProtocol') -ne 'out-of-process') { throw 'Only external holders need recovery.' }
    $branch = Read-Property 'BranchHeadSha'
    $main = Read-Property 'MainHeadSha'
    if ($null -eq $branch) { $branch = 'unknown-branch' }
    if ($null -eq $main) { $main = 'unknown-main' }
    $candidate = "${goalId}:${branch}:${main}"
    $slot = 'slot-' + (Read-Property 'SlotIndex')
}
else {
    $candidate = Read-Property 'IdentityValue'
    $slot = (Read-Property 'Kind') + ':' + $candidate
}
$receipt = [ordered]@{
    Binding = [ordered]@{ AttemptId = Read-Property 'AttemptId'; MetadataPath = $metadataFile; Candidate = $candidate; Slot = $slot }
    HolderProcessId = $holderId
    HolderStartedAt = $holderStartedAt
    HolderImage = $holderImage
    AttemptStartedAt = Read-Property 'StartedAt'
    ConfirmedAt = [DateTimeOffset]::UtcNow.ToString('O')
    HolderObservedAt = $holderObservedAt
    Evidence = 'sha256:' + (Get-FileHash -LiteralPath $evidenceFile -Algorithm SHA256).Hash + ':' + $evidenceFile
    Transition = 'operator-confirmed-tree-exit'
}
if ($null -ne $bootEvidence) { $receipt.Evidence += '; ' + $bootEvidence }
$receiptPath = $metadataFile + '.legacy-holder-settled.json'
$temporaryPath = $receiptPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($temporaryPath, ($receipt | ConvertTo-Json -Depth 5))
    # Recheck the owning artifact and holder immediately before publication.
    $currentMetadata = [IO.File]::ReadAllText($metadataFile) | ConvertFrom-Json
    if (($currentMetadata | ConvertTo-Json -Depth 30 -Compress) -ne ($metadata | ConvertTo-Json -Depth 30 -Compress)) {
        throw 'Metadata changed during recovery; inspect the new artifact before retrying.'
    }
    Assert-RecordedHolderGone
    [IO.File]::Move($temporaryPath, $receiptPath, $true)
}
finally { if ([IO.File]::Exists($temporaryPath)) { Remove-Item -LiteralPath $temporaryPath } }
Write-Output "ACCEPTANCE_HOLDER_RECOVERY attempt=$($receipt.Binding.AttemptId) slot=$slot holder=$holderId receipt=$receiptPath evidence=$($receipt.Evidence)"
