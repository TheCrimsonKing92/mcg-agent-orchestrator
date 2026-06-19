<#
.SYNOPSIS
  Smoke-test the OS worker-sandbox primitives against the real mcg-worker account.

.DESCRIPTION
  Validates, before any dispatch-host integration, the two risky pieces of the design:
   1. The orchestrator (running as you, non-elevated) can READ mcg-worker's password back from
      Credential Manager (CredRead) to launch the worker.
   2. Launch-as-user works AND NTFS ACLs confine writes: a process run AS mcg-worker can write
      inside a granted sandbox root but is DENIED writing outside it.

  Run as yourself (NOT elevated) — that is exactly how the orchestrator will call it.

.EXAMPLE
  .\scripts\Test-WorkerSandbox.ps1
#>
[CmdletBinding()]
param(
    [string]$UserName = 'mcg-worker',
    [string]$CredentialTarget = 'mcg-orchestrator-worker'
)
$ErrorActionPreference = 'Stop'

# --- read the stored password from Credential Manager via CredRead (advapi32) ---
Add-Type -Namespace McgCred -Name Native -MemberDefinition @'
[DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern bool CredReadW(string target, int type, int flags, out IntPtr credentialPtr);
[DllImport("advapi32.dll")] public static extern void CredFree(IntPtr cred);
[StructLayout(LayoutKind.Sequential)]
public struct CREDENTIAL {
    public int Flags; public int Type; public IntPtr TargetName; public IntPtr Comment;
    public long LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob;
    public int Persist; public int AttributeCount; public IntPtr Attributes;
    public IntPtr TargetAlias; public IntPtr UserName;
}
public static string ReadPassword(string target) {
    IntPtr p;
    if (!CredReadW(target, 1 /*CRED_TYPE_GENERIC*/, 0, out p)) return null;
    try {
        var c = (CREDENTIAL)System.Runtime.InteropServices.Marshal.PtrToStructure(p, typeof(CREDENTIAL));
        if (c.CredentialBlobSize == 0) return "";
        return System.Runtime.InteropServices.Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
    } finally { CredFree(p); }
}
'@

$plain = [McgCred.Native]::ReadPassword($CredentialTarget)
if ([string]::IsNullOrEmpty($plain)) {
    Write-Error "CredRead returned nothing for '$CredentialTarget'. The orchestrator (you, non-elevated) cannot see the credential — likely the elevation/vault quirk. Re-store it non-elevated: cmdkey /generic:$CredentialTarget /user:$UserName /pass:<password>"
    exit 1
}
Write-Output "OK: read credential '$CredentialTarget' from Credential Manager."
$secure = ConvertTo-SecureString $plain -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential("$env:COMPUTERNAME\$UserName", $secure)

# --- sandbox root (granted) + an out-of-sandbox target that must be DENIED ---
$root = Join-Path $env:TEMP ("mcg-sbx-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root | Out-Null
# Grant the worker Modify on the sandbox root (+ inheritance). Traverse to it works via the
# default "Bypass traverse checking" privilege even though parents aren't granted.
icacls $root /grant "${UserName}:(OI)(CI)M" | Out-Null
$outsideTarget = Join-Path $PSScriptRoot ("..\mcg-escape-test-" + [Guid]::NewGuid().ToString('N') + ".txt")
$outsideTarget = [System.IO.Path]::GetFullPath($outsideTarget)   # inside the repo (your profile) — should be DENIED

# --- probe script (runs AS mcg-worker); literal here-string, takes the outside target as an arg ---
$probe = Join-Path $root 'probe.ps1'
@'
param([string]$OutsideTarget)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$lines = @("whoami=" + (whoami))
try { Set-Content -Path (Join-Path $here 'inside.txt') -Value 'ok' -ErrorAction Stop; $lines += 'inside=ok' }
catch { $lines += 'inside=FAIL ' + $_.Exception.Message }
try { Set-Content -Path $OutsideTarget -Value 'escaped' -ErrorAction Stop; $lines += 'outside=WROTE(BAD)' }
catch { $lines += 'outside=denied(good)' }
Set-Content -Path (Join-Path $here 'result.txt') -Value ($lines -join "`n")
'@ | Set-Content -Path $probe

Write-Output "Launching probe AS $UserName ..."
# WorkingDirectory must be a path the worker can access (the cwd default is your profile, which
# it can't reach). For a real dispatch this is the granted worktree; here it's the sandbox root.
Start-Process -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $probe, $outsideTarget) `
    -Credential $cred -WorkingDirectory $root -Wait -WindowStyle Hidden

# --- report ---
$resultPath = Join-Path $root 'result.txt'
if (Test-Path $resultPath) {
    Write-Output "--- probe result (ran as the worker) ---"
    Get-Content $resultPath
} else {
    Write-Warning "No result.txt produced — the launch-as-user likely failed (logon right / winsta / profile). Investigate."
}
Write-Output "Escape file created outside sandbox (MUST be False): $(Test-Path $outsideTarget)"

# --- cleanup ---
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $outsideTarget -Force -ErrorAction SilentlyContinue
