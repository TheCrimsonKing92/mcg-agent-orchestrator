Set-StrictMode -Version Latest

$script:ExitCodes = [pscustomobject]@{
    Manifest = 20
    InvalidPartition = 21
    ResultsDirectory = 22
    Build = 23
    MissingRunnerArtifact = 24
    InvalidTarget = 25
    Runner = 26
    ZeroTests = 27
    Cleanup = 28
    Timeout = 29
    StartupHook = 30
}

$script:MtpGracefulExitSeconds = 15
$script:MtpExitConfirmationSeconds = 10
$script:MtpOutputDrainSeconds = 10
$script:MtpClosureMarkerName = '.mcg-mtp-closure.json'
$script:MtpClosureSchemaVersion = 1
$script:MtpBuildReceiptName = '.mcg-build-receipt.txt'
$script:MtpBuildReceiptSchemaVersion = 1
$script:MtpClosureSnapshotAttempts = 3
$script:MtpStartupHookSchemaVersion = 1
$script:MtpStartupHookAssemblyName = 'Mcg.AgentOrchestrator.FaultDialogStartupHook.dll'
$script:MtpStartupHookMarkerName = '.mcg-startup-hook.json'
$script:MtpStartupHookSource = @'
using System;
using System.Runtime.InteropServices;

public static class StartupHook
{
    private const uint FailCriticalErrors = 0x0001;
    private const uint NoGpFaultErrorBox = 0x0002;
    private const uint NoOpenFileErrorBox = 0x8000;
    private const uint WerFaultReportingNoUi = 0x0020;

    public static void Initialize()
    {
        uint mode = GetErrorMode();
        SetErrorMode((mode | FailCriticalErrors | NoOpenFileErrorBox) & ~NoGpFaultErrorBox);
        uint werFlags;
        if (WerGetFlags(GetCurrentProcess(), out werFlags) == 0)
        {
            WerSetFlags(werFlags | WerFaultReportingNoUi);
        }
        else
        {
            WerSetFlags(WerFaultReportingNoUi);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern int WerSetFlags(uint flags);

    [DllImport("kernel32.dll")]
    private static extern int WerGetFlags(IntPtr process, out uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
'@

if ($null -eq ('McgMtpProcessOutputCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
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

public sealed class McgMtpOwnedJob : IDisposable
{
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private IntPtr handle;

    public McgMtpOwnedJob(bool allowBreakaway)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        }

        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose |
            (allowBreakaway ? JobObjectLimitBreakawayOk : 0);
        int length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(handle, 9, buffer, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "AssignProcessToJobObject failed for PID " + process.Id + ".");
        }
    }

    public bool TerminateAndWait(int milliseconds)
    {
        if (handle == IntPtr.Zero)
        {
            return true;
        }
        if (!TerminateJobObject(handle, 28))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject failed.");
        }
        return WaitForEmpty(milliseconds);
    }

    public bool WaitForEmpty(int milliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            if (ActiveProcessCount() == 0)
            {
                return true;
            }
            Thread.Sleep(25);
        }
        while (stopwatch.ElapsedMilliseconds < milliseconds);

        return ActiveProcessCount() == 0;
    }

    public bool IsEmpty
    {
        get { return handle == IntPtr.Zero || ActiveProcessCount() == 0; }
    }

    private uint ActiveProcessCount()
    {
        var accounting = new JobObjectBasicAccountingInformation();
        int length = Marshal.SizeOf(typeof(JobObjectBasicAccountingInformation));
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryInformationJobObject(handle, 1, buffer, (uint)length, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject failed.");
            }
            accounting = (JobObjectBasicAccountingInformation)Marshal.PtrToStructure(
                buffer, typeof(JobObjectBasicAccountingInformation));
            return accounting.ActiveProcesses;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr job, int informationClass, IntPtr information, uint informationLength, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
'@
}

# Keep this type in its own guard. PowerShell modules can be re-imported after the output-capture
# type above is already loaded, and coupling the guards would leave this newer helper undefined.
if ($null -eq ('McgMtpSuppressedProcessStart' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class McgMtpSuppressedProcessStart
{
    public const uint FailCriticalErrors = 0x0001;
    public const uint NoGpFaultErrorBox = 0x0002;
    public const uint NoOpenFileErrorBox = 0x8000;
    public const uint SuppressedFlags = FailCriticalErrors | NoGpFaultErrorBox | NoOpenFileErrorBox;

    private static readonly object Gate = new object();

    public static bool Start(Process process)
    {
        if (process == null)
        {
            throw new ArgumentNullException("process");
        }

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return process.Start();
        }

        lock (Gate)
        {
            uint original = GetErrorMode();
            SetErrorMode(original | SuppressedFlags);
            try
            {
                // Windows child processes inherit the parent's process error mode unless created
                // with CREATE_DEFAULT_ERROR_MODE. System.Diagnostics.Process does not request it.
                return process.Start();
            }
            finally
            {
                SetErrorMode(original);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
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

function Write-MtpRunOwnershipSidecar {
    param(
        [Parameter(Mandatory = $true)][string]$ResultsDirectory,
        [Parameter(Mandatory = $true)][string]$RunLabel
    )

    $receiptPath = Join-Path $ResultsDirectory '.mtp-run-ownership.json'
    $temporaryPath = $receiptPath + ".tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        $attemptId = [System.Environment]::GetEnvironmentVariable('MCG_ACCEPTANCE_GATE_ATTEMPT_ID')
        if ([string]::IsNullOrWhiteSpace($attemptId)) {
            $attemptId = 'unowned'
        }
        $receipt = [ordered]@{
            schemaVersion = 1
            attemptId = $attemptId
            machineName = [System.Environment]::MachineName
            ownerProcessId = $PID
            createdAt = [DateTimeOffset]::UtcNow
            runLabel = $RunLabel
        }
        [System.IO.File]::WriteAllText($temporaryPath, ($receipt | ConvertTo-Json -Compress))
        Move-Item -LiteralPath $temporaryPath -Destination $receiptPath -ErrorAction Stop
        return $true
    }
    catch {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        return $false
    }
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
            throw "Results directory '$resolvedRoot' is not under the Low-integrity-writable root '$canonicalLowRoot'. Use -ResultsRoot beneath that root; an ordinary Medium-integrity directory is not writable by the MTP test process."
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
    [void](Write-MtpRunOwnershipSidecar -ResultsDirectory $runDirectory -RunLabel $RunLabel)
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
    # DOTNET_CLI_HOME is unique per run, so the SDK treats every run as a first use and PERSISTS
    # "$DOTNET_CLI_HOME\.dotnet\tools" into HKCU\Environment\Path - one dead entry per run, forever.
    # Twelve had accumulated before anyone noticed. DOTNET_SKIP_FIRST_TIME_EXPERIENCE does NOT prevent
    # this (measured: it still wrote the entry); the SDK dropped that variable years ago. Only
    # DOTNET_ADD_GLOBAL_TOOLS_TO_PATH suppresses the PATH write.
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
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

function Get-MtpProjectName {
    param([Parameter(Mandatory = $true)]$Invocation)

    return [System.IO.Path]::GetFileNameWithoutExtension([string]$Invocation.project)
}

function Get-MtpProjectToken {
    param([Parameter(Mandatory = $true)]$Invocation)

    $projectName = Get-MtpProjectName -Invocation $Invocation
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($projectName))
        return ([System.BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 12)
    }
    finally {
        $sha.Dispose()
    }
}

function Get-MtpClosureDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)]$Invocation
    )

    return Join-Path (Join-Path $RunDirectory '.c') (Get-MtpProjectToken -Invocation $Invocation)
}

function Get-MtpClosureManagedAssemblyPath {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)]$Invocation
    )

    $projectName = Get-MtpProjectName -Invocation $Invocation
    return Join-Path (Get-MtpClosureDirectory -RunDirectory $RunDirectory -Invocation $Invocation) "$projectName.dll"
}

function Get-MtpClosureSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$ExcludeMarker
    )

    $root = [System.IO.Path]::GetFullPath($Path)
    if (-not $root.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal)) {
        $root += [System.IO.Path]::DirectorySeparatorChar
    }
    $payload = [System.Text.StringBuilder]::new()
    $fileCount = 0
    foreach ($file in (Get-ChildItem -LiteralPath $Path -Force -Recurse -File | Sort-Object FullName)) {
        if ($ExcludeMarker -and $file.Name.Equals($script:MtpClosureMarkerName, [System.StringComparison]::Ordinal)) {
            continue
        }
        if (-not $file.FullName.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "MTP closure file escaped its root: $($file.FullName)"
        }
        $relative = $file.FullName.Substring($root.Length).Replace('\', '/')
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $stream = $null
        try {
            $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            $fileHash = [System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', ''
        }
        finally {
            if ($null -ne $stream) {
                $stream.Dispose()
            }
            $sha.Dispose()
        }
        [void]$payload.Append($relative)
        [void]$payload.Append(':')
        [void]$payload.Append($fileHash)
        [void]$payload.Append("`n")
        $fileCount++
    }

    $payloadSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digestBytes = $payloadSha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($payload.ToString()))
        $digest = [System.BitConverter]::ToString($digestBytes) -replace '-', ''
    }
    finally {
        $payloadSha.Dispose()
    }
    return [pscustomobject]@{ Digest = $digest; FileCount = $fileCount }
}

function Assert-MtpClosureShape {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ManagedAssemblyLeaf
    )

    $managedAssembly = Join-Path $Path $ManagedAssemblyLeaf
    if (-not (Test-Path -LiteralPath $managedAssembly -PathType Leaf)) {
        throw "MTP closure is missing managed test assembly '$ManagedAssemblyLeaf': $Path"
    }

    $depsLeaf = [System.IO.Path]::ChangeExtension($ManagedAssemblyLeaf, '.deps.json')
    $depsPath = Join-Path $Path $depsLeaf
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw "MTP closure is missing dependency manifest '$depsLeaf': $Path"
    }
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    foreach ($library in @($deps.libraries.PSObject.Properties)) {
        $assemblyName = ([string]$library.Name -split '/', 2)[0]
        if (-not $assemblyName.StartsWith('Mcg.AgentOrchestrator.', [System.StringComparison]::Ordinal)) {
            continue
        }
        $dependencyLeaf = "$assemblyName.dll"
        if (-not (Test-Path -LiteralPath (Join-Path $Path $dependencyLeaf) -PathType Leaf)) {
            throw "MTP closure dependency manifest requires missing repository assembly '$dependencyLeaf': $Path"
        }
    }

    $repositoryAssemblies = @(Get-ChildItem -LiteralPath $Path -Force -File -Filter 'Mcg.AgentOrchestrator*.dll')
    if ($repositoryAssemblies.Count -eq 0) {
        throw "MTP closure contains no repository assemblies: $Path"
    }
    $productVersions = [System.Collections.Generic.List[string]]::new()
    foreach ($assembly in $repositoryAssemblies) {
        $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly.FullName).ProductVersion
        if ([string]::IsNullOrWhiteSpace([string]$productVersion)) {
            throw "MTP closure repository assembly has no ProductVersion and is not trusted: $($assembly.FullName)"
        }
        $productVersions.Add([string]$productVersion)
    }
    $productVersions = @($productVersions | Sort-Object -Unique)
    if ($productVersions.Count -ne 1) {
        throw "MTP closure repository assemblies do not share one ProductVersion: path='$Path' versions='$($productVersions -join ',')'."
    }
}

function Complete-MtpClosurePublication {
    param(
        [Parameter(Mandatory = $true)][string]$StagingDirectory,
        [Parameter(Mandatory = $true)][string]$PublishedDirectory,
        [Parameter(Mandatory = $true)][string]$ManagedAssemblyLeaf,
        [Parameter(Mandatory = $true)][string]$ProjectName
    )

    Assert-MtpClosureShape -Path $StagingDirectory -ManagedAssemblyLeaf $ManagedAssemblyLeaf
    $snapshot = Get-MtpClosureSnapshot -Path $StagingDirectory
    $marker = [ordered]@{
        schemaVersion = $script:MtpClosureSchemaVersion
        project = $ProjectName
        digest = $snapshot.Digest
        fileCount = $snapshot.FileCount
        sealedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $StagingDirectory $script:MtpClosureMarkerName),
        ($marker | ConvertTo-Json -Compress),
        [System.Text.Encoding]::UTF8)
    [System.IO.Directory]::Move($StagingDirectory, $PublishedDirectory)
    Assert-MtpSealedClosure -Path $PublishedDirectory -ManagedAssemblyLeaf $ManagedAssemblyLeaf -ExpectedProject $ProjectName
}

function Assert-MtpSealedClosure {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ManagedAssemblyLeaf,
        [Parameter(Mandatory = $true)][string]$ExpectedProject
    )

    Assert-MtpClosureShape -Path $Path -ManagedAssemblyLeaf $ManagedAssemblyLeaf
    $markerPath = Join-Path $Path $script:MtpClosureMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "MTP closure has no completion marker and will not be executed: $Path"
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ([int]$marker.schemaVersion -ne $script:MtpClosureSchemaVersion -or
        -not ([string]$marker.project).Equals($ExpectedProject, [System.StringComparison]::Ordinal)) {
        throw "MTP closure completion marker is incompatible: $markerPath"
    }
    $snapshot = Get-MtpClosureSnapshot -Path $Path -ExcludeMarker
    if (-not ([string]$marker.digest).Equals($snapshot.Digest, [System.StringComparison]::Ordinal) -or
        [int]$marker.fileCount -ne $snapshot.FileCount) {
        throw "MTP closure completion marker does not match its recursive payload: $Path"
    }
}

function Copy-MtpStableNoBuildClosure {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$PublishedDirectory,
        [Parameter(Mandatory = $true)][string]$ManagedAssemblyLeaf,
        [Parameter(Mandatory = $true)][string]$ProjectName
    )

    $sourceManagedAssembly = Join-Path $SourceDirectory $ManagedAssemblyLeaf
    if (-not (Test-Path -LiteralPath $sourceManagedAssembly -PathType Leaf)) {
        throw "No-build source managed assembly is missing: $sourceManagedAssembly"
    }
    $parent = Split-Path -Parent $PublishedDirectory
    [void](New-Item -ItemType Directory -Force -Path $parent)
    $lastFailure = $null
    for ($attempt = 1; $attempt -le $script:MtpClosureSnapshotAttempts; $attempt++) {
        $staging = Join-Path $parent ".s-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
        try {
            $before = Get-MtpClosureSnapshot -Path $SourceDirectory
            [void](New-Item -ItemType Directory -Path $staging -ErrorAction Stop)
            Get-ChildItem -LiteralPath $SourceDirectory -Force | Copy-Item -Destination $staging -Recurse -Force -ErrorAction Stop
            $staged = Get-MtpClosureSnapshot -Path $staging
            $after = Get-MtpClosureSnapshot -Path $SourceDirectory
            if (-not $before.Digest.Equals($staged.Digest, [System.StringComparison]::Ordinal) -or
                -not $before.Digest.Equals($after.Digest, [System.StringComparison]::Ordinal) -or
                $before.FileCount -ne $staged.FileCount -or
                $before.FileCount -ne $after.FileCount) {
                $lastFailure = "source output changed while snapshotting attempt $attempt of $script:MtpClosureSnapshotAttempts"
                continue
            }
            Complete-MtpClosurePublication -StagingDirectory $staging -PublishedDirectory $PublishedDirectory -ManagedAssemblyLeaf $ManagedAssemblyLeaf -ProjectName $ProjectName
            $staging = $null
            return
        }
        catch {
            $lastFailure = $_.Exception.Message
            if ($attempt -eq $script:MtpClosureSnapshotAttempts) {
                throw
            }
        }
        finally {
            if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
                Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    throw "Could not create a stable sealed MTP closure for '$ProjectName'. $lastFailure"
}

function Get-MtpBuildReceiptPath {
    param([Parameter(Mandatory = $true)][string]$Directory)

    return Join-Path $Directory $script:MtpBuildReceiptName
}

function Read-MtpBuildReceipt {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "build receipt is missing: $Path"
    }
    $headers = @{}
    $sources = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($line in @(Get-Content -LiteralPath $Path -ErrorAction Stop)) {
            $parts = @([string]$line -split "`t", 4)
            if ($parts.Count -eq 3 -and $parts[0] -eq 'K' -and -not [string]::IsNullOrWhiteSpace($parts[1]) -and -not $headers.ContainsKey($parts[1])) {
                $headers[$parts[1]] = $parts[2]
                continue
            }
            if ($parts.Count -eq 2 -and $parts[0] -eq 'K' -and $parts[1] -eq 'runtimeIdentifier' -and -not $headers.ContainsKey($parts[1])) {
                $headers[$parts[1]] = ''
                continue
            }
            if ($parts.Count -eq 4 -and $parts[0] -eq 'S' -and $parts[1] -eq 'source' -and -not [string]::IsNullOrWhiteSpace($parts[2])) {
                $sources.Add([pscustomobject]@{ Path = $parts[2]; Sha256 = $parts[3] })
                continue
            }
            throw "receipt line is malformed: '$line'"
        }
    }
    catch {
        throw "build receipt is unreadable at '$Path': $($_.Exception.Message)"
    }
    foreach ($required in @('schemaVersion', 'project', 'configuration', 'targetFramework', 'runtimeIdentifier', 'repositoryRoot', 'outputDirectory', 'assemblyLeaf', 'assemblySha256', 'pdbSha256')) {
        if (-not $headers.ContainsKey($required) -or ($required -ne 'runtimeIdentifier' -and [string]::IsNullOrWhiteSpace([string]$headers[$required]))) {
            throw "build receipt is missing required key '$required': $Path"
        }
    }
    if ([string]$headers['schemaVersion'] -ne [string]$script:MtpBuildReceiptSchemaVersion) {
        throw "build receipt has unsupported schemaVersion '$($headers['schemaVersion'])': $Path"
    }
    return [pscustomobject]@{ Path = $Path; Headers = $headers; Sources = $sources.ToArray() }
}

function Assert-MtpBuildReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$ManagedAssemblyLeaf,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$TargetFramework,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$RuntimeIdentifier
    )

    $repair = "dotnet build `"$ProjectPath`" --configuration $Configuration -p:McgIsolatedArtifactsPath=`"$RepositoryRoot`""
    $receiptPath = Get-MtpBuildReceiptPath -Directory $Directory
    try {
        $receipt = Read-MtpBuildReceipt -Path $receiptPath
        $expected = @{
            project = [System.IO.Path]::GetFullPath($ProjectPath)
            configuration = $Configuration
            targetFramework = $TargetFramework
            runtimeIdentifier = $RuntimeIdentifier
            repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            outputDirectory = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            assemblyLeaf = $ManagedAssemblyLeaf
        }
        foreach ($key in $expected.Keys) {
            if (-not ([string]$receipt.Headers[$key]).Equals([string]$expected[$key], [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "receipt key '$key' does not match: recorded='$($receipt.Headers[$key])' expected='$($expected[$key])'"
            }
        }
        $assemblyPath = Join-Path $Directory $ManagedAssemblyLeaf
        $pdbPath = [System.IO.Path]::ChangeExtension($assemblyPath, '.pdb')
        foreach ($pair in @(@($assemblyPath, 'assemblySha256'), @($pdbPath, 'pdbSha256'))) {
            if (-not (Test-Path -LiteralPath $pair[0] -PathType Leaf)) {
                throw "receipt-bound file is missing: $($pair[0])"
            }
            $actual = Get-MtpSha256File -Path $pair[0]
            if (-not $actual.Equals([string]$receipt.Headers[$pair[1]], [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "receipt $($pair[1]) mismatch: recorded='$($receipt.Headers[$pair[1]])' actual='$actual' path='$($pair[0])'"
            }
        }
        foreach ($source in @($receipt.Sources)) {
            if (-not (Test-Path -LiteralPath $source.Path -PathType Leaf)) {
                throw "receipt source is missing or unverifiable: $($source.Path)"
            }
            $actual = Get-MtpSha256File -Path $source.Path
            if (-not $actual.Equals([string]$source.Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "receipt source checksum mismatch: path='$($source.Path)' recorded='$($source.Sha256)' actual='$actual'"
            }
        }
        return $receipt
    }
    catch {
        throw "No-build build receipt verification failed for '$Directory': $($_.Exception.Message). Repair: $repair"
    }
}

function Get-MtpSha256Text {
    param([Parameter(Mandatory = $true)][string]$Text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text))
        return [System.BitConverter]::ToString($bytes) -replace '-', ''
    }
    finally {
        $sha.Dispose()
    }
}

function Get-MtpSha256File {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        $bytes = $sha.ComputeHash($stream)
        return [System.BitConverter]::ToString($bytes) -replace '-', ''
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        $sha.Dispose()
    }
}

function Assert-MtpFaultDialogStartupHook {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceDigest
    )

    $assemblyPath = Join-Path $Path $script:MtpStartupHookAssemblyName
    $markerPath = Join-Path $Path $script:MtpStartupHookMarkerName
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Fault-dialog startup-hook cache is incomplete: $Path"
    }
    try {
        $marker = Get-Content -LiteralPath $markerPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Fault-dialog startup-hook marker is invalid at '$markerPath': $($_.Exception.Message)"
    }
    if ([int]$marker.schemaVersion -ne $script:MtpStartupHookSchemaVersion -or
        -not ([string]$marker.sourceDigest).Equals($ExpectedSourceDigest, [System.StringComparison]::Ordinal)) {
        throw "Fault-dialog startup-hook marker does not match the current source: $markerPath"
    }
    $assemblyDigest = Get-MtpSha256File -Path $assemblyPath
    if (-not $assemblyDigest.Equals([string]$marker.assemblyDigest, [System.StringComparison]::Ordinal)) {
        throw "Fault-dialog startup-hook assembly digest does not match its marker: $assemblyPath"
    }
    return $assemblyPath
}

function Resolve-MtpFaultDialogStartupHook {
    if (-not (Test-MtpWindows)) {
        return $null
    }

    $sourceDigest = Get-MtpSha256Text -Text $script:MtpStartupHookSource
    $cacheParent = Join-Path ([System.IO.Path]::GetTempPath()) 'mcg-mtp-startup-hook\v1'
    $publishedDirectory = Join-Path $cacheParent $sourceDigest
    if (Test-Path -LiteralPath $publishedDirectory) {
        return Assert-MtpFaultDialogStartupHook -Path $publishedDirectory -ExpectedSourceDigest $sourceDigest
    }

    [void](New-Item -ItemType Directory -Force -Path $cacheParent)
    $stagingDirectory = Join-Path $cacheParent ".staging-$PID-$([Guid]::NewGuid().ToString('N'))"
    try {
        [void](New-Item -ItemType Directory -Path $stagingDirectory -ErrorAction Stop)
        $stagingAssembly = Join-Path $stagingDirectory $script:MtpStartupHookAssemblyName
        Add-Type -TypeDefinition $script:MtpStartupHookSource -OutputAssembly $stagingAssembly -OutputType Library -ErrorAction Stop
        $assemblyDigest = Get-MtpSha256File -Path $stagingAssembly
        $marker = [ordered]@{
            schemaVersion = $script:MtpStartupHookSchemaVersion
            sourceDigest = $sourceDigest
            assemblyDigest = $assemblyDigest
        }
        $marker | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $stagingDirectory $script:MtpStartupHookMarkerName) -Encoding UTF8 -ErrorAction Stop

        try {
            [System.IO.Directory]::Move($stagingDirectory, $publishedDirectory)
            $stagingDirectory = $null
        }
        catch [System.IO.IOException] {
            if (-not (Test-Path -LiteralPath $publishedDirectory)) {
                throw
            }
        }
        return Assert-MtpFaultDialogStartupHook -Path $publishedDirectory -ExpectedSourceDigest $sourceDigest
    }
    finally {
        if ($null -ne $stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory)) {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-MtpBuildProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$OutputLog,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string]$StartupHookPath
    )

    $process = [System.Diagnostics.Process]::new()
    $capture = $null
    $started = $false
    $processId = $null
    $startTimeUtc = $null
    $exitCode = $null
    $startFailureMessage = $null
    $monitoringFailureMessage = $null
    $cleanupConfirmed = $true
    $drainConfirmed = $false
    try {
        $startInfo = New-MtpProcessStartInfo -Executable $Executable -Arguments $Arguments -StartupHookPath $StartupHookPath
        $startInfo.WorkingDirectory = $WorkingDirectory
        $process.StartInfo = $startInfo
        $capture = [McgMtpProcessOutputCapture]::new($OutputLog)
        $capture.Attach($process)

        try {
            $started = [McgMtpSuppressedProcessStart]::Start($process)
            if (-not $started) {
                $startFailureMessage = 'Process.Start returned false.'
            }
        }
        catch {
            $startFailureMessage = $_.Exception.Message
        }

        if ($started) {
            try {
                $processId = $process.Id
                $startTimeUtc = $process.StartTime.ToUniversalTime()
                $process.BeginOutputReadLine()
                $process.BeginErrorReadLine()
                $process.WaitForExit()
            }
            catch {
                $monitoringFailureMessage = $_.Exception.Message
            }
        }
    }
    catch {
        if (-not $started -and [string]::IsNullOrWhiteSpace($startFailureMessage)) {
            $startFailureMessage = $_.Exception.Message
        }
        elseif ([string]::IsNullOrWhiteSpace($monitoringFailureMessage)) {
            $monitoringFailureMessage = $_.Exception.Message
        }
    }
    finally {
        try {
            if ($started) {
                $mustTerminate = -not [string]::IsNullOrWhiteSpace($monitoringFailureMessage) -or -not $process.HasExited
                if ($mustTerminate -and $null -ne $startTimeUtc) {
                    $cleanupConfirmed = Stop-MtpOwnedProcessTree -Process $process -StartTimeUtc $startTimeUtc -OwnedJob $null
                }
                $processExitConfirmed = $process.HasExited -or $process.WaitForExit($script:MtpExitConfirmationSeconds * 1000)
                $cleanupConfirmed = $cleanupConfirmed -and $processExitConfirmed
                if ($process.HasExited) {
                    $exitCode = $process.ExitCode
                }
                $drainConfirmed = $capture.WaitForCompletion($script:MtpOutputDrainSeconds * 1000)
            }
            if ($null -ne $capture) {
                try {
                    $capture.Detach($process)
                }
                finally {
                    $capture.Dispose()
                }
            }
        }
        finally {
            $process.Dispose()
        }
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        ProcessId = $processId
        Started = $started
        StartFailureMessage = $startFailureMessage
        MonitoringFailureMessage = $monitoringFailureMessage
        CleanupConfirmed = $cleanupConfirmed
        DrainConfirmed = $drainConfirmed
    }
}

function Invoke-MtpBuild {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][object[]]$Projects,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$DotnetPath,
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [string]$StartupHookPath,
        [switch]$RequireManagedClosure
    )

    foreach ($project in $Projects) {
        $buildTarget = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$project.project)))
        $projectName = Get-MtpProjectName -Invocation $project
        $publishedDirectory = Get-MtpClosureDirectory -RunDirectory $RunDirectory -Invocation $project
        $closureParent = Split-Path -Parent $publishedDirectory
        [void](New-Item -ItemType Directory -Force -Path $closureParent)
        $outputDirectory = Join-Path $closureParent ".b-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
        $buildLogNameBudget = [Math]::Max(32, [Math]::Min(200, 240 - $RunDirectory.Length - 1))
        $buildLogName = Get-MtpBoundedFileName -Stem "build-$projectName" -Suffix '.log' -MaximumLength $buildLogNameBudget
        $buildLogPath = Join-Path $RunDirectory $buildLogName
        $captureLogName = Get-MtpBoundedFileName -Stem "build-$projectName-capture" -Suffix '.log' -MaximumLength $buildLogNameBudget
        $captureLogPath = Join-Path $RunDirectory $captureLogName
        Write-Host "Building test target: $buildTarget ($Configuration) -> $outputDirectory"
        Write-Host "Build output log: $buildLogPath"
        Write-Host "Build capture log: $captureLogPath"
        $arguments = [string[]]@(
            $DotnetPath,
            'build',
            $buildTarget,
            '--configuration',
            $Configuration,
            '--output',
            $outputDirectory,
            '--nologo',
            '--verbosity',
            'minimal',
            '-clp:ErrorsOnly;Summary',
            '-fl',
            "-flp:LogFile=$buildLogPath;Verbosity=Normal",
            '-nodeReuse:false'
        )
        try {
            $build = Invoke-MtpBuildProcess -Executable $DotnetPath -Arguments $arguments -OutputLog $captureLogPath -WorkingDirectory $RepositoryRoot -StartupHookPath $StartupHookPath
            if (-not $build.Started -or -not [string]::IsNullOrWhiteSpace([string]$build.StartFailureMessage)) {
                Write-Host "BUILD FAILURE - could not start '$DotnetPath build': $($build.StartFailureMessage)"
                return $false
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$build.MonitoringFailureMessage)) {
                Write-Host "BUILD MONITOR FAILURE - PID $($build.ProcessId) ('$DotnetPath build'): $($build.MonitoringFailureMessage)"
                return $false
            }
            if (-not $build.CleanupConfirmed) {
                Write-Host "BUILD CLEANUP FAILURE - exact owned process lifetime rooted at PID $($build.ProcessId) ('$DotnetPath build') was not fully terminated."
                return $false
            }
            if (-not $build.DrainConfirmed) {
                Write-Host "BUILD OUTPUT DRAIN INCOMPLETE - continuing with exit code $($build.ExitCode). Captured stderr/stdout: $captureLogPath"
            }
            if ($build.ExitCode -ne 0) {
                Write-Host "BUILD FAILURE - '$DotnetPath build' exited $($build.ExitCode). The managed MTP runner was not launched."
                return $false
            }

            if ($RequireManagedClosure) {
                try {
                    Complete-MtpClosurePublication -StagingDirectory $outputDirectory -PublishedDirectory $publishedDirectory -ManagedAssemblyLeaf "$projectName.dll" -ProjectName $projectName
                    $outputDirectory = $null
                }
                catch {
                    Write-Host "BUILD CLOSURE FAILURE - '$projectName' output was not published or executed: $($_.Exception.Message)"
                    return $false
                }
            }
        }
        finally {
            if ($null -ne $outputDirectory -and (Test-Path -LiteralPath $outputDirectory)) {
                Remove-Item -LiteralPath $outputDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
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

function Resolve-MtpManagedAssemblyPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $projectPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$Invocation.project)))
    $template = [string]$Invocation.executablePathTemplate
    if ([string]::IsNullOrWhiteSpace($template) -or -not $template.Contains('{executableExtension}')) {
        throw "Manifest MTP invocation for '$($Invocation.project)' must include {executableExtension} in executablePathTemplate."
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    $relativePath = $template.
        Replace('{projectName}', $projectName).
        Replace('{configuration}', $Configuration).
        Replace('{executableExtension}', '.dll')
    $resolved = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativePath))
    $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolved.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MTP managed assembly path for '$($Invocation.project)' is invalid: $resolved"
    }
    return $resolved
}

function Resolve-MtpEvaluatedTargetPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$DotnetPath,
        [Parameter(Mandatory = $true)][string]$RunDirectory
    )

    $projectPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$Invocation.project)))
    $logPath = Join-Path $RunDirectory (Get-MtpBoundedFileName -Stem "msbuild-target-$([System.IO.Path]::GetFileNameWithoutExtension($projectPath))" -Suffix '.log' -MaximumLength 180)
    $arguments = [string[]]@($DotnetPath, 'msbuild', $projectPath, '--nologo', "-p:Configuration=$Configuration", '-getProperty:TargetPath,TargetFramework,RuntimeIdentifier')
    $probe = Invoke-MtpBuildProcess -Executable $DotnetPath -Arguments $arguments -OutputLog $logPath -WorkingDirectory $RepositoryRoot
    if (-not $probe.Started -or -not [string]::IsNullOrWhiteSpace([string]$probe.StartFailureMessage) -or -not [string]::IsNullOrWhiteSpace([string]$probe.MonitoringFailureMessage) -or -not $probe.CleanupConfirmed -or $probe.ExitCode -ne 0) {
        throw "MSBuild target-path evaluation failed for '$projectPath'; diagnostic log: $logPath"
    }
    try {
        $properties = (Get-Content -LiteralPath $logPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop).Properties
        if ($null -eq $properties -or
            [string]::IsNullOrWhiteSpace([string]$properties.TargetPath) -or
            [string]::IsNullOrWhiteSpace([string]$properties.TargetFramework) -or
            $null -eq $properties.PSObject.Properties['RuntimeIdentifier']) {
            throw 'TargetPath, TargetFramework, or RuntimeIdentifier is missing.'
        }
    }
    catch {
        throw "MSBuild build-identity evaluation returned an invalid result for '$projectPath'; diagnostic log: $logPath; $($_.Exception.Message)"
    }
    $resolved = [System.IO.Path]::GetFullPath([string]$properties.TargetPath)
    $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or -not $resolved.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSBuild target-path evaluation escaped the repository or did not resolve a managed assembly: $resolved"
    }
    return [pscustomobject]@{
        TargetPath = $resolved
        TargetFramework = [string]$properties.TargetFramework
        RuntimeIdentifier = [string]$properties.RuntimeIdentifier
    }
}

function Select-MtpVerifiedBuildOutput {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$DotnetPath,
        [Parameter(Mandatory = $true)][string]$RunDirectory
    )

    $projectName = Get-MtpProjectName -Invocation $Invocation
    $projectPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$Invocation.project)))
    $evaluatedIdentity = Resolve-MtpEvaluatedTargetPath -RepositoryRoot $RepositoryRoot -Invocation $Invocation -Configuration $Configuration -DotnetPath $DotnetPath -RunDirectory $RunDirectory
    $declaredAssembly = Resolve-MtpManagedAssemblyPath -RepositoryRoot $RepositoryRoot -Invocation $Invocation -Configuration $Configuration
    $declaredDirectory = Split-Path -Parent $declaredAssembly
    $rejections = [System.Collections.Generic.List[string]]::new()
    try {
        $receipt = Assert-MtpBuildReceipt -Directory $declaredDirectory -ManagedAssemblyLeaf "$projectName.dll" -RepositoryRoot $RepositoryRoot -ProjectPath $projectPath -Configuration $Configuration -TargetFramework $evaluatedIdentity.TargetFramework -RuntimeIdentifier $evaluatedIdentity.RuntimeIdentifier
        Write-Host "NO-BUILD BUILD RECEIPT SELECTED - declared output '$declaredDirectory' assemblySha256=$($receipt.Headers['assemblySha256'])"
        return $declaredDirectory
    }
    catch {
        $rejections.Add("declared path '$declaredDirectory': $($_.Exception.Message)")
    }

    try {
        $evaluatedAssembly = $evaluatedIdentity.TargetPath
        $evaluatedDirectory = Split-Path -Parent $evaluatedAssembly
        $receipt = Assert-MtpBuildReceipt -Directory $evaluatedDirectory -ManagedAssemblyLeaf "$projectName.dll" -RepositoryRoot $RepositoryRoot -ProjectPath $projectPath -Configuration $Configuration -TargetFramework $evaluatedIdentity.TargetFramework -RuntimeIdentifier $evaluatedIdentity.RuntimeIdentifier
        Write-Host "NO-BUILD BUILD RECEIPT SELECTED - evaluated output '$evaluatedDirectory' assemblySha256=$($receipt.Headers['assemblySha256'])"
        return $evaluatedDirectory
    }
    catch {
        $rejections.Add("evaluated path: $($_.Exception.Message)")
    }

    throw "No verified no-build output exists for '$projectName'. $($rejections -join ' | ')"
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
        $charactersRequiringQuotes += @('&', '|', '<', '>', '(', ')', '^', ';')
    }
    $requiresQuotes = $Value.Length -eq 0 -or $Value.IndexOfAny([char[]]$charactersRequiringQuotes) -ge 0
    if (-not $requiresQuotes) {
        if ($QuoteCmdMetaCharacters) {
            return $Value.Replace('%', '^%')
        }
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
        if ($QuoteCmdMetaCharacters -and $character -eq '%') {
            [void]$quoted.Append('^%')
            continue
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
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$StartupHookPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $extension = [System.IO.Path]::GetExtension($Executable)
    if ($extension.Equals('.cmd', [System.StringComparison]::OrdinalIgnoreCase) -or
        $extension.Equals('.bat', [System.StringComparison]::OrdinalIgnoreCase)) {
        $cmdValues = @($Executable) + @($Arguments | Select-Object -Skip 1)
        $cmdTokens = [System.Collections.Generic.List[string]]::new()
        for ($index = 0; $index -lt $cmdValues.Count; $index++) {
            $value = [string]$cmdValues[$index]
            if ($value.Contains('%')) {
                $environmentName = "MCG_MTP_CMD_VALUE_$index"
                $startInfo.EnvironmentVariables[$environmentName] = $value
                $cmdTokens.Add('"%' + $environmentName + '%"')
            }
            else {
                $cmdTokens.Add((ConvertTo-MtpCommandLineArgument $value -QuoteCmdMetaCharacters))
            }
        }
        $commandInterpreter = $env:ComSpec
        if ([string]::IsNullOrWhiteSpace($commandInterpreter)) {
            $commandInterpreter = Join-Path $env:SystemRoot 'System32\cmd.exe'
        }
        $commandLine = $cmdTokens -join ' '
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
    if (-not [string]::IsNullOrWhiteSpace($StartupHookPath)) {
        $existingHooks = if ($startInfo.EnvironmentVariables.ContainsKey('DOTNET_STARTUP_HOOKS')) {
            [string]$startInfo.EnvironmentVariables['DOTNET_STARTUP_HOOKS']
        }
        else {
            $null
        }
        $startInfo.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] = if ([string]::IsNullOrWhiteSpace($existingHooks)) {
            $StartupHookPath
        }
        else {
            $existingHooks + [System.IO.Path]::PathSeparator + $StartupHookPath
        }
    }
    return $startInfo
}

function Stop-MtpOwnedProcessTree {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][datetime]$StartTimeUtc,
        [AllowNull()]$OwnedJob
    )

    try {
        if ($null -ne $OwnedJob) {
            if (-not $OwnedJob.TerminateAndWait($script:MtpExitConfirmationSeconds * 1000)) {
                Write-Host "CLEANUP FAILURE - owned process job rooted at PID $($Process.Id) did not become empty."
                return $false
            }
            return $true
        }
        if ($Process.HasExited) {
            if (Test-MtpWindows) {
                Write-Host "CLEANUP FAILURE - owned PID $($Process.Id) exited before exact descendant ownership was established."
                return $false
            }
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
            if (-not [McgMtpSuppressedProcessStart]::Start($taskkill)) {
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
        [string]$StartupHookPath,
        [switch]$AllowBreakaway,
        [ValidateRange(1, 86400)][int]$TestHostTimeoutSeconds = 780
    )

    $process = $null
    $capture = $null
    $ownedJob = $null
    $processId = $null
    $startTimeUtc = $null
    $runnerExit = $script:ExitCodes.Runner
    $timedOut = $false
    $exitConfirmed = $false
    $processExitConfirmed = $false
    $streamsDrained = $false
    $processStarted = $false
    $runnerFailed = $false
    $cleanupDiagnostic = $null
    $captured = @()
    $jobAssigned = $false
    try {
        $capture = [McgMtpProcessOutputCapture]::new($OutputLog)
        if (Test-MtpWindows) {
            $ownedJob = [McgMtpOwnedJob]::new($AllowBreakaway.IsPresent)
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = New-MtpProcessStartInfo -Executable $Executable -Arguments $Arguments -StartupHookPath $StartupHookPath
        $capture.Attach($process)
        if (-not [McgMtpSuppressedProcessStart]::Start($process)) {
            throw "Process.Start returned false for '$Executable'."
        }
        $processStarted = $true
        $processId = $process.Id
        $startTimeUtc = $process.StartTime.ToUniversalTime()
        if ($null -ne $ownedJob) {
            $ownedJob.Assign($process)
            $jobAssigned = $true
        }
        $process.BeginOutputReadLine()
        $process.BeginErrorReadLine()

        if (-not $process.WaitForExit($TestHostTimeoutSeconds * 1000)) {
            $timedOut = $true
            Write-Host "TEST HOST TIMEOUT - owned PID $processId exceeded ${TestHostTimeoutSeconds}s; allowing ${script:MtpGracefulExitSeconds}s for MTP cancellation."
            [void]$process.WaitForExit($script:MtpGracefulExitSeconds * 1000)
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
            $mustTerminate = $timedOut -or $runnerFailed -or -not $process.HasExited
            if ($mustTerminate -and $null -ne $startTimeUtc) {
                Write-Host "PROCESS CLEANUP - terminating the exact owned process lifetime rooted at PID $processId."
                $cleanupJob = if ($jobAssigned) { $ownedJob } else { $null }
                if (-not (Stop-MtpOwnedProcessTree -Process $process -StartTimeUtc $startTimeUtc -OwnedJob $cleanupJob)) {
                    $cleanupDiagnostic = "CLEANUP FAILURE - exact owned process lifetime rooted at PID $processId could not be terminated."
                }
            }
            $processExitConfirmed = $process.HasExited -or $process.WaitForExit($script:MtpExitConfirmationSeconds * 1000)
        }
        if ($null -ne $capture) {
            if ($processStarted) {
                $streamsDrained = $capture.WaitForCompletion($script:MtpOutputDrainSeconds * 1000)
            }

            $jobExitConfirmed = $true
            if ($processStarted -and $jobAssigned) {
                try {
                    if (-not $ownedJob.IsEmpty) {
                        if (-not (Stop-MtpOwnedProcessTree -Process $process -StartTimeUtc $startTimeUtc -OwnedJob $ownedJob)) {
                            $cleanupDiagnostic = "CLEANUP FAILURE - exact owned process job rooted at PID $processId retained active descendants."
                        }
                        $processExitConfirmed = $process.HasExited -or $process.WaitForExit($script:MtpExitConfirmationSeconds * 1000)
                        if (-not $streamsDrained) {
                            $streamsDrained = $capture.WaitForCompletion($script:MtpOutputDrainSeconds * 1000)
                        }
                    }
                    $jobExitConfirmed = $ownedJob.IsEmpty
                }
                catch {
                    $jobExitConfirmed = $false
                    $cleanupDiagnostic = "CLEANUP FAILURE - owned process job rooted at PID $processId could not confirm descendant exit: $($_.Exception.Message)"
                }
            }

            $exitConfirmed = $processStarted -and $processExitConfirmed -and $jobExitConfirmed -and $streamsDrained
            $captured = @($capture.Snapshot())
            if ($null -ne $process) {
                $capture.Detach($process)
            }
            $capture.Dispose()
        }
        else {
            $captured = @()
        }
        if ($processStarted -and $null -ne $process -and $process.HasExited) {
            $runnerExit = $process.ExitCode
        }
        if ($processStarted -and -not $exitConfirmed -and [string]::IsNullOrWhiteSpace($cleanupDiagnostic)) {
            $cleanupDiagnostic = "CLEANUP FAILURE - owned PID $processId exit, descendant exit, or redirected-stream drain was not confirmed."
        }
        if (-not [string]::IsNullOrWhiteSpace($cleanupDiagnostic)) {
            Write-Host $cleanupDiagnostic
        }
        if ($null -ne $ownedJob) {
            $ownedJob.Dispose()
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
        CleanupDiagnostic = $cleanupDiagnostic
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
        [bool]$ArtifactsRetained = $false,
        [string[]]$Diagnostics = @()
    )

    $terminalDiagnostics = @($Diagnostics | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($Outcome -eq 'timed-out' -and $ExitConfirmed -is [bool] -and -not [bool]$ExitConfirmed -and $terminalDiagnostics.Count -eq 0) {
        $terminalDiagnostics = @('CLEANUP FAILURE - timed-out test host exit or redirected-stream drain was not confirmed.')
    }

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
        diagnostics = $terminalDiagnostics
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
        [switch]$AllowBreakaway,
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
        $startupHookPath = Resolve-MtpFaultDialogStartupHook
    }
    catch {
        Write-Host "STARTUP HOOK FAILURE - managed build/test processes were not launched: $($_.Exception.Message)"
        Write-Host "Retained diagnostic directory: $runDirectory"
        return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.StartupHook -ResultsDirectory $runDirectory -ArtifactsRetained $true
    }
    try {
        Set-MtpHermeticEnvironment -RepositoryRoot $RepositoryRoot -WritableRoot $runDirectory
        Write-Host "Results directory: $runDirectory"
        $usesManagedAssembly = [string]::IsNullOrWhiteSpace($RunnerPath)
        if (-not $NoBuild) {
            if (-not (Invoke-MtpBuild -RepositoryRoot $RepositoryRoot -Projects $projects -Configuration $Configuration -DotnetPath $DotnetPath -RunDirectory $runDirectory -StartupHookPath $startupHookPath -RequireManagedClosure:$usesManagedAssembly)) {
                Write-Host "Retained diagnostic directory: $runDirectory"
                return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Build -ResultsDirectory $runDirectory -ArtifactsRetained $true
            }
        }
        elseif ($usesManagedAssembly) {
            foreach ($project in $projects) {
                $projectName = Get-MtpProjectName -Invocation $project
                $publishedDirectory = Get-MtpClosureDirectory -RunDirectory $runDirectory -Invocation $project
                try {
                    $sourceDirectory = Select-MtpVerifiedBuildOutput -RepositoryRoot $RepositoryRoot -Invocation $project -Configuration $Configuration -DotnetPath $DotnetPath -RunDirectory $runDirectory
                    Copy-MtpStableNoBuildClosure -SourceDirectory $sourceDirectory -PublishedDirectory $publishedDirectory -ManagedAssemblyLeaf "$projectName.dll" -ProjectName $projectName
                }
                catch {
                    Write-Host "NO-BUILD CLOSURE FAILURE - '$projectName' shared output was not copied or executed: $($_.Exception.Message)"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.MissingRunnerArtifact -ResultsDirectory $runDirectory -ArtifactsRetained $true
                }
            }
        }

        $filterList = @($Filters)
        if ($filterList.Count -eq 0) {
            $filterList = @('')
        }
        $invocationIndex = 0
        foreach ($project in $projects) {
            $projectName = Get-MtpProjectName -Invocation $project
            $managedAssembly = Get-MtpClosureManagedAssemblyPath -RunDirectory $runDirectory -Invocation $project
            $executable = if ($usesManagedAssembly) { $DotnetPath } else { [System.IO.Path]::GetFullPath($RunnerPath) }
            $requiredExecutable = if ($usesManagedAssembly) { $managedAssembly } else { $executable }
            if (-not (Test-Path -LiteralPath $requiredExecutable -PathType Leaf)) {
                $projectPath = Join-Path $RepositoryRoot ([string]$project.project)
                $missingArtifact = if ($usesManagedAssembly) { 'MANAGED ASSEMBLY' } else { 'RUNNER' }
                Write-Host "MISSING $missingArtifact - expected sealed run-local artifact '$requiredExecutable'. Build it with the managed MTP runner: $DotnetPath build `"$projectPath`" --configuration $Configuration"
                Write-Host "Retained diagnostic directory: $runDirectory"
                return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.MissingRunnerArtifact -ResultsDirectory $runDirectory -ArtifactsRetained $true
            }
            if ($usesManagedAssembly) {
                try {
                    Assert-MtpSealedClosure -Path (Split-Path -Parent $managedAssembly) -ManagedAssemblyLeaf "$projectName.dll" -ExpectedProject $projectName
                }
                catch {
                    Write-Host "RUNNER CLOSURE FAILURE - '$projectName' closure was not executed: $($_.Exception.Message)"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.MissingRunnerArtifact -ResultsDirectory $runDirectory -ArtifactsRetained $true
                }
            }

            foreach ($filter in $filterList) {
                for ($attempt = 1; $attempt -le $Repeat; $attempt++) {
                $invocationIndex++
                $stem = "$RunLabel-$projectName-$invocationIndex-r$attempt-$filter"
                $fileNameBudget = [Math]::Min(200, 240 - $runDirectory.Length - 1)
                $trxFileName = Get-MtpBoundedFileName -Stem $stem -MaximumLength $fileNameBudget
                $trxPath = Join-Path $runDirectory $trxFileName
                $outputLog = Join-Path $runDirectory ([System.IO.Path]::ChangeExtension($trxFileName, '.runner.log'))
                $expectedTrxPaths.Add($trxPath)
                try {
                    $argumentExecutable = if ($usesManagedAssembly) { $managedAssembly } else { $executable }
                    $arguments = New-MtpRunnerArguments -Invocation $project -Executable $argumentExecutable -ResultsDirectory $runDirectory -TrxFileName $trxFileName -TestHostTimeoutSeconds $TestHostTimeoutSeconds -Filter $filter
                }
                catch {
                    Write-Host "RUNNER/TOOLING FAILURE - $($_.Exception.Message)"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Runner -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $lastOwnedProcessId -ExitConfirmed $allExitsConfirmed -ArtifactsRetained $true
                }

                Write-Host "Running MTP host: $executable $argumentExecutable"
                if (-not [string]::IsNullOrWhiteSpace($filter)) {
                    Write-Host "Filter: $filter"
                }
                Write-Host "Runner output log: $outputLog"
                $runnerLogPaths.Add($outputLog)
                if ($usesManagedAssembly) {
                    $arguments = @($executable) + @($arguments)
                }
                $run = Invoke-MtpAppHost -Executable $executable -Arguments $arguments -OutputLog $outputLog -StartupHookPath $startupHookPath -AllowBreakaway:$AllowBreakaway -TestHostTimeoutSeconds $TestHostTimeoutSeconds
                $lastOwnedProcessId = $run.OwnedProcessId
                $lastRunnerExitCode = $run.ExitCode
                $allExitsConfirmed = $allExitsConfirmed -and [bool]$run.ExitConfirmed
                if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
                    $trxPaths.Add($trxPath)
                }
                if ($run.TimedOut) {
                    Write-Host "TEST HOST TIMED OUT - managed MTP runner exceeded ${TestHostTimeoutSeconds}s. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome timed-out -ExitCode $script:ExitCodes.Timeout -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed ([bool]$run.ExitConfirmed) -ArtifactsRetained $true -Diagnostics @($run.CleanupDiagnostic)
                }
                if ($run.RunnerFailed) {
                    Write-Host "RUNNER/TOOLING FAILURE - managed MTP runner could not be started or monitored. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Runner -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed ([bool]$run.ExitConfirmed) -ArtifactsRetained $true
                }
                if (-not $run.ExitConfirmed) {
                    Write-Host "CLEANUP FAILURE - owned PID $($run.OwnedProcessId) exit or output drain was not confirmed."
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Cleanup -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $false -ArtifactsRetained $true
                }
                if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
                    Write-Host "COMPLETED WITH MISSING TRX - managed MTP runner exited $($run.ExitCode) without producing TRX '$trxPath'. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    $exitCode = if ($run.ExitCode -ne 0) { $run.ExitCode } else { $script:ExitCodes.Runner }
                    return New-MtpTerminalResult -Outcome completed-with-missing-trx -ExitCode $exitCode -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $true -ArtifactsRetained $true
                }

                Write-Host "TRX: $trxPath"
                try {
                    $summary = Read-MtpTrxResult -Path $trxPath
                }
                catch {
                    Write-Host "RUNNER/TOOLING FAILURE - $($_.Exception.Message) Managed runner exit: $($run.ExitCode). Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return New-MtpTerminalResult -Outcome failed -ExitCode $script:ExitCodes.Runner -RunnerExitCode $run.ExitCode -ResultsDirectory $runDirectory -RunnerLogPaths $runnerLogPaths.ToArray() -TrxPaths $trxPaths.ToArray() -ExpectedTrxPaths $expectedTrxPaths.ToArray() -OwnedProcessId $run.OwnedProcessId -ExitConfirmed $true -ArtifactsRetained $true
                }
                Write-Host ("{0}: total={1} passed={2} failed={3} skipped={4}" -f $trxFileName, $summary.Total, $summary.Passed, $summary.Failed, $summary.Skipped)
                if ($summary.Total -le 0) {
                    Write-Host "ZERO TESTS - filter matched no tests. Managed runner exit: $($run.ExitCode). TRX: $trxPath"
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
                    Write-Host "RUNNER/TOOLING FAILURE - managed runner exited $($run.ExitCode) although its TRX contains no failing tests. This is not a compile failure. Captured stderr/stdout: $outputLog"
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
    'Get-MtpBuildReceiptPath',
    'Read-MtpBuildReceipt',
    'Assert-MtpBuildReceipt',
    'Resolve-MtpEvaluatedTargetPath',
    'Select-MtpVerifiedBuildOutput',
    'New-MtpRunnerArguments',
    'Read-MtpTrxResult',
    'New-MtpTerminalResult',
    'Write-MtpTerminalSummary',
    'Invoke-MtpTestRun'
)
