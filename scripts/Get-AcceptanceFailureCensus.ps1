<#
.SYNOPSIS
  Print a compact failure-signature census for one retained acceptance attempt.

.DESCRIPTION
  Reads existing acceptance TRX and goal-operation journal artifacts. The reader
  tolerates absent, incomplete, locked, and malformed artifacts and never runs a
  test command.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Get-AcceptanceFailureCensus.ps1 -Goal a04cdfe9

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Get-AcceptanceFailureCensus.ps1 -Attempt 01234567 -Top 10
#>
[CmdletBinding()]
param(
    [string]$Goal,
    [string]$Attempt,
    [string]$AttemptsRoot,
    [string]$JournalRoot,
    [ValidateRange(1, 10000)]
    [int]$Top = 20
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Resolve-AttemptsRoot {
    param([string]$ExplicitRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        return [System.IO.Path]::GetFullPath($ExplicitRoot)
    }

    $repoRoot = Split-Path -Parent $PSScriptRoot
    return Join-Path $repoRoot '.orchestrator\acceptance-gate-attempts'
}

function Read-SharedText {
    param([string]$Path)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-AttemptIdFromTrxName {
    param([string]$Name)

    $separator = $Name.IndexOf('.')
    if ($separator -le 0) {
        return $null
    }

    $attemptId = $Name.Substring(0, $separator)
    foreach ($suffix in @('-candidate', '-baseline')) {
        if ($attemptId.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $attemptId.Substring(0, $attemptId.Length - $suffix.Length)
        }
    }
    return $attemptId
}

function Test-TrxBelongsToAttempt {
    param([string]$Name, [string]$AttemptId)

    return $Name.StartsWith("$AttemptId.", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Name.StartsWith("$AttemptId-candidate.", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Name.StartsWith("$AttemptId-baseline.", [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-AttemptCandidates {
    param([System.IO.DirectoryInfo]$GoalDirectory)

    $byId = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($file in @(Get-ChildItem -LiteralPath $GoalDirectory.FullName -Filter '*.attempt.json' -File -ErrorAction SilentlyContinue)) {
        $attemptId = $file.Name.Substring(0, $file.Name.Length - '.attempt.json'.Length)
        $startedAt = [datetimeoffset]$file.LastWriteTimeUtc
        $ordinal = -1
        $metadataDiagnostic = $null
        try {
            $metadata = (Read-SharedText $file.FullName) | ConvertFrom-Json -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace([string]$metadata.attemptId)) {
                $attemptId = [string]$metadata.attemptId
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$metadata.startedAt)) {
                $startedAt = [datetimeoffset]::Parse([string]$metadata.startedAt, $invariant)
            }
            if ($null -ne $metadata.ordinal) {
                $ordinal = [int]$metadata.ordinal
            }
        }
        catch {
            $metadataDiagnostic = "metadata-unparseable file=$($file.Name) detail=$(Normalize-Text $_.Exception.Message)"
        }

        $byId[$attemptId] = [pscustomobject]@{
            GoalDirectory = $GoalDirectory
            GoalId = $GoalDirectory.Name
            AttemptId = $attemptId
            StartedAt = $startedAt
            Ordinal = $ordinal
            MetadataDiagnostic = $metadataDiagnostic
        }
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $GoalDirectory.FullName -Filter '*.trx' -File -ErrorAction SilentlyContinue)) {
        $attemptId = Get-AttemptIdFromTrxName $file.Name
        if ([string]::IsNullOrWhiteSpace($attemptId)) {
            continue
        }
        if (-not $byId.ContainsKey($attemptId)) {
            $byId[$attemptId] = [pscustomobject]@{
                GoalDirectory = $GoalDirectory
                GoalId = $GoalDirectory.Name
                AttemptId = $attemptId
                StartedAt = [datetimeoffset]$file.LastWriteTimeUtc
                Ordinal = -1
                MetadataDiagnostic = $null
            }
        }
    }

    return @($byId.Values)
}

function Sort-AttemptCandidates {
    param([object[]]$Candidates)

    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($candidate in $Candidates) {
        [void]$list.Add($candidate)
    }
    $list.Sort([System.Comparison[object]]{
        param($left, $right)
        $comparison = $right.StartedAt.CompareTo($left.StartedAt)
        if ($comparison -ne 0) { return $comparison }
        $comparison = $right.Ordinal.CompareTo($left.Ordinal)
        if ($comparison -ne 0) { return $comparison }
        return [string]::CompareOrdinal($right.AttemptId, $left.AttemptId)
    })
    return @($list)
}

function Resolve-SelectedAttempt {
    param(
        [string]$Root,
        [string]$GoalSelector,
        [string]$AttemptSelector
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return [pscustomobject]@{ Kind = 'Missing'; Message = "attempts root not found: $Root" }
    }

    $goalDirectories = @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue)
    if (-not [string]::IsNullOrWhiteSpace($GoalSelector)) {
        $goalMatches = @($goalDirectories | Where-Object {
            $_.Name.Equals($GoalSelector, [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($goalMatches.Count -eq 0) {
            $goalMatches = @($goalDirectories | Where-Object {
                $_.Name.StartsWith($GoalSelector, [System.StringComparison]::OrdinalIgnoreCase)
            })
        }
        if ($goalMatches.Count -eq 0) {
            return [pscustomobject]@{ Kind = 'Missing'; Message = "goal '$GoalSelector' not found under: $Root" }
        }
        if ($goalMatches.Count -gt 1) {
            $names = @($goalMatches.Name | Sort-Object)
            return [pscustomobject]@{ Kind = 'Invalid'; Message = "goal prefix '$GoalSelector' is ambiguous: $($names -join ', ')" }
        }

        $candidates = @(Get-AttemptCandidates $goalMatches[0])
        if ($candidates.Count -eq 0) {
            return [pscustomobject]@{ Kind = 'Missing'; Message = "no retained attempts found for goal '$($goalMatches[0].Name)'"; GoalId = $goalMatches[0].Name }
        }
        return [pscustomobject]@{ Kind = 'Found'; Candidate = (Sort-AttemptCandidates $candidates)[0] }
    }

    $allCandidates = foreach ($goalDirectory in $goalDirectories) {
        Get-AttemptCandidates $goalDirectory
    }
    $attemptMatches = @($allCandidates | Where-Object {
        $_.AttemptId.Equals($AttemptSelector, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($attemptMatches.Count -eq 0) {
        $attemptMatches = @($allCandidates | Where-Object {
            $_.AttemptId.StartsWith($AttemptSelector, [System.StringComparison]::OrdinalIgnoreCase)
        })
    }
    if ($attemptMatches.Count -eq 0) {
        return [pscustomobject]@{ Kind = 'Missing'; Message = "attempt '$AttemptSelector' not found under: $Root" }
    }
    if ($attemptMatches.Count -gt 1) {
        $names = @($attemptMatches | ForEach-Object { "$($_.GoalId)/$($_.AttemptId)" } | Sort-Object)
        return [pscustomobject]@{ Kind = 'Invalid'; Message = "attempt prefix '$AttemptSelector' is ambiguous: $($names -join ', ')" }
    }
    return [pscustomobject]@{ Kind = 'Found'; Candidate = $attemptMatches[0] }
}

function Normalize-Text {
    param([string]$Value, [string]$Fallback = 'unavailable')

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Fallback
    }
    return ([regex]::Replace($Value, '\s+', ' ')).Trim()
}

function Get-TopFrame {
    param([string]$StackTrace)

    if ([string]::IsNullOrWhiteSpace($StackTrace)) {
        return 'unavailable'
    }
    foreach ($line in ($StackTrace -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            return $line.Trim()
        }
    }
    return 'unavailable'
}

function Get-FrameSource {
    param([string]$Frame)

    if ($Frame -eq 'unavailable') {
        return 'unavailable'
    }
    $match = [regex]::Match(
        $Frame,
        '\s+in\s+(?<file>.+):line\s+(?<line>\d+)\s*$',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return 'unavailable'
    }
    return [string]::Format($invariant, '{0}:{1}', $match.Groups['file'].Value.Trim(), $match.Groups['line'].Value)
}

function Get-FailedChecks {
    param([string]$Root, [string]$GoalId)

    if ([string]::IsNullOrWhiteSpace($GoalId)) {
        return 'unavailable'
    }
    $path = Join-Path $Root "$GoalId.jsonl"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return 'unavailable'
    }
    try {
        $latest = $null
        foreach ($line in ((Read-SharedText $path) -split '\r?\n')) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            try {
                $record = $line | ConvertFrom-Json -ErrorAction Stop
                $names = @($record.failedCheckNames | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
                if ($names.Count -gt 0) {
                    $latest = $names -join ','
                }
            }
            catch {
                # A trailing JSONL record may still be in flight.
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($latest)) {
            return $latest
        }
    }
    catch {
        # Journal data is best-effort and never blocks a TRX census.
    }
    return 'unavailable'
}

function Get-PrintableText {
    param([string]$Value)

    if ($Value.Length -le 300) {
        return $Value
    }
    return $Value.Substring(0, 297) + '...'
}

function Write-MissingReport {
    param([string]$Root, [object]$Resolution)

    $goalValue = if (-not [string]::IsNullOrWhiteSpace($Resolution.GoalId)) { $Resolution.GoalId } elseif (-not [string]::IsNullOrWhiteSpace($Goal)) { $Goal } else { 'unavailable' }
    $attemptValue = if (-not [string]::IsNullOrWhiteSpace($Attempt)) { $Attempt } else { 'unavailable' }
    [Console]::WriteLine("goal=$goalValue attempt=$attemptValue root=$Root")
    [Console]::WriteLine('status=missing')
    [Console]::WriteLine("message=$($Resolution.Message)")
    [Console]::WriteLine('failedChecks=unavailable')
    [Console]::WriteLine('trx=0 executed~0 failed=unknown distinctSignatures=unknown')
    [Console]::WriteLine('unreadable=0 unparseable=0 otherNonPassing=0 signaturesSuppressed=0')
}

$hasGoal = -not [string]::IsNullOrWhiteSpace($Goal)
$hasAttempt = -not [string]::IsNullOrWhiteSpace($Attempt)
if ($hasGoal -eq $hasAttempt) {
    [Console]::Error.WriteLine('usage: specify exactly one of -Goal <id-or-prefix> or -Attempt <id-or-prefix>.')
    exit 2
}

$resolvedRoot = Resolve-AttemptsRoot $AttemptsRoot
$resolution = Resolve-SelectedAttempt $resolvedRoot $Goal $Attempt
if ($resolution.Kind -eq 'Invalid') {
    [Console]::Error.WriteLine($resolution.Message)
    exit 2
}
if ($resolution.Kind -eq 'Missing') {
    Write-MissingReport $resolvedRoot $resolution
    exit 0
}

$selected = $resolution.Candidate
$trxFiles = @(Get-ChildItem -LiteralPath $selected.GoalDirectory.FullName -Filter '*.trx' -File -ErrorAction SilentlyContinue | Where-Object {
    Test-TrxBelongsToAttempt $_.Name $selected.AttemptId
})
$trxList = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($trxFile in $trxFiles) {
    [void]$trxList.Add($trxFile)
}
$trxList.Sort([System.Comparison[System.IO.FileInfo]]{
    param($left, $right)
    return [string]::CompareOrdinal($left.FullName, $right.FullName)
})

$executed = 0L
$counterFailed = 0L
$parsedFailed = 0L
$otherNonPassing = 0L
$unreadable = 0
$unparseable = 0
$parsedFiles = 0
$diagnostics = [System.Collections.Generic.List[object]]::new()
$signatures = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)

foreach ($trxFile in $trxList) {
    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $trxFile.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite)
    }
    catch {
        $unreadable++
        [void]$diagnostics.Add([pscustomobject]@{ Kind = 'unreadable'; File = $trxFile.Name; Detail = (Normalize-Text $_.Exception.Message) })
        continue
    }

    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.PreserveWhitespace = $false
        $document.Load($stream)
        $counters = $document.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
        if ($null -eq $counters) {
            throw [System.FormatException]::new('missing TestRun/ResultSummary/Counters')
        }
        $fileExecuted = 0L
        $fileFailed = 0L
        if (-not [long]::TryParse($counters.GetAttribute('executed'), [System.Globalization.NumberStyles]::Integer, $invariant, [ref]$fileExecuted)) {
            throw [System.FormatException]::new('missing or invalid Counters@executed')
        }
        if (-not [long]::TryParse($counters.GetAttribute('failed'), [System.Globalization.NumberStyles]::Integer, $invariant, [ref]$fileFailed)) {
            throw [System.FormatException]::new('missing or invalid Counters@failed')
        }

        $executed += $fileExecuted
        $counterFailed += $fileFailed
        $parsedFiles++
        foreach ($result in @($document.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))) {
            $outcome = $result.GetAttribute('outcome')
            if ($outcome.Equals('Failed', [System.StringComparison]::OrdinalIgnoreCase)) {
                $parsedFailed++
                $messageNode = $result.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
                $stackNode = $result.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='StackTrace']")
                $message = Normalize-Text $(if ($null -ne $messageNode) { $messageNode.InnerText } else { $null })
                $frame = Get-TopFrame $(if ($null -ne $stackNode) { $stackNode.InnerText } else { $null })
                $key = $message + [char]0x1f + $frame
                if (-not $signatures.ContainsKey($key)) {
                    $signatures[$key] = [pscustomobject]@{
                        Count = 0L
                        Message = $message
                        Frame = $frame
                        Source = Get-FrameSource $frame
                        Example = Normalize-Text $result.GetAttribute('testName')
                    }
                }
                $signatures[$key].Count++
            }
            elseif (-not $outcome.Equals('Passed', [System.StringComparison]::OrdinalIgnoreCase)) {
                $otherNonPassing++
            }
        }
    }
    catch {
        $unparseable++
        [void]$diagnostics.Add([pscustomobject]@{ Kind = 'unparseable'; File = $trxFile.Name; Detail = (Normalize-Text $_.Exception.Message) })
    }
    finally {
        $stream.Dispose()
    }
}

$ordered = [System.Collections.Generic.List[object]]::new()
foreach ($signature in $signatures.Values) {
    [void]$ordered.Add($signature)
}
$ordered.Sort([System.Comparison[object]]{
    param($left, $right)
    $comparison = $right.Count.CompareTo($left.Count)
    if ($comparison -ne 0) { return $comparison }
    $comparison = [string]::CompareOrdinal($left.Message, $right.Message)
    if ($comparison -ne 0) { return $comparison }
    return [string]::CompareOrdinal($left.Frame, $right.Frame)
})

$status = if ($trxList.Count -eq 0 -or $unreadable -gt 0 -or $unparseable -gt 0 -or -not [string]::IsNullOrWhiteSpace($selected.MetadataDiagnostic)) { 'partial' } else { 'ok' }
$suppressed = [Math]::Max(0, $ordered.Count - $Top)
$resolvedJournalRoot = if (-not [string]::IsNullOrWhiteSpace($JournalRoot)) {
    [System.IO.Path]::GetFullPath($JournalRoot)
} else {
    Join-Path (Split-Path -Parent $resolvedRoot) 'goal-operations'
}
$failedChecks = Get-FailedChecks $resolvedJournalRoot $selected.GoalId

[Console]::WriteLine("goal=$($selected.GoalId) attempt=$($selected.AttemptId) root=$resolvedRoot")
[Console]::WriteLine("status=$status")
if ($trxList.Count -eq 0) {
    [Console]::WriteLine('diagnostic=no-trx-files-yet')
}
if (-not [string]::IsNullOrWhiteSpace($selected.MetadataDiagnostic)) {
    [Console]::WriteLine("diagnostic=$($selected.MetadataDiagnostic)")
}
foreach ($diagnostic in $diagnostics) {
    [Console]::WriteLine("diagnostic=$($diagnostic.Kind) file=$($diagnostic.File) detail=$($diagnostic.Detail)")
}
[Console]::WriteLine("failedChecks=$failedChecks")
if ($status -eq 'ok') {
    [Console]::WriteLine([string]::Format($invariant, 'trx={0} executed~{1} failed={2} distinctSignatures={3}', $trxList.Count, $executed, $parsedFailed, $ordered.Count))
}
else {
    [Console]::WriteLine([string]::Format($invariant, 'trx={0} executed~{1} failed>={2} distinctSignatures>={3}', $trxList.Count, $executed, $parsedFailed, $ordered.Count))
}
[Console]::WriteLine([string]::Format($invariant, 'unreadable={0} unparseable={1} otherNonPassing={2} signaturesSuppressed={3}', $unreadable, $unparseable, $otherNonPassing, $suppressed))
if ($counterFailed -ne $parsedFailed -and $parsedFiles -gt 0) {
    [Console]::WriteLine([string]::Format($invariant, 'warning=counters-mismatch parsed={0} counters={1}', $parsedFailed, $counterFailed))
}
if ($parsedFailed -eq 0 -and $parsedFiles -gt 0 -and $status -eq 'ok') {
    [Console]::WriteLine('No failing test results in this attempt corpus.')
}
elseif ($parsedFailed -eq 0 -and $parsedFiles -gt 0) {
    [Console]::WriteLine('No failing test results found in readable TRX files; attempt corpus is partial.')
}

$shown = [Math]::Min($Top, $ordered.Count)
for ($index = 0; $index -lt $shown; $index++) {
    $signature = $ordered[$index]
    $percentage = if ($parsedFailed -eq 0) { 0.0 } else { 100.0 * $signature.Count / $parsedFailed }
    [Console]::WriteLine([string]::Format($invariant, '[{0}] count={1} ({2:0.0}%)', $index + 1, $signature.Count, $percentage))
    [Console]::WriteLine("    message=$(Get-PrintableText $signature.Message)")
    [Console]::WriteLine("    frame=$($signature.Frame)")
    [Console]::WriteLine("    source=$($signature.Source)")
    [Console]::WriteLine("    example=$($signature.Example)")
}

exit 0
