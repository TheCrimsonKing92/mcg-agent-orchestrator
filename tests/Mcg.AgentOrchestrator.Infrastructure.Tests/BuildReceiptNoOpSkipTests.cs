using System.Diagnostics;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owns an external temp tree and artifacts, clears child environments and never operates on shared processes.
public sealed class BuildReceiptNoOpSkipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "receipt-no-op", Guid.NewGuid().ToString("N"));
    private readonly string _repository = VerifiedRepositoryRoot.Find();

    [Fact]
    public void NoOpPreservesReceiptWhileSourceAndClosureChangesRefreshIt()
    {
        var tree = Path.Combine(_root, "tree");
        Assert.False(Path.GetFullPath(tree).StartsWith(
            Path.TrimEndingDirectorySeparator(_repository) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var project = Path.Combine(tree, "Fixture", "Fixture.csproj");
        var source = Path.Combine(tree, "Fixture", "Value.cs");
        Directory.CreateDirectory(Path.Combine(tree, "Fixture", "content"));
        File.Copy(Path.Combine(_repository, "Directory.Build.targets"), Path.Combine(tree, "Directory.Build.targets"));
        File.WriteAllText(Path.Combine(tree, "Directory.Build.props"),
            "<Project><PropertyGroup><UseSharedCompilation>false</UseSharedCompilation><Deterministic>true</Deterministic></PropertyGroup></Project>");
        File.WriteAllText(project,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><None Update="content\**" CopyToOutputDirectory="PreserveNewest" /></ItemGroup></Project>""");
        File.WriteAllText(source, "public static class FixtureValue { public static int Read() => 1; }");
        File.WriteAllText(Path.Combine(tree, "Fixture", "content", "a.txt"), "a");
        var environment = EnvironmentFor();

        Build(tree, project, environment);
        var assembly = Directory.EnumerateFiles(Path.Combine(environment.ArtifactsPath, "bin", "Fixture"),
            "Fixture.dll", SearchOption.AllDirectories).Single();
        var directory = Path.GetDirectoryName(assembly)!;
        var receipt = Path.Combine(directory, ".mcg-build-receipt.txt");
        Assert.True(File.Exists(receipt));
        var firstBytes = File.ReadAllBytes(receipt);
        var firstWrite = File.GetLastWriteTimeUtc(receipt);
        var firstAssemblyHash = ReceiptAssemblyHash(receipt);

        Build(tree, project, environment);
        Assert.Equal(firstBytes, File.ReadAllBytes(receipt));
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(receipt));
        ValidateReceipt(tree, project, directory, environment, expectValid: true);

        File.WriteAllText(source, "public static class FixtureValue { public static int Read() => 2; }");
        Build(tree, project, environment);
        Assert.NotEqual(firstWrite, File.GetLastWriteTimeUtc(receipt));
        var changedAssemblyHash = ReceiptAssemblyHash(receipt);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))), changedAssemblyHash, ignoreCase: true);
        Assert.False(string.Equals(firstAssemblyHash, changedAssemblyHash, StringComparison.OrdinalIgnoreCase));
        ValidateReceipt(tree, project, directory, environment, expectValid: true);

        var addedContent = Path.Combine(tree, "Fixture", "content", "b.txt");
        File.WriteAllText(addedContent, "b");
        File.SetLastWriteTimeUtc(addedContent, File.GetLastWriteTimeUtc(receipt).AddDays(-2));
        Build(tree, project, environment);
        var copiedContent = Path.Combine(directory, "content", "b.txt");
        Assert.True(File.Exists(copiedContent));
        Assert.Contains(File.ReadAllLines(receipt), line =>
            line.StartsWith($"F\tclosure\t{copiedContent}\t", StringComparison.OrdinalIgnoreCase));
        ValidateReceipt(tree, project, directory, environment, expectValid: true);

        File.AppendAllText(source, "\n// edited after build");
        var stale = ValidateReceipt(tree, project, directory, environment, expectValid: false);
        Assert.Contains("receipt source checksum mismatch", stale.Stdout);
    }

    private DotnetBuildEnvironment EnvironmentFor()
    {
        var owned = Path.Combine(_root, "owned");
        var artifacts = Path.Combine(owned, "artifacts");
        return new("receipt-no-op", owned, artifacts, Path.Combine(owned, "lock"),
            ["--artifacts-path", artifacts, "-maxcpucount:1", "-p:UseSharedCompilation=false", "-nodeReuse:false"], "fixture");
    }

    private static void Build(string tree, string project, DotnetBuildEnvironment environment)
    {
        var result = Run(tree, environment,
            ["dotnet", "build", project, "--configuration", "Debug", "--verbosity", "minimal", .. environment.Arguments]);
        Assert.True(result.ExitCode == 0, $"Fixture build exited {result.ExitCode}:\n{result.Stdout}\n{result.Stderr}");
    }

    private TestChildProcessCapture.Result ValidateReceipt(
        string tree, string project, string directory, DotnetBuildEnvironment environment, bool expectValid)
    {
        var script = Path.Combine(_root, "receipt-check.ps1");
        File.WriteAllText(script, """
            param($Module, $Directory, $Root, $Project)
            $ErrorActionPreference = 'Stop'
            Import-Module $Module -Force
            try {
                $null = Assert-MtpBuildReceipt -Directory $Directory -ManagedAssemblyLeaf 'Fixture.dll' -RepositoryRoot $Root -ProjectPath $Project -Configuration Debug -TargetFramework net10.0 -RuntimeIdentifier '' -ExpectedSourcePaths @((Join-Path (Split-Path $Project) 'Value.cs'))
                Write-Output 'RECEIPT_VALID'
            } catch { Write-Output $_.Exception.Message; exit 24 }
            """);
        var result = Run(tree, environment, [WorkerShell.Executable, "-NoProfile", "-NonInteractive", "-File", script,
            Path.Combine(_repository, "scripts", "MtpTestRunner.psm1"), directory, tree, project]);
        Assert.True(result.ExitCode == (expectValid ? 0 : 24),
            $"Receipt check exited {result.ExitCode}:\n{result.Stdout}\n{result.Stderr}");
        if (expectValid) Assert.Contains("RECEIPT_VALID", result.Stdout);
        return result;
    }

    private static string ReceiptAssemblyHash(string receipt) =>
        File.ReadAllLines(receipt).Single(line => line.StartsWith("K\tassemblySha256\t", StringComparison.Ordinal)).Split('\t')[2];

    private static Dictionary<string, string> ProcessEnvironment(string tree, DotnetBuildEnvironment environment)
    {
        var values = new ProcessStartInfo().Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(values, tree, environment.RootPath);
        values["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        return values.ToDictionary(pair => pair.Key, pair => pair.Value ?? "");
    }

    private static TestChildProcessCapture.Result Run(string tree, DotnetBuildEnvironment environment, string[] args)
    {
        var start = new ProcessStartInfo(args[0]) { WorkingDirectory = tree, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args.Skip(1)) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var pair in ProcessEnvironment(tree, environment)) start.Environment[pair.Key] = pair.Value;
        return TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(3));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
