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
    New-Item -ItemType Directory -Force -Path $receiptDirectory | Out-Null
    $ReceiptPath = Join-Path $receiptDirectory "$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff')).json"
}

$rules = @(Get-NetFirewallRule -DisplayName "MCG-testhost-slot*" -ErrorAction SilentlyContinue)
$removed = [System.Collections.Generic.List[object]]::new()
foreach ($rule in $rules) {
    $application = $rule | Get-NetFirewallApplicationFilter
    $address = $rule | Get-NetFirewallAddressFilter
    $port = $rule | Get-NetFirewallPortFilter
    $receipt = [ordered]@{
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
    }

    if ($PSCmdlet.ShouldProcess($rule.DisplayName, "Remove retired MCG test-slot firewall rule")) {
        Remove-NetFirewallRule -Name $rule.Name
        $removed.Add([pscustomobject]$receipt)
    }
}

$receiptParent = Split-Path -Parent $ReceiptPath
if (-not [string]::IsNullOrWhiteSpace($receiptParent)) {
    New-Item -ItemType Directory -Force -Path $receiptParent | Out-Null
}

[ordered]@{
    version = 1
    removedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    machineName = $env:COMPUTERNAME
    rulePattern = "MCG-testhost-slot*"
    removedCount = $removed.Count
    rules = $removed
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReceiptPath

Write-Output "Firewall cleanup: removed $($removed.Count) retired test-slot rule(s); receipt=$ReceiptPath"
