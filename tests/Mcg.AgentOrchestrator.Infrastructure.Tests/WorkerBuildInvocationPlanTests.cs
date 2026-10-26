using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each case owns its files and sets environment only on its child.
public sealed class WorkerBuildInvocationPlanTests
{
    [Xunit.Fact]
    public void SingleProject_PreservesDirectArgumentsAndClosure()
    {
        WithPlan(single: true, maxCpuCount: null, (plan, projects, artifacts) =>
        {
            Assert.Equal("direct", plan.GetProperty("Mode").GetString());
            var arguments = Invocation(plan);
            Assert.Equal("build", arguments[0]);
            Assert.Equal(projects[1], arguments[1]);
            AssertShared(arguments, artifacts, 1);
            Assert.DoesNotContain("-nodeReuse:false", arguments);
            Assert.Equal(1, plan.GetProperty("SeparateEvaluations").GetInt32());
            Assert.Equal(plan.GetProperty("SeparateEvaluations").GetInt32(),
                plan.GetProperty("TraversalEvaluations").GetInt32());
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, 1)]
    [Xunit.InlineData("3", 3)]
    public void SharedReferences_PlanOneTraversalWithDistinctClosure(string? cpu, int expected)
    {
        WithPlan(single: false, maxCpuCount: cpu, (plan, projects, artifacts) =>
        {
            Assert.Equal("traversal", plan.GetProperty("Mode").GetString());
            Assert.Equal(8, plan.GetProperty("SeparateEvaluations").GetInt32());
            Assert.Equal(4, plan.GetProperty("TraversalEvaluations").GetInt32());
            var arguments = Invocation(plan);
            Assert.Equal("build", arguments[0]);
            Assert.DoesNotContain(arguments[1], projects);
            Assert.Equal(plan.GetProperty("TraversalPath").GetString(), arguments[1]);
            AssertShared(arguments, artifacts, expected);
            Assert.Contains("-nodeReuse:false", arguments);
            Assert.Equal("Debug", arguments[Array.IndexOf(arguments, "--configuration") + 1]);
            Assert.DoesNotContain("--no-restore", arguments);
            var traversal = XDocument.Load(arguments[1]);
            Assert.Equal(projects, traversal.Descendants("ProjectReference")
                .Select(item => Uri.UnescapeDataString(item.Attribute("Include")!.Value)).ToArray());
        });
    }

    [Xunit.Fact]
    public void DirectAndTraversalPlans_DisableAnalyzers()
    {
        foreach (var single in new[] { true, false })
            WithPlan(single, maxCpuCount: null, (plan, _, _) =>
            {
                foreach (var property in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
                    Assert.Contains(property, Invocation(plan));
            });
    }

    private static string[] Invocation(JsonElement plan)
    {
        var invocations = plan.GetProperty("Invocations").EnumerateArray().ToArray();
        Assert.Single(invocations);
        return invocations[0].EnumerateArray().Select(value => value.GetString()!).ToArray();
    }

    private static void AssertShared(string[] arguments, string artifacts, int cpu)
    {
        Assert.Contains("--artifacts-path", arguments);
        Assert.Equal(artifacts, arguments[Array.IndexOf(arguments, "--artifacts-path") + 1]);
        Assert.Contains($"-maxcpucount:{cpu}", arguments);
        Assert.Contains("-p:BuildInParallel=false", arguments);
        Assert.Contains("-clp:ErrorsOnly", arguments);
        Assert.Contains("-tl:off", arguments);
    }

    private static void WithPlan(bool single, string? maxCpuCount,
        Action<JsonElement, string[], string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "worker-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Core.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "Providers.csproj"),
                "<Project><ItemGroup><ProjectReference Include=\"Core.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Execution.csproj"),
                "<Project><ItemGroup><ProjectReference Include=\"Core.csproj\" /><ProjectReference Include=\"Providers.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project><ItemGroup><ProjectReference Include=\"Core.csproj\" /><ProjectReference Include=\"Providers.csproj\" /><ProjectReference Include=\"Execution.csproj\" /></ItemGroup></Project>");
            var driver = Path.Combine(root, "plan.ps1");
            File.WriteAllText(driver, """
                param($Module, $Root, $Single)
                $ErrorActionPreference = 'Stop'
                Import-Module $Module -Force
                $projects = if ($Single -eq 'True') { @('Core.csproj') } else { @('App.csproj', 'Core.csproj', 'Execution.csproj') }
                Get-WorkerBuildInvocationPlan -Root $Root -Projects $projects -Configuration Debug -ArtifactsPath (Join-Path $Root 'artifacts') -TraversalDirectory (Join-Path $Root 'traversal') | ConvertTo-Json -Depth 6 -Compress
                """);
            var info = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME", "DOTNET_ROOT", "ProgramFiles", "PSModulePath" })
                if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
            if (maxCpuCount is not null) info.Environment["MCG_BUILD_MAXCPUCOUNT"] = maxCpuCount;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", driver, Path.Combine(RepositoryRoot(), "scripts", "WorkerBuildReceipt.psm1"), root, single.ToString() })
                info.ArgumentList.Add(argument);
            var result = TestChildProcessCapture.Run(info, hangBound: TimeSpan.FromSeconds(60));
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            using var document = JsonDocument.Parse(result.Stdout);
            assert(document.RootElement,
                new[] { "App.csproj", "Core.csproj", "Execution.csproj" }.Select(name => Path.Combine(root, name)).ToArray(),
                Path.Combine(root, "artifacts"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root missing.");
    }
}
