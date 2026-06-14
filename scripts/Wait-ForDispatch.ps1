# Polls for a dispatch worker's exit file and returns when the worker finishes (or times out).
# Used by the operator loop to wait on a background worker without active polling.
param(
    [Parameter(Mandatory = $true)][string]$ExitFile,
    [int]$TimeoutMinutes = 100,
    [int]$PollSeconds = 30
)
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path $ExitFile) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $PollSeconds
}
if (Test-Path $ExitFile) {
    "WORKER DONE: exit=$((Get-Content $ExitFile -Raw).Trim())"
} else {
    "TIMEOUT after $TimeoutMinutes min; worker still running"
}
