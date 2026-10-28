Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'WorkerBuildReceipt.psm1')

function Get-CapHash {
    param([string]$Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Get-CapField {
    param($Object, [string]$Name, $Default = $null)
    if ($null -ne $Object -and $Object.Contains($Name)) { return $Object[$Name] }
    return $Default
}

function New-CapMetric {
    param($Value, [string]$Unit, [string]$Reason = '')
    return [ordered]@{ Value = $Value; Unit = $Unit; Available = ($null -ne $Value); Reason = $Reason }
}

function Invoke-CapReadProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$Root)
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $Root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    # A read-only Git inspection must not inherit another worktree's index or repository.
    foreach ($key in @('GIT_DIR','GIT_WORK_TREE','GIT_COMMON_DIR','GIT_INDEX_FILE','GIT_OBJECT_DIRECTORY','GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
        $info.Environment.Remove($key) | Out-Null
    }
    $info.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'inspection-start-failed' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
            throw 'inspection-timeout'
        }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw 'inspection-output-incomplete' }
        if ($process.ExitCode -ne 0) { throw "inspection-failed: $Executable exit=$($process.ExitCode) $($stderr.Result)" }
        return $stdout.Result.Trim()
    }
    finally { $process.Dispose() }
}

function Get-CapSourceIdentity {
    param([string]$Root, [switch]$Fixture)
    $project = Join-Path $Root 'src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj'
    [xml]$xml = [IO.File]::ReadAllText($project)
    if ($xml.Project.PropertyGroup.OutputType -notcontains 'Exe' -or
        @($xml.SelectNodes('//ProjectReference')).Count -eq 0) { throw 'workload-is-not-app-project-reference-build' }
    if ($Fixture) {
        $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName)
        $digest = Get-CapHash (($files | ForEach-Object { $_.FullName.Substring($Root.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n")
        return [ordered]@{ Commit = 'synthetic'; Tree = $digest; Inputs = $digest; Clean = $true }
    }
    $head = Invoke-CapReadProcess 'git' @('-c','core.fsmonitor=false','rev-parse','HEAD') $Root
    $tree = Invoke-CapReadProcess 'git' @('rev-parse','HEAD^{tree}') $Root
    $dirty = Invoke-CapReadProcess 'git' @('-c','core.fsmonitor=false','status','--porcelain','--untracked-files=normal') $Root
    $paths = (Invoke-CapReadProcess 'git' @('ls-files','-z') $Root).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    $inputLines = foreach ($path in $paths) {
        $full = Join-Path $Root $path
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { "${path}:missing" }
        else { "${path}:$((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash)" }
    }
    return [ordered]@{ Commit = $head; Tree = $tree; Inputs = (Get-CapHash ($inputLines -join "`n")); Clean = [string]::IsNullOrWhiteSpace($dirty) }
}

function Get-CapSdkIdentity {
    param([string]$Root)
    $commandName = if ([OperatingSystem]::IsWindows()) { 'dotnet.exe' } else { 'dotnet' }
    $commands = @(Get-Command $commandName -CommandType Application -ErrorAction Stop)
    $dotnet = $commands[0].Source
    $version = Invoke-CapReadProcess $dotnet @('--version') $Root
    $sdks = Invoke-CapReadProcess $dotnet @('--list-sdks') $Root
    $line = @($sdks -split "`n" | Where-Object { $_.StartsWith("$version [", [StringComparison]::Ordinal) })
    if ($line.Count -ne 1) { throw 'sdk-location-unresolved' }
    $sdkRoot = Join-Path ($line[0].Substring($version.Length + 2).TrimEnd(']', "`r")) $version
    $compiler = Join-Path $sdkRoot 'Roslyn/bincore/csc.dll'
    $targets = Join-Path $sdkRoot 'Roslyn/Microsoft.CSharp.Core.targets'
    $forwarding = [IO.File]::ReadAllText($targets).Contains('EnvironmentVariables="$(CscEnvironment)"')
    return [ordered]@{
        Version = $version; Executable = $dotnet; Root = $sdkRoot; Compiler = $compiler
        CompilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
        BuildSha256 = (Get-FileHash -LiteralPath (Join-Path $sdkRoot 'MSBuild.dll') -Algorithm SHA256).Hash
        FrameworkSha256 = (Get-FileHash -LiteralPath (Join-Path $sdkRoot 'Microsoft.Build.Framework.dll') -Algorithm SHA256).Hash
        TargetsSha256 = (Get-FileHash -LiteralPath $targets -Algorithm SHA256).Hash; CscEnvironmentForwarded = $forwarding
    }
}

function Assert-CapOwnedPath {
    param([string]$Path, [string]$Boundary)
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Boundary).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'output-outside-owned-boundary' }
    Assert-WorkerBuildPathNotRedirected -Path $full
    return $full
}

function Write-CapReport {
    param($Report)
    Assert-WorkerBuildPathNotRedirected -Path $Report.Plan.OutputRoot -Recurse
    $text = $Report | ConvertTo-Json -Depth 64
    $temporary = $Report.ReportPath + '.tmp'
    [IO.File]::WriteAllText($temporary, $text, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $Report.ReportPath, $true)
}

function Get-CapArguments {
    param($Plan, $Arm, [string]$Phase)
    $properties = @(
        "-p:Configuration=$($Plan.Configuration)", '-p:UseSharedCompilation=false', '-p:BuildProjectReferences=true',
        "-p:McgIsolatedArtifactsPath=$($Arm.OutputRoot)/outputs", "-p:RestorePackagesPath=$($Arm.OutputRoot)/packages",
        '-nodeReuse:false', '-maxcpucount:1', '-verbosity:diagnostic',
        "-binaryLogger:$($Arm.OutputRoot)/$Phase.binlog;ProjectImports=Embed"
    )
    $properties += "-p:CustomAfterMicrosoftCommonTargets=$($Arm.OutputRoot)/protocol.targets"
    if ($Phase -eq 'setup') { return @('build', $Plan.Project, '-target:Restore') + $properties }
    return @('build', $Plan.Project, '--no-restore', "-logger:CompilerCapLogger,$($Plan.OutputRoot)/logger.dll;$($Arm.OutputRoot)/events.tsv") + $properties
}

function Get-CapEnvironment {
    param($Arm)
    return [ordered]@{
        MSBUILDDISABLENODEREUSE = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'; DOTNET_CLI_HOME = (Join-Path $Arm.OutputRoot 'cli-home')
        NUGET_HTTP_CACHE_PATH = (Join-Path $Arm.OutputRoot 'http-cache'); NUGET_SCRATCH = (Join-Path $Arm.OutputRoot 'nuget-scratch')
        TEMP = (Join-Path $Arm.OutputRoot 'temp'); TMP = (Join-Path $Arm.OutputRoot 'temp'); TMPDIR = (Join-Path $Arm.OutputRoot 'temp')
    }
}

function Get-CapNormalizedPath {
    param([string]$Path, $Plan, $Arm)
    if ([string]::IsNullOrEmpty($Path)) { return $Path }
    # MSBuild can report the same location relative to the declared repository or absolute.
    # Resolve both against that context, never the runner's working directory. GetFullPath
    # also reconciles dot segments without erasing distinct locations outside the repository.
    $value = [IO.Path]::GetFullPath($Path, $Plan.RepositoryRoot).Replace('\','/')
    foreach ($prefix in @(@($Arm.OutputRoot,'<owned>'), @($Plan.RepositoryRoot,'<repo>'))) {
        $root = $prefix[0].Replace('\','/').TrimEnd('/')
        if ($value.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { return $prefix[1] }
        if ($value.StartsWith($root + '/', [StringComparison]::OrdinalIgnoreCase)) { return $prefix[1] + $value.Substring($root.Length) }
    }
    return $value
}

function Get-CapDiagnosticKeys {
    param($Diagnostics, $Plan, $Arm)
    $keys = foreach ($d in $Diagnostics) {
        @($d.Id, $d.Severity, (Get-CapNormalizedPath $d.Project $Plan $Arm), (Get-CapNormalizedPath $d.File $Plan $Arm),
            $d.Line, $d.Column, $d.EndLine, $d.EndColumn) | ConvertTo-Json -Compress
    }
    return @($keys | Sort-Object -CaseSensitive)
}

function ConvertTo-CapMeasurements {
    param($Observation, $Plan, $Arm)
    $reasons = [Collections.Generic.List[string]]::new()
    $coverage = $Observation.Coverage
    foreach ($key in @('PopulationComplete','LifetimesComplete','CountersAvailable','InitialFinalReconciled','OutputComplete','DiagnosticsComplete','ForwardingVerified','HostPopulationComplete')) {
        if ((Get-CapField $coverage $key $false) -ne $true) { $reasons.Add("missing-$key") }
    }
    if ((Get-CapField $coverage 'SharedCompiler' $true) -ne $false) { $reasons.Add('shared-or-unaccounted-compiler') }
    $processes = @($Observation.Processes)
    if ($processes.Count -eq 0 -or $Observation.CompilerExecutions -le 0) { $reasons.Add('no-actual-compiler-execution') }
    $known = @{}
    $accounted = @{}
    $total = 0.0
    foreach ($p in $processes) {
        $identity = "$($p.Pid):$($p.StartUtc):$($p.Image)"
        if ($known.ContainsKey($identity)) { $reasons.Add('duplicate-process-identity') }
        $known[$identity] = $p
        $accounted[$identity] = 0.0
        if ($p.Image -ne $Plan.Sdk.Executable -or (Get-CapField $p 'CompilerAssembly') -ne $Plan.Sdk.Compiler -or
            $p.CompilerSha256 -ne $Plan.Sdk.CompilerSha256) { $reasons.Add('compiler-identity-mismatch') }
        if ($null -eq $p.FinalCpuSeconds -or $p.FinalCpuSeconds -lt 0 -or -not $p.ExitAccounted -or -not $p.StartAccounted -or
            $null -eq (Get-CapField $p 'StartSeconds') -or $null -eq (Get-CapField $p 'ExitSeconds') -or $p.ExitSeconds -lt $p.StartSeconds) {
            $reasons.Add('incomplete-lifetime-accounting')
        } else { $total += [double]$p.FinalCpuSeconds }
    }
    if ($Observation.CompilerExecutions -ne $processes.Count) { $reasons.Add('compiler-population-mismatch') }
    $windows = [Collections.Generic.List[object]]::new()
    $lastEnd = 0.0
    $previous = @{}
    $peak = $null
    $validWindows = 0
    $backgroundCpu = 0.0
    $activeSeconds = 0.0
    foreach ($window in @($Observation.Windows)) {
        $windowValid = $true
        $duration = [double]$window.EndSeconds - [double]$window.StartSeconds
        $active = @($window.ProcessCounters).Count -gt 0
        if ($duration -le 0 -or $window.StartSeconds -lt $lastEnd) { $reasons.Add('invalid-monotonic-window'); continue }
        foreach ($id in $known.Keys) {
            $p = $known[$id]
            if ((Get-CapField $p 'StartSeconds') -lt $window.EndSeconds -and (Get-CapField $p 'ExitSeconds') -gt $window.StartSeconds -and
                @($window.ProcessCounters | Where-Object { "$($_.Pid):$($_.StartUtc):$($_.Image)" -eq $id }).Count -ne 1) { $reasons.Add('compiler-population-window-gap'); $windowValid = $false }
        }
        if ($active -and ($duration -gt 0.5 -or ([double]$window.StartSeconds - $lastEnd) -gt 0.5)) { $reasons.Add('compiler-active-gap') }
        $lastEnd = [double]$window.EndSeconds
        $delta = 0.0
        $seen = @{}
        foreach ($counter in @($window.ProcessCounters)) {
            $id = "$($counter.Pid):$($counter.StartUtc):$($counter.Image)"
            if (-not $known.ContainsKey($id) -or $seen.ContainsKey($id)) { $reasons.Add('pid-identity-ambiguity'); $windowValid = $false; continue }
            $seen[$id] = $true
            if ($null -eq $counter.StartCpuSeconds -or $null -eq $counter.EndCpuSeconds) { $reasons.Add('counter-unavailable'); $windowValid = $false; continue }
            if ($counter.EndCpuSeconds -lt $counter.StartCpuSeconds -or $counter.StartCpuSeconds -lt 0 -or
                (-not $previous.ContainsKey($id) -and $counter.StartCpuSeconds -ne 0) -or
                ($previous.ContainsKey($id) -and $counter.StartCpuSeconds -ne $previous[$id]) -or
                ($null -ne $known[$id].FinalCpuSeconds -and $counter.EndCpuSeconds -gt $known[$id].FinalCpuSeconds)) { $reasons.Add('counter-regression-or-gap'); $windowValid = $false }
            $previous[$id] = $counter.EndCpuSeconds
            $accounted[$id] += [double]$counter.EndCpuSeconds - [double]$counter.StartCpuSeconds
            $delta += [double]$counter.EndCpuSeconds - [double]$counter.StartCpuSeconds
        }
        if ($active) {
            if ($null -eq $window.HostCpuSeconds -or $null -eq $window.ExperimentCpuSeconds -or
                $window.HostCpuSeconds -lt $window.ExperimentCpuSeconds -or $window.ExperimentCpuSeconds -lt $delta) { $reasons.Add('host-counter-unavailable') }
            else { $backgroundCpu += [double]$window.HostCpuSeconds - [double]$window.ExperimentCpuSeconds }
            $activeSeconds += $duration
            $cores = $null
            if ($windowValid) {
                $cores = $delta / $duration
                $validWindows++
                if ($null -eq $peak -or $cores -gt $peak) { $peak = $cores }
            }
            $windows.Add([ordered]@{ StartSeconds = $window.StartSeconds; EndSeconds = $window.EndSeconds; Valid = $windowValid;
                CoreEquivalents = $cores; Reason = $(if (-not $windowValid) { 'counter-or-population-incomplete' } else { '' }); Processes = $window.ProcessCounters })
        }
    }
    foreach ($id in $known.Keys) {
        if ($null -ne $known[$id].FinalCpuSeconds -and [Math]::Abs($accounted[$id] - [double]$known[$id].FinalCpuSeconds) -gt 0.000001) { $reasons.Add('initial-final-counter-accounting-gap') }
    }
    if ($validWindows -lt 10) { $reasons.Add('fewer-than-10-active-windows') }
    if ($total -le 0 -or $null -eq $peak -or $peak -le 0) { $reasons.Add('zero-compiler-work') }
    $background = $null
    if ($activeSeconds -gt 0 -and $Observation.Host.LogicalProcessors -gt 0) { $background = 100.0 * $backgroundCpu / ($activeSeconds * $Observation.Host.LogicalProcessors) }
    else { $reasons.Add('host-capacity-unavailable') }
    if ($null -ne $background -and $background -gt 10) { $reasons.Add('background-cpu-above-10-percent') }
    foreach ($key in @('WorkloadIdentity','DependencyIdentity','EffectiveSettings')) {
        if ($null -eq (Get-CapField $Observation $key) -or [string]::IsNullOrWhiteSpace([string]$Observation[$key])) { $reasons.Add("identity-evidence-missing-$key") }
    }
    foreach ($key in @('Hardware','LogicalProcessors','OS','Priority','Affinity','DefaultCpuSet','PowerPlan','Throttling')) {
        if ($null -eq (Get-CapField $Observation.Host $key) -or [string]::IsNullOrWhiteSpace([string]$Observation.Host[$key])) { $reasons.Add("host-fact-unavailable-$key") }
    }
    $cpuEligible = @($reasons | Where-Object { $_ -match 'counter|lifetime|identity|population|Population|Lifetimes|Counters|InitialFinal|actual-compiler|shared|compiler-work' }).Count -eq 0
    $peakEligible = $cpuEligible -and @($reasons | Where-Object { $_ -match 'window|gap' }).Count -eq 0
    $backgroundEligible = (Get-CapField $coverage 'HostPopulationComplete' $false) -and @($reasons | Where-Object { $_ -match 'host-counter|capacity' }).Count -eq 0
    return [ordered]@{
        Reasons = @($reasons | Sort-Object -Unique)
        CompilerCpu = (New-CapMetric $(if ($cpuEligible) { $total } else { $null }) 'cpu-seconds' $(if (-not $cpuEligible) { 'compiler-accounting-incomplete' }))
        SampledPeak = (New-CapMetric $(if ($peakEligible) { $peak } else { $null }) 'core-equivalents' $(if (-not $peakEligible) { 'sampling-coverage-incomplete' }))
        BackgroundCpu = (New-CapMetric $(if ($backgroundEligible) { $background } else { $null }) 'percent-machine-logical-capacity' $(if (-not $backgroundEligible) { 'host-accounting-incomplete' }))
        ActiveWindows = $windows.ToArray(); ValidActiveWindows = $validWindows; SampleIntervalMilliseconds = 250
        Diagnostics = @(Get-CapDiagnosticKeys $Observation.Diagnostics $Plan $Arm)
    }
}

function Get-CapComparisons {
    param($Report)
    $baseline = $Report.Arms[0]
    foreach ($arm in $Report.Arms[1..2]) {
        $reasons = [Collections.Generic.List[string]]::new()
        foreach ($candidate in @($baseline, $arm)) {
            if ($candidate.Status -ne 'completed' -or $null -eq $candidate.Measurements) { $reasons.Add("$($candidate.Name)-incomplete"); continue }
            foreach ($reason in $candidate.Measurements.Reasons) { $reasons.Add("$($candidate.Name):$reason") }
        }
        $ratios = $null
        $parity = $null
        if ($null -ne $baseline.Measurements -and $null -ne $arm.Measurements) {
            $parity = ($baseline.Measurements.Diagnostics | ConvertTo-Json -Compress -AsArray) -ceq ($arm.Measurements.Diagnostics | ConvertTo-Json -Compress -AsArray)
            if (-not $parity) { $reasons.Add('diagnostic-multiset-mismatch') }
            foreach ($key in @('Host','WorkloadIdentity','DependencyIdentity','EffectiveSettings')) {
                if (($baseline.Observation[$key] | ConvertTo-Json -Depth 12 -Compress) -cne ($arm.Observation[$key] | ConvertTo-Json -Depth 12 -Compress)) { $reasons.Add("non-equivalent-$key") }
            }
            $bg1 = $baseline.Measurements.BackgroundCpu.Value
            $bg2 = $arm.Measurements.BackgroundCpu.Value
            if ($null -ne $bg1 -and $null -ne $bg2 -and [Math]::Abs($bg2 - $bg1) -gt 5) { $reasons.Add('background-arm-difference-above-5-points') }
            foreach ($candidate in @($baseline, $arm)) {
                foreach ($metric in @($candidate.Measurements.CompilerCpu, $candidate.Measurements.SampledPeak, $candidate.WallTime)) {
                    if (-not $metric.Available -or $null -eq $metric.Value -or $metric.Value -le 0) { $reasons.Add('unavailable-or-nonpositive-denominator') }
                }
            }
            if ($reasons.Count -eq 0) {
                $ratios = [ordered]@{
                    Peak = $arm.Measurements.SampledPeak.Value / $baseline.Measurements.SampledPeak.Value
                    Wall = $arm.WallTime.Value / $baseline.WallTime.Value
                    Cpu = $arm.Measurements.CompilerCpu.Value / $baseline.Measurements.CompilerCpu.Value
                }
            }
        }
        $verdict = 'inconclusive'
        if ($null -ne $ratios) {
            # Compare unrounded observations directly; displayed ratios never decide the result.
            if ($arm.Measurements.SampledPeak.Value -le 0.80 * $baseline.Measurements.SampledPeak.Value -and
                $arm.WallTime.Value -le 1.10 * $baseline.WallTime.Value -and
                $arm.Measurements.CompilerCpu.Value -le 1.05 * $baseline.Measurements.CompilerCpu.Value) { $verdict = 'exploratory promising' }
            else { $verdict = 'not justified'; $reasons.Add('performance-threshold-not-met') }
        }
        [ordered]@{ Arm = $arm.Name; Verdict = $verdict; Reasons = $reasons.ToArray(); Ratios = $ratios; DiagnosticsMatch = $parity }
    }
}

function New-CapProtocolTargets {
    param($Arm)
    # This target is inside the owned experiment root. It appends to each project's evaluated
    # environment immediately before CoreCompile, rather than replacing existing CscEnvironment.
    $treatment = if ($null -ne $Arm.ProcessorCount) {
        '<PropertyGroup><CscEnvironment>$(CscEnvironment);DOTNET_PROCESSOR_COUNT=' + $Arm.ProcessorCount + '</CscEnvironment></PropertyGroup>'
    } else { '' }
    $text = @'
<Project>
  <Target Name="CompilerCapProtocol" BeforeTargets="CoreCompile">
    <Error Condition="'$(UseSharedCompilation)' != 'false'" Text="Compiler-cap protocol requires non-shared compilation." />
    <Error Condition="$([System.String]::Copy('$(CscEnvironment)').ToUpperInvariant().Contains('DOTNET_PROCESSOR_COUNT'))" Text="Compiler-cap protocol rejects pre-existing processor-count treatment." />
    TREATMENT
    <WriteLinesToFile File="$(BaseIntermediateOutputPath)compiler-cap-settings.txt" Overwrite="true"
      Lines="Project=$(MSBuildProjectFullPath);Configuration=$(Configuration);UseSharedCompilation=$(UseSharedCompilation);CscEnvironment=$(CscEnvironment);AnalysisLevel=$(AnalysisLevel);AnalysisMode=$(AnalysisMode);EnforceCodeStyleInBuild=$(EnforceCodeStyleInBuild);RunAnalyzers=$(RunAnalyzers);RunAnalyzersDuringBuild=$(RunAnalyzersDuringBuild);EnableNETAnalyzers=$(EnableNETAnalyzers);TreatWarningsAsErrors=$(TreatWarningsAsErrors);WarningsAsErrors=$(WarningsAsErrors);NoWarn=$(NoWarn);DefaultCpuSet=unavailable" />
  </Target>
</Project>
'@
    [IO.File]::WriteAllText((Join-Path $Arm.OutputRoot 'protocol.targets'), $text.Replace('TREATMENT', $treatment))
}

function Initialize-CapLogger {
    param($Plan)
    # Build-event diagnostics retain duplicates and source locations. Console text is not used as
    # an empty diagnostic set when the event stream is missing or truncated.
    $source = @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Microsoft.Build.Framework;
public sealed class CompilerCapLogger : ILogger
{
    public string Parameters { get; set; }
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;
    private StreamWriter writer;
    private readonly object sync = new object();
    private readonly HashSet<string> compilers = new HashSet<string>();
    private static string Key(BuildEventContext c) => c == null ? "unknown" : c.NodeId + ":" + c.ProjectContextId + ":" + c.TargetId + ":" + c.TaskId;
    private void Row(params object[] fields) {
        lock (sync) {
            var encoded = new List<string>();
            foreach (object field in fields) encoded.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToString(field, System.Globalization.CultureInfo.InvariantCulture) ?? "")));
            writer.WriteLine(string.Join("\t", encoded)); writer.Flush();
        }
    }
    public void Initialize(IEventSource events) {
        writer = new StreamWriter(Parameters, false, new UTF8Encoding(false));
        Row("start");
        events.WarningRaised += (s,e) => Row("diagnostic", "warning", e.Code, e.ProjectFile, e.File, e.LineNumber, e.ColumnNumber, e.EndLineNumber, e.EndColumnNumber, e.Message);
        events.ErrorRaised += (s,e) => Row("diagnostic", "error", e.Code, e.ProjectFile, e.File, e.LineNumber, e.ColumnNumber, e.EndLineNumber, e.EndColumnNumber, e.Message);
        events.TaskStarted += (s,e) => { if (e.TaskName == "Csc") { compilers.Add(Key(e.BuildEventContext)); Row("csc-start", Key(e.BuildEventContext), e.ProjectFile); } };
        events.TaskFinished += (s,e) => { if (e.TaskName == "Csc") Row("csc-end", Key(e.BuildEventContext), e.Succeeded); };
        events.MessageRaised += (s,e) => { if (compilers.Contains(Key(e.BuildEventContext))) Row("csc-message", Key(e.BuildEventContext), e.Message); };
        events.BuildFinished += (s,e) => Row("finish", e.Succeeded);
    }
    public void Shutdown() { if (writer != null) writer.Dispose(); }
}
'@
    $references = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File).FullName)
    $references += Join-Path $Plan.Sdk.Root 'Microsoft.Build.Framework.dll'
    Add-Type -TypeDefinition $source -ReferencedAssemblies $references -OutputAssembly (Join-Path $Plan.OutputRoot 'logger.dll') -ErrorAction Stop
}

function Read-CapBuildEvents {
    param([string]$Path)
    $diagnostics = [Collections.Generic.List[object]]::new()
    $starts = @{}
    $ends = @{}
    $finished = $false
    $began = $false
    $commands = [Collections.Generic.List[string]]::new()
    $valid = $true
    if (Test-Path -LiteralPath $Path) {
        try {
            foreach ($line in [IO.File]::ReadLines($Path)) {
                $fields = @($line.Split("`t") | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) })
                switch ($fields[0]) {
                    'start' { $began = $true }
                    'finish' { $finished = $fields[1] -eq 'True' }
                    'csc-start' { $starts[$fields[1]] = $fields[2] }
                    'csc-end' { $ends[$fields[1]] = $fields[2] }
                    'csc-message' { $commands.Add($fields[2]) }
                    'diagnostic' {
                        $diagnostics.Add([ordered]@{ Severity = $fields[1]; Id = $fields[2]; Project = $fields[3]; File = $fields[4];
                            Line = [int]$fields[5]; Column = [int]$fields[6]; EndLine = [int]$fields[7]; EndColumn = [int]$fields[8]; Message = $fields[9] })
                    }
                }
            }
        } catch { $valid = $false }
    }
    return [ordered]@{ Complete = ($valid -and $began -and $finished -and $starts.Count -eq $ends.Count);
        CompilerExecutions = $starts.Count; Diagnostics = $diagnostics.ToArray(); CompilerMessages = $commands.ToArray(); Tasks = $starts }
}

function Invoke-CapNativePhase {
    param([string]$Executable, [string[]]$Arguments, [string]$Root, [string]$EvidenceRoot, [int]$TimeoutSeconds, [string]$Compiler,
        [System.Collections.IDictionary]$Environment, [System.Collections.IDictionary]$ProcessFixture)
    # The diagnostic wrapper's optional observation seam calls this bounded executor. Ordinary
    # diagnostic calls retain their existing behavior. Job close kills only this phase's children.
    Import-Module (Join-Path $PSScriptRoot 'MtpTestRunner.psm1')
    $runRoot = Split-Path -Parent (Split-Path -Parent $EvidenceRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $runRoot 'owner.json') -PathType Leaf)) { throw 'observation-owned-root-required' }
    $owner = Get-Content -LiteralPath (Join-Path $runRoot 'owner.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($owner.Root -ne $runRoot -or $owner.OwnerPid -ne $PID -or $owner.OwnerStartUtc -ne (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('O')) { throw 'observation-owner-identity-mismatch' }
    $null = Assert-CapOwnedPath $EvidenceRoot $runRoot
    $deadlineEvent = $null
    $deadlineEventName = ''
    if ($null -ne $ProcessFixture) {
        # Explicit, temp-owned test boundary: substitute only the executable and deadline wait.
        # Launch, argument forwarding, job assignment, pipes and cleanup still execute below.
        $fixtureBase = Split-Path -Parent $Root
        $null = Assert-CapOwnedPath $fixtureBase ([IO.Path]::GetTempPath())
        if ($ProcessFixture.Contract -ne 'compiler-cap-native-process-fixture-v1' -or
            -not (Test-Path -LiteralPath (Join-Path $fixtureBase '.compiler-cap-test-fixture')) -or
            $Root -ne (Join-Path $fixtureBase 'source')) { throw 'native-process-fixture-ownership-required' }
        $script = Assert-CapOwnedPath $ProcessFixture.Script $fixtureBase
        $null = Assert-CapOwnedPath $EvidenceRoot (Join-Path $fixtureBase 'experiments')
        if ($ProcessFixture.DeadlineAfterReady) {
            $deadlineEventName = [string]$ProcessFixture.DeadlineEventName
            if ($deadlineEventName -notmatch '^Local\\compiler-cap-[a-f0-9]{32}$') { throw 'invalid-fixture-deadline-event' }
            $deadlineEvent = [Threading.EventWaitHandle]::OpenExisting($deadlineEventName)
        }
        $Executable = (Get-Process -Id $PID).Path
        $Arguments = @('-NoProfile','-NonInteractive','-File',$script,$EvidenceRoot,$deadlineEventName) + $Arguments
    }
    foreach ($key in @('DOTNET_CLI_HOME','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','TEMP','TMP','TMPDIR')) {
        $path = Assert-CapOwnedPath $Environment[$key] (Split-Path -Parent $EvidenceRoot)
        [void][IO.Directory]::CreateDirectory($path)
    }
    # The gated guardian starts no descendants until after assignment to our owned job.
    # This closes the process-start/Assign race without changing global process settings.
    $guardianPath = Join-Path $EvidenceRoot 'guardian.ps1'
    $requestPath = Join-Path $EvidenceRoot 'request.json'
    $gatePath = Join-Path $EvidenceRoot 'start-owned'
    $completionPath = Join-Path $EvidenceRoot 'completion.json'
    $guardian = @'
param([string]$RequestFile)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestFile -Raw | ConvertFrom-Json -AsHashtable
$gateClock = [Diagnostics.Stopwatch]::StartNew()
while (-not [IO.File]::Exists($request.Gate)) {
    if ($gateClock.Elapsed.TotalSeconds -gt 30) { throw 'owned-launch-gate-not-released' }
    Start-Sleep -Milliseconds 10
}
$info = [Diagnostics.ProcessStartInfo]::new($request.Executable)
$info.WorkingDirectory = $request.Root
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($arg in $request.Arguments) { $info.ArgumentList.Add($arg) }
$process = [Diagnostics.Process]::new()
$process.StartInfo = $info
$result = [ordered]@{ Status = 'apparatus-failure'; ExitCode = $null; ElapsedSeconds = $null; Process = $null; OutputComplete = $false; Reason = '' }
$stdoutFile = $null
$stderrFile = $null
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw 'build-process-start-failed' }
    $process.StandardInput.Close()
    $result.Process = @{ Pid = $process.Id; StartUtc = $process.StartTime.ToUniversalTime().ToString('O'); Image = $process.MainModule.FileName }
    $stdoutFile = [IO.FileStream]::new($request.Stdout, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, $true)
    $stderrFile = [IO.FileStream]::new($request.Stderr, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, $true)
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
    $stderr = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
    if ($request.FixtureDeadlineEvent) {
        # A controlled deadline signal replaces elapsed time only in explicit process fixtures.
        # The test signals after retaining ready process handles; timeout/cleanup is shared.
        $deadline = [Threading.EventWaitHandle]::OpenExisting($request.FixtureDeadlineEvent)
        try {
            if (-not $deadline.WaitOne(120000)) { throw 'fixture-deadline-was-not-signalled' }
            $completed = $false
        } finally { $deadline.Dispose() }
    } else { $completed = $process.WaitForExit($request.TimeoutSeconds * 1000) }
    if (-not $completed) {
        $result.Status = 'timeout'
        # The owning parent terminates the job after reading this terminal receipt.
    } else {
        $clock.Stop()
        $result.Status = 'completed'
        $result.ExitCode = $process.ExitCode
        $result.ElapsedSeconds = $clock.Elapsed.TotalSeconds
        $result.OutputComplete = $stdout.Wait(5000) -and $stderr.Wait(5000)
    }
} catch { $result.Reason = $_.Exception.Message }
finally {
    [IO.File]::WriteAllText($request.Completion, ($result | ConvertTo-Json -Depth 8))
    if ($null -ne $stdoutFile) { $stdoutFile.Dispose() }
    if ($null -ne $stderrFile) { $stderrFile.Dispose() }
    $process.Dispose()
}
'@
    [IO.File]::WriteAllText($guardianPath, $guardian)
    [IO.File]::WriteAllText($requestPath, (@{ Executable = $Executable; Arguments = $Arguments; Root = $Root; TimeoutSeconds = $TimeoutSeconds;
        Gate = $gatePath; Completion = $completionPath; FixtureDeadlineEvent = $deadlineEventName;
        Stdout = (Join-Path $EvidenceRoot 'build.stdout.log'); Stderr = (Join-Path $EvidenceRoot 'build.stderr.log') } | ConvertTo-Json -Depth 8))
    $shell = (Get-Process -Id $PID).Path
    $info = [Diagnostics.ProcessStartInfo]::new($shell)
    $info.WorkingDirectory = $Root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in @('-NoProfile','-NonInteractive','-File',$guardianPath,'-RequestFile',$requestPath)) { $info.ArgumentList.Add($arg) }
    # Invocation-local build-server isolation, identical for every phase. Never set the processor
    # count on this process or its environment: the treatment belongs only to the Csc task.
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $job = [McgMtpOwnedJob]::new($false)
    $clock = [Diagnostics.Stopwatch]::new()
    $samples = [Collections.Generic.List[object]]::new()
    $observed = @{}
    $status = 'apparatus-failure'
    $exitCode = $null
    $stdout = $null
    $stderr = $null
    $cleanup = $false
    $outputComplete = $false
    $identity = $null
    $measuredElapsed = $null
    $buildIdentity = $null
    $childOutputComplete = $false
    $hostFacts = [ordered]@{ Hardware = $null; LogicalProcessors = [Environment]::ProcessorCount; OS = [Environment]::OSVersion.VersionString;
        Priority = $null; Affinity = $null; DefaultCpuSet = $null; PowerPlan = $null; Throttling = $null }
    $reason = ''
    try {
        if ($null -eq $ProcessFixture) {
            $cpu = @(Get-CimInstance -ClassName Win32_Processor -OperationTimeoutSec 2 -ErrorAction Stop)
            $hostFacts.Hardware = ($cpu | ForEach-Object { "$($_.Name):$($_.ProcessorId)" }) -join ';'
            $hostFacts.LogicalProcessors = [int](($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum)
            try { $hostFacts.PowerPlan = Invoke-CapReadProcess 'powercfg.exe' @('/getactivescheme') $Root } catch { }
        }
        $clock.Start()
        if (-not $process.Start()) { throw 'phase-start-failed' }
        $identity = [ordered]@{ Pid = $process.Id; StartUtc = $process.StartTime.ToUniversalTime().ToString('O'); Image = $process.MainModule.FileName }
        # Assignment is fail-closed. If it fails, finally kills the exact retained process handle.
        $job.Assign($process)
        if ($job.IsEmpty) { throw 'owned-job-empty-after-assignment' }
        [IO.File]::WriteAllText($gatePath, 'job-assigned')
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $hostFacts.Priority = $process.PriorityClass.ToString()
        $hostFacts.Affinity = $process.ProcessorAffinity.ToString()
        while (-not $process.WaitForExit(250)) {
            if ($clock.Elapsed.TotalSeconds -ge ($TimeoutSeconds + 30 + 10)) { $status = 'timeout'; break }
            # Process fixtures exercise lifecycle only; they never enumerate machine processes
            # or claim compiler/host measurement evidence.
            if ($null -ne $ProcessFixture) { continue }
            # Observational snapshots cannot prove complete population/lifetimes. Keep them, but
            # never promote their sums to total compiler CPU or an eligible sampled peak.
            $now = $clock.Elapsed.TotalSeconds
            $rows = @(Get-CimInstance -ClassName Win32_Process -OperationTimeoutSec 2 -ErrorAction Stop)
            $owned = [Collections.Generic.HashSet[uint32]]::new()
            [void]$owned.Add([uint32]$process.Id)
            do {
                $changed = $false
                foreach ($row in $rows) {
                    if ($owned.Contains([uint32]$row.ParentProcessId) -and $owned.Add([uint32]$row.ProcessId)) { $changed = $true }
                }
            } while ($changed)
            $compilerRows = [Collections.Generic.List[object]]::new()
            $hostCpu = 0.0
            $ownedCpu = 0.0
            foreach ($row in $rows) {
                $seconds = if ($null -eq $row.KernelModeTime -or $null -eq $row.UserModeTime) { $null } else { ([double]$row.KernelModeTime + [double]$row.UserModeTime) / 10000000 }
                if ($null -ne $seconds) { $hostCpu += $seconds }
                if ($owned.Contains([uint32]$row.ProcessId)) {
                    if ($null -ne $seconds) { $ownedCpu += $seconds }
                    if ($row.CommandLine -and $row.CommandLine.Contains($Compiler, [StringComparison]::OrdinalIgnoreCase)) {
                        $key = "$($row.ProcessId):$($row.CreationDate.ToUniversalTime().ToString('O')):$($row.ExecutablePath)"
                        $sample = [ordered]@{ Pid = $row.ProcessId; StartUtc = $row.CreationDate.ToUniversalTime().ToString('O');
                            Image = $row.ExecutablePath; Compiler = $Compiler; CpuSeconds = $seconds; CommandLine = $row.CommandLine }
                        $observed[$key] = $sample
                        $compilerRows.Add($sample)
                    }
                }
            }
            $samples.Add([ordered]@{ MonotonicSeconds = $now; CompilerProcesses = $compilerRows.ToArray();
                HostSnapshotCpuSeconds = $hostCpu; ExperimentSnapshotCpuSeconds = $ownedCpu; PopulationComplete = $false })
            [IO.File]::AppendAllText((Join-Path $EvidenceRoot 'samples.jsonl'), (($samples[$samples.Count - 1] | ConvertTo-Json -Depth 10 -Compress) + "`n"))
        }
        if ($status -ne 'timeout') {
            if (Test-Path -LiteralPath $completionPath) {
                $completion = Get-Content -LiteralPath $completionPath -Raw | ConvertFrom-Json -AsHashtable
                $status = $completion.Status
                $exitCode = $completion.ExitCode
                $measuredElapsed = $completion.ElapsedSeconds
                $buildIdentity = $completion.Process
                $childOutputComplete = $completion.OutputComplete
                $reason = $completion.Reason
            } else { $status = 'apparatus-failure'; $reason = 'missing-build-process-completion' }
        }
    }
    catch [Management.Automation.PipelineStoppedException] { $status = 'cancelled'; $reason = 'pipeline-cancelled' }
    catch { $reason = $_.Exception.Message }
    finally {
        try {
            $cleanup = $job.TerminateAndWait(5000)
            if ($null -ne $identity -and -not $process.HasExited) { $process.Kill($true); $cleanup = $process.WaitForExit(5000) -and $cleanup }
        } catch { $cleanup = $false }
        $clock.Stop()
        if ($null -ne $stdout -and $null -ne $stderr) {
            try {
                $outputComplete = $stdout.Wait(5000) -and $stderr.Wait(5000)
                if ($outputComplete) {
                    [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'stdout.log'), $stdout.Result)
                    [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'stderr.log'), $stderr.Result)
                }
            } catch { $outputComplete = $false }
        }
        $job.Dispose()
        $process.Dispose()
        if ($null -ne $deadlineEvent) { $deadlineEvent.Dispose() }
    }
    if (-not $cleanup) { $status = 'cleanup-failure' }
    $outputComplete = $outputComplete -and $childOutputComplete
    if (-not $outputComplete -and $status -eq 'completed') { $status = 'incomplete-output' }
    return [ordered]@{ Status = $status; ExitCode = $exitCode; ElapsedSeconds = $measuredElapsed; Process = $buildIdentity; Guardian = $identity;
        ApparatusElapsedSeconds = $clock.Elapsed.TotalSeconds; CleanupConfirmed = $cleanup; OutputComplete = $outputComplete; Reason = $reason; Host = $hostFacts;
        Samples = $samples.ToArray(); ObservedCompilers = @($observed.Values);
        MeasurementLimitations = @('snapshot-population-and-final-accounting-unavailable', 'default-cpu-set-and-throttling-unavailable') }
}

function Get-CapNativeObservation {
    param($Plan, $Arm, $Phase)
    $events = Read-CapBuildEvents (Join-Path $Arm.OutputRoot 'events.tsv')
    $processes = @($Phase.ObservedCompilers | ForEach-Object {
        [ordered]@{ Pid = $_.Pid; StartUtc = $_.StartUtc; Image = $_.Image; CompilerAssembly = $Plan.Sdk.Compiler; CompilerSha256 = $Plan.Sdk.CompilerSha256;
            StartAccounted = $false; ExitAccounted = $false; StartSeconds = $null; ExitSeconds = $null; FinalCpuSeconds = $null; ObservedCommandLine = $_.CommandLine }
    })
    $settings = @(Get-ChildItem -LiteralPath (Join-Path $Arm.OutputRoot 'outputs') -Filter compiler-cap-settings.txt -File -Recurse | Sort-Object FullName | ForEach-Object {
        $value = [IO.File]::ReadAllText($_.FullName).Replace($Arm.OutputRoot, '<owned>')
        # The sole allowed difference is the declared CscEnvironment treatment.
        $value = $value.Replace(';DOTNET_PROCESSOR_COUNT=' + $Arm.ProcessorCount, '').Replace("`nDOTNET_PROCESSOR_COUNT=" + $Arm.ProcessorCount, '')
        [ordered]@{ Project = $_.Directory.Name; Settings = $value }
    })
    $dependencies = @(Get-ChildItem -LiteralPath (Join-Path $Arm.OutputRoot 'packages') -Filter '*.nupkg.sha512' -File -Recurse | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring((Join-Path $Arm.OutputRoot 'packages').Length).Replace('\','/') + ':' + [IO.File]::ReadAllText($_.FullName).Trim()
    })
    return [ordered]@{
        Coverage = [ordered]@{ PopulationComplete = $false; LifetimesComplete = $false; CountersAvailable = $false; InitialFinalReconciled = $false;
            OutputComplete = $Phase.OutputComplete; DiagnosticsComplete = $events.Complete; ForwardingVerified = $false;
            HostPopulationComplete = $false; SharedCompiler = $false }
        CompilerExecutions = $events.CompilerExecutions; Processes = $processes; Windows = @(); Diagnostics = $events.Diagnostics
        Host = $Phase.Host; WorkloadIdentity = $Plan.Source; DependencyIdentity = (Get-CapHash ($dependencies -join "`n")); EffectiveSettings = $settings
        BuildEvents = $events; MissingMeasurements = $Phase.MeasurementLimitations
    }
}

function Invoke-CompilerCapExperiment {
    param([bool]$Run, [string]$RepositoryRoot, [string]$ExpectedCommit, [string]$ExpectedSdk, [string]$Configuration,
        [string]$FixtureFile, [int]$SetupTimeoutSeconds, [int]$BuildTimeoutSeconds, [string]$QuietWindowDeclaration)
    $fixture = $null
    if ($FixtureFile) {
        $fixturePath = [IO.Path]::GetFullPath($FixtureFile)
        $fixtureBase = [IO.Path]::GetDirectoryName($fixturePath)
        $null = Assert-CapOwnedPath $fixturePath ([IO.Path]::GetTempPath())
        $fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json -AsHashtable
        if ($fixture.Contract -ne 'compiler-cap-fixture-v1' -or -not (Test-Path -LiteralPath (Join-Path $fixtureBase '.compiler-cap-test-fixture'))) { throw 'fixture-ownership-required' }
        $RepositoryRoot = Join-Path $fixtureBase 'source'
        $boundary = Join-Path $fixtureBase 'experiments'
        $sdk = $fixture.Sdk
    } else {
        $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
        $boundary = Join-Path (Get-WorkerBuildRunRoot -Root $RepositoryRoot) 'artifacts/compiler-cap'
        $sdk = Get-CapSdkIdentity $RepositoryRoot
    }
    $source = Get-CapSourceIdentity $RepositoryRoot -Fixture:($null -ne $fixture)
    $runId = if ($null -ne $fixture) { [string](Get-CapField $fixture 'RunId' 'fixture-run') } else { [Guid]::NewGuid().ToString('N') }
    if ($runId -notmatch '^[a-z0-9-]{1,64}$') { throw 'invalid-run-id' }
    $output = Assert-CapOwnedPath (Join-Path $boundary $runId) $boundary
    if ($output.StartsWith($RepositoryRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'outputs-must-be-outside-source' }
    $plan = [ordered]@{
        ContractVersion = 1; Mode = $(if ($Run) { 'run' } else { 'plan' }); EvidenceSource = $(if ($null -ne $fixture) { 'synthetic-fixture' } else { 'native' })
        RepositoryRoot = $RepositoryRoot; Project = (Join-Path $RepositoryRoot 'src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj')
        Source = $source; Sdk = $sdk; Configuration = $Configuration; OutputRoot = $output
        ExpectedCommit = $ExpectedCommit; ExpectedSdk = $ExpectedSdk; QuietWindowDeclaration = $QuietWindowDeclaration
        Limits = [ordered]@{ SetupSeconds = $SetupTimeoutSeconds; BuildSeconds = $BuildTimeoutSeconds; CleanupSeconds = 5; OutputDrainSeconds = 5;
            LaunchGateSeconds = 30; GuardianOutputGraceSeconds = 10; CimOperationSeconds = 2; InspectionSeconds = 30 }
        Protocol = [ordered]@{ SampleIntervalMilliseconds = 250; MinimumActiveWindows = 10; MaximumActiveGapMilliseconds = 500;
            PeakRatioMaximum = 0.80; WallRatioMaximum = 1.10; CpuRatioMaximum = 1.05; BackgroundPercentMaximum = 10; BackgroundDifferencePointsMaximum = 5;
            UseSharedCompilation = $false; ColdOutputs = 'fresh-per-arm'; Setup = 'separate-unmeasured-restore'; AnalyzerSettings = 'unchanged'; Repetitions = 1;
            NativeAccounting = 'snapshots-only-incomplete-population-and-lifetimes'; Adoption = 'requires-repeated-matched-runs-and-separate-owner-action' }
        Prerequisites = @('clean-source-at-expected-commit', 'actual-sdk-equals-expected-sdk', 'quiet-window-declaration', 'owned-isolated-roots', 'complete-compiler-and-host-accounting-for-eligible-comparison')
        Arms = @()
    }
    foreach ($count in @($null,8,4)) {
        $name = if ($null -eq $count) { 'baseline' } else { "count-$count" }
        $arm = [ordered]@{ Name = $name; ProcessorCount = $count; OutputRoot = (Join-Path $output $name); Executable = $sdk.Executable;
            ScopedEnvironment = $(if ($null -eq $count) { @{} } else { @{ CscEnvironmentAppend = "DOTNET_PROCESSOR_COUNT=$count" } }); Environment = @{}; SetupArguments = @(); BuildArguments = @() }
        $arm.Environment = Get-CapEnvironment $arm
        $arm.SetupArguments = @(Get-CapArguments $plan $arm 'setup')
        $arm.BuildArguments = @(Get-CapArguments $plan $arm 'build')
        $plan.Arms += $arm
    }
    if (-not $Run) { return @{ Plan = $plan } }
    if (Test-Path -LiteralPath $output) { throw 'owned-root-already-exists' }
    [void][IO.Directory]::CreateDirectory($output)
    [IO.File]::WriteAllText((Join-Path $output 'owner.json'), (@{ ContractVersion = 1; RunId = (Split-Path $output -Leaf); Root = $output; Source = $source; OwnerPid = $PID; OwnerStartUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('O') } | ConvertTo-Json -Depth 8))
    $report = [ordered]@{ ContractVersion = 1; Plan = $plan; ReportPath = (Join-Path $output 'report.json'); Status = 'running'; Reasons = @(); Arms = @(); Comparisons = @(); Invocations = @() }
    foreach ($arm in $plan.Arms) {
        $report.Arms += [ordered]@{ Name = $arm.Name; OutputRoot = $arm.OutputRoot; Status = 'not-executed'; Phases = @(); Observation = $null; Measurements = $null;
            WallTime = (New-CapMetric $null 'seconds' 'build-not-completed') }
    }
    Write-CapReport $report
    try {
        if (-not $source.Clean) { throw 'candidate-not-clean' }
        if ($null -eq $fixture -and ($ExpectedCommit -ne $source.Commit -or $ExpectedSdk -ne $sdk.Version)) { throw 'candidate-or-sdk-mismatch' }
        if ($null -eq $fixture -and [string]::IsNullOrWhiteSpace($QuietWindowDeclaration)) { throw 'quiet-window-declaration-required' }
        if (-not $sdk.CscEnvironmentForwarded) { throw 'csc-environment-channel-unverified' }
        if ($env:DOTNET_PROCESSOR_COUNT) { throw 'ambient-processor-count-would-change-baseline' }
        if ($null -eq $fixture) { Initialize-CapLogger $plan }
        $phaseIndex = 0
        for ($index = 0; $index -lt 3; $index++) {
            $arm = $plan.Arms[$index]
            $record = $report.Arms[$index]
            $null = Assert-CapOwnedPath $arm.OutputRoot $output
            [void][IO.Directory]::CreateDirectory($arm.OutputRoot)
            New-CapProtocolTargets $arm
            $record.Status = 'running'
            foreach ($phaseName in @('setup','build')) {
                $current = Get-CapSourceIdentity $RepositoryRoot -Fixture:($null -ne $fixture)
                if (($current | ConvertTo-Json -Compress) -cne ($source | ConvertTo-Json -Compress)) { throw 'candidate-or-input-mismatch' }
                if ($null -eq $fixture -and ((Get-CapSdkIdentity $RepositoryRoot) | ConvertTo-Json -Compress) -cne ($sdk | ConvertTo-Json -Compress)) { throw 'sdk-identity-mismatch' }
                $arguments = if ($phaseName -eq 'setup') { $arm.SetupArguments } else { $arm.BuildArguments }
                $timeout = if ($phaseName -eq 'setup') { $SetupTimeoutSeconds } else { $BuildTimeoutSeconds }
                $phaseRoot = Join-Path $arm.OutputRoot $phaseName
                [void][IO.Directory]::CreateDirectory($phaseRoot)
                $invocation = [ordered]@{ Arm = $arm.Name; Phase = $phaseName; Executable = $sdk.Executable; Arguments = $arguments;
                    Environment = $arm.Environment; TimeoutSeconds = $timeout; Status = 'started'; Result = $null }
                $report.Invocations += $invocation
                $record.Phases += $invocation
                Write-CapReport $report
                if ($null -ne $fixture) {
                    if ($phaseIndex -ge $fixture.Phases.Count) { throw 'fixture-phase-evidence-missing' }
                    $phase = $fixture.Phases[$phaseIndex]
                    if ((Get-CapField $phase 'Identity' $source | ConvertTo-Json -Compress) -cne ($source | ConvertTo-Json -Compress)) { throw 'candidate-or-sdk-or-input-mismatch' }
                    $processFixture = Get-CapField $fixture 'NativeProcess' $null
                    if ($null -ne $processFixture) {
                        $phase = & (Join-Path $PSScriptRoot 'Invoke-WorkerBuildDiagnostic.ps1') -CompilerCapObservation @{
                            Executable = $sdk.Executable; Root = $RepositoryRoot; EvidenceRoot = $phaseRoot; Compiler = $sdk.Compiler;
                            Environment = $invocation.Environment; ProcessFixture = $processFixture
                        } -TimeoutSeconds $timeout -Arguments $arguments
                        # Keep report-state fixtures separate from executed lifecycle evidence.
                        # No supplied observation can make process-fixture measurements eligible.
                    } else {
                        # Pure report fixtures have no executable content or native fallback.
                        [IO.File]::WriteAllText((Join-Path $phaseRoot 'fixture-phase.json'), ($phase | ConvertTo-Json -Depth 32))
                    }
                } else {
                    $phase = & (Join-Path $PSScriptRoot 'Invoke-WorkerBuildDiagnostic.ps1') -CompilerCapObservation @{
                        Executable = $sdk.Executable; Root = $RepositoryRoot; EvidenceRoot = $phaseRoot; Compiler = $sdk.Compiler; Environment = $invocation.Environment
                    } -TimeoutSeconds $timeout -Arguments $arguments
                }
                $phaseIndex++
                $invocation.Result = $phase
                $invocation.Status = $phase.Status
                Write-CapReport $report
                if ($phase.Status -ne 'completed' -or $phase.ExitCode -ne 0 -or -not $phase.CleanupConfirmed -or -not $phase.OutputComplete) {
                    $record.Status = 'failed'
                    throw "terminal-$phaseName-$($phase.Status)-exit-$($phase.ExitCode)"
                }
                if ($phaseName -eq 'build') {
                    $record.WallTime = New-CapMetric $phase.ElapsedSeconds 'seconds'
                    $record.Observation = if ($null -ne $fixture -and $null -eq (Get-CapField $fixture 'NativeProcess' $null)) {
                        $phase.Observation
                    } else { Get-CapNativeObservation $plan $arm $phase }
                    $record.Measurements = ConvertTo-CapMeasurements $record.Observation $plan $arm
                }
                $current = Get-CapSourceIdentity $RepositoryRoot -Fixture:($null -ne $fixture)
                if (($current | ConvertTo-Json -Compress) -cne ($source | ConvertTo-Json -Compress)) { throw 'candidate-or-input-mismatch' }
                if ($null -eq $fixture -and ((Get-CapSdkIdentity $RepositoryRoot) | ConvertTo-Json -Compress) -cne ($sdk | ConvertTo-Json -Compress)) { throw 'sdk-identity-mismatch' }
            }
            $record.Status = 'completed'
            Write-CapReport $report
        }
        $report.Status = 'completed'
    }
    catch [Management.Automation.PipelineStoppedException] { $report.Status = 'cancelled'; $report.Reasons += 'pipeline-cancelled' }
    catch { $report.Status = 'terminated'; $report.Reasons += $_.Exception.Message }
    finally {
        foreach ($record in $report.Arms) {
            if ($record.Status -eq 'running') { $record.Status = 'interrupted' }
            elseif ($record.Status -eq 'not-executed') { $record.Status = 'skipped' }
        }
        $report.Comparisons = @(Get-CapComparisons $report)
        if ($report.Status -ne 'completed') {
            foreach ($comparison in $report.Comparisons) { $comparison.Verdict = 'inconclusive'; $comparison.Reasons += $report.Reasons; $comparison.Ratios = $null }
        }
        Write-CapReport $report
        $summary = @("status=$($report.Status)", "evidence=$($plan.EvidenceSource)", 'exploratory only; no measured optimization or policy adoption established')
        $summary += @($report.Comparisons | ForEach-Object { "$($_.Arm): $($_.Verdict) $($_.Reasons -join ',')" })
        [IO.File]::WriteAllLines((Join-Path $output 'summary.txt'), [string[]]$summary)
    }
    return $report
}

Export-ModuleMember -Function Invoke-CompilerCapExperiment, Invoke-CapNativePhase
