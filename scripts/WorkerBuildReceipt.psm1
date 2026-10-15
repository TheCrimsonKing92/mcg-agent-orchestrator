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

Export-ModuleMember -Function Get-IsolatedRootBase, Get-WorkerBuildRunRoot, Get-WorktreeTreeDigest, Assert-WorkerBuildArtifactsPath, Assert-WorkerBuildPathNotRedirected
