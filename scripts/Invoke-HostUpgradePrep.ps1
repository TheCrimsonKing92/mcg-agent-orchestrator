<#
.SYNOPSIS
  Capture elevated-only host baselines before a RAM upgrade. WITH SWITCHES, THIS SCRIPT PERMANENTLY
  CHANGES SYSTEM SETTINGS (NTFS behaviour and/or pagefile configuration) AND REQUIRES A REBOOT.

.DESCRIPTION
  Run from an ELEVATED PowerShell.

  MUTATION WARNING - read before choosing switches:

    -SetLastAccessOff
        Runs `fsutil behavior set disablelastaccess 1`. System-wide and persistent. NTFS stops
        recording a last-access timestamp on file reads. Anything that relies on last-access times
        (some backup, archival and forensic tooling) loses that signal. Survives reboots until
        explicitly set back.

    -SetFixedPagefileMB <n>
        Sets initial = max = n, i.e. a HARD pagefile ceiling. Your commit limit becomes
        RAM + n with no growth. If commit ever reaches it, allocations FAIL - applications hit
        out-of-memory rather than the system paging further. Do not choose this unless you have
        checked observed commit against the resulting limit with real margin.

    -PagefileInitialMB <a> -PagefileMaxMB <b>
        Bounded growth. Starts at <a>, grows on demand up to <b>. Behaves like system-managed for
        everyday use (no OOM cliff) but cannot expand without limit and fill the volume. Overrides
        -SetFixedPagefileMB if both are given.

  Passing NO switches performs no mutation: it only reads state and writes a timestamped report.
  That capture is the time-critical part - SSD wear counters, Defender exclusions, 8dot3 state,
  memory-compression config and pagefile PeakUsage either require elevation, reset at boot, or
  cannot be reconstructed after a hardware change.

.EXAMPLE
  .\scripts\Invoke-HostUpgradePrep.ps1
  Capture only. No system changes.

.EXAMPLE
  .\scripts\Invoke-HostUpgradePrep.ps1 -SetLastAccessOff -PagefileInitialMB 4096 -PagefileMaxMB 32768
  Capture, disable NTFS last-access updates, and give the pagefile bounded growth (4 GB initial,
  32 GB ceiling). Reboot afterwards.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = "$env:USERPROFILE\Desktop",
    [switch]$SetLastAccessOff,
    # Fixed size: initial = max. Simple, but the commit limit becomes a hard ceiling.
    [int]$SetFixedPagefileMB = 0,
    # Bounded growth: separate initial and max. Grows on demand like system-managed, so there is no
    # OOM cliff, but it cannot expand without limit and consume the disk. Prefer this if you want
    # normal desktop behaviour. Overrides -SetFixedPagefileMB when both are supplied.
    [int]$PagefileInitialMB = 0,
    [int]$PagefileMaxMB = 0
)

$ErrorActionPreference = 'Continue'

$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $OutputDirectory "host-upgrade-baseline-$stamp.txt"
$lines = New-Object System.Collections.Generic.List[string]

function Add-Section {
    param([string]$Title)
    $lines.Add('')
    $lines.Add('=== ' + $Title + ' ===')
}

function Add-Result {
    param([string]$Label, $Value)
    if ($null -eq $Value -or ($Value -is [string] -and [string]::IsNullOrWhiteSpace($Value))) {
        $lines.Add(($Label + ': <none>'))
    } else {
        $lines.Add(($Label + ':'))
        $text = ($Value | Out-String).TrimEnd()
        foreach ($line in ($text -split "`r?`n")) { $lines.Add('  ' + $line) }
    }
}

function Invoke-Safely {
    param([string]$Label, [scriptblock]$Action)
    # Promote non-terminating errors so a denied/missing value is RECORDED in the report
    # instead of spraying red text past the operator and leaving a silent gap.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Stop'
    try { Add-Result $Label (& $Action) }
    catch { $lines.Add(($Label + ': UNAVAILABLE - ' + $_.Exception.Message)) }
    finally { $ErrorActionPreference = $previous }
}

$lines.Add("Host upgrade baseline - $stamp")
$lines.Add("Computer: $env:COMPUTERNAME   Elevated: $isElevated")
if (-not $isElevated) {
    $lines.Add('WARNING: not elevated. The most valuable values (SSD wear, Defender exclusions,')
    $lines.Add('         8dot3 state, memory-compression config) will be missing. Re-run as admin.')
}

Add-Section 'Memory - installed topology and speed'
Invoke-Safely 'DIMMs' { Get-CimInstance Win32_PhysicalMemory |
    Select-Object DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber |
    Format-Table -AutoSize }
Invoke-Safely 'Baseboard' { Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product, Version }
Invoke-Safely 'BIOS' { Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion, ReleaseDate }

Add-Section 'Memory - live pressure (the before picture)'
Invoke-Safely 'Counters' { Get-Counter -Counter `
    '\Memory\Available MBytes', `
    '\Memory\Committed Bytes', `
    '\Memory\Commit Limit', `
    '\Memory\Pages Input/sec', `
    '\Memory\Standby Cache Normal Priority Bytes', `
    '\Paging File(_Total)\% Usage' -MaxSamples 1 |
    Select-Object -ExpandProperty CounterSamples |
    Select-Object Path, CookedValue | Format-Table -AutoSize }
Invoke-Safely 'Top 20 by private commit' { Get-Process |
    Sort-Object PrivateMemorySize64 -Descending |
    Select-Object -First 20 Name, Id, WorkingSet64, PrivateMemorySize64 | Format-Table -AutoSize }
Invoke-Safely 'Total private commit' { Get-Process |
    Measure-Object -Property PrivateMemorySize64 -Sum | Select-Object Count, Sum }

Add-Section 'Pagefile and hibernation'
Invoke-Safely 'PageFileUsage (PeakUsage RESETS AT BOOT - capture before reboot)' {
    Get-CimInstance Win32_PageFileUsage |
    Select-Object Name, AllocatedBaseSize, CurrentUsage, PeakUsage }
Invoke-Safely 'PageFileSetting' { Get-CimInstance Win32_PageFileSetting |
    Select-Object Name, InitialSize, MaximumSize }
Invoke-Safely 'AutomaticManagedPagefile' { (Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile }
Invoke-Safely 'Crash dump policy' { Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' |
    Select-Object CrashDumpEnabled, DumpFile, AutoReboot }
Invoke-Safely 'Sleep states' { powercfg /a }
Invoke-Safely 'hiberfil.sys size' { Get-Item 'C:\hiberfil.sys' -Force |
    Select-Object FullName, Length }

Add-Section 'Storage'
Invoke-Safely 'Volume C' { Get-Volume -DriveLetter C |
    Select-Object DriveLetter, FileSystemType, Size, SizeRemaining }
Invoke-Safely 'Physical disks' { Get-PhysicalDisk |
    Select-Object FriendlyName, MediaType, BusType, Size, HealthStatus, FirmwareVersion }
Invoke-Safely 'SSD wear/reliability (ELEVATED ONLY - cannot be reconstructed later)' {
    Get-PhysicalDisk | Get-StorageReliabilityCounter 2>$null |
    Select-Object DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal }

Add-Section 'NTFS behaviour'
Invoke-Safely 'disablelastaccess' { fsutil behavior query disablelastaccess }
Invoke-Safely 'disable8dot3 (ELEVATED ONLY)' { fsutil behavior query disable8dot3 C: }
Invoke-Safely 'memoryusage' { fsutil behavior query memoryusage }
Invoke-Safely 'TRIM (DisableDeleteNotify 0 = TRIM on)' { fsutil behavior query DisableDeleteNotify }

Add-Section 'Memory compression'
Invoke-Safely 'MMAgent (ELEVATED ONLY)' { Get-MMAgent 2>$null }

Add-Section 'Defender'
Invoke-Safely 'Exclusion paths (ELEVATED ONLY)' { (Get-MpPreference).ExclusionPath }
Invoke-Safely 'Exclusion processes (ELEVATED ONLY)' { (Get-MpPreference).ExclusionProcess }
Invoke-Safely 'Exclusion extensions (ELEVATED ONLY)' { (Get-MpPreference).ExclusionExtension }

# ---------------- optional changes ----------------

$applied = New-Object System.Collections.Generic.List[string]

if ($SetLastAccessOff) {
    Add-Section 'CHANGE: disable NTFS last-access updates'
    if (-not $isElevated) {
        $lines.Add('  SKIPPED - requires elevation.')
    } else {
        Invoke-Safely 'Before' { fsutil behavior query disablelastaccess }
        Invoke-Safely 'Set' { fsutil behavior set disablelastaccess 1 }
        Invoke-Safely 'After' { fsutil behavior query disablelastaccess }
        $applied.Add('NTFS last-access updates disabled (takes effect after reboot).')
    }
}

$wantInitial = 0
$wantMax = 0
if ($PagefileInitialMB -gt 0 -and $PagefileMaxMB -gt 0) {
    $wantInitial = $PagefileInitialMB
    $wantMax = $PagefileMaxMB
}
elseif ($SetFixedPagefileMB -gt 0) {
    $wantInitial = $SetFixedPagefileMB
    $wantMax = $SetFixedPagefileMB
}

if ($wantMax -gt 0) {
    $shape = if ($wantInitial -eq $wantMax) { 'fixed' } else { 'bounded-growth' }
    Add-Section "CHANGE: pagefile on C: initial=${wantInitial} MB max=${wantMax} MB ($shape)"
    if (-not $isElevated) {
        $lines.Add('  SKIPPED - requires elevation.')
    } else {
        try {
            $cs = Get-CimInstance Win32_ComputerSystem
            if ($cs.AutomaticManagedPagefile) {
                Set-CimInstance -InputObject $cs -Property @{ AutomaticManagedPagefile = $false }
                $lines.Add('  Disabled automatic management.')
            }

            $existing = Get-CimInstance Win32_PageFileSetting -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'C:*' }
            if ($existing) {
                Set-CimInstance -InputObject $existing -Property @{
                    InitialSize = $wantInitial
                    MaximumSize = $wantMax
                }
                $lines.Add('  Updated existing pagefile setting.')
            } else {
                New-CimInstance -ClassName Win32_PageFileSetting -Property @{
                    Name        = 'C:\pagefile.sys'
                    InitialSize = $wantInitial
                    MaximumSize = $wantMax
                } | Out-Null
                $lines.Add('  Created pagefile setting.')
            }

            Invoke-Safely 'After' { Get-CimInstance Win32_PageFileSetting |
                Select-Object Name, InitialSize, MaximumSize }
            $applied.Add("Pagefile initial=${wantInitial} MB max=${wantMax} MB (takes effect after reboot).")
        }
        catch {
            $lines.Add('  FAILED - ' + $_.Exception.Message)
        }
    }
}

# ---------------- write and summarise ----------------

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Force $OutputDirectory | Out-Null }
Set-Content -Path $reportPath -Value $lines -Encoding UTF8

Write-Output ''
Write-Output "Baseline written to: $reportPath"
Write-Output ''
if (-not $isElevated) {
    Write-Output 'NOT ELEVATED - the elevated-only values are missing. Re-run from an admin shell.'
}
foreach ($change in $applied) { Write-Output ("APPLIED: " + $change) }
if ($applied.Count -gt 0) { Write-Output 'Reboot required for the applied changes to take effect.' }
Write-Output ''
Write-Output 'Keep this file. After the upgrade, re-run without switches and diff the two reports.'
