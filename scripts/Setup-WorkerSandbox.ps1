<#
.SYNOPSIS
  One-time elevated setup for the OS-level worker sandbox (Windows).

.DESCRIPTION
  Creates a dedicated low-privilege local user that codex (and other implementation workers)
  run AS, so the OS enforces that the worker can only WRITE where the orchestrator explicitly
  grants it (the per-run worktree + git common dir + scratch), and nothing else — true
  filesystem isolation that does NOT depend on codex's own `workspace-write` sandbox (which
  breaks on Windows via `CreateProcessAsUserW failed: 5` under build/test load).

  Design (per two independent expert assessments, 2026-06-19): set the confinement boundary
  ONCE at the root by launching the worker as this account; children inherit the standard-user
  token via ordinary CreateProcess, so heavy `dotnet`/MSBuild/testhost bursts behave like a
  normal standard-user session and never hit codex's per-spawn restricted-token failure.

  This account is a STANDARD user in a SEPARATE profile, so by default it cannot write the
  operator's profile (where the repo lives) or system locations. The orchestrator grants it
  write on just the per-run sandbox root at dispatch time (it owns those dirs, so no admin is
  needed per run). Only THIS account-creation step needs elevation.

  The generated password is stored in Windows Credential Manager under -CredentialTarget so the
  orchestrator (running as you) can retrieve it to launch the worker; it is never written to disk.

.EXAMPLE
  # Run from an ELEVATED PowerShell:
  .\scripts\Setup-WorkerSandbox.ps1
  .\scripts\Setup-WorkerSandbox.ps1 -UserName mcg-worker -CredentialTarget mcg-orchestrator-worker -Rotate
#>
[CmdletBinding()]
param(
    [string]$UserName = 'mcg-worker',
    [string]$CredentialTarget = 'mcg-orchestrator-worker',
    [switch]$Rotate
)

$ErrorActionPreference = 'Stop'

# --- must be elevated ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script creates a local user and must be run from an ELEVATED PowerShell (Run as administrator)."
    exit 1
}

# --- generate a strong random password ---
function New-StrongPassword {
    # Use an RNG instance + GetBytes (available in both Windows PowerShell 5.1 / .NET Framework
    # and PowerShell 7 / .NET); the static RandomNumberGenerator.Fill is .NET Core only.
    $bytes = [byte[]]::new(24)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    # Base64 is mixed-case + digits; append fixed symbols to satisfy complexity policy deterministically.
    return ([Convert]::ToBase64String($bytes) -replace '[/+=]', 'x') + '!Aa9'
}
$password = New-StrongPassword
$securePassword = ConvertTo-SecureString $password -AsPlainText -Force

# --- create or rotate the local user ---
$existing = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
if ($existing -and -not $Rotate) {
    Write-Output "Local user '$UserName' already exists. Re-run with -Rotate to reset its password + credential."
} elseif ($existing -and $Rotate) {
    Set-LocalUser -Name $UserName -Password $securePassword
    Write-Output "Rotated password for existing local user '$UserName'."
} else {
    New-LocalUser -Name $UserName -Password $securePassword -FullName 'MCG Orchestrator Worker' `
        -Description 'Low-priv worker account (OS sandbox).' `
        -PasswordNeverExpires -UserMayNotChangePassword | Out-Null
    Write-Output "Created low-privilege local user '$UserName'."
}

# Ensure it is ONLY a standard user (member of Users, nothing else).
$usersGroup = (Get-LocalGroup -SID 'S-1-5-32-545').Name   # localized "Users"
if (-not (Get-LocalGroupMember -Group $usersGroup -Member $UserName -ErrorAction SilentlyContinue)) {
    Add-LocalGroupMember -Group $usersGroup -Member $UserName
}
# Defense-in-depth: make sure it is NOT an administrator.
$adminsGroup = (Get-LocalGroup -SID 'S-1-5-32-544').Name
if (Get-LocalGroupMember -Group $adminsGroup -Member $UserName -ErrorAction SilentlyContinue) {
    Remove-LocalGroupMember -Group $adminsGroup -Member $UserName
    Write-Warning "Removed '$UserName' from Administrators."
}

# --- store the password in Credential Manager (readable by you, never written to disk) ---
# cmdkey stores a generic credential in the current user's vault; the orchestrator (running as
# you) reads it via CredRead to launch the worker.
cmdkey /generic:$CredentialTarget /user:$UserName /pass:$password | Out-Null

Write-Output ""
Write-Output "Done. Worker sandbox account is ready:"
Write-Output "  user:               $UserName  (standard user, separate profile)"
Write-Output "  credential target:  $CredentialTarget  (Windows Credential Manager)"
Write-Output ""
Write-Output "Next: set the orchestrator config to use this account (worker-account name + credential target)"
Write-Output "and enable the OS sandbox. The orchestrator grants this account write on each per-run sandbox"
Write-Output "root (worktree + git common dir + scratch) at dispatch time; everything else stays default-deny."
