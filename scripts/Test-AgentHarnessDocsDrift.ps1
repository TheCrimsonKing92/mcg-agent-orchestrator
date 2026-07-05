param(
    [string]$AgentsPath = (Join-Path $PSScriptRoot '..\AGENTS.md'),
    [string]$ClaudePath = (Join-Path $PSScriptRoot '..\CLAUDE.md')
)

$ErrorActionPreference = 'Stop'

$sharedSections = @(
    @{ Anchor = 'output-discipline'; Heading = '## Output Discipline' },
    @{ Anchor = 'retry-and-loop-control'; Heading = '## Retry and Loop Control' },
    @{ Anchor = 'repository-rules'; Heading = '## Repository Rules' },
    @{ Anchor = 'architecture-and-design-discipline'; Heading = '## Architecture & Design Discipline' },
    @{ Anchor = 'specification-discipline'; Heading = '## Specification Discipline' },
    @{ Anchor = 'diagnosis-discipline'; Heading = '## Diagnosis Discipline' },
    @{ Anchor = 'dashboard-dogfood-boundary'; Heading = '## Dashboard / Dogfood Boundary' },
    @{ Anchor = 'operating-the-goal-loop'; Heading = '## Operating the goal loop' },
    @{ Anchor = 'safety'; Heading = '## Safety' },
    @{ Anchor = 'evidence'; Heading = '## Evidence' }
)

function Assert-Contains([string]$Text, [string]$Needle, [string]$Message) {
    if (-not $Text.Contains($Needle)) {
        throw $Message
    }
}

function Assert-NotContains([string]$Text, [string]$Needle, [string]$Message) {
    if ($Text.Contains($Needle)) {
        throw $Message
    }
}

function Get-Field([string[]]$Lines, [string]$Field, [string]$Path) {
    $matches = @($Lines | Where-Object { $_.StartsWith($Field) })
    if ($matches.Count -ne 1) {
        throw "$Path counterpart contract must include exactly one $Field"
    }

    return $matches[0].Substring($Field.Length).Trim()
}

function Get-Contract([string]$Path, [string]$Text) {
    $matches = [regex]::Matches($Text, '<!-- HARNESS-COUNTERPART-CONTRACT:BEGIN -->(?<body>.*?)<!-- HARNESS-COUNTERPART-CONTRACT:END -->', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($matches.Count -ne 1) {
        throw "$Path must contain exactly one counterpart contract block; found $($matches.Count)."
    }

    $lines = @($matches[0].Groups['body'].Value -replace "`r`n", "`n" -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $anchorHeaderIndex = [Array]::IndexOf($lines, 'Shared anchors:')
    if ($anchorHeaderIndex -lt 0) {
        throw "$Path counterpart contract must include Shared anchors."
    }

    $anchors = @()
    for ($index = $anchorHeaderIndex + 1; $index -lt $lines.Count; $index++) {
        if (-not $lines[$index].StartsWith('- ')) {
            break
        }

        $anchors += $lines[$index].Substring(2)
    }

    if ($anchors.Count -eq 0) {
        throw "$Path counterpart contract must list shared anchors."
    }

    [pscustomobject]@{
        Owns = Get-Field $lines 'Owns:' $Path
        Counterpart = Get-Field $lines 'Counterpart:' $Path
        Rule = Get-Field $lines 'Rule:' $Path
        SharedAnchors = $anchors
    }
}

$agents = Get-Content -Raw $AgentsPath
$claude = Get-Content -Raw $ClaudePath

Assert-Contains $agents 'docs/operator-runbook.md' 'AGENTS.md must reference docs/operator-runbook.md.'
Assert-Contains $claude 'docs/operator-runbook.md' 'CLAUDE.md must reference docs/operator-runbook.md.'

$agentsContract = Get-Contract 'AGENTS.md' $agents
$claudeContract = Get-Contract 'CLAUDE.md' $claude

if ($agentsContract.Owns -ne 'shared repository discipline plus Codex harness operating guidance.') {
    throw "AGENTS.md contract Owns changed."
}

if ($agentsContract.Counterpart -ne 'CLAUDE.md owns Claude Code harness operating guidance and links back to AGENTS.md for shared discipline.') {
    throw "AGENTS.md contract Counterpart changed."
}

if ($claudeContract.Owns -ne 'Claude Code harness operating guidance.') {
    throw "CLAUDE.md contract Owns changed."
}

if ($claudeContract.Counterpart -ne 'AGENTS.md owns shared repository discipline plus Codex harness operating guidance.') {
    throw "CLAUDE.md contract Counterpart changed."
}

if ($agentsContract.Rule -ne $claudeContract.Rule) {
    throw 'Counterpart contract rules must match.'
}

Assert-Contains $agentsContract.Rule 'update BOTH AGENTS.md and CLAUDE.md' 'Contract rule must require updating both files.'
Assert-Contains $agentsContract.Rule 'move the content to docs/operator-runbook.md or another shared home' 'Contract rule must name the shared-home escape hatch.'

$expectedAnchors = @($sharedSections | ForEach-Object { $_.Anchor })
if (($expectedAnchors -join "`n") -ne ($agentsContract.SharedAnchors -join "`n")) {
    throw "AGENTS.md shared-anchor list changed. Expected [$($expectedAnchors -join ', ')], got [$($agentsContract.SharedAnchors -join ', ')]."
}

if (($agentsContract.SharedAnchors -join "`n") -ne ($claudeContract.SharedAnchors -join "`n")) {
    throw "Shared anchor lists must match. AGENTS.md has [$($agentsContract.SharedAnchors -join ', ')], CLAUDE.md has [$($claudeContract.SharedAnchors -join ', ')]."
}

foreach ($section in $sharedSections) {
    $marker = "<!-- shared-discipline:$($section.Anchor) -->"
    Assert-Contains $agents $marker "AGENTS.md must carry shared marker $marker."
    Assert-Contains $agents $section.Heading "AGENTS.md must carry shared heading $($section.Heading)."
    Assert-NotContains $claude $marker "CLAUDE.md must not duplicate shared marker $marker."
    Assert-NotContains $claude $section.Heading "CLAUDE.md must not duplicate shared heading $($section.Heading)."
}

Write-Output 'agent harness docs drift check passed'
