Set-StrictMode -Version Latest

$script:ProductDirectoryName = "Mcg.AgentOrchestrator"
$script:AutoResumeDirectoryName = "AutoResume"
$script:LauncherExecutableName = "Mcg.HiddenLauncher.exe"
$script:MinimumMediumIntegrityRid = 0x2000

function Get-AutoResumeLifecycleMutexName {
    [CmdletBinding()]
    param(
        [string]$UserSid
    )

    if ([string]::IsNullOrWhiteSpace($UserSid)) {
        $UserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    }
    if ($UserSid -notmatch '^S-\d+(?:-\d+)+$') {
        throw "Cannot derive the auto-resume lifecycle mutex from an invalid user SID."
    }

    "Global\Mcg.AgentOrchestrator.AutoResume." + $UserSid.Replace('-', '_')
}

function Enter-AutoResumeLifecycleLock {
    [CmdletBinding()]
    param(
        [string]$UserSid,
        [TimeSpan]$Timeout = [TimeSpan]::FromSeconds(30),
        [string]$MutexName,
        [scriptblock]$WaitStarted
    )

    if ([string]::IsNullOrWhiteSpace($MutexName)) {
        $MutexName = Get-AutoResumeLifecycleMutexName -UserSid $UserSid
    }
    if ($MutexName -notmatch '^Global\\Mcg\.AgentOrchestrator\.AutoResume\.S_\d+(?:_\d+)+$') {
        throw "The auto-resume lifecycle mutex must use the per-user Global namespace."
    }

    $mutex = $null
    $lockTaken = $false
    try {
        $mutex = [System.Threading.Mutex]::new($false, $MutexName)
        if ($null -ne $WaitStarted) {
            & $WaitStarted
        }
        try { $lockTaken = $mutex.WaitOne($Timeout) }
        catch [System.Threading.AbandonedMutexException] { $lockTaken = $true }
        if (-not $lockTaken) {
            throw "Timed out waiting for the auto-resume lifecycle lock."
        }

        [pscustomobject]@{
            Mutex = $mutex
            Name = $MutexName
        }
    }
    catch {
        if ($null -ne $mutex -and -not $lockTaken) {
            $mutex.Dispose()
        }
        throw
    }
}

function Exit-AutoResumeLifecycleLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Lease
    )

    try {
        $Lease.Mutex.ReleaseMutex()
    }
    finally {
        $Lease.Mutex.Dispose()
    }
}

function Get-AutoResumeLayout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LocalApplicationData
    )

    if ([string]::IsNullOrWhiteSpace($LocalApplicationData) -or
        -not [System.IO.Path]::IsPathRooted($LocalApplicationData)) {
        throw "LocalApplicationData must be a non-empty absolute path."
    }

    $knownFolder = [System.IO.Path]::GetFullPath($LocalApplicationData)
    $root = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($knownFolder, $script:ProductDirectoryName, $script:AutoResumeDirectoryName))

    [pscustomobject]@{
        KnownFolder = $knownFolder
        Root = $root
        Releases = [System.IO.Path]::Combine($root, "releases")
        Staging = [System.IO.Path]::Combine($root, "staging")
    }
}

function ConvertTo-WindowsCommandLineArgument {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $result = [System.Text.StringBuilder]::new()
    [void]$result.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$result.Append('\', (2 * $backslashes) + 1)
            [void]$result.Append('"')
        }
        else {
            if ($backslashes -gt 0) {
                [void]$result.Append('\', $backslashes)
            }
            [void]$result.Append($character)
        }
        $backslashes = 0
    }

    if ($backslashes -gt 0) {
        [void]$result.Append('\', 2 * $backslashes)
    }
    [void]$result.Append('"')
    $result.ToString()
}

function Get-AutoResumeTaskArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PowerShellPath,
        [Parameter(Mandatory)][string]$ResumeScriptPath,
        [Parameter(Mandatory)][string]$TaskName
    )

    @(
        (ConvertTo-WindowsCommandLineArgument $PowerShellPath),
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", (ConvertTo-WindowsCommandLineArgument $ResumeScriptPath),
        "-TaskName", (ConvertTo-WindowsCommandLineArgument $TaskName)
    ) -join " "
}

function Get-AutoResumeFileSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Get-LauncherPayloadIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Launcher payload directory does not exist: $Directory"
    }

    $files = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Launcher payload is empty: $Directory"
    }

    $manifestLines = foreach ($file in $files) {
        $directoryPrefix = [System.IO.Path]::GetFullPath($Directory).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $relative = $file.FullName.Substring($directoryPrefix.Length).Replace('\', '/')
        $hash = Get-AutoResumeFileSha256 -Path $file.FullName
        "$relative`:$hash"
    }
    $manifest = ($manifestLines -join "`n") + "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($manifest)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $identity = ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    [pscustomobject]@{
        Identity = $identity
        Manifest = $manifest
        Files = @($files.FullName)
    }
}

function Assert-LauncherPayloadComplete {
    param([Parameter(Mandatory)][string]$Directory)

    $required = @(
        "Mcg.HiddenLauncher.exe",
        "Mcg.HiddenLauncher.dll",
        "Mcg.HiddenLauncher.deps.json",
        "Mcg.HiddenLauncher.runtimeconfig.json"
    )
    foreach ($name in $required) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Launcher payload is incomplete; missing '$name' in '$Directory'."
        }
    }
}

function Initialize-MandatoryLabelReader {
    if ("Mcg.AgentOrchestrator.WindowsMandatoryLabelReader" -as [type]) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator
{
    public static class WindowsMandatoryLabelReader
    {
        private const int SE_FILE_OBJECT = 1;
        private const uint LABEL_SECURITY_INFORMATION = 0x00000010;
        private const byte SYSTEM_MANDATORY_LABEL_ACE_TYPE = 0x11;

        [StructLayout(LayoutKind.Sequential)]
        private struct ACL
        {
            public byte AclRevision;
            public byte Sbz1;
            public ushort AclSize;
            public ushort AceCount;
            public ushort Sbz2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ACE_HEADER
        {
            public byte AceType;
            public byte AceFlags;
            public ushort AceSize;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetNamedSecurityInfo(
            string objectName,
            int objectType,
            uint securityInfo,
            out IntPtr owner,
            out IntPtr group,
            out IntPtr dacl,
            out IntPtr sacl,
            out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetAce(IntPtr acl, int aceIndex, out IntPtr ace);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static int[] Read(string path)
        {
            IntPtr owner, group, dacl, sacl, descriptor;
            uint error = GetNamedSecurityInfo(
                path,
                SE_FILE_OBJECT,
                LABEL_SECURITY_INFORMATION,
                out owner,
                out group,
                out dacl,
                out sacl,
                out descriptor);
            if (error != 0)
            {
                throw new Win32Exception((int)error);
            }

            try
            {
                var result = new List<int>();
                if (sacl == IntPtr.Zero)
                {
                    return result.ToArray();
                }

                ACL acl = Marshal.PtrToStructure<ACL>(sacl);
                for (int index = 0; index < acl.AceCount; index++)
                {
                    IntPtr ace;
                    if (!GetAce(sacl, index, out ace))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    ACE_HEADER header = Marshal.PtrToStructure<ACE_HEADER>(ace);
                    if (header.AceType != SYSTEM_MANDATORY_LABEL_ACE_TYPE || header.AceSize < 12)
                    {
                        continue;
                    }

                    IntPtr sid = IntPtr.Add(ace, 8);
                    byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    if (count == 0)
                    {
                        throw new InvalidOperationException("Mandatory label SID has no sub-authorities.");
                    }
                    result.Add(Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))));
                }
                return result.ToArray();
            }
            finally
            {
                if (descriptor != IntPtr.Zero)
                {
                    LocalFree(descriptor);
                }
            }
        }
    }
}
'@
}

function Get-WindowsMandatoryIntegrityEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Exists = $false; ParseSucceeded = $false; Rids = @(); Diagnostic = "image-missing" }
    }

    try {
        Initialize-MandatoryLabelReader
        $rids = @([Mcg.AgentOrchestrator.WindowsMandatoryLabelReader]::Read($Path))
        [pscustomobject]@{ Exists = $true; ParseSucceeded = $true; Rids = $rids; Diagnostic = "numeric-label-query" }
    }
    catch {
        [pscustomobject]@{ Exists = $true; ParseSucceeded = $false; Rids = @(); Diagnostic = $_.Exception.Message }
    }
}

function Assert-MediumMandatoryIntegrity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string]$Path
    )

    if (-not $Evidence.Exists) {
        throw "Launcher image does not exist: $Path"
    }
    if (-not $Evidence.ParseSucceeded) {
        throw "Mandatory-integrity evidence is unavailable or unparseable for '$Path': $($Evidence.Diagnostic)"
    }

    $rids = @($Evidence.Rids)
    if ($rids.Count -eq 0) {
        throw "Mandatory-integrity label is missing for '$Path'."
    }
    $numericRids = @($rids | ForEach-Object {
        $value = 0
        if (-not [int]::TryParse([string]$_, [ref]$value)) {
            throw "Mandatory-integrity label is unparseable for '$Path'."
        }
        $value
    })
    if (@($numericRids | Select-Object -Unique).Count -ne 1) {
        throw "Conflicting mandatory-integrity labels were returned for '$Path'."
    }
    if ($numericRids[0] -lt $script:MinimumMediumIntegrityRid) {
        throw "Launcher image integrity is below Medium for '$Path' (RID=$($numericRids[0]))."
    }
}

function Test-PathWithin {
    param([string]$Path, [string]$Parent)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $canonicalParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd($separator) + $separator
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    $canonicalPath.StartsWith($canonicalParent, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-AutoResumeOwnedRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$LocalApplicationData,
        [Parameter(Mandatory)][string]$OwnedRoot,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [scriptblock]$ReparsePointProbe
    )

    if ($null -eq $ReparsePointProbe) {
        $ReparsePointProbe = {
            param($Path)
            if (-not (Test-Path -LiteralPath $Path)) { return $false }
            $item = Get-Item -LiteralPath $Path -Force
            ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        }
    }

    $layout = Get-AutoResumeLayout -LocalApplicationData $LocalApplicationData
    $knownFolder = [System.IO.Path]::GetFullPath($layout.KnownFolder)
    $expected = [System.IO.Path]::GetFullPath($layout.Root)
    $candidate = [System.IO.Path]::GetFullPath($OwnedRoot)
    $repository = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $profile = [System.IO.Path]::GetFullPath([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile))
    $root = [System.IO.Path]::GetPathRoot($candidate)
    $knownFolderRoot = [System.IO.Path]::GetPathRoot($knownFolder)

    if ($candidate -ne $expected -or
        $candidate -eq $root -or
        $knownFolder -eq $knownFolderRoot -or
        $candidate -eq $knownFolder -or
        $candidate -eq $profile -or
        $candidate -eq $repository -or
        (Test-PathWithin -Path $candidate -Parent $repository) -or
        -not (Test-PathWithin -Path $candidate -Parent $knownFolder)) {
        throw "Refusing unsafe auto-resume removal target '$OwnedRoot'."
    }

    $current = $knownFolder
    foreach ($segment in @($script:ProductDirectoryName, $script:AutoResumeDirectoryName)) {
        $current = Join-Path $current $segment
        if (& $ReparsePointProbe $current) {
            throw "Refusing auto-resume removal through reparse point '$current'."
        }
    }

    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $pending = [System.Collections.Generic.Stack[string]]::new()
        $pending.Push($candidate)
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            foreach ($child in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
                if (& $ReparsePointProbe $child.FullName) {
                    throw "Refusing auto-resume removal containing reparse point '$($child.FullName)'."
                }
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
            }
        }
    }

    $candidate
}

function Invoke-AutoResumeOperation {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Operations,
        [Parameter(Mandatory)][string]$Name,
        [object[]]$Arguments = @()
    )
    if (-not $Operations.Contains($Name) -or $Operations[$Name] -isnot [scriptblock]) {
        throw "Auto-resume operation '$Name' is unavailable."
    }
    & $Operations[$Name] @Arguments
}

function Assert-TaskMatches {
    param($Snapshot, [string]$Executable, [string]$Arguments)
    if ($null -eq $Snapshot -or -not $Snapshot.Exists) {
        throw "Scheduled task verification failed: task is absent."
    }
    if ([System.IO.Path]::GetFullPath([string]$Snapshot.Execute) -ne [System.IO.Path]::GetFullPath($Executable) -or
        [string]$Snapshot.Arguments -cne $Arguments) {
        throw "Scheduled task verification failed: action does not match the validated launcher."
    }
}

function Invoke-AutoResumeInstall {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$LocalApplicationData,
        [Parameter(Mandatory)][string]$LauncherProject,
        [Parameter(Mandatory)][string]$ResumeScriptPath,
        [Parameter(Mandatory)][string]$PowerShellPath,
        [Parameter(Mandatory)][string]$TaskName,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Operations,
        [string]$LifecycleMutexName
    )

    $layout = Get-AutoResumeLayout -LocalApplicationData $LocalApplicationData
    [void](Assert-AutoResumeOwnedRoot -LocalApplicationData $LocalApplicationData -OwnedRoot $layout.Root -RepositoryRoot $RepositoryRoot)
    $lifecycleLock = $null
    $stagingDirectory = $null
    try {
        $lifecycleLock = Enter-AutoResumeLifecycleLock -MutexName $LifecycleMutexName

        $previousTask = Invoke-AutoResumeOperation $Operations "GetTask" @($TaskName)
        if ($null -eq $previousTask) { throw "Scheduled task query returned no result." }

        [System.IO.Directory]::CreateDirectory($layout.Staging) | Out-Null
        [System.IO.Directory]::CreateDirectory($layout.Releases) | Out-Null
        $stagingDirectory = Join-Path $layout.Staging ([Guid]::NewGuid().ToString('N'))
        [System.IO.Directory]::CreateDirectory($stagingDirectory) | Out-Null

        Invoke-AutoResumeOperation $Operations "Publish" @($LauncherProject, $stagingDirectory) | Out-Null
        Assert-LauncherPayloadComplete $stagingDirectory
        $stagedImage = Join-Path $stagingDirectory $script:LauncherExecutableName
        Invoke-AutoResumeOperation $Operations "SetMediumIntegrity" @($stagedImage) | Out-Null
        $stagedEvidence = Invoke-AutoResumeOperation $Operations "QueryIntegrity" @($stagedImage)
        Assert-MediumMandatoryIntegrity -Evidence $stagedEvidence -Path $stagedImage
        $payload = Get-LauncherPayloadIdentity $stagingDirectory
        $releaseDirectory = Join-Path $layout.Releases $payload.Identity

        if (Test-Path -LiteralPath $releaseDirectory) {
            Assert-LauncherPayloadComplete $releaseDirectory
            $existingPayload = Get-LauncherPayloadIdentity $releaseDirectory
            if ($existingPayload.Manifest -cne $payload.Manifest) {
                throw "Existing launcher release manifest does not match '$($payload.Identity)'."
            }
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
            $stagingDirectory = $null
        }
        else {
            Invoke-AutoResumeOperation $Operations "Promote" @($stagingDirectory, $releaseDirectory) | Out-Null
            $stagingDirectory = $null
        }

        $launcherPath = Join-Path $releaseDirectory $script:LauncherExecutableName
        $promotedEvidence = Invoke-AutoResumeOperation $Operations "QueryIntegrity" @($launcherPath)
        Assert-MediumMandatoryIntegrity -Evidence $promotedEvidence -Path $launcherPath
        $arguments = Get-AutoResumeTaskArguments -PowerShellPath $PowerShellPath -ResumeScriptPath $ResumeScriptPath -TaskName $TaskName
        $taskSpec = [pscustomobject]@{ Execute = $launcherPath; Arguments = $arguments }

        $registrationAttempted = $false
        try {
            $registrationAttempted = $true
            Invoke-AutoResumeOperation $Operations "RegisterTask" @($TaskName, $taskSpec) | Out-Null
            $installedTask = Invoke-AutoResumeOperation $Operations "GetTask" @($TaskName)
            Assert-TaskMatches -Snapshot $installedTask -Executable $launcherPath -Arguments $arguments
        }
        catch {
            $registrationFailure = $_
            if ($registrationAttempted) {
                Invoke-AutoResumeOperation $Operations "RestoreTask" @($TaskName, $previousTask) | Out-Null
                $restoredTask = Invoke-AutoResumeOperation $Operations "GetTask" @($TaskName)
                if ($previousTask.Exists) {
                    Assert-TaskMatches -Snapshot $restoredTask -Executable $previousTask.Execute -Arguments $previousTask.Arguments
                }
                elseif ($restoredTask.Exists) {
                    throw "Task registration failed and rollback did not restore prior task absence. Original error: $($registrationFailure.Exception.Message)"
                }
            }
            throw $registrationFailure
        }

        [pscustomobject]@{ TaskName = $TaskName; LauncherPath = $launcherPath; Arguments = $arguments; Release = $payload.Identity }
    }
    finally {
        if ($null -ne $stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory)) {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $lifecycleLock) {
            Exit-AutoResumeLifecycleLock -Lease $lifecycleLock
        }
    }
}

function Invoke-AutoResumeRemoval {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$LocalApplicationData,
        [Parameter(Mandatory)][string]$TaskName,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Operations,
        [string]$LifecycleMutexName,
        [scriptblock]$LifecycleLockWaitStarted
    )

    $layout = Get-AutoResumeLayout $LocalApplicationData
    $ownedRoot = Assert-AutoResumeOwnedRoot -LocalApplicationData $LocalApplicationData -OwnedRoot $layout.Root -RepositoryRoot $RepositoryRoot
    $lifecycleLock = $null
    try {
        $lifecycleLock = Enter-AutoResumeLifecycleLock -MutexName $LifecycleMutexName -WaitStarted $LifecycleLockWaitStarted

        $task = Invoke-AutoResumeOperation $Operations "GetTask" @($TaskName)
        if ($null -eq $task) { throw "Scheduled task query returned no result." }
        if ($task.Exists) {
            Invoke-AutoResumeOperation $Operations "UnregisterTask" @($TaskName) | Out-Null
            $after = Invoke-AutoResumeOperation $Operations "GetTask" @($TaskName)
            if ($after.Exists) { throw "Scheduled task still exists after unregister: $TaskName" }
        }
        if (Test-Path -LiteralPath $ownedRoot) {
            Invoke-AutoResumeOperation $Operations "RemoveOwnedRoot" @($ownedRoot) | Out-Null
        }
        [pscustomobject]@{ TaskName = $TaskName; RemovedRoot = $ownedRoot; TaskWasPresent = [bool]$task.Exists }
    }
    finally {
        if ($null -ne $lifecycleLock) {
            Exit-AutoResumeLifecycleLock -Lease $lifecycleLock
        }
    }
}

function New-WindowsAutoResumeOperations {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CurrentUser)

    $getTask = {
        param($Name)
        $matches = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -eq $Name })
        if ($matches.Count -eq 0) {
            return [pscustomobject]@{ Exists = $false; Xml = $null; Execute = $null; Arguments = $null }
        }
        if ($matches.Count -ne 1) { throw "Scheduled task query returned multiple tasks named '$Name'." }
        $task = $matches[0]
        if (@($task.Actions).Count -ne 1) { throw "Scheduled task '$Name' must have exactly one action." }
        $xml = Export-ScheduledTask -TaskName $Name -ErrorAction Stop
        [pscustomobject]@{ Exists = $true; Xml = $xml; Execute = [string]$task.Actions[0].Execute; Arguments = [string]$task.Actions[0].Arguments }
    }

    $publish = {
            param($Project, $Destination)
            & dotnet publish $Project --nologo --configuration Release --output $Destination --verbosity quiet | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to publish hidden launcher: $Project" }
        }
    $setMediumIntegrity = {
            param($Path)
            & icacls.exe $Path /setintegritylevel 'Medium:(NW)' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to apply Medium mandatory-integrity label to '$Path'." }
        }
    $queryIntegrity = { param($Path) Get-WindowsMandatoryIntegrityEvidence $Path }
    $promote = { param($Source, $Destination) [System.IO.Directory]::Move($Source, $Destination) }
    $registerTask = {
            param($Name, $Spec)
            $action = New-ScheduledTaskAction -Execute $Spec.Execute -Argument $Spec.Arguments
            $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $CurrentUser
            $repetitionTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 10) -RepetitionDuration (New-TimeSpan -Days 3650)
            $principal = New-ScheduledTaskPrincipal -UserId $CurrentUser -LogonType Interactive -RunLevel Limited
            $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
            Register-ScheduledTask -TaskName $Name -Action $action -Trigger @($logonTrigger, $repetitionTrigger) -Principal $principal -Settings $settings -Description "Resume the last journaled mcg orchestrator conduct loop after logon or loop crashes." -Force | Out-Null
        }.GetNewClosure()
    $restoreTask = {
            param($Name, $Snapshot)
            if ($Snapshot.Exists) {
                Register-ScheduledTask -TaskName $Name -Xml $Snapshot.Xml -Force | Out-Null
            }
            else {
                $current = & $getTask $Name
                if ($current.Exists) { Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction Stop }
            }
        }.GetNewClosure()
    @{
        Publish = $publish
        SetMediumIntegrity = $setMediumIntegrity
        QueryIntegrity = $queryIntegrity
        Promote = $promote
        GetTask = $getTask
        RegisterTask = $registerTask
        RestoreTask = $restoreTask
        UnregisterTask = { param($Name) Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction Stop }
        RemoveOwnedRoot = { param($Path) Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop }
    }
}

Export-ModuleMember -Function @(
    'Get-AutoResumeLifecycleMutexName',
    'Enter-AutoResumeLifecycleLock',
    'Exit-AutoResumeLifecycleLock',
    'Get-AutoResumeLayout',
    'ConvertTo-WindowsCommandLineArgument',
    'Get-AutoResumeTaskArguments',
    'Get-LauncherPayloadIdentity',
    'Get-WindowsMandatoryIntegrityEvidence',
    'Assert-MediumMandatoryIntegrity',
    'Assert-AutoResumeOwnedRoot',
    'Invoke-AutoResumeInstall',
    'Invoke-AutoResumeRemoval',
    'New-WindowsAutoResumeOperations'
)
