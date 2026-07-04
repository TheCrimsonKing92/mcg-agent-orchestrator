[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string]$MarkerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

try {
    $gitHead = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ([string]::IsNullOrWhiteSpace($gitHead)) {
        return
    }

    [System.IO.File]::WriteAllText(
        $MarkerPath,
        $gitHead,
        [System.Text.Encoding]::ASCII)
}
catch {
}
