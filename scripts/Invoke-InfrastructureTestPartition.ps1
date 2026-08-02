<#
.SYNOPSIS
  Run or list stable Infrastructure test-suite partitions.

.DESCRIPTION
  Provides repo-bounded, named partitions for high-cost Infrastructure test
  areas without changing the full acceptance gate. Test execution uses the
  isolated dotnet wrapper and prints compact TRX-backed results.

.EXAMPLE
  .\scripts\Invoke-InfrastructureTestPartition.ps1 -List
  .\scripts\Invoke-InfrastructureTestPartition.ps1 -Partition Cli
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'List', Mandatory = $true)]
    [switch]$List,

    [Parameter(ParameterSetName = 'Run', Mandatory = $true)]
    [ValidateSet('Cli', 'WorkerDispatch', 'GoalWorktree', 'Dashboard', 'Conductor', 'ChaosGate', 'Remainder')]
    [string]$Partition,

    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(1, 25)]
    [int]$Repeat = 1
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$target = 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'

function Set-HermeticVerificationEnvironment {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $nugetPackages = $env:NUGET_PACKAGES
    $userProfile = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    # Capture the REAL per-user application-data folders BEFORE the loop below strips the environment and
    # USERPROFILE is repointed. These must mirror GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment,
    # which pins APPDATA/LOCALAPPDATA to their real locations: relocating them into the profile root removed the
    # per-user temp directory, pushed git object paths past MAX_PATH, and stopped the per-user PowerShell 7
    # install from resolving so callers silently degraded to Windows PowerShell 5.1.
    $realAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ApplicationData)
    $realLocalAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)

    $allowedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('PATH', 'PATHEXT', 'SystemRoot', 'WINDIR', 'COMSPEC', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'TEMP', 'TMP', 'TMPDIR')) {
        [void]$allowedNames.Add($name)
    }

    foreach ($item in @(Get-ChildItem Env:)) {
        if (-not $allowedNames.Contains($item.Name) -and
            -not $item.Name.StartsWith('DOTNET_', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $item.Name.StartsWith('NUGET_', [System.StringComparison]::OrdinalIgnoreCase)) {
            [System.Environment]::SetEnvironmentVariable($item.Name, $null, [System.EnvironmentVariableTarget]::Process)
        }
    }

    $profileRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'mcg-hvp'
    if ([string]::IsNullOrWhiteSpace($nugetPackages)) {
        $packageProfile = if ([string]::IsNullOrWhiteSpace($userProfile)) { $profileRoot } else { $userProfile }
        $nugetPackages = Join-Path $packageProfile '.nuget\packages'
    }
    $appData = $realAppData
    $localAppData = $realLocalAppData
    New-Item -ItemType Directory -Force $profileRoot | Out-Null
    $env:HOME = $profileRoot
    $env:USERPROFILE = $profileRoot
    $env:DOTNET_CLI_HOME = $profileRoot
    # Git resolves user.name/user.email from $HOME/.gitconfig, and the profile root above is empty, so a
    # verification child would inherit NO IDENTITY and any commit-replaying git operation exits 128. These
    # four variables outrank every config file. Mirrors the production hermetic environment.
    $env:GIT_AUTHOR_NAME = 'MCG Acceptance Gate'
    $env:GIT_AUTHOR_EMAIL = 'acceptance-gate@localhost'
    $env:GIT_COMMITTER_NAME = 'MCG Acceptance Gate'
    $env:GIT_COMMITTER_EMAIL = 'acceptance-gate@localhost'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = $nugetPackages
    $profileRootPath = [System.IO.Path]::GetPathRoot($profileRoot)
    $env:HOMEDRIVE = $profileRootPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $env:HOMEPATH = ([string][System.IO.Path]::DirectorySeparatorChar) + $profileRoot.Substring($profileRootPath.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localAppData
    $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = [System.IO.Path]::GetFullPath($RepositoryRoot)
}

Set-HermeticVerificationEnvironment -RepositoryRoot $repoRoot

$partitions = [ordered]@{
    Cli = [pscustomobject]@{
        Description = 'CLI command/help coverage; largest current Infrastructure test file is CliCommandTests.'
        Filters = @('FullyQualifiedName~CliCommandTests', 'FullyQualifiedName~CliHelpTests')
    }
    WorkerDispatch = [pscustomobject]@{
        Description = 'Worker dispatch/profile/process/sandbox coverage; includes the WorkerDispatch god-class area.'
        Filters = @(
            'FullyQualifiedName~WorkerDispatchTests',
            'FullyQualifiedName~WorkerProfileTests',
            'FullyQualifiedName~WorkerProcessJobsTests',
            'FullyQualifiedName~WorkerShellTests',
            'FullyQualifiedName~WorkerSandboxCapabilityPlannerTests',
            'FullyQualifiedName~DispatchProcessHostTests'
        )
    }
    GoalWorktree = [pscustomobject]@{
        Description = 'Goal worktree and acceptance-verifier coverage, including focused and full worktree test files.'
        Filters = @('FullyQualifiedName~GoalWorktreeTests', 'FullyQualifiedName~GoalAcceptanceVerifierTests')
    }
    Dashboard = [pscustomobject]@{
        Description = 'Dashboard rendering, host, and validation-harness coverage.'
        Filters = @(
            'FullyQualifiedName~DashboardRenderingTests',
            'FullyQualifiedName~DashboardHostTests',
            'FullyQualifiedName~DashboardValidationHarnessTests'
        )
    }
    Conductor = [pscustomobject]@{
        Description = 'Conductor advance/batch/driver/watch-sweep coverage.'
        Filters = @(
            'FullyQualifiedName~AdvanceLoopTests',
            'FullyQualifiedName~ConductorBatchLoopTests',
            'FullyQualifiedName~ConductorDriverTests',
            'FullyQualifiedName~ConductWatchSweepScopingTests'
        )
    }
    ChaosGate = [pscustomobject]@{
        Description = 'Chaos-gate acceptance contract coverage split out of the former remainder lane.'
        Filters = @('FullyQualifiedName~ChaosGate&Category!=HostIntegration')
    }
    Remainder = [pscustomobject]@{
        Description = 'Acceptance-gate remainder lane: all Infrastructure tests outside the named high-cost partitions.'
        Filters = @(
            'FullyQualifiedName!~CliCommandTests&FullyQualifiedName!~CliHelpTests' +
            '&FullyQualifiedName!~WorkerDispatchTests&FullyQualifiedName!~WorkerProfileTests' +
            '&FullyQualifiedName!~WorkerProcessJobsTests&FullyQualifiedName!~WorkerShellTests' +
            '&FullyQualifiedName!~WorkerSandboxCapabilityPlannerTests&FullyQualifiedName!~DispatchProcessHostTests' +
            '&FullyQualifiedName!~GoalWorktreeTests&FullyQualifiedName!~GoalAcceptanceVerifierTests' +
            '&FullyQualifiedName!~DashboardRenderingTests&FullyQualifiedName!~DashboardHostTests' +
            '&FullyQualifiedName!~DashboardValidationHarnessTests&FullyQualifiedName!~AdvanceLoopTests' +
            '&FullyQualifiedName!~ConductorBatchLoopTests&FullyQualifiedName!~ConductorDriverTests' +
            '&FullyQualifiedName!~ConductWatchSweepScopingTests&FullyQualifiedName!~ChaosGate&Category!=HostIntegration'
        )
    }
}

if ($List) {
    "Infrastructure test partitions:"
    foreach ($name in $partitions.Keys) {
        $entry = $partitions[$name]
        "- {0}: {1}" -f $name, $entry.Description
        "  Filters: {0}" -f ($entry.Filters -join ', ')
    }
    exit 0
}

$selected = $partitions[$Partition]
"Infrastructure partition: $Partition"
"Target: $target"
"Filters: $($selected.Filters -join ', ')"

function Write-TrxSummary {
    param([string]$ResultsPath)

    $trxFiles = Get-ChildItem $ResultsPath -Filter *.trx -ErrorAction SilentlyContinue
    if (-not $trxFiles) {
        Write-Output "NO TRX OUTPUT - dotnet test did not produce results."
        $script:LastTrxGreen = $false
        return
    }

    $green = $true
    foreach ($file in $trxFiles) {
        [xml]$trx = Get-Content $file.FullName
        $c = $trx.TestRun.ResultSummary.Counters
        $total = [int]$c.total
        $failed = [int]$c.failed
        if ($total -le 0) {
            $green = $false
        }

        if ($failed -gt 0) {
            $green = $false
        }

        "{0}: total={1} passed={2} failed={3} skipped={4}" -f $file.Name, $c.total, $c.passed, $failed, $c.notExecuted
        if ($total -le 0) {
            "  ZERO TESTS - filter matched no tests."
        }

        $trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.outcome -eq 'Failed' } |
            ForEach-Object {
                "  FAIL  $($_.testName)"
                $msg = $_.Output.ErrorInfo.Message
                if ($msg) {
                    "        " + (($msg -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 1)
                }
            }
    }

    $script:LastTrxGreen = $green
}

$orchestrator = Join-Path $repoRoot 'mcg-orchestrator.cmd'
$appDll = Join-Path $repoRoot 'src/Mcg.AgentOrchestrator.App/bin/Debug/net10.0/Mcg.AgentOrchestrator.App.dll'
$resultsRoot = Join-Path $repoRoot '.test-results'

function Ensure-AppDllAvailable {
    if (Test-Path -LiteralPath $appDll -PathType Leaf) {
        return
    }

    & $orchestrator gate-status | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $appDll -PathType Leaf)) {
        throw "Unable to prepare orchestrator app DLL for stable-slot-dotnet: $appDll"
    }
}

for ($index = 0; $index -lt $selected.Filters.Count; $index++) {
    $filter = $selected.Filters[$index]
    for ($attempt = 1; $attempt -le $Repeat; $attempt++) {
        "Running filter: $filter"
        if ($Repeat -gt 1) {
            "Repeat attempt: $attempt/$Repeat"
        }

        New-Item -ItemType Directory -Force $resultsRoot | Out-Null
        Get-ChildItem $resultsRoot -Filter *.trx -ErrorAction SilentlyContinue | Remove-Item -Force

        $hasFilterMetacharacters = $filter.IndexOfAny([char[]]'&|<>()') -ge 0
        $dotnetArguments = @(
            'test',
            (Join-Path $repoRoot $target),
            '--logger', 'trx',
            '--results-directory', $resultsRoot,
            '-clp:ErrorsOnly',
            '--filter', $filter
        )

        if ($hasFilterMetacharacters) {
            Ensure-AppDllAvailable
            $responsePath = Join-Path $resultsRoot "infra-$($Partition.ToLowerInvariant())-$index-r$attempt.rsp"
            try {
                $dotnetArguments | Set-Content -LiteralPath $responsePath -Encoding UTF8
                & dotnet $appDll stable-slot-dotnet "@$responsePath" | Out-Null
            }
            finally {
                Remove-Item -LiteralPath $responsePath -Force -ErrorAction SilentlyContinue
            }
        }
        else {
            & $orchestrator stable-slot-dotnet @dotnetArguments | Out-Null
        }

        $testExit = $LASTEXITCODE
        $script:LastTrxGreen = $false
        Write-TrxSummary -ResultsPath $resultsRoot
        if ($testExit -ne 0 -or -not $script:LastTrxGreen) {
            if ($testExit -ne 0) {
                exit $testExit
            }

            exit 1
        }
    }
}
"PARTITION GREEN"; exit 0
