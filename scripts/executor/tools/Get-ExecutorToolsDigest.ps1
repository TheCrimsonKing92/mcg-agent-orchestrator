param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$OutFile
)

$ErrorActionPreference = 'Stop'
$temporaryPath = "$OutFile.tmp"
try {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Tools folder is missing: $Path"
    }
    $files = @(Get-ChildItem -LiteralPath $Path -File -Force | Where-Object { $_.Extension -ieq '.ps1' })
    if ($files.Count -eq 0) { throw "Tools folder has no PowerShell scripts: $Path" }

    [string[]]$names = @($files | ForEach-Object { $_.Name.ToLowerInvariant() })
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $pathsByName = @{}
    foreach ($file in $files) { $pathsByName[$file.Name.ToLowerInvariant()] = $file.FullName }
    $lines = New-Object System.Text.StringBuilder
    foreach ($name in $names) {
        $hash = (Get-FileHash -LiteralPath $pathsByName[$name] -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$lines.Append($hash).Append('  ').Append($name).Append("`n")
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = [BitConverter]::ToString($sha256.ComputeHash($utf8.GetBytes($lines.ToString()))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha256.Dispose() }
    $manifest = [ordered]@{ digest = $digest; files = [string[]]$names; utc = [DateTime]::UtcNow.ToString('o') }
    [System.IO.File]::WriteAllText($temporaryPath, (ConvertTo-Json -InputObject $manifest -Compress), $utf8)
    Move-Item -LiteralPath $temporaryPath -Destination $OutFile -Force
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
    exit 1
}
