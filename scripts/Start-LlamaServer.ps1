[CmdletBinding()]
param(
    [string] $ModelPath = (Join-Path $env:USERPROFILE 'models\Qwen3.6-35B-A3B-UD-IQ4_NL.gguf'),
    [string] $LlamaCppDir = (Join-Path $env:USERPROFILE 'tools\llama-b10488'),
    [string] $HostAddress = '127.0.0.1',
    [int] $Port = 8080,
    [int] $GpuLayers = 41,
    [int] $CpuMoe = 30,
    [int] $Threads = 20,
    [int] $Context = 8192,
    [string] $Alias = 'qwen3.6-35b-a3b',
    # Confirmed IQ4 fit-params: ngl 41, blk.14 ffn_down + blk.15-40 expert FFN on CPU.
    # Empty string restores the older -ngl 99 -ncmoe <CpuMoe> profile.
    [string] $OverrideTensor = 'blk\.14\.ffn_down_exps=CPU;blk\.(1[5-9]|[2-3][0-9]|40)\.ffn_(up|down|gate)_exps=CPU',
    [switch] $PrintArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$server = Join-Path $LlamaCppDir 'llama-server.exe'

$serverArgs = @(
    '-m', $ModelPath,
    '--host', $HostAddress,
    '--port', "$Port",
    '-ngl', "$GpuLayers",
    '-t', "$Threads",
    '-c', "$Context",
    '-fa', 'on',
    '-ctk', 'q8_0',
    '-ctv', 'q8_0',
    '-a', $Alias
)
# -ot and -ncmoe are mutually exclusive. Commas in -ot are a llama-bench sweep; this server path uses semicolons.
if ([string]::IsNullOrWhiteSpace($OverrideTensor)) {
    $serverArgs += @('-ncmoe', "$CpuMoe")
}
else {
    $serverArgs += @('-ot', $OverrideTensor)
}

if ($PrintArgs) {
    $serverArgs | ForEach-Object { Write-Output $_ }
    exit 0
}

if (-not (Test-Path -LiteralPath $server)) {
    throw "llama-server.exe not found at $server"
}

if (-not (Test-Path -LiteralPath $ModelPath)) {
    throw "Model not found at $ModelPath"
}

$logDir = Join-Path $env:USERPROFILE 'tools\llama-b10488\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$stdout = Join-Path $logDir "llama-server-$stamp.out.log"
$stderr = Join-Path $logDir "llama-server-$stamp.err.log"

$process = Start-Process -FilePath $server -ArgumentList $serverArgs -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
Write-Output "pid=$($process.Id)"
Write-Output "url=http://${HostAddress}:$Port/v1"
Write-Output "stdout=$stdout"
Write-Output "stderr=$stderr"
