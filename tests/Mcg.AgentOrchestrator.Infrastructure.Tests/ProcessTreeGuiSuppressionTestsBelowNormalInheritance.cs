using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsBelowNormalInheritance
{
    [Fact]
    public async Task OwnedGrandchildInheritsBelowNormalWhileUnchangedLaunchInheritsHostClass()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var host = Process.GetCurrentProcess();
        await CheckInheritance(lowerPriority: true, ProcessPriorityClass.BelowNormal);
        if (host.PriorityClass == ProcessPriorityClass.Normal)
        {
            await CheckInheritance(lowerPriority: false, ProcessPriorityClass.Normal);
        }
    }

    private static async Task CheckInheritance(bool lowerPriority, ProcessPriorityClass expectedClass)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-priority-probe", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        Process? parent = null;
        Process? grandchild = null;
        try
        {
            var assembly = ResolveProbeAssembly();
            var receiptPath = Path.Combine(directory, "priority.json");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(assembly);
            startInfo.ArgumentList.Add("--priority-grandchild");
            startInfo.ArgumentList.Add(lowerPriority.ToString());
            startInfo.ArgumentList.Add(receiptPath);
            parent = ProcessTreeGuiSuppression.Start(startInfo)
                ?? throw new InvalidOperationException("The priority probe did not start.");
            var stdout = parent.StandardOutput.ReadToEndAsync();
            var stderr = parent.StandardError.ReadToEndAsync();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!File.Exists(receiptPath) && !parent.HasExited && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(receiptPath),
                $"Priority probe did not publish readiness. exit={parent.HasExited}");
            using var document = JsonDocument.Parse(File.ReadAllText(receiptPath));
            var receipt = document.RootElement;
            grandchild = Process.GetProcessById(receipt.GetProperty("GrandchildPid").GetInt32());
            Assert.Equal(expectedClass.ToString(), receipt.GetProperty("ParentClass").GetString());
            Assert.Equal(expectedClass.ToString(), receipt.GetProperty("GrandchildClass").GetString());
            Assert.Equal(expectedClass, grandchild.PriorityClass);

            parent.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await parent.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, parent.ExitCode);
            _ = await stdout;
            _ = await stderr;
        }
        finally
        {
            if (parent is not null)
            {
                try { parent.StandardInput.Close(); } catch { }
                try { if (!parent.HasExited) parent.WaitForExit(5000); } catch { }
                try { if (!parent.HasExited) parent.Kill(entireProcessTree: true); } catch { }
                parent.Dispose();
            }
            if (grandchild is not null)
            {
                try { if (!grandchild.HasExited) grandchild.Kill(entireProcessTree: true); } catch { }
                grandchild.Dispose();
            }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static string ResolveProbeAssembly()
    {
        const string name = "Mcg.AgentOrchestrator.ConsoleIoProbe.dll";
        var outputDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(ProcessTreeGuiSuppressionTestsBelowNormalInheritance).Assembly.Location)!);
        var parent = outputDirectory.Parent;
        var candidates = new List<string> { Path.Combine(outputDirectory.FullName, name) };
        if (parent?.Parent?.Parent is { } projectDirectory)
        {
            candidates.Add(Path.Combine(projectDirectory.FullName, "Fixtures", "ConsoleIoProbe", "bin",
                parent.Name, outputDirectory.Name, name));
        }
        if (parent?.Parent is { } artifactsDirectory)
        {
            candidates.Add(Path.Combine(artifactsDirectory.FullName, "Mcg.AgentOrchestrator.ConsoleIoProbe",
                outputDirectory.Name, name));
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Console I/O probe was not built: {string.Join("; ", candidates)}");
    }
}
