<#
.SYNOPSIS
  Validate the dispatch host's EXACT launch path (System.Diagnostics.Process with UserName +
  redirected streams) running the real toolchain as mcg-worker, fast — without a full dispatch.

.DESCRIPTION
  The host launches the worker via .NET ProcessStartInfo with UserName set (CreateProcessWithLogonW)
  and redirected stdout/stderr. This mirrors that exactly, launching System32 powershell.exe (the
  sandbox shell) as mcg-worker and running `Get-Command codex` + `codex --version` to confirm the
  account can resolve and execute the worker toolchain. Surfaces PATH/access issues in one shot.
#>
[CmdletBinding()]
param(
    [string]$UserName = 'mcg-worker',
    [string]$CredentialTarget = 'mcg-orchestrator-worker'
)
$ErrorActionPreference = 'Stop'

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
    IntPtr p; if (!CredReadW(target, 1, 0, out p)) return null;
    try {
        var c = (CREDENTIAL)System.Runtime.InteropServices.Marshal.PtrToStructure(p, typeof(CREDENTIAL));
        if (c.CredentialBlobSize == 0) return "";
        return System.Runtime.InteropServices.Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize/2);
    } finally { CredFree(p); }
}
'@

$plain = [McgCred.Native]::ReadPassword($CredentialTarget)
if ([string]::IsNullOrEmpty($plain)) { Write-Error "CredRead failed for '$CredentialTarget'."; exit 1 }

$root = Join-Path $env:TEMP ("mcg-launch-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root | Out-Null
icacls $root /grant "${UserName}:(OI)(CI)M" | Out-Null
# nvm stores node/codex in the OPERATOR's profile (the nodejs symlink points there), and codex auth
# lives in the operator's ~/.codex. Grant the worker RX on those exact paths so it can resolve, run,
# and authenticate the toolchain. The operator owns these, so no elevation is needed.
$toolchain = @((Get-Item C:\nvm4w\nodejs).Target)[0]
$codexHome = Join-Path $env:USERPROFILE ".codex"
icacls $toolchain /grant "${UserName}:(OI)(CI)RX" 2>&1 | Out-Null
icacls $codexHome /grant "${UserName}:(OI)(CI)RX" 2>&1 | Out-Null
# Read-only traverse on the profile path components down to the toolchain so node can lstat/resolve
# it. RX = read+execute only: the worker still cannot WRITE or DELETE anywhere in the profile, which
# is the actual security goal (write/delete confinement to the worktree).
$cur = $toolchain
while ($cur -and $cur.Length -ge $env:USERPROFILE.Length) {
    icacls $cur /grant "${UserName}:(RX)" 2>&1 | Out-Null
    $cur = Split-Path $cur -Parent
}

$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $shell
$psi.WorkingDirectory = $root
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
[void]$psi.ArgumentList.Add('-NoProfile'); [void]$psi.ArgumentList.Add('-ExecutionPolicy'); [void]$psi.ArgumentList.Add('Bypass')
[void]$psi.ArgumentList.Add('-Command')
[void]$psi.ArgumentList.Add('whoami; "path-has-nvm=" + ($env:Path -split ";" -contains "C:\nvm4w\nodejs"); codex --version')
# Force an explicit env block so the operator's PATH (which has the nvm toolchain) reaches the worker,
# but point the writable dirs at the worker's OWN profile (it cannot write the operator's).
$psi.EnvironmentVariables["PATH"] = $env:Path
$mw = Join-Path $env:SystemDrive ("\Users\" + $UserName)
$psi.EnvironmentVariables["USERPROFILE"] = $mw
$psi.EnvironmentVariables["TEMP"] = (Join-Path $mw "AppData\Local\Temp")
$psi.EnvironmentVariables["TMP"]  = (Join-Path $mw "AppData\Local\Temp")
$psi.EnvironmentVariables["CODEX_HOME"] = $codexHome   # share the operator's codex auth
$psi.UserName = $UserName
$psi.Domain = '.'
$psi.PasswordInClearText = $plain

Write-Output "Launching System32 powershell as $UserName (mimicking the dispatch host)..."
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    Write-Output "exit=$($p.ExitCode)"
    Write-Output "--- stdout ---"; Write-Output $out
    Write-Output "--- stderr ---"; Write-Output $err
}
catch {
    Write-Output "LAUNCH FAILED: $($_.Exception.GetType().Name): $($_.Exception.Message)"
}

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
