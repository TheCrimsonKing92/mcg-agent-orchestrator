<#
.SYNOPSIS
  Regenerate bounded acceptance-gate flake inventory supplements from normalized receipt extracts.

.DESCRIPTION
  Validates a versioned extract of retained goal-acceptance receipts, groups observations by test,
  and atomically replaces the generated supplement block in the inventory document. Publication
  fails closed: missing, malformed, duplicate, or count-incomplete evidence never becomes a zero.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Update-AcceptanceGateFlakeInventory.ps1 `
    -EvidencePath docs\acceptance-gate-flake-inventory-receipts\2026-08-24-goals.json `
    -DocumentPath docs\acceptance-gate-flake-inventory.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$DocumentPath,

    [string]$ScanTimestampUtc
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$beginMarker = '<!-- acceptance-gate-flake-inventory-supplements:BEGIN -->'
$endMarker = '<!-- acceptance-gate-flake-inventory-supplements:END -->'

function Stop-Generation {
    param([string]$Message)

    [Console]::Error.WriteLine("inventory-generation failed: $Message")
    exit 2
}

function Require-Text {
    param([object]$Value, [string]$Field)

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        Stop-Generation "missing or blank $Field"
    }
    return $text.Trim()
}

function Escape-MarkdownCell {
    param([string]$Value)

    return $Value.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}

function Format-Timestamp {
    param([datetimeoffset]$Value)

    return $Value.ToUniversalTime().ToString('o', $invariant)
}

function Format-Date {
    param([datetimeoffset]$Value)

    return $Value.ToUniversalTime().ToString('yyyy-MM-dd', $invariant)
}

try {
    $resolvedEvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
    $resolvedDocumentPath = [System.IO.Path]::GetFullPath($DocumentPath)
    if (-not [System.IO.File]::Exists($resolvedEvidencePath)) {
        Stop-Generation "evidence file not found: $EvidencePath"
    }
    if (-not [System.IO.File]::Exists($resolvedDocumentPath)) {
        Stop-Generation "document not found: $DocumentPath"
    }

    try {
        $evidenceText = [System.IO.File]::ReadAllText($resolvedEvidencePath)
        $evidence = $evidenceText | ConvertFrom-Json -Depth 20 -ErrorAction Stop
    }
    catch {
        Stop-Generation "evidence is not valid JSON: $($_.Exception.Message)"
    }

    if ([int]$evidence.contractVersion -ne 1) {
        Stop-Generation "unsupported contractVersion '$($evidence.contractVersion)'"
    }

    $source = Require-Text $evidence.source 'source'
    if ($null -eq $evidence.corpus) {
        Stop-Generation 'missing corpus'
    }

    $scopeGoals = @($evidence.corpus.goalIds)
    if ($scopeGoals.Count -eq 0) {
        Stop-Generation 'corpus.goalIds must contain at least one goal'
    }
    $goalSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($goal in $scopeGoals) {
        $goalId = Require-Text $goal 'corpus.goalIds[]'
        if (-not $goalSet.Add($goalId)) {
            Stop-Generation "duplicate corpus goal '$goalId'"
        }
    }

    $expectedReceiptCount = [int]$evidence.corpus.expectedReceiptCount
    $expectedPassCount = [int]$evidence.corpus.expectedPassCount
    $expectedFailureCount = [int]$evidence.corpus.expectedFailureCount
    if ($expectedReceiptCount -le 0 -or $expectedPassCount -lt 0 -or $expectedFailureCount -lt 0) {
        Stop-Generation 'corpus expected counts must be non-negative and expectedReceiptCount must be positive'
    }
    if ($expectedPassCount + $expectedFailureCount -ne $expectedReceiptCount) {
        Stop-Generation 'corpus expected pass/failure counts do not sum to expectedReceiptCount'
    }

    $receipts = @($evidence.receipts)
    if ($receipts.Count -ne $expectedReceiptCount) {
        Stop-Generation "receipt count mismatch: expected=$expectedReceiptCount actual=$($receipts.Count)"
    }

    $identitySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $observedGoals = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $normalized = [System.Collections.Generic.List[object]]::new()
    $passCount = 0
    $failureCount = 0

    foreach ($receipt in $receipts) {
        $identity = Require-Text $receipt.receiptIdentity 'receipts[].receiptIdentity'
        if (-not $identitySet.Add($identity)) {
            Stop-Generation "duplicate receipt identity '$identity'"
        }

        $goalId = Require-Text $receipt.goalId "receipt '$identity' goalId"
        if (-not $goalSet.Contains($goalId)) {
            Stop-Generation "receipt '$identity' references goal outside corpus scope: $goalId"
        }
        [void]$observedGoals.Add($goalId)

        $testName = Require-Text $receipt.testName "receipt '$identity' testName"
        $outcome = (Require-Text $receipt.outcome "receipt '$identity' outcome").ToLowerInvariant()
        if ($outcome -ne 'passed' -and $outcome -ne 'failed') {
            Stop-Generation "receipt '$identity' has unsupported outcome '$outcome'"
        }

        $timestamp = [datetimeoffset]::MinValue
        if (-not [datetimeoffset]::TryParse(
            [string]$receipt.timestampUtc,
            $invariant,
            [System.Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$timestamp)) {
            Stop-Generation "receipt '$identity' has invalid timestampUtc"
        }

        $signature = [string]$receipt.signature
        if ($outcome -eq 'failed') {
            if ([string]::IsNullOrWhiteSpace($signature)) {
                Stop-Generation "failed receipt '$identity' has no signature"
            }
            $signature = $signature.Trim()
            $failureCount++
        }
        else {
            if (-not [string]::IsNullOrWhiteSpace($signature)) {
                Stop-Generation "passed receipt '$identity' unexpectedly has a failure signature"
            }
            $signature = $null
            $passCount++
        }

        [void]$normalized.Add([pscustomobject]@{
            ReceiptIdentity = $identity
            GoalId = $goalId
            TestName = $testName
            Outcome = $outcome
            Timestamp = $timestamp.ToUniversalTime()
            Signature = $signature
        })
    }

    if ($passCount -ne $expectedPassCount -or $failureCount -ne $expectedFailureCount) {
        Stop-Generation "outcome count mismatch: expected=$expectedPassCount passed/$expectedFailureCount failed actual=$passCount passed/$failureCount failed"
    }
    foreach ($goalId in $goalSet) {
        if (-not $observedGoals.Contains($goalId)) {
            Stop-Generation "corpus goal '$goalId' has no receipt"
        }
    }

    $scanTimestamp = [datetimeoffset]::UtcNow
    if (-not [string]::IsNullOrWhiteSpace($ScanTimestampUtc)) {
        if (-not [datetimeoffset]::TryParse(
            $ScanTimestampUtc,
            $invariant,
            [System.Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$scanTimestamp)) {
            Stop-Generation 'ScanTimestampUtc is invalid'
        }
    }
    $scanTimestamp = $scanTimestamp.ToUniversalTime()

    $evidenceHash = [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($evidenceText))).ToLowerInvariant()
    $goalScope = @($goalSet | Sort-Object) -join ', '
    $perGoalCounts = @($normalized | Group-Object GoalId | Sort-Object Name | ForEach-Object {
        $goalReceipts = @($_.Group)
        $goalPasses = @($goalReceipts | Where-Object Outcome -eq 'passed').Count
        $goalFailures = @($goalReceipts | Where-Object Outcome -eq 'failed').Count
        "``$($_.Name)`` $goalPasses pass/$goalFailures fail"
    }) -join '; '
    $invocation = ".\scripts\Invoke-RepoScript.ps1 scripts\Update-AcceptanceGateFlakeInventory.ps1 -EvidencePath $EvidencePath -DocumentPath $DocumentPath"
    if (-not [string]::IsNullOrWhiteSpace($ScanTimestampUtc)) {
        $invocation += " -ScanTimestampUtc $ScanTimestampUtc"
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    [void]$lines.Add($beginMarker)
    [void]$lines.Add('## Generated post-census receipt supplements')
    [void]$lines.Add('')
    [void]$lines.Add('These rows are regenerated from versioned normalized extracts of retained goal-acceptance receipts. They supplement the historical full-corpus census below; they do not reclassify its older rows or claim a cause for the observed failures.')
    [void]$lines.Add('')
    [void]$lines.Add("- Regeneration scan (UTC): ``$(Format-Timestamp $scanTimestamp)``")
    [void]$lines.Add("- Corpus scope: goal acceptance receipts for ``$goalScope``")
    [void]$lines.Add("- Input extract: ``$EvidencePath`` — SHA-256 ``$evidenceHash``")
    [void]$lines.Add("- Input counts after receipt-identity deduplication: $($normalized.Count) attempts; $passCount pass; $failureCount fail")
    [void]$lines.Add("- Per-goal counts: $perGoalCounts")
    [void]$lines.Add("- Source attestation: $source")
    [void]$lines.Add("- Exact invocation: ``$invocation``")
    [void]$lines.Add('- Completeness: validated; missing goals, duplicate identities, malformed timestamps, absent failure signatures, and count mismatches fail generation before the document is written.')
    [void]$lines.Add('')
    [void]$lines.Add('| Test | Attempts | Pass | Goal fail | Observed failure rate | Failing goals | First | Last | Mechanism |')
    [void]$lines.Add('|---|---:|---:|---:|---:|---:|---|---|---|')

    $groups = @($normalized | Group-Object TestName | Sort-Object Name)
    foreach ($group in $groups) {
        $items = @($group.Group)
        $failures = @($items | Where-Object Outcome -eq 'failed')
        if ($failures.Count -eq 0) {
            continue
        }
        $passes = @($items | Where-Object Outcome -eq 'passed').Count
        $failingGoals = @($failures.GoalId | Sort-Object -Unique).Count
        $first = ($items | Sort-Object Timestamp)[0].Timestamp
        $last = ($items | Sort-Object Timestamp)[-1].Timestamp
        $rate = (100.0 * $failures.Count / $items.Count).ToString('0.00', $invariant) + '%'
        $testCell = Escape-MarkdownCell $group.Name
        [void]$lines.Add("| $testCell | $($items.Count) | $passes | $($failures.Count) | $rate | $failingGoals | $(Format-Date $first) | $(Format-Date $last) | mechanism-undetermined |")
    }

    [void]$lines.Add('')
    [void]$lines.Add('### Supplement failure signatures')
    [void]$lines.Add('')
    foreach ($group in $groups) {
        $items = @($group.Group)
        $failures = @($items | Where-Object Outcome -eq 'failed')
        if ($failures.Count -eq 0) {
            continue
        }
        [void]$lines.Add("- ``$(Escape-MarkdownCell $group.Name)``")
        foreach ($signatureGroup in @($failures | Group-Object Signature | Sort-Object -Property @{ Expression = 'Count'; Descending = $true }, Name)) {
            [void]$lines.Add("  - $($signatureGroup.Count)x ``$(Escape-MarkdownCell $signatureGroup.Name)``")
        }
    }
    [void]$lines.Add($endMarker)

    $document = [System.IO.File]::ReadAllText($resolvedDocumentPath)
    $beginIndex = $document.IndexOf($beginMarker, [System.StringComparison]::Ordinal)
    $endIndex = $document.IndexOf($endMarker, [System.StringComparison]::Ordinal)
    $block = ($lines -join "`r`n") + "`r`n`r`n"
    if (($beginIndex -ge 0) -xor ($endIndex -ge 0)) {
        Stop-Generation 'document contains only one generated supplement marker'
    }
    if ($beginIndex -ge 0) {
        if ($endIndex -lt $beginIndex) {
            Stop-Generation 'generated supplement markers are out of order'
        }
        $afterEnd = $endIndex + $endMarker.Length
        while ($afterEnd -lt $document.Length -and ($document[$afterEnd] -eq "`r" -or $document[$afterEnd] -eq "`n")) {
            $afterEnd++
        }
        $updated = $document.Substring(0, $beginIndex) + $block + $document.Substring($afterEnd)
    }
    else {
        $anchor = '## Reproduction commands'
        $anchorIndex = $document.IndexOf($anchor, [System.StringComparison]::Ordinal)
        if ($anchorIndex -lt 0) {
            Stop-Generation "document insertion anchor not found: $anchor"
        }
        $updated = $document.Substring(0, $anchorIndex) + $block + $document.Substring($anchorIndex)
    }

    $encoding = [System.Text.UTF8Encoding]::new($false)
    $temporaryPath = $resolvedDocumentPath + '.tmp-' + [guid]::NewGuid().ToString('N')
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $updated, $encoding)
        [System.IO.File]::Move($temporaryPath, $resolvedDocumentPath, $true)
    }
    finally {
        if ([System.IO.File]::Exists($temporaryPath)) {
            [System.IO.File]::Delete($temporaryPath)
        }
    }

    $outputHash = [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($resolvedDocumentPath))).ToLowerInvariant()
    [Console]::WriteLine("status=updated receipts=$($normalized.Count) passed=$passCount failed=$failureCount groups=$($groups.Count) outputSha256=$outputHash")
}
catch {
    Stop-Generation $_.Exception.Message
}
