Set-StrictMode -Version Latest

function Get-IsolatedRootBase {
    if (-not [string]::IsNullOrWhiteSpace($env:MCG_DOTNET_ISOLATED_ROOT)) {
        return [System.IO.Path]::GetFullPath($env:MCG_DOTNET_ISOLATED_ROOT)
    }
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and
        -not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        return [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA '..\LocalLow\mcg-dotnet-isolated'))
    }
    return Join-Path ([System.IO.Path]::GetTempPath()) 'mcg-dotnet-isolated'
}

function Invoke-WorkerReceiptGit {
    param([string]$Root, [string[]]$Arguments, [hashtable]$Environment = @{})
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = 'git'
    $info.WorkingDirectory = $Root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Only fixed Git switches are passed here; repository paths use WorkingDirectory/environment.
    $info.Arguments = (@('-c', 'core.fsmonitor=false', '-c', 'gc.auto=0') + $Arguments) -join ' '
    foreach ($name in @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
        $info.EnvironmentVariables.Remove($name)
    }
    foreach ($name in $Environment.Keys) { $info.EnvironmentVariables[$name] = $Environment[$name] }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Could not start receipt Git process.' }
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'Receipt Git process did not exit within the hang guard.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Receipt Git command failed: $($Arguments -join ' ') exit=$($process.ExitCode)" }
        return $output.Trim()
    }
    finally { $process.Dispose() }
}

function Get-WorkerBuildRunRoot {
    param([Parameter(Mandatory = $true)][string]$Root)
    $normalized = [System.IO.Path]::GetFullPath($Root).Replace('\', '/')
    $match = [regex]::Match($normalized, '(?i)/\.orchestrator-worktrees/([^/]+)(?:/|$)')
    $goal = if ($match.Success) { $match.Groups[1].Value } else {
        try {
            $branch = Invoke-WorkerReceiptGit -Root $Root -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')
            if ($branch.StartsWith('goal/', [StringComparison]::OrdinalIgnoreCase)) { $branch.Substring(5) } else { 'manual' }
        }
        catch { 'manual' }
    }
    $safe = ($goal.Trim().ToLowerInvariant().ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_)) { $_ } else { '-' }
    }) -join ''
    $safe = $safe.Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) { $safe = 'manual' }
    return Join-Path (Get-IsolatedRootBase) "goals\$safe"
}

function Get-WorktreeHeadCommit {
    param([Parameter(Mandatory = $true)][string]$Root)
    try {
        $head = Invoke-WorkerReceiptGit -Root $Root -Arguments @('rev-parse', 'HEAD')
        if ($head -match '^[a-fA-F0-9]{40}$') { return $head.ToLowerInvariant() }
    }
    catch { }
    return $null
}

function Get-WorktreeTreeDigest {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$TemporaryRoot)
    # Each computation owns a private index and object store; never mutate the real index or
    # process-wide Git environment, including during concurrent test-helper invocations.
    $privateRoot = Join-Path $TemporaryRoot ('receipt-git-' + [Guid]::NewGuid().ToString('N'))
    $objects = Join-Path $privateRoot 'objects'
    [void](New-Item -ItemType Directory -Force -Path $objects)
    try {
        $commonObjects = Invoke-WorkerReceiptGit -Root $Root -Arguments @('rev-parse', '--path-format=absolute', '--git-path', 'objects')
        $environment = @{
            GIT_INDEX_FILE = Join-Path $privateRoot 'index'
            GIT_OBJECT_DIRECTORY = $objects
            GIT_ALTERNATE_OBJECT_DIRECTORIES = $commonObjects
            GIT_OPTIONAL_LOCKS = '0'
        }
        $null = Invoke-WorkerReceiptGit -Root $Root -Arguments @('read-tree', 'HEAD') -Environment $environment
        $null = Invoke-WorkerReceiptGit -Root $Root -Arguments @('add', '-A') -Environment $environment
        $tree = Invoke-WorkerReceiptGit -Root $Root -Arguments @('write-tree') -Environment $environment
        if ($tree -notmatch '^[a-fA-F0-9]{40}([a-fA-F0-9]{24})?$') { throw 'Invalid worktree tree digest.' }
        return "wbc1:git-tree:$($tree.ToLowerInvariant())"
    }
    finally {
        # privateRoot was created under the caller's explicit temporary directory.
        Remove-Item -LiteralPath $privateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Assert-WorkerBuildArtifactsPath {
    param([string]$Root, [string]$ArtifactsPath, [string]$RunRoot)
    if (-not [System.IO.Path]::IsPathRooted($ArtifactsPath)) { throw 'Worker artifacts path must be absolute.' }
    if ([string]::IsNullOrWhiteSpace($RunRoot)) { $RunRoot = Get-WorkerBuildRunRoot -Root $Root }
    $sanctionedRoot = [System.IO.Path]::GetFullPath($RunRoot).TrimEnd('\', '/')
    $resolved = [System.IO.Path]::GetFullPath($ArtifactsPath).TrimEnd('\', '/')
    if (-not (Split-Path -Parent $resolved).Equals($sanctionedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notmatch '^artifacts(?:-build-\d+)?$') {
        throw "Worker artifacts path is outside the sanctioned goal boundary: $resolved"
    }
    Assert-WorkerBuildPathNotRedirected -Path $resolved
    return $resolved
}

function Assert-WorkerBuildPathNotRedirected {
    param([string]$Path, [switch]$Recurse)
    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Worker build path is redirected: $current"
            }
        }
        $current = Split-Path -Parent $current
    }
    if ($Recurse) {
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Worker build closure is redirected: $($item.FullName)"
            }
        }
    }
}

function Get-WorkerBuildProjectClosure {
    param([string]$Project, [System.Collections.Generic.HashSet[string]]$Visited)
    if (-not $Visited.Add($Project)) { return }
    if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) { return }
    # These counts describe literal project references, not an MSBuild evaluation.
    try {
        [xml]$xml = [System.IO.File]::ReadAllText($Project)
        foreach ($reference in $xml.SelectNodes('//*[local-name()="ProjectReference"]/@Include')) {
            foreach ($include in $reference.Value.Split(';')) {
                if ([string]::IsNullOrWhiteSpace($include) -or $include.Contains('$(')) { continue }
                $path = if ([System.IO.Path]::IsPathRooted($include)) {
                    [System.IO.Path]::GetFullPath($include)
                } else {
                    [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $Project) $include))
                }
                Get-WorkerBuildProjectClosure -Project $path -Visited $Visited
            }
        }
    }
    catch { } # Diagnostic counts must not replace build diagnostics for malformed inputs.
}

function ConvertTo-WorkerBuildMsBuildPath {
    param([string]$Path)
    foreach ($character in @('%', '$', '@', ';', "'", '(', ')', '*', '?')) {
        $Path = $Path.Replace($character, ('%{0:X2}' -f [int][char]$character))
    }
    return [System.Security.SecurityElement]::Escape($Path)
}

function Get-WorkerBuildInvocationPlan {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Projects,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath,
        [string]$TraversalDirectory
    )
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $paths = @($Projects | ForEach-Object {
        $path = if ([System.IO.Path]::IsPathRooted($_)) { [System.IO.Path]::GetFullPath($_) } else { [System.IO.Path]::GetFullPath((Join-Path $Root $_)) }
        if ($seen.Add($path)) { $path }
    })
    if ($paths.Count -eq 0) { throw 'Worker build invocation plan requires at least one project.' }
    $union = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $separate = 0
    foreach ($path in $paths) {
        $closure = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        Get-WorkerBuildProjectClosure -Project $path -Visited $closure
        $separate += $closure.Count
        $union.UnionWith($closure)
    }
    $maxCpuCount = 0
    if (-not [int]::TryParse($env:MCG_BUILD_MAXCPUCOUNT, [ref]$maxCpuCount) -or $maxCpuCount -le 0) { $maxCpuCount = 1 }
    $mode = 'direct'
    $target = $paths[0]
    $resultsPath = $null
    if ($paths.Count -gt 1) {
        $mode = 'traversal'
        if ([string]::IsNullOrWhiteSpace($TraversalDirectory)) {
            $TraversalDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('worker-build-plan-' + [Guid]::NewGuid().ToString('N'))
        }
        [void](New-Item -ItemType Directory -Force -Path $TraversalDirectory)
        $target = Join-Path $TraversalDirectory 'worker-build.proj'
        $resultsPath = Join-Path $TraversalDirectory 'worker-build-results.txt'
        $items = for ($index = 0; $index -lt $paths.Count; $index++) {
            '<ProjectReference Include="{0}"><Order>{1:D2}</Order></ProjectReference>' -f (ConvertTo-WorkerBuildMsBuildPath $paths[$index]), ($index + 1)
        }
        $xml = @'
<Project DefaultTargets="Build">
  <PropertyGroup><WorkerBuildResultsFile>RESULTS_PATH</WorkerBuildResultsFile></PropertyGroup>
  <ItemGroup>PROJECT_ITEMS</ItemGroup>
  <Target Name="Restore">
    <MSBuild Projects="@(ProjectReference)" Targets="Restore" BuildInParallel="false" StopOnFirstFailure="false" />
  </Target>
  <Target Name="InitializeBuild">
    <Delete Files="$(WorkerBuildResultsFile)" />
  </Target>
  <Target Name="BuildProjects" Outputs="%(ProjectReference.Identity)">
    <Message Text="WorkerBuildProjectBegin:%(ProjectReference.Order)" Importance="high" />
    <MSBuild Projects="%(ProjectReference.Identity)" Targets="Build" BuildInParallel="false" ContinueOnError="ErrorAndContinue" />
    <WriteLinesToFile File="$(WorkerBuildResultsFile)" Lines="%(ProjectReference.Order)|$(MSBuildLastTaskResult)" Overwrite="false" />
    <Message Text="WorkerBuildProjectEnd:%(ProjectReference.Order)" Importance="high" />
  </Target>
  <Target Name="Build" DependsOnTargets="InitializeBuild;BuildProjects" />
</Project>
'@
        $xml = $xml.Replace('RESULTS_PATH', (ConvertTo-WorkerBuildMsBuildPath $resultsPath)).Replace('PROJECT_ITEMS', ($items -join "`n"))
        [System.IO.File]::WriteAllText($target, $xml, [System.Text.UTF8Encoding]::new($false))
    }
    $arguments = [string[]]@('build', $target, '--nologo', '--configuration', $Configuration, '--verbosity', 'minimal',
        '--artifacts-path', $ArtifactsPath, "-maxcpucount:$maxCpuCount", '-p:BuildInParallel=false', '-clp:ErrorsOnly', '-tl:off',
        '-p:RunAnalyzers=false', '-p:EnforceCodeStyleInBuild=false')
    # The temp traversal entry cannot inherit the repository's Directory.Build.rsp.
    if ($mode -eq 'traversal') { $arguments += '-nodeReuse:false' }
    $invocations = [System.Collections.Generic.List[string[]]]::new()
    $invocations.Add($arguments)
    return [pscustomobject]@{
        Mode = $mode; Invocations = $invocations; SeparateEvaluations = $separate; TraversalEvaluations = $union.Count
        TraversalPath = if ($mode -eq 'traversal') { $target } else { $null }
        ResultsPath = $resultsPath
    }
}

Export-ModuleMember -Function Get-IsolatedRootBase, Get-WorkerBuildRunRoot, Get-WorktreeTreeDigest, Get-WorktreeHeadCommit, Assert-WorkerBuildArtifactsPath, Assert-WorkerBuildPathNotRedirected, Get-WorkerBuildInvocationPlan
