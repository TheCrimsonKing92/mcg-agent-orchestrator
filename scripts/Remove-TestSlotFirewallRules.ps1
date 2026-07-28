#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ReceiptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) {
    throw "Test-slot firewall rule removal is supported only on Windows."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $receiptDirectory = Join-Path $repositoryRoot ".orchestrator\firewall-rule-removal"
    $ReceiptPath = Join-Path $receiptDirectory "$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff')).json"
}

$receiptParent = Split-Path -Parent $ReceiptPath
if (-not [string]::IsNullOrWhiteSpace($receiptParent)) {
    New-Item -ItemType Directory -Force -Path $receiptParent | Out-Null
}

function Write-RemovalReceipt {
    param([System.Collections.Generic.List[object]]$Entries)

    $payload = [ordered]@{
        version = 1
        updatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        machineName = $env:COMPUTERNAME
        rulePattern = "MCG-testhost-slot*"
        removedCount = @($Entries | Where-Object { $_.removalState -eq "removed" }).Count
        rules = $Entries
    } | ConvertTo-Json -Depth 6
    $temporaryPath = "$ReceiptPath.$PID.tmp"
    [System.IO.File]::WriteAllText($temporaryPath, $payload)
    [System.IO.File]::Move($temporaryPath, $ReceiptPath, $true)
}

$rules = @(Get-NetFirewallRule -DisplayName "MCG-testhost-slot*" -ErrorAction SilentlyContinue)
$entries = [System.Collections.Generic.List[object]]::new()
foreach ($rule in $rules) {
    $application = $rule | Get-NetFirewallApplicationFilter
    $address = $rule | Get-NetFirewallAddressFilter
    $port = $rule | Get-NetFirewallPortFilter
    $entries.Add([pscustomobject][ordered]@{
        name = $rule.Name
        displayName = $rule.DisplayName
        description = $rule.Description
        enabled = [string]$rule.Enabled
        profile = [string]$rule.Profile
        direction = [string]$rule.Direction
        action = [string]$rule.Action
        program = $application.Program
        localAddress = $address.LocalAddress
        remoteAddress = $address.RemoteAddress
        protocol = $port.Protocol
        localPort = $port.LocalPort
        remotePort = $port.RemotePort
        removalState = "captured"
        removalAttemptedAtUtc = $null
        removedAtUtc = $null
    })
}

# Persist every restoration definition before the first mutation. If removal is
# interrupted, the receipt still contains enough information to restore any rule.
Write-RemovalReceipt -Entries $entries

for ($index = 0; $index -lt $rules.Count; $index++) {
    $rule = $rules[$index]
    $entry = $entries[$index]
    if ($PSCmdlet.ShouldProcess($rule.DisplayName, "Remove retired MCG test-slot firewall rule")) {
        $entry.removalState = "removing"
        $entry.removalAttemptedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        Write-RemovalReceipt -Entries $entries
        Remove-NetFirewallRule -Name $rule.Name
        $entry.removalState = "removed"
        $entry.removedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        Write-RemovalReceipt -Entries $entries
    }
}

$removedCount = @($entries | Where-Object { $_.removalState -eq "removed" }).Count
Write-Output "Firewall cleanup: removed $removedCount retired test-slot rule(s); receipt=$ReceiptPath"
