<#
.SYNOPSIS
    Lands a verified-green goal that the acceptance gate refuses to auto-land because of the
    fingerprint / "state changed during acceptance verification" guard false-positive.

.DESCRIPTION
    The acceptance gate compares a full goal-snapshot fingerprint taken before the ~4.5-min
    test suite against one taken at landing time (CliPersistentStateRunner.BuildGoalFingerprint).
    For escalated / retried goals the per-command reconcile churns that snapshot, so the guard
    trips with "state changed during acceptance verification; retry acceptance" EVEN WHEN the
    suite is green and the goal is fully accepted. That escalates correct goals as
    "repeated failures (count: 2)" in the conductor loop.

    This script performs the orchestrator's SANCTIONED out-of-band landing (merge the goal
    branch into main, then `goal-mark-landed`) but ONLY after confirming the goal is genuinely
    landable. It is deliberately conservative:
      - runs `acceptance <prefix>` and ABORTS unless the suite is GREEN (Failed: 0) and the
        goal is fully accepted (5/5 tasks); a real red suite is never landed
      - if acceptance lands the goal directly (no guard trip), it just reports success
      - merges --no-ff and ABORTS (with `git merge --abort`) on any conflict
      - requires the working tree to be on `main`

    The proper long-term fix is to make the acceptance fingerprint guard resilient to benign
    churn so green goals land through the normal gate; until then this is the unblock.

.PARAMETER GoalPrefix
    The 8-char goal prefix (also the worktree dir and the `goal/<prefix>` branch name).

.PARAMETER SkipAcceptance
    Skip the acceptance re-run. Only use when you have JUST confirmed the suite is green for
    this exact worktree state; otherwise the green-suite safety check is bypassed.

.PARAMETER Note
    Optional extra text appended to the integration commit message.

.EXAMPLE
    pwsh scripts/Land-VerifiedGoal.ps1 -GoalPrefix a645091f

.EXAMPLE
    pwsh scripts/Land-VerifiedGoal.ps1 -GoalPrefix 74ec6639 -SkipAcceptance -Note "reconcile-wedge fix (d5d108cf)"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GoalPrefix,
    [switch]$SkipAcceptance,
    [string]$Note = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $repo 'mcg-orchestrator.cmd'
$branch = "goal/$GoalPrefix"

function Abort([string]$m) { Write-Host "[land] ABORT: $m" -ForegroundColor Red; exit 1 }
function Info([string]$m)  { Write-Host "[land] $m" -ForegroundColor Cyan }

Push-Location $repo
try {
    # --- preconditions ---------------------------------------------------------------
    $current = (& git rev-parse --abbrev-ref HEAD).Trim()
    if ($current -ne 'main') { Abort "working tree is on '$current', not 'main'. Switch to main first." }

    & git rev-parse --verify --quiet "$branch" *> $null
    if ($LASTEXITCODE -ne 0) { Abort "branch '$branch' not found." }

    # --- 1. confirm the goal is genuinely landable (green suite, fully accepted) -------
    if (-not $SkipAcceptance) {
        Info "running acceptance for $GoalPrefix (full suite, ~5 min)..."
        $acc = (& $launcher acceptance $GoalPrefix 2>&1 | Out-String)

        if ($acc -notmatch 'acceptance:\s*accepted') {
            Abort "goal is not fully accepted (a verification task may be incomplete). Run 'status $GoalPrefix'; 'recover' if a Tester/Reviewer is Failed.`n--- acceptance tail ---`n$(($acc -split "`n") | Select-Object -Last 12 | Out-String)"
        }

        $failedTotal = 0; $passedTotal = 0; $sawResult = $false
        foreach ($m in [regex]::Matches($acc, 'Failed:\s+(\d+),\s+Passed:\s+(\d+)')) {
            $sawResult = $true
            $failedTotal += [int]$m.Groups[1].Value
            $passedTotal += [int]$m.Groups[2].Value
        }
        if (-not $sawResult) { Abort "could not find a test result line in acceptance output; not landing.`n$acc" }
        if ($failedTotal -ne 0) { Abort "acceptance suite is RED ($failedTotal failed). Fix the failures before landing." }
        Info "suite GREEN: $passedTotal passed, 0 failed."

        if ($acc -notmatch 'changed during acceptance verification' -and $acc -notmatch '(?m)^Error:') {
            Info "acceptance landed the goal directly (no guard trip). Nothing to do."
            return
        }
        Info "green but blocked by the fingerprint/state-changed guard -> out-of-band landing."
    }
    else {
        Info "-SkipAcceptance set; trusting a prior green run for $GoalPrefix."
    }

    # --- 2. sanctioned out-of-band landing: merge --no-ff + goal-mark-landed -----------
    $msg = "Integrate $branch`: verified-green (acceptance suite passed), hand-landed past the acceptance fingerprint guard"
    if ($Note) { $msg = "Integrate $branch`: $Note; verified-green, hand-landed past the acceptance fingerprint guard" }

    Info "merging $branch into main (--no-ff)..."
    & git merge --no-ff $branch -m $msg
    if ($LASTEXITCODE -ne 0) {
        & git merge --abort 2>$null
        Abort "merge failed - aborted, main is unchanged. Rebase the branch onto main and resolve manually."
    }
    $conflicts = (& git diff --name-only --diff-filter=U)
    if ($conflicts) { & git merge --abort 2>$null; Abort "merge produced conflicts - aborted." }

    Info "recording landing via goal-mark-landed..."
    & $launcher goal-mark-landed $GoalPrefix --confirm-goal-mark-landed
    $markLandedExitCode = $LASTEXITCODE
    if ($markLandedExitCode -ne 0) {
        Write-Host "[land] ABORT: goal-mark-landed failed with exit code $markLandedExitCode (the merge IS in main; reconcile bookkeeping manually)." -ForegroundColor Red
        exit $markLandedExitCode
    }

    Write-Host "[land] DONE: $GoalPrefix merged to main and recorded as landed." -ForegroundColor Green
}
finally {
    Pop-Location
}
