<#
.SYNOPSIS
  Prototype: run a worker at LOW integrity as the SAME user — codex works (toolchain + auth
  reachable, same user) while writes are confined to a low-labeled worktree (MIC no-write-up blocks
  the medium-integrity profile/repo). A process can lower its OWN integrity with no privilege, so we
  launch a normal child that drops itself to Low, then runs codex (children inherit Low).
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$work = Join-Path $env:TEMP ("mcg-lowil-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
icacls $work /setintegritylevel "(OI)(CI)L" | Out-Null
$escape = Join-Path (Get-Location) ("lowil-escape-" + [Guid]::NewGuid().ToString('N') + ".txt")

# Probe is a LITERAL here-string (no outer expansion): it drops itself to Low, then probes.
$probe = Join-Path $work 'probe.ps1'
@'
param([string]$EscapeTarget)
$ErrorActionPreference = 'Continue'
Add-Type -Namespace P -Name N -MemberDefinition @"
[DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
[DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr h, uint a, out IntPtr t);
[DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool ConvertStringSidToSidW(string s, out IntPtr sid);
[DllImport("advapi32.dll", SetLastError=true)] public static extern bool SetTokenInformation(IntPtr t, int c, ref TML info, int len);
[StructLayout(LayoutKind.Sequential)] public struct SAA { public IntPtr Sid; public uint Attr; }
[StructLayout(LayoutKind.Sequential)] public struct TML { public SAA Label; }
public static void DropToLow() {
    IntPtr tok, sid;
    if (!OpenProcessToken(GetCurrentProcess(), 0x0088, out tok)) throw new System.ComponentModel.Win32Exception();
    if (!ConvertStringSidToSidW("S-1-16-4096", out sid)) throw new System.ComponentModel.Win32Exception();
    var t = new TML(); t.Label.Sid = sid; t.Label.Attr = 0x20;
    if (!SetTokenInformation(tok, 25, ref t, Marshal.SizeOf(typeof(TML))+16)) throw new System.ComponentModel.Win32Exception();
}
"@
[P.N]::DropToLow()
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:TEMP = $here; $env:TMP = $here
# Point CODEX_HOME at a Low-labeled writable dir, seeded with the operator's auth so codex stays
# authenticated. The Low process can READ the operator's .codex (reads aren't restricted by MIC).
$ch = Join-Path $here 'codex-home'
New-Item -ItemType Directory -Force $ch | Out-Null
Copy-Item -Path (Join-Path $env:USERPROFILE '.codex\auth.json') -Destination (Join-Path $ch 'auth.json') -ErrorAction SilentlyContinue
$env:CODEX_HOME = $ch
$r = @()
$r += 'whoami-il=' + ((& whoami /groups | Select-String 'Mandatory Level' | ForEach-Object { $_.Line.Trim() }) -join ' ')
$cv = ''
try { $cv = ((& codex --version 2>&1) -join ' ') } catch { $cv = 'ERR ' + $_.Exception.Message }
$r += 'codex-version=' + $cv
try { Set-Content -Path (Join-Path $here 'low-write.txt') -Value 'ok' -ErrorAction Stop; $r += 'write-worktree=ok' } catch { $r += 'write-worktree=FAIL ' + $_.Exception.Message }
try { Set-Content -Path $EscapeTarget -Value 'bad' -ErrorAction Stop; $r += 'write-medium=WROTE(BAD)' } catch { $r += 'write-medium=denied(good)' }
Set-Content -Path (Join-Path $here 'result.txt') -Value ($r -join [Environment]::NewLine)
'@ | Set-Content -Path $probe

$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$pout = Join-Path $work 'probe-out.txt'; $perr = Join-Path $work 'probe-err.txt'
Write-Output "Launching probe (drops itself to Low integrity)..."
Start-Process -FilePath $shell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $probe, $escape) `
    -Wait -WindowStyle Hidden -RedirectStandardOutput $pout -RedirectStandardError $perr

$resultPath = Join-Path $work 'result.txt'
if (Test-Path $resultPath) { Write-Output "--- result ---"; Get-Content $resultPath }
Write-Output "--- probe stdout ---"; Get-Content $pout -ErrorAction SilentlyContinue
Write-Output "--- probe stderr ---"; Get-Content $perr -ErrorAction SilentlyContinue
Write-Output "escape file exists (MUST be False): $(Test-Path $escape)"

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $escape -Force -ErrorAction SilentlyContinue
