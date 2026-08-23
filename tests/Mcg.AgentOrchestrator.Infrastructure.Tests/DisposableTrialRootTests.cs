using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class DisposableTrialRootTests(Xunit.ITestOutputHelper output)
{
    public static bool IsWindowsAtMediumOrHigher =>
        DisposableTrialRootNegativeControlTests.IsWindowsAtMediumOrHigher;

    [Xunit.Fact]
    public void CreateProducesStandaloneCloneAndRootLocalStateThenDestroyReportsClean()
    {
        var fixture = CreateFixtureRepository();
        try
        {
            var trialFactory = new DisposableTrialRoot();
            var lease = trialFactory.Create(new TrialRootRequest(
                fixture.Source,
                fixture.Commit,
                fixture.Trials,
                "standalone"));

            Xunit.Assert.True(Directory.Exists(lease.GitPath));
            Xunit.Assert.False(File.Exists(lease.GitPath));
            Xunit.Assert.Equal(fixture.Commit, lease.ResolvedBaseCommit, ignoreCase: true);
            Xunit.Assert.Equal(fixture.Commit, RunGit(lease.RootPath, "rev-parse", "HEAD").Trim(), ignoreCase: true);
            Xunit.Assert.Empty(RunGit(lease.RootPath, "remote").Trim());

            var commonDir = RunGit(lease.RootPath, "rev-parse", "--git-common-dir").Trim();
            var resolvedCommonDir = Path.GetFullPath(Path.Combine(lease.RootPath, commonDir));
            Xunit.Assert.True(TrialRootEnvironment.IsBelow(lease.RootPath, resolvedCommonDir));
            Xunit.Assert.False(File.Exists(Path.Combine(lease.GitPath, "objects", "info", "alternates")));

            foreach (var variable in TrialRootEnvironment.RootLocalVariables)
            {
                var value = Xunit.Assert.IsType<string>(lease.ChildEnvironment[variable]);
                Xunit.Assert.True(
                    TrialRootEnvironment.IsBelow(lease.RootPath, value),
                    $"{variable} escaped the trial root: {value}");
                Xunit.Assert.True(Directory.Exists(value), $"{variable} directory was not created: {value}");
            }

            var report = lease.Destroy();
            Xunit.Assert.True(report.RootRemoved);
            Xunit.Assert.Empty(report.SurvivingProcessIds);
            Xunit.Assert.Empty(report.OutsideWrites);
            Xunit.Assert.True(report.Clean);
            Xunit.Assert.True(lease.CreateDuration > TimeSpan.Zero);
            Xunit.Assert.True(report.DestroyDuration > TimeSpan.Zero);
            Xunit.Assert.True(File.Exists(report.ReceiptPath));
            Xunit.Assert.Same(report, lease.Destroy());
            output.WriteLine(
                $"create_ms={lease.CreateDuration.TotalMilliseconds:F1} destroy_ms={report.DestroyDuration.TotalMilliseconds:F1}");
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact(
        Skip = "Requires Windows at Medium integrity or above.",
        SkipUnless = nameof(IsWindowsAtMediumOrHigher))]
    public void ChildWritesThroughEveryRootLocalEnvironmentVariable()
    {
        var fixture = CreateFixtureRepository();
        try
        {
            using var lease = new DisposableTrialRoot().Create(new TrialRootRequest(
                fixture.Source,
                fixture.Commit,
                fixture.Trials,
                "child-environment"));
            var variableNames = string.Join(
                ",",
                TrialRootEnvironment.RootLocalVariables.Select(name => $"'{name}'"));
            var script =
                $"$names=@({variableNames}); " +
                "foreach ($name in $names) { " +
                "$value=[Environment]::GetEnvironmentVariable($name); " +
                "if ([string]::IsNullOrWhiteSpace($value)) { Write-Error \"missing:$name\"; exit 10 }; " +
                "$marker=Join-Path $value (\"child-$name.marker\"); " +
                "Set-Content -LiteralPath $marker -Value $name -ErrorAction Stop }; exit 0";
            using var process = lease.Start(PowerShellCommand(script));

            Xunit.Assert.True(process.Process.WaitForExit(15_000), "Trial environment writer did not exit.");
            Xunit.Assert.Equal(0, process.Process.ExitCode);
            foreach (var variable in TrialRootEnvironment.RootLocalVariables)
            {
                var directory = Xunit.Assert.IsType<string>(lease.ChildEnvironment[variable]);
                var marker = Path.Combine(directory, $"child-{variable}.marker");
                Xunit.Assert.True(File.Exists(marker), $"Child did not write through {variable}: {marker}");
                Xunit.Assert.True(
                    TrialRootEnvironment.IsBelow(lease.RootPath, marker),
                    $"Child marker escaped the trial root: {marker}");
            }

            var report = lease.Destroy();
            Xunit.Assert.True(report.Clean);
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact]
    public void CreateDestroyCanRepeatThreeTimesAndRecordsEachCycleCost()
    {
        var fixture = CreateFixtureRepository();
        try
        {
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                using var lease = new DisposableTrialRoot().Create(new TrialRootRequest(
                    fixture.Source,
                    fixture.Commit,
                    fixture.Trials,
                    $"repeat-{cycle}"));
                var report = lease.Destroy();
                Xunit.Assert.True(report.Clean);
                output.WriteLine(
                    $"cycle={cycle} create_ms={lease.CreateDuration.TotalMilliseconds:F1} destroy_ms={report.DestroyDuration.TotalMilliseconds:F1}");
            }
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact]
    public void UnresolvableRevisionFailsBeforeCreatingTrialDirectory()
    {
        var fixture = CreateFixtureRepository();
        try
        {
            var before = Directory.Exists(fixture.Trials)
                ? Directory.GetFileSystemEntries(fixture.Trials)
                : [];

            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                new DisposableTrialRoot().Create(new TrialRootRequest(
                    fixture.Source,
                    "definitely-not-a-revision",
                    fixture.Trials,
                    "bad-revision")));

            Xunit.Assert.Contains("unknown revision", error.Message, StringComparison.OrdinalIgnoreCase);
            var after = Directory.Exists(fixture.Trials)
                ? Directory.GetFileSystemEntries(fixture.Trials)
                : [];
            Xunit.Assert.Equal(before, after);
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    private static (string Root, string Source, string Trials, string Commit) CreateFixtureRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-trial-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var trials = Path.Combine(root, "trials");
        Directory.CreateDirectory(source);
        RunGit(source, "init", "--initial-branch=main");
        RunGit(source, "config", "user.name", "Trial Root Test");
        RunGit(source, "config", "user.email", "trial-root@example.invalid");
        File.WriteAllText(Path.Combine(source, "tracked.txt"), "baseline");
        RunGit(source, "add", "tracked.txt");
        RunGit(source, "commit", "-m", "fixture");
        return (root, source, trials, RunGit(source, "rev-parse", "HEAD").Trim());
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        Xunit.Assert.True(result.Succeeded, result.Error);
        return result.Output;
    }

    private static ProcessStartInfo PowerShellCommand(string script)
    {
        var command = new ProcessStartInfo { FileName = "powershell.exe" };
        command.ArgumentList.Add("-NoLogo");
        command.ArgumentList.Add("-NoProfile");
        command.ArgumentList.Add("-NonInteractive");
        command.ArgumentList.Add("-Command");
        command.ArgumentList.Add(script);
        return command;
    }

    private static void DeleteFixture(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
