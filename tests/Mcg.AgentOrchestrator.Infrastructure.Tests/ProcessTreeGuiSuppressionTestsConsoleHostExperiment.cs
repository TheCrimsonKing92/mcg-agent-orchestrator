using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsConsoleHostExperiment(ITestOutputHelper output)
{
    [Fact]
    public async Task WindowlessConsoleArmsMeasureConhostsAndPreserveHiddenDescendantsAndOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launchesText = Environment.GetEnvironmentVariable("MCG_CONHOST_EXPERIMENT_LAUNCHES") ?? "4";
        Assert.True(int.TryParse(launchesText, NumberStyles.None, CultureInfo.InvariantCulture, out var launches) && launches > 0,
            "MCG_CONHOST_EXPERIMENT_LAUNCHES must be a positive integer.");
        var selectedArms = Environment.GetEnvironmentVariable("MCG_CONHOST_EXPERIMENT_ARMS") ?? "both";
        Assert.True(selectedArms is "both" or "off" or "inherit", "MCG_CONHOST_EXPERIMENT_ARMS must be both, off or inherit.");
        var reportPath = Environment.GetEnvironmentVariable("MCG_CONHOST_EXPERIMENT_REPORT");
        using var document = await ConsoleHostExperimentHarness.Run(launches, selectedArms, reportPath: reportPath, writeOutput: output.WriteLine);
        var report = document.RootElement;
        Assert.Equal("InheritWindowlessConsole", report.GetProperty("switchAtStartup").GetString());
        Assert.Equal(0, report.GetProperty("consoleWindow").GetInt64());
        Assert.True(report.GetProperty("consoleProcessCount").GetUInt32() > 0, report.GetRawText());
        var shell = report.GetProperty("powerShellExecutable").GetString()!;
        Assert.True(Path.IsPathFullyQualified(shell), report.GetRawText());
        Assert.Equal("pwsh.exe", Path.GetFileName(shell), ignoreCase: true);
        Assert.False(WorkerShell.IsWindowsAppsPath(shell), $"Contained launches require standalone PowerShell: {shell}");
        var arms = report.GetProperty("arms").EnumerateArray().ToArray();
        Assert.Equal(selectedArms == "both" ? 2 : 1, arms.Length);
        Assert.Equal(selectedArms == "inherit" ? "InheritWindowlessConsole" : "Off", arms[0].GetProperty("mode").GetString());
        if (arms.Length == 2) Assert.Equal("InheritWindowlessConsole", arms[1].GetProperty("mode").GetString());
        var off = arms.FirstOrDefault(arm => arm.GetProperty("mode").GetString() == "Off");
        foreach (var arm in arms)
        {
            foreach (var field in new[] { "launchCount", "totalConhosts", "exitCodes", "stdoutMatches", "shownWindowEvents",
                         "foregroundEvents", "codePageBefore", "codePageAfter", "hookInstalled", "desktop" })
                Assert.True(arm.TryGetProperty(field, out _), $"Missing {field}: {arm}");
            Assert.Equal(launches, arm.GetProperty("launchCount").GetInt32());
            Assert.True(arm.GetProperty("hookInstalled").GetBoolean());
            Assert.NotEmpty(arm.GetProperty("desktop").GetString()!);
            Assert.Empty(arm.GetProperty("hookErrors").EnumerateArray());
            Assert.Equal(0, arm.GetProperty("shownWindowEvents").GetInt32());
            Assert.Equal(0, arm.GetProperty("foregroundEvents").GetInt32());
            var isOff = arm.GetProperty("mode").GetString() == "Off";
            if (isOff) Assert.Equal(arm.GetProperty("codePageBefore").GetUInt32(), arm.GetProperty("codePageAfter").GetUInt32());
            var children = arm.GetProperty("children").EnumerateArray().ToArray();
            Assert.Equal(3 * launches, children.Length);
            foreach (var command in new[] { "git", "pwsh", "dotnet" })
            {
                var matching = children.Where(child => child.GetProperty("command").GetString() == command).ToArray();
                Assert.Equal(launches, matching.Length);
                foreach (var child in matching)
                {
                    CheckOutput(child);
                    CheckCensus(child, isOff ? 1 : 0);
                    if (off.ValueKind != JsonValueKind.Undefined)
                    {
                        var baseline = off.GetProperty("children").EnumerateArray().First(c => c.GetProperty("command").GetString() == command);
                        Assert.Equal(baseline.GetProperty("stdoutBase64").GetString(), child.GetProperty("stdoutBase64").GetString());
                    }
                }
            }
            var startChildren = arm.GetProperty("startChildren").EnumerateArray().ToArray();
            Assert.Equal(3, startChildren.Length);
            Assert.Equal(new[] { "dotnet", "git", "pwsh" }, startChildren.Select(c => c.GetProperty("command").GetString()).OrderBy(c => c).ToArray());
            foreach (var child in startChildren)
            {
                CheckOutput(child);
                var owned = children.First(c => c.GetProperty("command").GetString() == child.GetProperty("command").GetString());
                Assert.Equal(owned.GetProperty("stdoutBase64").GetString(), child.GetProperty("stdoutBase64").GetString());
            }
            var held = arm.GetProperty("heldProbe");
            Assert.Equal(0, held.GetProperty("exitCode").GetInt32());
            CheckCensus(held, isOff ? 1 : 0);
            var heldImages = held.GetProperty("heldImages").EnumerateArray().ToArray();
            Assert.Contains(heldImages, image => !image.GetProperty("isConhost").GetBoolean() && image.GetProperty("processId").GetInt32() == held.GetProperty("processId").GetInt32());
            Assert.Equal(isOff ? 1 : 0, heldImages.Count(image => image.GetProperty("isConhost").GetBoolean()));

            var descendant = arm.GetProperty("descendant");
            Assert.Equal(0, descendant.GetProperty("exitCode").GetInt32());
            CheckCensus(descendant, expectedConhosts: null);
            var states = descendant.GetProperty("stdout").GetString()!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, states.Length);
            foreach (var state in states)
            {
                using var console = JsonDocument.Parse(state);
                Assert.True(console.RootElement.GetProperty("hasConsole").GetBoolean());
                Assert.False(console.RootElement.GetProperty("consoleVisible").GetBoolean());
                Assert.Equal(ProcessTreeGuiSuppression.SuppressedErrorModeFlags,
                    console.RootElement.GetProperty("errorMode").GetUInt32() & ProcessTreeGuiSuppression.SuppressedErrorModeFlags);
            }
            var codePageChild = arm.GetProperty("codePageChild");
            Assert.Equal(0, codePageChild.GetProperty("exitCode").GetInt32());
            CheckCensus(codePageChild, expectedConhosts: null);
            Assert.Equal(children.Sum(c => c.GetProperty("conhostCount").GetInt32()) + held.GetProperty("conhostCount").GetInt32() +
                descendant.GetProperty("conhostCount").GetInt32() + codePageChild.GetProperty("conhostCount").GetInt32(), arm.GetProperty("totalConhosts").GetInt32());
            var exitCodes = arm.GetProperty("exitCodes").EnumerateArray().ToArray();
            Assert.Equal(3 * launches + 3, exitCodes.Length);
            Assert.All(exitCodes, code => Assert.Equal(0, code.GetInt32()));
            var matches = arm.GetProperty("stdoutMatches").EnumerateArray().ToArray();
            Assert.Equal(exitCodes.Length, matches.Length);
            if (off.ValueKind != JsonValueKind.Undefined)
                Assert.All(matches, match => Assert.True(match.GetProperty("matchesOff").GetBoolean()));
            else
                Assert.All(matches, match => Assert.Equal(JsonValueKind.Null, match.GetProperty("matchesOff").ValueKind));
        }
    }

    private static void CheckCensus(JsonElement child, int? expectedConhosts)
    {
        Assert.True(child.GetProperty("unclassifiedProcesses").GetInt32() == 0, $"Incomplete process census: {child}");
        var images = child.GetProperty("images").EnumerateArray().ToArray();
        Assert.Contains(images, image => image.GetProperty("processId").GetInt32() == child.GetProperty("processId").GetInt32()
            && !image.GetProperty("isConhost").GetBoolean());
        Assert.All(images, image =>
        {
            var extension = Path.GetExtension(image.GetProperty("path").GetString());
            Assert.True(string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase), $"Unexpected executable image: {image}");
        });
        Assert.Equal(child.GetProperty("totalProcesses").GetUInt32(), (uint)images.Length);
        Assert.Equal(images.Count(image => !image.GetProperty("isConhost").GetBoolean()), child.GetProperty("nonConhostProcesses").GetInt32());
        var conhosts = child.GetProperty("conhostCount").GetInt32();
        Assert.Equal(images.Count(image => image.GetProperty("isConhost").GetBoolean()), conhosts);
        if (expectedConhosts is { } expected) Assert.Equal(expected, conhosts);
    }

    private static void CheckOutput(JsonElement child)
    {
        Assert.Equal(0, child.GetProperty("exitCode").GetInt32());
        Assert.Equal("", child.GetProperty("stderr").GetString());
        var stdout = child.GetProperty("stdout").GetString()!;
        switch (child.GetProperty("command").GetString())
        {
            case "git": Assert.StartsWith("git version", stdout); break;
            case "pwsh": Assert.Equal("", stdout); break;
            case "dotnet": Assert.Matches(@"\A\d+\.\d+\.\d+[^\r\n]*\r?\n?\z", stdout); break;
            default: Assert.Fail("Unexpected representative command."); break;
        }
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(stdout), Convert.FromBase64String(child.GetProperty("stdoutBase64").GetString()!));
        _ = Convert.FromBase64String(child.GetProperty("stderrBase64").GetString()!);
    }
}

internal static class ConsoleHostExperimentHarness
{
    internal static async Task<JsonDocument> Run(int launches, string arms, bool startupOnly = false,
        string? reportPath = null, Action<string>? writeOutput = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null, bool ownConsoleCheck = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-conhost-test", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        Process? host = null;
        try
        {
            var path = Path.Combine(directory, "report.json");
            var info = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { ResolveProbeAssembly(), "--conhost-experiment", "--launches", launches.ToString(CultureInfo.InvariantCulture),
                         "--arms", arms, "--report", path, "--startup-only", startupOnly.ToString().ToLowerInvariant(), "--work-dir", directory })
                info.ArgumentList.Add(arg);
            info.Environment.Remove("DOTNET_STARTUP_HOOKS");
            info.Environment.Remove(ChildConsoleLaunchPolicy.OffSwitchVariable);
            if (environmentOverrides is not null)
                foreach (var entry in environmentOverrides) info.Environment[entry.Key] = entry.Value;
            info.ArgumentList.Add("--own-console-check");
            info.ArgumentList.Add(ownConsoleCheck.ToString().ToLowerInvariant());
            // Explicit CreateNoWindow gives this fixture its own windowless console even in an interactive test run.
            using (ProcessTreeGuiSuppression.AcquireErrorModeForChildSpawn())
                host = Process.Start(info) ?? throw new InvalidOperationException("Measurement host did not start.");
            host.StandardInput.Close();
            var stdout = host.StandardOutput.ReadToEndAsync();
            var stderr = host.StandardError.ReadToEndAsync();
            // Bounds a missing host exit, not a performance assertion. Larger operator samples need a larger guard.
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            guard.CancelAfter(TimeSpan.FromSeconds(Math.Max(120L, launches * 30L)));
            await host.WaitForExitAsync(guard.Token);
            var output = await stdout.WaitAsync(guard.Token);
            var errors = await stderr.WaitAsync(guard.Token);
            Assert.True(File.Exists(path), $"Measurement report missing: exit={host.ExitCode} stdout={output} stderr={errors}");
            var json = await File.ReadAllTextAsync(path, guard.Token);
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                var destination = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, json, guard.Token);
            }
            writeOutput?.Invoke(json);
            Assert.True(host.ExitCode == 0, $"Measurement host failed: exit={host.ExitCode} stdout={output} stderr={errors} report={json}");
            var report = JsonDocument.Parse(json);
            if (report.RootElement.GetProperty("error").ValueKind != JsonValueKind.Null)
            {
                var error = report.RootElement.GetProperty("error").GetString();
                report.Dispose();
                Assert.Fail(error);
            }
            return report;
        }
        finally
        {
            if (host is not null)
            {
                try { if (!host.HasExited) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); } }
                finally { host.Dispose(); }
            }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static string ResolveProbeAssembly()
    {
        const string name = "Mcg.AgentOrchestrator.ConsoleIoProbe.dll";
        var output = new DirectoryInfo(Path.GetDirectoryName(typeof(ProcessTreeGuiSuppressionTestsConsoleHostExperiment).Assembly.Location)!);
        var parent = output.Parent;
        var candidates = new List<string> { Path.Combine(output.FullName, name) };
        if (parent?.Parent?.Parent is { } project)
            candidates.Add(Path.Combine(project.FullName, "Fixtures", "ConsoleIoProbe", "bin", parent.Name, output.Name, name));
        if (parent?.Parent is { } artifacts)
            candidates.Add(Path.Combine(artifacts.FullName, "Mcg.AgentOrchestrator.ConsoleIoProbe", output.Name, name));
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"Console I/O probe was not built: {string.Join("; ", candidates)}");
    }
}
