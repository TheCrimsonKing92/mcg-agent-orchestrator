using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private const string RunBuildTestCycleUrl = "/api/system/run-build-test-cycle";

    private static DashboardHostInfoDto BuildHostInfo(DashboardEndpointServices services)
    {
        var browserUrl = DashboardHost.GetBrowserUrl(services.HostArgs);
        var hostedUrlPrefixes = DashboardHost.GetHostedUrlPrefixes(services.HostArgs);
        return new DashboardHostInfoDto(
            services.HostArgs.CommandName,
            services.HostArgs.UrlPrefix,
            browserUrl,
            DashboardHost.GetDashboardPageUrl(browserUrl),
            hostedUrlPrefixes
                .Select(DashboardHost.GetDashboardPageUrl)
                .ToList(),
            DashboardHost.GetSourceSurveyUrl(browserUrl),
            hostedUrlPrefixes
                .Select(DashboardHost.GetSourceSurveyUrl)
                .ToList(),
            DashboardHost.GetHostedAccessNote(services.HostArgs.UrlPrefix, hostedUrlPrefixes),
            services.HostArgs.AutoRefreshSeconds,
            services.HostArgs.OpenBrowser,
            services.HostArgs.EnableOperatorControls,
            BuildDashboardRestartCommand(services),
            services.Workspace.TenantName,
            services.Workspace.IsTenantScoped,
            services.Workspace.SqliteStatePath,
            services.Workspace.AgentCatalogPath,
            services.Workspace.WorkerProfilePath,
            services.Workspace.ContinuationStorePath);
    }

    internal static DistributedArchitectureDto BuildArchitectureReport(DashboardEndpointServices services)
    {
        var agents = services.LoadAgentCatalog().Agents;
        var workerProfiles = Mcg.AgentOrchestrator.Infrastructure.WorkerProfileStore.Load(services.WorkerProfilePath);
        return DistributedArchitectureDto.Create(
            services.Workspace,
            agents,
            workerProfiles,
            services.HostArgs.EnableOperatorControls);
    }

    internal static IReadOnlyList<TaskDurationStatsDto> BuildTaskDurationStatsReport(AgentOrchestratorKernel kernel, bool includeModel = false)
    {
        return kernel.BuildTaskDurationStats(includeModel)
            .Select(ToTaskDurationStatsDto)
            .ToList();
    }

    private static TaskDurationStatsDto ToTaskDurationStatsDto(TaskDurationStatsRecord record)
    {
        var insufficient = $"n/a (n={record.TaskCount})";
        return new TaskDurationStatsDto(
            record.Scope,
            record.Role,
            record.Complexity,
            record.ProviderName,
            record.ModelName,
            record.TaskCount,
            record.AttemptCount,
            record.FailedAttemptCount,
            TaskDurationReport.MinSamplesForPublishedStats,
            record.HasPublishedStats,
            record.HasPublishedStats ? FormatDuration(record.MedianLegitimateRuntime) : insufficient,
            record.HasPublishedStats ? FormatDuration(record.P90LegitimateRuntime) : insufficient,
            record.HasPublishedStats ? FormatDuration(record.MedianFailureInterventionOverhead) : insufficient,
            record.FailureRate);
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "n/a";
        }

        var value = duration.Value;
        return value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#}m"
            : $"{value.TotalSeconds:0.#}s";
    }

    private static DashboardBuildTestCleanupDto BuildTestCleanup(DashboardEndpointServices services)
    {
        var diagnostic = DashboardProcessInspector.InspectCurrent();
        var siblings = diagnostic.SiblingProcesses
            .Select(process => new DashboardStopSiblingDto(
                process.ProcessId,
                process.ListeningPorts,
                process.SafeStopCommand))
            .ToList();
        var verifyNoAppProcesses = $"Get-Process {DashboardProcessInspector.DashboardProcessName} -ErrorAction SilentlyContinue";
        var dashboardUrl = DashboardHost.GetBrowserUrl(services.HostArgs);
        var buildTestCycleCommand = $".\\scripts\\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl {dashboardUrl}";
        var checklist = new List<string>
        {
            $"Optional single-command cycle: {buildTestCycleCommand}",
            $"Stop current dashboard PID {diagnostic.CurrentProcessId} through POST /api/system/stop-dashboard.",
            siblings.Count == 0
                ? "No sibling dashboard processes are currently detected."
                : "Stop only stale sibling dashboard PIDs listed in SiblingProcesses after confirming their ports are not the active dashboard.",
            $"Verify no dashboard app process remains with: {verifyNoAppProcesses}",
            "Run isolated verification with .\\scripts\\Invoke-IsolatedDotnet.ps1 test Mcg.AgentOrchestrator.sln --verbosity minimal.",
            $"Restart with: {BuildDashboardRestartCommand(services)}"
        };

        return new DashboardBuildTestCleanupDto(
            diagnostic.CurrentProcessId,
            diagnostic.CurrentListeningPorts,
            "/api/system/stop-dashboard",
            RunBuildTestCycleUrl,
            siblings,
            verifyNoAppProcesses,
            ".\\scripts\\Invoke-IsolatedDotnet.ps1 build Mcg.AgentOrchestrator.sln --no-restore --verbosity minimal",
            ".\\scripts\\Invoke-IsolatedDotnet.ps1 test Mcg.AgentOrchestrator.sln --verbosity minimal",
            BuildDashboardRestartCommand(services),
            buildTestCycleCommand,
            checklist);
    }

    internal static IReadOnlyList<DashboardBuildTestRunSummaryDto> BuildTestRuns(DashboardEndpointServices services)
    {
        return BuildTestRuns(services.Workspace.LogDirectory);
    }

    internal static IReadOnlyList<DashboardBuildTestRunSummaryDto> BuildTestRuns(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(logDirectory, "dashboard-build-test-cycle-*.ps1")
            .Select(path => BuildTestRunSummary(logDirectory, path))
            .OrderByDescending(run => run.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(run => run.Stamp, StringComparer.Ordinal)
            .Take(8)
            .ToList();
    }

    private static DashboardBuildTestRunSummaryDto BuildTestRunSummary(string logDirectory, string runnerPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(runnerPath);
        var stamp = fileName["dashboard-build-test-cycle-".Length..];
        var outputLogPath = Path.Combine(logDirectory, $"{fileName}.out.log");
        var errorLogPath = Path.Combine(logDirectory, $"{fileName}.err.log");
        var outputText = ReadLogTail(outputLogPath, 12000);
        var errorText = ReadLogTail(errorLogPath, 4000);
        var buildSucceeded = outputText.Contains("BuildSucceeded        : True", StringComparison.Ordinal)
            || outputText.Contains("Build succeeded.", StringComparison.Ordinal);
        var testSucceeded = outputText.Contains("TestSucceeded         : True", StringComparison.Ordinal)
            || outputText.Contains("Failed:     0", StringComparison.Ordinal);
        var hasFailure = outputText.Contains("BuildSucceeded        : False", StringComparison.Ordinal)
            || outputText.Contains("TestSucceeded         : False", StringComparison.Ordinal)
            || outputText.Contains("Build FAILED.", StringComparison.Ordinal)
            || outputText.Contains("Failed!", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(errorText);
        var status = buildSucceeded && testSucceeded
            ? "Passed"
            : hasFailure
                ? "Failed"
                : "Running or incomplete";

        return new DashboardBuildTestRunSummaryDto(
            stamp,
            TryGetLastWriteTime(runnerPath),
            File.Exists(outputLogPath),
            File.Exists(errorLogPath),
            buildSucceeded,
            testSucceeded,
            status,
            runnerPath,
            outputLogPath,
            errorLogPath,
            BuildPreview(outputText),
            BuildPreview(errorText));
    }

    private static DateTimeOffset? TryGetLastWriteTime(string path)
    {
        return File.Exists(path)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
            : null;
    }

    private static string ReadLogTail(string path, int maxChars)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var text = File.ReadAllText(path);
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    private static string BuildPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Passed!", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Failed!", StringComparison.OrdinalIgnoreCase)
                || line.Contains("BuildSucceeded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("TestSucceeded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("warning", StringComparison.OrdinalIgnoreCase))
            .TakeLast(10)
            .ToList();

        return string.Join(Environment.NewLine, lines.Count == 0 ? text.Split('\n').TakeLast(10) : lines);
    }

    internal static string BuildTestLogFileUrl(string path)
    {
        return "/api/system/build-test-runs/log?path=" + Uri.EscapeDataString(path);
    }

    private static IResult BuildTestRunLog(HttpContext context, DashboardEndpointServices services)
    {
        var requestedPath = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return Results.BadRequest("path is required.");
        }

        var logRoot = Path.GetFullPath(services.Workspace.LogDirectory);
        var fullPath = Path.GetFullPath(requestedPath);
        var fileName = Path.GetFileName(fullPath);
        if (!fullPath.StartsWith(logRoot, StringComparison.OrdinalIgnoreCase)
            || !fileName.StartsWith("dashboard-build-test-cycle-", StringComparison.OrdinalIgnoreCase)
            || !(fileName.EndsWith(".out.log", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".err.log", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
        {
            return Results.BadRequest("path must reference a dashboard build/test run file under the orchestrator log directory.");
        }

        if (!File.Exists(fullPath))
        {
            return Results.NotFound("dashboard build/test run log was not found.");
        }

        return Text(File.ReadAllText(fullPath), "text/plain; charset=utf-8");
    }

    private static IResult RunBuildTestCycle(DashboardEndpointServices services)
    {
        var dashboardUrl = DashboardHost.GetBrowserUrl(services.HostArgs);
        var scriptPath = Path.Combine(services.Workspace.ExecutionDirectory, "scripts", "Invoke-DashboardBuildTestCycle.ps1");
        if (!File.Exists(scriptPath))
        {
            return Results.BadRequest($"dashboard build/test helper was not found: {scriptPath}");
        }

        Directory.CreateDirectory(services.Workspace.LogDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var runnerPath = Path.Combine(services.Workspace.LogDirectory, $"dashboard-build-test-cycle-{stamp}.ps1");
        var outputLogPath = Path.Combine(services.Workspace.LogDirectory, $"dashboard-build-test-cycle-{stamp}.out.log");
        var errorLogPath = Path.Combine(services.Workspace.LogDirectory, $"dashboard-build-test-cycle-{stamp}.err.log");
        var command = $".\\scripts\\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl {dashboardUrl}";

        File.WriteAllText(
            runnerPath,
            string.Join(
                Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                $"Set-Location -LiteralPath {QuotePowerShell(services.Workspace.ExecutionDirectory)}",
                $"& {QuotePowerShell(scriptPath)} -DashboardUrl {QuotePowerShell(dashboardUrl)} 1> {QuotePowerShell(outputLogPath)} 2> {QuotePowerShell(errorLogPath)}",
                "exit $LASTEXITCODE",
                string.Empty));

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Mcg.AgentOrchestrator.Infrastructure.WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = services.Workspace.ExecutionDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
        }

        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(runnerPath);

        var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dashboard build/test cycle process.");

        return Json(new DashboardBuildTestRunDto(
            process.Id,
            command,
            runnerPath,
            outputLogPath,
            errorLogPath,
            $"Dashboard build/test cycle started as PID {process.Id}. This dashboard will stop while the cycle runs and restart after tests finish."));
    }

    private static IResult StopDashboard(DashboardEndpointServices services)
    {
        var diagnostic = DashboardProcessInspector.InspectCurrent();
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            services.Lifetime.StopApplication();
        });

        return Json(new DashboardStopResultDto(
            Environment.ProcessId,
            diagnostic.CurrentListeningPorts,
            diagnostic.SiblingProcesses
                .Select(process => new DashboardStopSiblingDto(
                    process.ProcessId,
                    process.ListeningPorts,
                    process.SafeStopCommand))
                .ToList(),
            BuildDashboardRestartCommand(services),
            diagnostic.SiblingProcesses.Count == 0
                ? "Dashboard stop requested. Run build/test after the process exits, then restart with the provided command."
                : "Dashboard stop requested for the current PID. Sibling dashboard processes are still listed; stop only exact stale PIDs before build/test if locks remain."));
    }

    private static string BuildDashboardRestartCommand(DashboardEndpointServices services)
        => DashboardHost.BuildDashboardRestartCommand(services.HostArgs, services.Workspace);

    private static string QuotePowerShell(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
