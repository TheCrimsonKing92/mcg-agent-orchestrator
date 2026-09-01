[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [Parameter(Mandatory = $true)]
    [string]$MarkerPath,

    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$SourcePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($extension in ".cs", ".csproj", ".props", ".targets", ".json", ".rsp") {
    [void]$sourceExtensions.Add($extension)
}

function Test-IsGeneratedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $segments = $RelativePath.Split(
        [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar),
        [System.StringSplitOptions]::RemoveEmptyEntries)
    return $segments | Where-Object {
        $_ -in @("bin", "obj", ".scratch", ".orchestrator-prototype")
    } | Select-Object -First 1
}

try {
    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        exit 1
    }

    $gitOutput = @(& git -C $RepositoryRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) {
        exit 1
    }

    $gitHead = ($gitOutput -join "").Trim()
    $markerHead = (Get-Content -Raw -LiteralPath $MarkerPath).Trim()
    if ([string]::IsNullOrWhiteSpace($gitHead) -or $markerHead -ne $gitHead) {
        exit 1
    }

    $artifactTimestamp = (Get-Item -LiteralPath $ArtifactPath).LastWriteTimeUtc
    foreach ($candidatePath in $SourcePath) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $candidate = Get-Item -LiteralPath $candidatePath
            if ($sourceExtensions.Contains($candidate.Extension) -and
                $candidate.LastWriteTimeUtc -gt $artifactTimestamp) {
                exit 1
            }

            continue
        }

        if (-not (Test-Path -LiteralPath $candidatePath -PathType Container)) {
            exit 1
        }

        $sourceRoot = Get-Item -LiteralPath $candidatePath
        foreach ($candidate in Get-ChildItem -LiteralPath $sourceRoot.FullName -Recurse -File -ErrorAction Stop) {
            $relativePath = $candidate.FullName.Substring($sourceRoot.FullName.Length)
            if ((Test-IsGeneratedPath -RelativePath $relativePath) -or
                -not $sourceExtensions.Contains($candidate.Extension)) {
                continue
            }

            if ($candidate.LastWriteTimeUtc -gt $artifactTimestamp) {
                exit 1
            }
        }
    }

    exit 0
}
catch {
    exit 1
}
