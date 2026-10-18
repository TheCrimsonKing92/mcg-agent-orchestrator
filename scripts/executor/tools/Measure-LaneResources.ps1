param([Parameter(Mandatory)][int]$Root, [int]$Skip = 0, [Parameter(Mandatory)][string]$Path)
Add-Type -AssemblyName Microsoft.VisualBasic
$info = [Microsoft.VisualBasic.Devices.ComputerInfo]::new()
$cpu = @{}
$peak = [long]0
$minAvailable = [long]::MaxValue
$samples = 0
while (Get-Process -Id $Root -ErrorAction SilentlyContinue) {
    $all = @(Get-Process)
    $parentOf = @{}
    foreach ($p in $all) { try { if ($null -ne $p.Parent) { $parentOf[$p.Id] = $p.Parent.Id } } catch { } }
    $workingSet = [long]0
    foreach ($p in $all) {
        if ($p.Id -in @($Root, $PID, $Skip)) { continue }
        $id = $p.Id
        for ($depth = 0; $depth -lt 16 -and $id -ne $Root -and $parentOf.ContainsKey($id); $depth++) { $id = $parentOf[$id] }
        if ($id -ne $Root) { continue }
        try { $cpu[$p.Id] = $p.TotalProcessorTime.TotalSeconds; $workingSet += $p.WorkingSet64 } catch { }
    }
    if ($workingSet -gt $peak) { $peak = $workingSet }
    $available = [long]($info.AvailablePhysicalMemory / 1MB)
    if ($available -lt $minAvailable) { $minAvailable = $available }
    $samples++
    $record = [ordered]@{ testHostCpuSeconds = [math]::Round([double]($cpu.Values | Measure-Object -Sum).Sum, 1)
        testHostPeakWorkingSetBytes = $peak; availableMemoryMbMin = $minAvailable; resourceSamples = $samples }
    try { [IO.File]::WriteAllText("$Path.tmp", ($record | ConvertTo-Json -Compress)); Move-Item -LiteralPath "$Path.tmp" -Destination $Path -Force } catch { }
    Start-Sleep -Seconds 2
}
