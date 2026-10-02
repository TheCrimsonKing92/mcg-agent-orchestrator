param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Read', 'Add')]
    [string] $Operation,
    [string] $ExclusionPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
try {
    if ($Operation -eq 'Read') {
        $paths = @((Get-MpPreference -ErrorAction Stop).ExclusionPath |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        ConvertTo-Json -InputObject $paths -Compress
    } else {
        if ([string]::IsNullOrWhiteSpace($ExclusionPath)) { throw 'An exclusion path is required.' }
        Add-MpPreference -ExclusionPath $ExclusionPath -ErrorAction Stop
    }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
