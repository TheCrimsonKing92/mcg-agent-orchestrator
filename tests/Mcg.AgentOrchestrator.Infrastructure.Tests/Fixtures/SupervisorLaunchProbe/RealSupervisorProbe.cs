using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class RealSupervisorProbe
{
    internal static async Task<int> RunAsync(string root, string token, int adoptedRenewalSeconds,
        TimeSpan custodianWatchdog)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows supervisor fixture required.");
        if (adoptedRenewalSeconds <= 12 || adoptedRenewalSeconds > 120 ||
            custodianWatchdog <= TimeSpan.FromSeconds(adoptedRenewalSeconds) || custodianWatchdog > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(custodianWatchdog), "Fixture renewal and custodian budgets are invalid.");
        using var stdout = new StreamWriter(Path.Combine(root, "predecessor.out.log")) { AutoFlush = true };
        using var stderr = new StreamWriter(Path.Combine(root, "predecessor.err.log")) { AutoFlush = true };
        ConsoleCapture.Install(stdout, stderr);
        var storageReceipt = JsonSerializer.Serialize(new
        {
            buildRoot = DotnetBuildEnvironmentManager.CaptureStorageRoot().RootPath,
            tempRoot = Path.GetTempPath(),
            mtpResultsRoot = StorageRetentionMaintenance.DefaultMtpResultsRoot(),
            adoptedRenewalSeconds,
            custodianWatchdogSeconds = custodianWatchdog.TotalSeconds
        });
        Console.WriteLine(storageReceipt);
        File.WriteAllText(Path.Combine(root, "fixture-storage.json"), storageReceipt);
        using var handedOff = EventWaitHandle.OpenExisting(token + "-handoff");
        var stageA = Path.Combine(root, "stage-a");
        var stageB = Path.Combine(root, "stage-b");
        var marker = Path.Combine(stageA, "Mcg.AgentOrchestrator.App.dll.git-head");
        var commit = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "unknown";
        var continuity = Path.Combine(root, ".orchestrator", "continuity");
        Directory.CreateDirectory(continuity);
        var seam = new SystemConductorSupervisorHandoffSeam(continuity);
        var own = new ConductorSupervisorBuildIdentity(commit, stageA);
        var log = new ConductEventLogWriter(Path.Combine(root, ".orchestrator", "conduct-events.log"));
        // The initial child renews promptly to select stage B. Give the adopted
        // child its own renewal budget so activation/readiness do not consume
        // the entire interval in which the fixture must observe post-transfer progress.
        // Both children still run the candidate App via the production process host.
        var host = new SystemConductorSupervisorProcessHost(request =>
        {
            var prefix = request.CommandPrefix;
            if (prefix is not { Count: > 1 })
                throw new InvalidOperationException("Fixture child has no App command prefix.");
            var appDll = prefix[1];
            var initialApp = Path.Combine(stageA, "Mcg.AgentOrchestrator.App.dll");
            var adoptedApp = Path.Combine(stageB, "Mcg.AgentOrchestrator.App.dll");
            if (appDll != initialApp && appDll != adoptedApp)
                throw new InvalidOperationException("Unexpected fixture child build: " + appDll);
            var childArgs = request.Arguments.ToList();
            var durationIndex = childArgs.IndexOf("--max-duration");
            if (durationIndex < 0 || durationIndex + 1 >= childArgs.Count)
                throw new InvalidOperationException("Fixture child lacks its renewal budget.");
            var renewalSeconds = appDll == adoptedApp ? adoptedRenewalSeconds : 12;
            childArgs[durationIndex + 1] = renewalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine(JsonSerializer.Serialize(new { fixtureChildLaunch = true, appDll,
                renewalSeconds, request.StdoutPath, arguments = childArgs }));
            var info = new ProcessStartInfo(prefix[0]) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in prefix.Skip(1).Concat(childArgs)) info.ArgumentList.Add(argument);
            return info;
        });
        // Staging and per-child duration are fixture inputs; native supervisor launch,
        // readiness, lease transfer, pipe custody and inherited observation are production code.
        var supervisor = new ConductorContinuitySupervisor(host,
            new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db")),
            stageSuccessor: _ => new ConductorPreparedSuccessor(stageB,
                Path.Combine(stageB, "Mcg.AgentOrchestrator.App.dll"), commit, commit, "fixture=current-candidate"),
            appendConductEvent: (kind, goal, detail) =>
            {
                log.Append(kind, goal, detail);
                if (detail.StartsWith("SUPERVISOR_HANDOFF ", StringComparison.Ordinal))
                {
                    var receipt = JsonSerializer.Serialize(new { incumbent = seam.Self, own, detail,
                        candidateMvid = typeof(ConductorContinuitySupervisor).Module.ModuleVersionId });
                    var path = Path.Combine(root, "handoff.json");
                    File.WriteAllText(path + ".tmp", receipt);
                    File.Move(path + ".tmp", path);
                    handedOff.Set();
                }
            },
            supervisorHandoff: new ConductorSupervisorHandoffOptions(seam, own,
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50)),
            inspectStores: () => StoreSetupRunner.Plan(Path.Combine(root, ".orchestrator"), includeState: true));
        using var timeout = new CancellationTokenSource(custodianWatchdog);
        using var watchdogReceipt = timeout.Token.Register(() => Console.Error.WriteLine(
            JsonSerializer.Serialize(new { fixtureCustodianWatchdogExpired = true,
                incumbent = seam.Self, custodianWatchdogSeconds = custodianWatchdog.TotalSeconds })));
        try
        {
            return await supervisor.RunAsync(["conduct", "--loop", "--daemon", "--watch",
                "--poll-seconds", "1", "--max-duration", "12", "--quiet"],
                root, continuity, "default", "default", cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
