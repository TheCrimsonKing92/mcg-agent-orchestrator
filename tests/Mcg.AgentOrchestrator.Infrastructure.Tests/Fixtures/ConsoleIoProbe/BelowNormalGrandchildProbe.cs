using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class BelowNormalGrandchildProbe
{
    internal static async Task<int> Run(string[] args)
    {
        var lowerPriority = bool.Parse(args[0]);
        var receiptPath = args[1];
        if (lowerPriority && !ProcessTreeGuiSuppression.TryLowerCurrentProcessToBelowNormal())
        {
            throw new InvalidOperationException("The priority probe could not lower its process class.");
        }

        var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR")
            ?? throw new InvalidOperationException("WINDIR was not set.");
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(windowsDirectory, "System32", "ping.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("121");
        startInfo.ArgumentList.Add("127.0.0.1");

        using var child = WorkerProcessJobs.StartRegisteredOwnedOrThrow(startInfo, "priority-inheritance-probe");
        try
        {
            using var current = Process.GetCurrentProcess();
            using var grandchild = Process.GetProcessById(child.Id);
            var temporaryPath = receiptPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new
            {
                ParentClass = current.PriorityClass.ToString(),
                GrandchildPid = child.Id,
                GrandchildClass = grandchild.PriorityClass.ToString()
            }));
            File.Move(temporaryPath, receiptPath, overwrite: true);
            _ = Console.ReadLine();
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await child.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
        }

        return 0;
    }
}
