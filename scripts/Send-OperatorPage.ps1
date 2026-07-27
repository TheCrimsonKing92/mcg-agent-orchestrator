<#
.SYNOPSIS
  Last-resort escape hatch: page the operator's phone from any driving agent.

.DESCRIPTION
  Standalone by design - no orchestrator coupling, no state reads, no locks.
  Primary channel: ntfy.sh push to a secret topic (free, no account). The
  operator subscribes once in the ntfy mobile app; 'urgent' priority can be
  configured there to override quiet hours. Optional fallback: carrier
  email-to-SMS via SMTP when smsGatewayEmail/smtpHost are configured in
  ~\.mcg-operator-page.json (password comes from the env var named by
  smtpPasswordEnvVar - never stored in the file).

  The topic name is the only secret for ntfy: anyone who knows it can post to
  it, so it is a long random string kept in the operator-home config, outside
  the repository.

  Exit code 0 means the primary (or fallback) accepted the message; nonzero
  means every configured channel failed - callers must not assume delivery.

.EXAMPLE
  .\scripts\Send-OperatorPage.ps1 -Message "conduct loop wedged 30m, operator input needed"
.EXAMPLE
  .\scripts\Send-OperatorPage.ps1 -Message "all landings blocked" -Priority urgent -Title "orchestrator stuck"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateLength(1, 2000)]
    [string]$Message,

    [ValidateSet("min", "low", "default", "high", "urgent")]
    [string]$Priority = "high",

    [string]$Title = "mcg orchestrator page",

    [string]$ConfigPath = (Join-Path $HOME ".mcg-operator-page.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Write-Output "PAGE_FAILED reason=config-missing path=$ConfigPath"
    exit 2
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$sent = $false

# --- Primary: ntfy.sh push ---
if (-not [string]::IsNullOrWhiteSpace([string]$config.ntfyTopic)) {
    $uri = "$($config.ntfyServer)/$($config.ntfyTopic)"
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Post -Body $Message -Headers @{
            Title    = $Title
            Priority = $Priority
            Tags     = "rotating_light"
        } -TimeoutSec 20 -UseBasicParsing
        if ($response.StatusCode -eq 200) {
            Write-Output "PAGE_SENT channel=ntfy topic=$($config.ntfyTopic) priority=$Priority bytes=$($Message.Length)"
            $sent = $true
        }
        else {
            Write-Output "PAGE_CHANNEL_FAILED channel=ntfy status=$($response.StatusCode)"
        }
    }
    catch {
        Write-Output "PAGE_CHANNEL_FAILED channel=ntfy error=$($_.Exception.Message)"
    }
}

# --- Optional fallback: carrier email-to-SMS via SMTP ---
if (-not $sent -and
    -not [string]::IsNullOrWhiteSpace([string]$config.smsGatewayEmail) -and
    -not [string]::IsNullOrWhiteSpace([string]$config.smtpHost)) {
    try {
        $password = [Environment]::GetEnvironmentVariable([string]$config.smtpPasswordEnvVar, "User")
        if ([string]::IsNullOrWhiteSpace($password)) {
            throw "SMTP password env var '$($config.smtpPasswordEnvVar)' is not set."
        }

        $client = [System.Net.Mail.SmtpClient]::new([string]$config.smtpHost, [int]$config.smtpPort)
        $client.EnableSsl = $true
        $client.Credentials = [System.Net.NetworkCredential]::new([string]$config.smtpUser, $password)
        $mail = [System.Net.Mail.MailMessage]::new(
            [string]$config.smtpUser,
            [string]$config.smsGatewayEmail,
            $Title,
            $Message.Substring(0, [Math]::Min(140, $Message.Length)))
        $client.Send($mail)
        Write-Output "PAGE_SENT channel=sms-gateway to=$($config.smsGatewayEmail)"
        $sent = $true
    }
    catch {
        Write-Output "PAGE_CHANNEL_FAILED channel=sms-gateway error=$($_.Exception.Message)"
    }
}

if (-not $sent) {
    Write-Output "PAGE_FAILED reason=all-channels-failed"
    exit 1
}
