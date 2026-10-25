using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using ManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// DotnetBuildSlots owns real builds; each fact owns its fixture and child environment.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsAnalyzerAuthority
{
    [Xunit.Fact]
    public void GateBuild_AnalyzerErrorBlocksUntilAnalyzersAreDisabled()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "analyzer-authority-" + Guid.NewGuid().ToString("N")));
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(VerifiedRepositoryRoot.Find()));
        Assert.False(root.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The analyzer fixture must build outside the repository tree.");
        // Keep both artifacts folders outside the project's source glob.
        var projectRoot = Path.Combine(root, "project");
        Directory.CreateDirectory(projectRoot);
        try
        {
            var project = Path.Combine(projectRoot, "Fixture.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AnalysisLevel>latest</AnalysisLevel><AnalysisMode>Recommended</AnalysisMode><EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild></PropertyGroup></Project>
                """);
            File.WriteAllText(Path.Combine(projectRoot, ".editorconfig"),
                "root = true\n[*.cs]\ndotnet_diagnostic.CA1050.severity = error\n");
            File.WriteAllText(Path.Combine(projectRoot, "GlobalType.cs"), "public class GlobalType { }");
            File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.targets"), "<Project />");
            var phase = AcceptanceDotnetBuildPhase.Create(projectRoot,
                new ManifestCheck[] { new() { Type = "dotnet-test", Project = project } },
                null, PolicyShardPlan.NotApplicable("analyzer negative control"), _ => null);
            Assert.Equal("dotnet", phase.BuildArguments[0]);
            Assert.Equal("build", phase.BuildArguments[1]);

            var gate = Build(phase, projectRoot, Path.Combine(root, "artifacts-gate"));
            Assert.True(gate.ExitCode != 0, gate.Stdout + gate.Stderr);
            Assert.Contains("error CA1050", gate.Stdout + gate.Stderr, StringComparison.Ordinal);

            var withoutAnalyzers = Build(AcceptanceDotnetBuildPhase.WithoutAnalyzers(phase),
                projectRoot, Path.Combine(root, "artifacts-off"));
            Assert.True(withoutAnalyzers.ExitCode == 0, withoutAnalyzers.Stdout + withoutAnalyzers.Stderr);
            Assert.DoesNotContain("CA1050", withoutAnalyzers.Stdout + withoutAnalyzers.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TestChildProcessCapture.Result Build(
        DotnetTestBuildPhase phase, string projectRoot, string artifacts)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "PATHEXT", "USERPROFILE", "LOCALAPPDATA",
            "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "NUGET_PACKAGES" })
            if (Environment.GetEnvironmentVariable(key) is { } value)
                info.Environment[key] = value;
        var temporary = Path.Combine(Path.GetDirectoryName(projectRoot)!, "temp");
        Directory.CreateDirectory(temporary);
        info.Environment["TEMP"] = temporary;
        info.Environment["TMP"] = temporary;
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        foreach (var argument in phase.BuildArguments.Skip(1)
            .Concat(["--artifacts-path", artifacts, "-p:UseSharedCompilation=false"]))
            info.ArgumentList.Add(argument);
        return TestChildProcessCapture.Run(info, hangBound: TimeSpan.FromMinutes(5));
    }
}
