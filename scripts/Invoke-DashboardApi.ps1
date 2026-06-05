param(
    [string]$DashboardUrl = "http://localhost:5087/",
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [ValidateSet("GET", "POST")]
    [string]$Method = "GET",
    [string]$Body = "",
    [string[]]$Select = @(),
    [switch]$Raw
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Join-DashboardUrl {
    param(
        [string]$Base,
        [string]$RelativePath
    )

    $normalizedBase = if ($Base.EndsWith("/", [StringComparison]::Ordinal)) { $Base } else { "$Base/" }
    $normalizedPath = if ($RelativePath.StartsWith("/", [StringComparison]::Ordinal)) { $RelativePath.Substring(1) } else { $RelativePath }
    return [Uri]::new([Uri]::new($normalizedBase), $normalizedPath)
}

$uri = Join-DashboardUrl -Base $DashboardUrl -RelativePath $Path
$invokeArgs = @{
    UseBasicParsing = $true
    Method = $Method
    Uri = $uri
}

if (-not [string]::IsNullOrWhiteSpace($Body)) {
    $invokeArgs["Body"] = $Body
    $invokeArgs["ContentType"] = "application/json"
}

$response = Invoke-WebRequest @invokeArgs
if ($Raw) {
    Write-Output $response.Content
    return
}

$json = ConvertFrom-Json -InputObject $response.Content
if ($Select.Count -gt 0) {
    $selected = [ordered]@{}
    foreach ($name in $Select) {
        $selected[$name] = $json.$name
    }

    ConvertTo-Json -InputObject ([pscustomobject]$selected) -Depth 12
    return
}

ConvertTo-Json -InputObject $json -Depth 12
