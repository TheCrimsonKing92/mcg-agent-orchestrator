[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [Parameter(Mandatory = $true)]
    [string]$MarkerPath,

    [string]$ScanReceiptPath,

    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$SourcePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$excludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($directoryName in "bin", "obj", ".scratch", ".orchestrator-prototype") {
    [void]$excludedDirectoryNames.Add($directoryName)
}

function Get-RelevantSourceFile {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.DirectoryInfo]$Root
    )

    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($childDirectory in $directory.EnumerateDirectories()) {
            if (-not $excludedDirectoryNames.Contains($childDirectory.Name)) {
                $pending.Push($childDirectory)
            }
        }

        foreach ($pattern in "*.cs", "*.csproj", "*.props") {
            foreach ($file in $directory.EnumerateFiles(
                    $pattern,
                    [System.IO.SearchOption]::TopDirectoryOnly)) {
                Write-Output $file
            }
        }
    }
}

function Write-ScanReceipt {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Count
    )

    if (-not [string]::IsNullOrWhiteSpace($ScanReceiptPath)) {
        [System.IO.File]::WriteAllText(
            $ScanReceiptPath,
            $Count.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    }
}

try {
    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        exit 1
    }

    try {
        $gitOutput = @(& git -C $RepositoryRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) {
            exit 2
        }
    }
    catch {
        exit 2
    }

    $gitHead = ($gitOutput -join "").Trim()
    $markerHead = (Get-Content -Raw -LiteralPath $MarkerPath).Trim()
    if ([string]::IsNullOrWhiteSpace($gitHead) -or $markerHead -ne $gitHead) {
        exit 1
    }

    $artifactTimestamp = (Get-Item -LiteralPath $ArtifactPath).LastWriteTimeUtc
    $scannedSourceCount = 0
    foreach ($candidatePath in $SourcePath) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $candidate = Get-Item -LiteralPath $candidatePath
            $scannedSourceCount++
            if ($candidate.LastWriteTimeUtc -gt $artifactTimestamp) {
                Write-ScanReceipt -Count $scannedSourceCount
                exit 1
            }

            continue
        }

        if (-not (Test-Path -LiteralPath $candidatePath -PathType Container)) {
            exit 1
        }

        $sourceRoot = Get-Item -LiteralPath $candidatePath
        foreach ($candidate in Get-RelevantSourceFile -Root $sourceRoot) {
            $scannedSourceCount++
            if ($candidate.LastWriteTimeUtc -gt $artifactTimestamp) {
                Write-ScanReceipt -Count $scannedSourceCount
                exit 1
            }
        }
    }

    Write-ScanReceipt -Count $scannedSourceCount
    exit 0
}
catch {
    exit 1
}
