Set-StrictMode -Version Latest

$script:ExitCodes = [pscustomobject]@{
    Manifest = 20
    InvalidPartition = 21
    ResultsDirectory = 22
    Build = 23
    MissingAppHost = 24
    InvalidTarget = 25
    Runner = 26
    ZeroTests = 27
    Cleanup = 28
    Timeout = 29
}

$script:MtpGracefulExitSeconds = 15
$script:MtpExitConfirmationSeconds = 10
$script:MtpOutputDrainSeconds = 5

if ($null -eq ('McgMtpProcessOutputCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

public sealed class McgMtpProcessOutputCapture : IDisposable
{
    private readonly object gate = new object();
    private readonly StreamWriter writer;
    private readonly List<string> lines = new List<string>();
    private readonly ManualResetEvent streamsClosed = new ManualResetEvent(false);
    private int openStreams = 2;

    public McgMtpProcessOutputCapture(string path)
    {
        writer = new StreamWriter(path, false) { AutoFlush = true };
    }

    public void Attach(Process process)
    {
        process.OutputDataReceived += Receive;
        process.ErrorDataReceived += Receive;
    }

    public void Detach(Process process)
    {
        process.OutputDataReceived -= Receive;
        process.ErrorDataReceived -= Receive;
    }

    private void Receive(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data == null)
        {
            if (Interlocked.Decrement(ref openStreams) == 0)
            {
                streamsClosed.Set();
            }
            return;
        }

        lock (gate)
        {
            lines.Add(eventArgs.Data);
            writer.WriteLine(eventArgs.Data);
            Console.Out.WriteLine(eventArgs.Data);
        }
    }

    public bool WaitForCompletion(int milliseconds)
    {
        return streamsClosed.WaitOne(milliseconds);
    }

    public string[] Snapshot()
    {
        lock (gate)
        {
            return lines.ToArray();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer.Dispose();
        }
        streamsClosed.Dispose();
    }
}
'@
}

function Test-MtpWindows {
    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Get-MtpTestExitCodes {
    return $script:ExitCodes
}

function Read-MtpTestManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Acceptance manifest is missing: $Path"
    }

    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Acceptance manifest is unreadable: $Path. $($_.Exception.Message)"
    }

    if ($null -eq $manifest.engine -or
        $null -eq $manifest.engine.infrastructureTestLanes -or
        $null -eq $manifest.engine.mtpInvocations) {
        throw "Acceptance manifest is missing engine.infrastructureTestLanes or engine.mtpInvocations: $Path"
    }

    return $manifest
}

function Get-MtpLocalPartitions {
    param([Parameter(Mandatory = $true)]$Manifest)

    if ($null -eq $Manifest.engine.localTestPartitions) {
        throw 'Acceptance manifest is missing engine.localTestPartitions.'
    }

    $lanes = @($Manifest.engine.infrastructureTestLanes)
    $partitions = foreach ($partition in @($Manifest.engine.localTestPartitions)) {
        if ([string]::IsNullOrWhiteSpace([string]$partition.name) -or $null -eq $partition.laneNames) {
            throw 'Every local test partition requires a non-empty name and laneNames.'
        }

        $resolvedLanes = foreach ($laneName in @($partition.laneNames)) {
            $matches = @($lanes | Where-Object {
                ([string]$_.name).Equals([string]$laneName, [System.StringComparison]::OrdinalIgnoreCase)
            })
            if ($matches.Count -ne 1) {
                throw "Local test partition '$($partition.name)' references missing or duplicate lane '$laneName'."
            }
            $matches[0]
        }
        $additionalFilters = if ($null -ne $partition.PSObject.Properties['additionalFilters']) {
            @($partition.additionalFilters | ForEach-Object { [string]$_ })
        }
        else {
            @()
        }

        [pscustomobject]@{
            Name = [string]$partition.name
            Description = [string]$partition.description
            Lanes = @($resolvedLanes)
            AdditionalFilters = @($additionalFilters)
            Filters = @(
                @($resolvedLanes | ForEach-Object { [string]$_.filter })
                $additionalFilters
            ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        }
    }

    $duplicate = @($partitions | Group-Object -Property Name | Where-Object Count -gt 1)
    if ($duplicate.Count -gt 0) {
        throw "Acceptance manifest local test partition '$($duplicate[0].Name)' is duplicated."
    }

    return @($partitions)
}

function ConvertTo-MtpFilterArguments {
    param([Parameter(Mandatory = $true)][string]$Filter)

    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($rawToken in [regex]::Split($Filter, '[&|]')) {
        $token = $rawToken.Trim().Trim('(', ')').Trim()
        if ($token.Length -eq 0) {
            continue
        }

        $fullyQualifiedName = [regex]::Match(
            $token,
            '^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($fullyQualifiedName.Success) {
            if ($fullyQualifiedName.Groups['op'].Value -eq '!~') {
                $arguments.Add('--filter-not-class')
            }
            else {
                $arguments.Add('--filter-class')
            }
            $arguments.Add("*$($fullyQualifiedName.Groups['value'].Value)*")
            continue
        }

        $methodName = [regex]::Match(
            $token,
            '^(DisplayName|Name)\s*(?<op>!~|~)\s*(?<value>[^\s&|()]+)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($methodName.Success) {
            if ($methodName.Groups['op'].Value -eq '!~') {
                $arguments.Add('--filter-not-method')
            }
            else {
                $arguments.Add('--filter-method')
            }
            $arguments.Add("*$($methodName.Groups['value'].Value)*")
            continue
        }

        $categoryExclusion = [regex]::Match(
            $token,
            '^Category\s*!=\s*(?<value>[A-Za-z_][A-Za-z0-9_.-]*)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($categoryExclusion.Success) {
            $arguments.Add('--filter-not-trait')
            $arguments.Add("Category=$($categoryExclusion.Groups['value'].Value)")
            continue
        }

        throw "MTP test filter '$Filter' contains unsupported token '$token'. Use FullyQualifiedName~Class, DisplayName~Method, or Category!=Trait syntax."
    }

    return $arguments.ToArray()
}

function Get-MtpBoundedFileName {
    param(
        [Parameter(Mandatory = $true)][string]$Stem,
        [string]$Suffix = '.trx',
        [ValidateRange(32, 200)][int]$MaximumLength = 200
    )

    $sanitized = [regex]::Replace($Stem.Trim(), '[^A-Za-z0-9_.-]+', '-').Trim('-', '.')
    if ([string]::IsNullOrWhiteSpace($sanitized)) {
        $sanitized = 'tests'
    }

    if ($sanitized.Length + $Suffix.Length -le $MaximumLength) {
        return $sanitized + $Suffix
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($sanitized.Trim())
        $hashBytes = $sha.ComputeHash($bytes)
        $hash = ([System.BitConverter]::ToString($hashBytes) -replace '-', '').Substring(0, 16).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    $keep = $MaximumLength - $Suffix.Length - $hash.Length - 1
    if ($keep -le 0) {
        return $hash + $Suffix
    }
    return $sanitized.Substring(0, $keep).TrimEnd('-', '.') + '-' + $hash + $Suffix
}

function Get-DefaultMtpResultsRoot {
    $localAppData = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $localAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
    }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'LOCALAPPDATA is unavailable; specify -ResultsRoot under a Low-integrity-writable directory.'
    }
    return Join-Path $localAppData 'Temp\Low\mcg-tests'
}

function Initialize-MtpResultsDirectory {
    param(
        [string]$ResultsRoot,
        [Parameter(Mandatory = $true)][string]$RunLabel
    )

    if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
        $ResultsRoot = Get-DefaultMtpResultsRoot
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($ResultsRoot)
    if (Test-MtpWindows) {
        $canonicalLowRoot = [System.IO.Path]::GetFullPath((Get-DefaultMtpResultsRoot)).TrimEnd('\', '/')
        $lowPrefix = $canonicalLowRoot + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRoot.Equals($canonicalLowRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $resolvedRoot.StartsWith($lowPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Results directory '$resolvedRoot' is not under the Low-integrity-writable root '$canonicalLowRoot'. Use -ResultsRoot beneath that root; an ordinary Medium-integrity directory is not writable by the MTP apphost."
        }
    }

    try {
        New-Item -ItemType Directory -Force -Path $resolvedRoot | Out-Null
        $probe = Join-Path $resolvedRoot ".write-probe-$PID-$([Guid]::NewGuid().ToString('N'))"
        [System.IO.File]::WriteAllText($probe, 'probe')
        Remove-Item -LiteralPath $probe -Force
    }
    catch {
        throw "Results directory '$resolvedRoot' is not writable. MTP requires a Low-integrity-writable results root. $($_.Exception.Message)"
    }

    $safeLabel = Get-MtpBoundedFileName -Stem $RunLabel -Suffix '' -MaximumLength 40
    $runDirectory = Join-Path $resolvedRoot "$safeLabel-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    try {
        New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
    }
    catch {
        throw "Could not create invocation results directory '$runDirectory'. MTP requires a Low-integrity-writable results root. $($_.Exception.Message)"
    }
    if ((240 - $runDirectory.Length - 1) -lt 48) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction Stop
        throw "Results root '$resolvedRoot' is too long for bounded MTP TRX paths. Choose a shorter directory beneath the Low-integrity-writable root."
    }
    return $runDirectory
}

function Set-MtpHermeticEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$WritableRoot
    )

    $nugetPackages = $env:NUGET_PACKAGES
    $userProfile = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    $allowedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('PATH', 'PATHEXT', 'SystemRoot', 'WINDIR', 'COMSPEC', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'LOCALAPPDATA', 'TEMP', 'TMP', 'TMPDIR')) {
        [void]$allowedNames.Add($name)
    }
    foreach ($item in @(Get-ChildItem Env:)) {
        if (-not $allowedNames.Contains($item.Name) -and
            -not $item.Name.StartsWith('DOTNET_', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $item.Name.StartsWith('NUGET_', [System.StringComparison]::OrdinalIgnoreCase)) {
            [System.Environment]::SetEnvironmentVariable($item.Name, $null, [System.EnvironmentVariableTarget]::Process)
        }
    }

    $profileRoot = Join-Path $WritableRoot 'profile'
    $appData = Join-Path $profileRoot 'AppData\Roaming'
    $nugetHttpCache = Join-Path $WritableRoot 'nuget-http-cache'
    $nugetPluginsCache = Join-Path $WritableRoot 'nuget-plugins-cache'
    New-Item -ItemType Directory -Force -Path $profileRoot, $appData, $nugetHttpCache, $nugetPluginsCache | Out-Null
    if ([string]::IsNullOrWhiteSpace($nugetPackages)) {
        $packageProfile = if ([string]::IsNullOrWhiteSpace($userProfile)) { $profileRoot } else { $userProfile }
        $nugetPackages = Join-Path $packageProfile '.nuget\packages'
    }

    $env:HOME = $profileRoot
    $env:USERPROFILE = $userProfile
    $env:DOTNET_CLI_HOME = $profileRoot
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = $nugetPackages
    $env:NUGET_HTTP_CACHE_PATH = $nugetHttpCache
    $env:NUGET_PLUGINS_CACHE_PATH = $nugetPluginsCache
    $env:APPDATA = $appData
    $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = [System.IO.Path]::GetFullPath($RepositoryRoot)
}

function Get-MtpEnvironmentSnapshot {
    $snapshot = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @(Get-ChildItem Env:)) {
        $snapshot[$item.Name] = [string]$item.Value
    }
    return ,$snapshot
}

function Restore-MtpEnvironment {
    param([Parameter(Mandatory = $true)]$Snapshot)

    foreach ($item in @(Get-ChildItem Env:)) {
        if (-not $Snapshot.ContainsKey($item.Name)) {
            [System.Environment]::SetEnvironmentVariable($item.Name, $null, [System.EnvironmentVariableTarget]::Process)
        }
    }
    foreach ($entry in $Snapshot.GetEnumerator()) {
        [System.Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, [System.EnvironmentVariableTarget]::Process)
    }
}

function Get-MtpTargetProjects {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Target
    )

    $targetPath = if ([System.IO.Path]::IsPathRooted($Target)) {
        [System.IO.Path]::GetFullPath($Target)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Target))
    }
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
        throw "Test target does not exist: $targetPath"
    }

    $invocations = @($Manifest.engine.mtpInvocations)
    if ([System.IO.Path]::GetExtension($targetPath).Equals('.sln', [System.StringComparison]::OrdinalIgnoreCase)) {
        $solutionText = Get-Content -LiteralPath $targetPath -Raw
        $selected = @($invocations | Where-Object {
            $project = ([string]$_.project).Replace('/', '\')
            $solutionText.IndexOf($project, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        if ($selected.Count -eq 0) {
            throw "Solution target contains no manifest-declared MTP test projects: $targetPath"
        }
        return $selected
    }

    $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $targetPath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Test project target must be inside the repository: $targetPath"
    }
    $relativeTarget = $targetPath.Substring($repositoryPrefix.Length).Replace('\', '/')
    $selected = @($invocations | Where-Object {
        ([string]$_.project).Replace('\', '/').TrimStart('/').Equals(
            $relativeTarget.TrimStart('/'),
            [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($selected.Count -ne 1) {
        throw "Acceptance manifest has no unique direct-MTP invocation for target '$relativeTarget'."
    }
    return $selected
}

function Invoke-MtpBuild {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][object[]]$Projects,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$DotnetPath
    )

    foreach ($project in $Projects) {
        $buildTarget = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$project.project)))
        $appHost = Resolve-MtpAppHostPath -RepositoryRoot $RepositoryRoot -Invocation $project -Configuration $Configuration
        $outputDirectory = Split-Path -Parent $appHost
        Write-Host "Building test target: $buildTarget ($Configuration) -> $outputDirectory"
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            # Windows PowerShell promotes native stderr redirected through 2>&1 to an
            # ErrorRecord. Build warnings still need to stream, but they must not turn a
            # successful dotnet exit code into a synthetic BUILD FAILURE.
            $ErrorActionPreference = 'Continue'
            & $DotnetPath build $buildTarget --configuration $Configuration --output $outputDirectory --nologo --verbosity minimal 2>&1 |
                ForEach-Object { Write-Host $_ }
            $buildExit = $LASTEXITCODE
        }
        catch {
            Write-Host "BUILD FAILURE - could not start '$DotnetPath build': $($_.Exception.Message)"
            return $false
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($buildExit -ne 0) {
            Write-Host "BUILD FAILURE - '$DotnetPath build' exited $buildExit. The MTP apphost was not launched."
            return $false
        }
    }
    Write-Host 'Build succeeded.'
    return $true
}

function Resolve-MtpAppHostPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $projectPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$Invocation.project)))
    $template = [string]$Invocation.executablePathTemplate
    if ([string]::IsNullOrWhiteSpace($template)) {
        throw "Manifest MTP invocation for '$($Invocation.project)' has no executablePathTemplate."
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    $extension = if (Test-MtpWindows) { '.exe' } else { '' }
    $relativePath = $template.
        Replace('{projectName}', $projectName).
        Replace('{configuration}', $Configuration).
        Replace('{executableExtension}', $extension)
    $resolved = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativePath))
    $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MTP executable path for '$($Invocation.project)' escapes the repository: $resolved"
    }
    return $resolved
}

function New-MtpRunnerArguments {
    param(
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$ResultsDirectory,
        [Parameter(Mandatory = $true)][string]$TrxFileName,
        [ValidateRange(1, 86400)][int]$TestHostTimeoutSeconds = 780,
        [string]$Filter
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($template in @($Invocation.arguments)) {
        $argument = ([string]$template).
            Replace('{executable}', $Executable).
            Replace('{resultsDirectory}', $ResultsDirectory).
            Replace('{trxFileName}', $TrxFileName)
        $arguments.Add($argument)
    }
    if ($arguments.Count -eq 0 -or -not $arguments[0].Equals($Executable, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest MTP invocation for '$($Invocation.project)' must put {executable} first."
    }
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments.AddRange([string[]](ConvertTo-MtpFilterArguments -Filter $Filter))
    }
    $timeoutIndex = $arguments.IndexOf('--timeout')
    if ($timeoutIndex -lt 0) {
        $arguments.Add('--timeout')
        $arguments.Add("${TestHostTimeoutSeconds}s")
    }
    elseif (($timeoutIndex + 1) -ge $arguments.Count) {
        throw "Manifest MTP invocation for '$($Invocation.project)' has --timeout without a value."
    }
    else {
        $arguments[$timeoutIndex + 1] = "${TestHostTimeoutSeconds}s"
    }
    return $arguments.ToArray()
}

function ConvertTo-MtpCommandLineArgument {
    param(
        [AllowNull()][string]$Value,
        [switch]$QuoteCmdMetaCharacters
    )

    if ($null -eq $Value) {
        return '""'
    }
    $charactersRequiringQuotes = @(' ', "`t", "`n", "`r", '"')
    if ($QuoteCmdMetaCharacters) {
        $charactersRequiringQuotes += @('&', '|', '<', '>', '(', ')', '^')
    }
    if ($Value.Length -gt 0 -and $Value.IndexOfAny([char[]]$charactersRequiringQuotes) -lt 0) {
        return $Value
    }

    $quoted = [System.Text.StringBuilder]::new()
    [void]$quoted.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$quoted.Append('\', ($backslashes * 2) + 1)
            [void]$quoted.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$quoted.Append('\', $backslashes)
            $backslashes = 0
        }
        [void]$quoted.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$quoted.Append('\', $backslashes * 2)
    }
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function New-MtpProcessStartInfo {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $extension = [System.IO.Path]::GetExtension($Executable)
    if ($extension.Equals('.cmd', [System.StringComparison]::OrdinalIgnoreCase) -or
        $extension.Equals('.bat', [System.StringComparison]::OrdinalIgnoreCase)) {
        $argumentLine = (@($Arguments | Select-Object -Skip 1 | ForEach-Object {
            ConvertTo-MtpCommandLineArgument $_ -QuoteCmdMetaCharacters
        }) -join ' ')
        $commandInterpreter = $env:ComSpec
        if ([string]::IsNullOrWhiteSpace($commandInterpreter)) {
            $commandInterpreter = Join-Path $env:SystemRoot 'System32\cmd.exe'
        }
        $commandLine = (ConvertTo-MtpCommandLineArgument $Executable -QuoteCmdMetaCharacters)
        if (-not [string]::IsNullOrWhiteSpace($argumentLine)) {
            $commandLine += ' ' + $argumentLine
        }
        $startInfo.FileName = $commandInterpreter
        $startInfo.Arguments = '/d /s /c "' + $commandLine + '"'
    }
    else {
        $argumentLine = (@($Arguments | Select-Object -Skip 1 | ForEach-Object {
            ConvertTo-MtpCommandLineArgument $_
        }) -join ' ')
        $startInfo.FileName = $Executable
        $startInfo.Arguments = $argumentLine
    }
    return $startInfo
}

function Stop-MtpOwnedProcessTree {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][datetime]$StartTimeUtc
    )

    try {
        if ($Process.HasExited) {
            return $true
        }
        $candidate = [System.Diagnostics.Process]::GetProcessById($Process.Id)
        try {
            if ($candidate.StartTime.ToUniversalTime() -ne $StartTimeUtc) {
                Write-Host "CLEANUP FAILURE - owned PID $($Process.Id) no longer has the recorded start identity; refusing to terminate it."
                return $false
            }
        }
        finally {
            $candidate.Dispose()
        }

        if (Test-MtpWindows) {
            $taskkillPath = Join-Path $env:SystemRoot 'System32\taskkill.exe'
            $taskkillInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $taskkillInfo.FileName = $taskkillPath
            $taskkillInfo.Arguments = "/PID $($Process.Id) /T /F"
            $taskkillInfo.UseShellExecute = $false
            $taskkillInfo.CreateNoWindow = $true
            $taskkillInfo.RedirectStandardOutput = $true
            $taskkillInfo.RedirectStandardError = $true
            $taskkill = [System.Diagnostics.Process]::new()
            $taskkill.StartInfo = $taskkillInfo
            try {
                if (-not $taskkill.Start()) {
                    Write-Host "CLEANUP FAILURE - taskkill did not start for owned PID $($Process.Id)."
                    return $false
                }
                if (-not $taskkill.WaitForExit($script:MtpExitConfirmationSeconds * 1000)) {
                    $taskkill.Kill()
                    [void]$taskkill.WaitForExit($script:MtpExitConfirmationSeconds * 1000)
                    Write-Host "CLEANUP FAILURE - taskkill did not finish for owned PID $($Process.Id)."
                    return $false
                }
                $taskkillError = $taskkill.StandardError.ReadToEnd().Trim()
                if ($taskkill.ExitCode -ne 0 -and -not $Process.HasExited) {
                    Write-Host "CLEANUP FAILURE - taskkill exited $($taskkill.ExitCode) for owned PID $($Process.Id): $taskkillError"
                    return $false
                }
            }
            finally {
                $taskkill.Dispose()
            }
        }
        else {
            $Process.Kill()
        }

        return $Process.WaitForExit($script:MtpExitConfirmationSeconds * 1000)
    }
    catch [System.ArgumentException] {
        return $true
    }
    catch [System.InvalidOperationException] {
        return $true
    }
    catch {
        Write-Host "CLEANUP FAILURE - exact owned PID $($Process.Id) could not be terminated: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-MtpAppHost {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$OutputLog,
        [ValidateRange(1, 86400)][int]$TestHostTimeoutSeconds = 780
    )

    $process = $null
    $capture = $null
    $processId = $null
    $startTimeUtc = $null
    $runnerExit = $script:ExitCodes.Runner
    $timedOut = $false
    $exitConfirmed = $false
    $processExitConfirmed = $false
    $streamsDrained = $false
    $processStarted = $false
    $runnerFailed = $false
    try {
        $capture = [McgMtpProcessOutputCapture]::new($OutputLog)
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = New-MtpProcessStartInfo -Executable $Executable -Arguments $Arguments
        $capture.Attach($process)
        if (-not $process.Start()) {
            throw "Process.Start returned false for '$Executable'."
        }
        $processStarted = $true
        $processId = $process.Id
        $startTimeUtc = $process.StartTime.ToUniversalTime()
        $process.BeginOutputReadLine()
        $process.BeginErrorReadLine()

        if (-not $process.WaitForExit($TestHostTimeoutSeconds * 1000)) {
            $timedOut = $true
            Write-Host "TEST HOST TIMEOUT - owned PID $processId exceeded ${TestHostTimeoutSeconds}s; allowing ${script:MtpGracefulExitSeconds}s for MTP cancellation."
            if (-not $process.WaitForExit($script:MtpGracefulExitSeconds * 1000)) {
                Write-Host "TEST HOST TIMEOUT - terminating exact owned process tree rooted at PID $processId."
                [void](Stop-MtpOwnedProcessTree -Process $process -StartTimeUtc $startTimeUtc)
            }
        }

        $processExitConfirmed = $process.HasExited -or $process.WaitForExit($script:MtpExitConfirmationSeconds * 1000)
        $streamsDrained = $capture.WaitForCompletion($script:MtpOutputDrainSeconds * 1000)
        $exitConfirmed = $processExitConfirmed -and $streamsDrained
        if ($process.HasExited) {
            $runnerExit = $process.ExitCode
        }
    }
    catch {
        $runnerFailed = $true
        $line = $_.Exception.Message
        Write-Host $line
        if ($null -eq $capture) {
            Set-Content -LiteralPath $OutputLog -Value $line
        }
    }
    finally {
        if ($null -ne $process -and $processStarted) {
            if (-not $process.HasExited -and $null -ne $startTimeUtc) {
                [void](Stop-MtpOwnedProcessTree -Process $process -StartTimeUtc $startTimeUtc)
            }
            $processExitConfirmed = $process.HasExited -or $process.WaitForExit($script:MtpExitConfirmationSeconds * 1000)
        }
        if ($null -ne $capture) {
            if ($processStarted) {
                $streamsDrained = $capture.WaitForCompletion($script:MtpOutputDrainSeconds * 1000)
            }
            $exitConfirmed = $processStarted -and $processExitConfirmed -and $streamsDrained
            $captured = @($capture.Snapshot())
            if ($null -ne $process) {
                $capture.Detach($process)
            }
            $capture.Dispose()
        }
        else {
            $captured = @()
        }
        if ($null -ne $process) {
            $process.Dispose()
        }
    }

    return [pscustomobject]@{
        ExitCode = $runnerExit
        Output = @($captured)
        TimedOut = $timedOut
        OwnedProcessId = $processId
        ExitConfirmed = $exitConfirmed
        RunnerFailed = $runnerFailed
    }
}

function Read-MtpTrxResult {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        [xml]$trx = Get-Content -LiteralPath $Path -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($null -eq $counters) {
            throw 'ResultSummary/Counters is missing.'
        }
        return [pscustomobject]@{
            Total = [int]$counters.total
            Passed = [int]$counters.passed
            Failed = [int]$counters.failed
            Skipped = [int]$counters.notExecuted
            Document = $trx
        }
    }
    catch {
        throw "TRX '$Path' is unreadable: $($_.Exception.Message)"
    }
}

function New-MtpTerminalResult {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('timed-out', 'failed', 'completed-with-missing-trx', 'completed')][string]$Outcome,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        $RunnerExitCode = $null,
        [AllowNull()][string]$ResultsDirectory,
        [string[]]$RunnerLogPaths = @(),
        [string[]]$TrxPaths = @(),
        [string[]]$ExpectedTrxPaths = @(),
        $OwnedProcessId = $null,
        $ExitConfirmed = $null,
        [bool]$ArtifactsRetained = $false
    )

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        outcome = $Outcome
        exitCode = $ExitCode
        runnerExitCode = $RunnerExitCode
        resultsDirectory = $ResultsDirectory
        runnerLogPaths = @($RunnerLogPaths)
        trxPaths = @($TrxPaths)
        expectedTrxPaths = @($ExpectedTrxPaths)
        ownedProcessId = $OwnedProcessId
        exitConfirmed = $ExitConfirmed
        artifactsRetained = $ArtifactsRetained
    }
}

function Write-MtpTerminalSummary {
    param([Parameter(Mandatory = $true)]$Result)

    Write-Host ('MTP_TERMINAL_SUMMARY ' + ($Result | ConvertTo-Json -Compress -Depth 4))
}

function Invoke-MtpTestRun {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Target,
        [string[]]$Filters = @(''),
        [Parameter(Mandatory = $true)][string]$RunLabel,
        [ValidateRange(1, 25)][int]$Repeat = 1,
        [string]$Configuration = 'Debug',
        [switch]$NoBuild,
        [string]$ResultsRoot,
        [string]$RunnerPath,
        [string]$DotnetPath = 'dotnet',
        [ValidateRange(1, 86400)][int]$TestHostTimeoutSeconds = 780
    )

    try {
        $projects = @(Get-MtpTargetProjects -Manifest $Manifest -RepositoryRoot $RepositoryRoot -Target $Target)
    }
    catch {
        Write-Host "TARGET FAILURE - $($_.Exception.Message)"
        return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.InvalidTarget -ResultsDirectory $null
    }
    try {
        $runDirectory = Initialize-MtpResultsDirectory -ResultsRoot $ResultsRoot -RunLabel $RunLabel
    }
    catch {
        Write-Host "RESULTS DIRECTORY FAILURE - $($_.Exception.Message)"
        return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.ResultsDirectory -ResultsDirectory $null
    }

    $runnerLogPaths = [System.Collections.Generic.List[string]]::new()
    $trxPaths = [System.Collections.Generic.List[string]]::new()
    $expectedTrxPaths = [System.Collections.Generic.List[string]]::new()
    $lastOwnedProcessId = $null
    $lastRunnerExitCode = $null
    $allExitsConfirmed = $true
    $environmentSnapshot = Get-MtpEnvironmentSnapshot
    try {
        Set-MtpHermeticEnvironment -RepositoryRoot $RepositoryRoot -WritableRoot $runDirectory
        Write-Host "Results directory: $runDirectory"
        if (-not $NoBuild) {
            if (-not (Invoke-MtpBuild -RepositoryRoot $RepositoryRoot -Projects $projects -Configuration $Configuration -DotnetPath $DotnetPath)) {
                Write-Host "Retained diagnostic directory: $runDirectory"
                return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Build -ResultsDirectory $runDirectory -ArtifactsRetained $true
            }
        }

        $filterList = @($Filters)
        if ($filterList.Count -eq 0) {
            $filterList = @('')
        }
        $invocationIndex = 0
        foreach ($project in $projects) {
            $expectedAppHost = Resolve-MtpAppHostPath -RepositoryRoot $RepositoryRoot -Invocation $project -Configuration $Configuration
            $executable = if ([string]::IsNullOrWhiteSpace($RunnerPath)) { $expectedAppHost } else { [System.IO.Path]::GetFullPath($RunnerPath) }
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
                $projectPath = Join-Path $RepositoryRoot ([string]$project.project)
                $outputDirectory = Split-Path -Parent $expectedAppHost
                Write-Host "MISSING APPHOST - expected '$executable'. Build it with: $DotnetPath build `"$projectPath`" --configuration $Configuration --output `"$outputDirectory`""
                Write-Host "Retained diagnostic directory: $runDirectory"
                return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.MissingAppHost -ResultsDirectory $runDirectory -ArtifactsRetained $true
            }

            foreach ($filter in $filterList) {
                for ($attempt = 1; $attempt -le $Repeat; $attempt++) {
                $invocationIndex++
                $projectName = [System.IO.Path]::GetFileNameWithoutExtension([string]$project.project)
                $stem = "$RunLabel-$projectName-$invocationIndex-r$attempt-$filter"
                $fileNameBudget = [Math]::Min(200, 240 - $runDirectory.Length - 1)
                $trxFileName = Get-MtpBoundedFileName -Stem $stem -MaximumLength $fileNameBudget
                $trxPath = Join-Path $runDirectory $trxFileName
                $outputLog = Join-Path $runDirectory ([System.IO.Path]::ChangeExtension($trxFileName, '.runner.log'))
                $expectedTrxPaths.Add($trxPath)
                try {
                    $arguments = New-MtpRunnerArguments -Invocation $project -Executable $executable -ResultsDirectory $runDirectory -TrxFileName $trxFileName -TestHostTimeoutSeconds $TestHostTimeoutSeconds -Filter $filter
                }
                catch {
                    Write-Host "RUNNER/TOOLING FAILURE - $($_.Exception.Message)"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Runner -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $lastOwnedProcessId -ExitConfirmed $allExitsConfirmed -ArtifactsRetained $true
                }

                Write-Host "Running MTP apphost: $executable"
                if (-not [string]::IsNullOrWhiteSpace($filter)) {
                    Write-Host "Filter: $filter"
                }
                Write-Host "Runner output log: $outputLog"
                $runnerLogPaths.Add($outputLog)
                $run = Invoke-MtpAppHost -Executable $executable -Arguments $arguments -OutputLog $outputLog -TestHostTimeoutSeconds $TestHostTimeoutSeconds
                $lastOwnedProcessId = $run.OwnedProcessId
                $lastRunnerExitCode = $run.ExitCode
                $allExitsConfirmed = $allExitsConfirmed -and [bool]$run.ExitConfirmed
                if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
                    $trxPaths.Add($trxPath)
                }
                if ($run.TimedOut) {
                    Write-Host "TEST HOST TIMED OUT - apphost exceeded ${TestHostTimeoutSeconds}s. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome timed-out -ExitCode $script:ExitCodes.Timeout -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed ([bool]$run.ExitConfirmed) -ArtifactsRetained $true
                }
                if ($run.RunnerFailed) {
                    Write-Host "RUNNER/TOOLING FAILURE - apphost could not be started or monitored. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Runner -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed ([bool]$run.ExitConfirmed) -ArtifactsRetained $true
                }
                if (-not $run.ExitConfirmed) {
                    Write-Host "CLEANUP FAILURE - owned PID $($run.OwnedProcessId) exit or output drain was not confirmed."
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Cleanup -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $false -ArtifactsRetained $true
                }
                if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
                    Write-Host "COMPLETED WITH MISSING TRX - apphost exited $($run.ExitCode) without producing TRX '$trxPath'. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    $exitCode = if ($run.ExitCode -ne 0) { $run.ExitCode } else { $script:ExitCodes.Runner }
                    return New-MtpTerminalResult -Outcome completed-with-missing-trx -ExitCode $exitCode -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $true -ArtifactsRetained $true
                }

                Write-Host "TRX: $trxPath"
                try {
                    $summary = Read-MtpTrxResult -Path $trxPath
                }
                catch {
                    Write-Host "RUNNER/TOOLING FAILURE - $($_.Exception.Message) Apphost exit: $($run.ExitCode). Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Runner -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $true -ArtifactsRetained $true
                }
                Write-Host ("{0}: total={1} passed={2} failed={3} skipped={4}" -f $trxFileName, $summary.Total, $summary.Passed, $summary.Failed, $summary.Skipped)
                if ($summary.Total -le 0) {
                    Write-Host "ZERO TESTS - filter matched no tests. Apphost exit: $($run.ExitCode). TRX: $trxPath"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.ZeroTests -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $true -ArtifactsRetained $true
                }
                if ($summary.Failed -gt 0) {
                    foreach ($failedResult in @($summary.Document.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed')) {
                        Write-Host "  FAIL  $($failedResult.testName)"
                        $message = [string]$failedResult.Output.ErrorInfo.Message
                        if (-not [string]::IsNullOrWhiteSpace($message)) {
                            Write-Host "        $(($message -split "`r?`n" | Where-Object { $_.Trim() })[0])"
                        }
                    }
                    Write-Host "TEST FAILURES - $($summary.Failed) test(s) failed. TRX: $trxPath"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    $exitCode = if ($run.ExitCode -ne 0) { $run.ExitCode } else { 1 }
                    return New-MtpTerminalResult -Outcome failed -ExitCode $exitCode -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $true -ArtifactsRetained $true
                }
                if ($run.ExitCode -ne 0) {
                    Write-Host "RUNNER/TOOLING FAILURE - apphost exited $($run.ExitCode) although its TRX contains no failing tests. This is not a compile failure. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $run.ExitCode -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $true -ArtifactsRetained $true
                }
                }
            }
        }

        Write-Host "ALL GREEN - clean-run TRX receipts were under '$runDirectory' and will now be removed."
        try {
            Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Host "CLEANUP FAILURE - clean test receipts could not be removed from '$runDirectory': $($_.Exception.Message)"
            Write-Host "Retained diagnostic directory: $runDirectory"
            return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Cleanup -RunnerExitCode $lastRunnerExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $lastOwnedProcessId -ExitConfirmed $allExitsConfirmed -ArtifactsRetained $true
        }
        return New-MtpTerminalResult -Outcome completed -ExitCode 0 -RunnerExitCode $lastRunnerExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $lastOwnedProcessId -ExitConfirmed $allExitsConfirmed -ArtifactsRetained $false
    }
    finally {
        Restore-MtpEnvironment -Snapshot $environmentSnapshot
    }
}

Export-ModuleMember -Function @(
    'Get-MtpTestExitCodes',
    'Read-MtpTestManifest',
    'Get-MtpLocalPartitions',
    'ConvertTo-MtpFilterArguments',
    'Get-MtpBoundedFileName',
    'Get-DefaultMtpResultsRoot',
    'Initialize-MtpResultsDirectory',
    'Get-MtpTargetProjects',
    'New-MtpRunnerArguments',
    'Read-MtpTrxResult',
    'Write-MtpTerminalSummary',
    'Invoke-MtpTestRun'
)
