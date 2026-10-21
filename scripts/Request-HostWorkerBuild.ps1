param(
    [Parameter(Mandatory = $true)][string[]]$Projects,
    [string]$Configuration = 'Debug',
    [string]$RequestKey = ([Guid]::NewGuid().ToString('N'))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$descriptorPath = Join-Path (Get-Location).Path '.orchestrator-context\worker-build-channel.json'
$descriptorStream = [System.IO.FileStream]::new($descriptorPath, [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
try {
    $descriptorReader = [System.IO.StreamReader]::new($descriptorStream)
    try { $descriptor = $descriptorReader.ReadToEnd() | ConvertFrom-Json }
    finally { $descriptorReader.Dispose() }
}
finally { $descriptorStream.Dispose() }
if ($descriptor.version -ne 1 -or $descriptor.pipe -notmatch '^mcg-worker-build-[a-f0-9]{32}$') {
    throw 'Unsupported managed worker build endpoint descriptor.'
}
# Request bytes and the descriptor are transport only. The host verifies this connection's OS peer,
# canonical dispatch, scope and candidate; only its canonical terminal record is build evidence.
if ($null -eq ('Mcg.WorkerBuildPipeClient' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Mcg {
    public static class WorkerBuildPipeClient {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafePipeHandle CreateFileW(string name, uint access, uint share,
            IntPtr attributes, uint creation, uint flags, IntPtr template);
        public static NamedPipeClientStream Open(string name) {
            // Do not request FILE_CREATE_PIPE_INSTANCE, which GenericWrite would include.
            var handle = CreateFileW(@"\\.\pipe\" + name, 0x0012019b, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new NamedPipeClientStream(PipeDirection.InOut, false, true, handle);
        }
    }
}
'@
}
$pipe = [Mcg.WorkerBuildPipeClient]::Open([string]$descriptor.pipe)
try {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.IO.StreamWriter]::new($pipe, $encoding, 4096, $true)
    $reader = [System.IO.StreamReader]::new($pipe, $encoding, $false, 4096, $true)
    try {
        $request = @{ Version = 1; RequestKey = $RequestKey; Projects = @($Projects); Configuration = $Configuration }
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 3))
        $writer.Flush()
        $responseText = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($responseText)) { throw 'Managed worker build host closed without a terminal response.' }
        $response = $responseText | ConvertFrom-Json
        Write-Output "worker_build_invocation=$($response.InvocationId); outcome=$($response.Outcome); $($response.Diagnostic)"
        exit ([int]$response.ExitCode)
    }
    finally { $writer.Dispose(); $reader.Dispose() }
}
finally { $pipe.Dispose() }
