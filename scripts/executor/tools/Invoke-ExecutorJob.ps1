# Runs ON an executor as one child of the parallel drain (Invoke-QueuedLanes.ps1).
# It reads one taken job file and runs that lane. The drain passes a path instead of the job's fields because
# lane names contain spaces and filters contain '|' and '~', which do not survive a command line.
param(
    [Parameter(Mandatory)][string]$JobPath,
    [string]$RunRoot = 'C:\mcg-executor'
)
$ErrorActionPreference = 'Stop'
$errorFile = [IO.Path]::ChangeExtension($JobPath, '.error.txt')
try {
    $job = Get-Content -Raw -Path $JobPath | ConvertFrom-Json
    $echo = @{}
    foreach ($name in 'attemptId', 'executorId', 'filterHash', 'mainSha', 'manifestIdentity') {
        if ($job.PSObject.Properties[$name]) { $echo[$name] = [string]$job.$name }
    }
    if ($job.PSObject.Properties['testHostTimeoutSeconds']) { $echo['TestHostTimeoutSeconds'] = [int]$job.testHostTimeoutSeconds }
    if ($job.PSObject.Properties['kind'] -and $job.kind -eq 'build-check') {
        & (Join-Path $RunRoot 'tools\Invoke-ExecutorBuildCheck.ps1') -JobPath $JobPath -RunRoot $RunRoot
    }
    else {
        & (Join-Path $RunRoot 'tools\Invoke-ExecutorLane.ps1') -Sha $job.sha -Project $job.project -Filter $job.filter -Lane $job.lane -RunRoot $RunRoot @echo
    }
}
catch {
    # One error file per job, so concurrent children never append to a shared log.
    Set-Content -Path $errorFile -Value "$([DateTime]::UtcNow.ToString('o')) $(Split-Path -Leaf $JobPath): $($_.Exception.Message)"
    exit 1
}
