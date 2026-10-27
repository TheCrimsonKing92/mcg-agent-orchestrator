using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// The one place the CLI shapes .NET build and MTP test command lines for the stable-slot runner.
internal static class CliStableSlotDotnetCommand
{
    internal static void Run(IReadOnlyList<string> parts, CliExecutionContext context)
    {
        if (parts.Count < 2)
        {
            throw new ArgumentException("Usage: stable-slot-dotnet <dotnet-arguments>");
        }

        // One root owns this command's slot lease and its run cleanup: acquiring under the configured
        // root and cleaning up under the ambient one would leave the run directory behind.
        var storageRoot = context.CleanupHooks.BuildStorageRoot;
        using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            storageRoot: storageRoot);
        if (parts[1].Equals("mtp-test", StringComparison.OrdinalIgnoreCase))
        {
            var resultsSurviveCleanup = RunMtpTest(parts, context, lease);
            if (resultsSurviveCleanup)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(lease.Environment, storageRoot);
            }

            return;
        }

        var exitCode = RunProcess(
            "dotnet",
            [.. parts.Skip(1), .. lease.Environment.Arguments],
            context.Workspace.RootDirectory,
            configureDotnetEnvironment: true);
        if (exitCode != 0)
        {
            throw new CliExitException(exitCode);
        }

        lease.ReleaseExecutionLock();
        DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(lease.Environment, storageRoot);
    }

    private static bool RunMtpTest(
        IReadOnlyList<string> parts,
        CliExecutionContext context,
        DotnetBuildEnvironmentLease buildLease)
    {
        var environment = buildLease.Environment;
        if (parts.Count < 3)
        {
            throw new ArgumentException("Usage: stable-slot-dotnet mtp-test <project> [--filter <filter>] [--results-directory <path>] [--no-build]");
        }

        var project = Path.GetFullPath(parts[2], context.Workspace.RootDirectory);
        var noBuild = parts.Any(part => part.Equals("--no-build", StringComparison.OrdinalIgnoreCase));
        var projectName = Path.GetFileNameWithoutExtension(project);
        var executable = Path.Combine(
            environment.ArtifactsPath,
            "bin",
            projectName,
            "debug",
            $"{projectName}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
        var managedAssembly = Path.Combine(
            environment.ArtifactsPath,
            "bin",
            projectName,
            "debug",
            $"{projectName}.dll");
        if (ShouldBuildMtpProject(
                noBuild,
                File.Exists(executable) && File.Exists(managedAssembly)))
        {
            var buildExit = RunProcess(
                "dotnet",
                ["build", project, "--nologo", "-v", "quiet", "-clp:ErrorsOnly", .. environment.Arguments],
                context.Workspace.RootDirectory,
                configureDotnetEnvironment: true);
            if (buildExit != 0)
            {
                throw new CliExitException(buildExit);
            }
        }

        if (!File.Exists(executable))
        {
            throw new InvalidOperationException($"MTP test executable was not produced: {executable}");
        }

        if (!File.Exists(managedAssembly))
        {
            throw new InvalidOperationException($"MTP test managed assembly was not produced: {managedAssembly}");
        }

        buildLease.ReleaseExecutionLock();
        var configuredResultsDirectory = ReadOption(parts, "--results-directory");
        var resultsDirectory = ResolveResultsDirectory(
            configuredResultsDirectory,
            context.Workspace.RootDirectory,
            Path.Combine(environment.ArtifactsPath, "TestResults"));
        Directory.CreateDirectory(resultsDirectory);
        var mtpArguments = new List<string>
        {
            "--no-ansi",
            "--progress",
            "off",
            "--results-directory",
            resultsDirectory,
            "--report-trx",
            "--report-trx-filename",
            $"{projectName}.trx"
        };
        var filter = ReadOption(parts, "--filter");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            mtpArguments.AddRange(TranslateMtpFilter(filter));
        }

        var testExit = RunProcess(
            "dotnet",
            [managedAssembly, .. mtpArguments],
            context.Workspace.RootDirectory,
            configureDotnetEnvironment: false);
        if (testExit != 0)
        {
            throw new CliExitException(testExit);
        }

        return configuredResultsDirectory is not null &&
            !Path.GetFullPath(resultsDirectory)
                .StartsWith(
                    Path.GetFullPath(environment.RootPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldBuildMtpProject(bool noBuild, bool artifactsExist) =>
        !noBuild || !artifactsExist;

    internal static string ResolveResultsDirectory(
        string? configuredResultsDirectory,
        string workspaceRoot,
        string defaultResultsDirectory) =>
        configuredResultsDirectory is null
            ? Path.GetFullPath(defaultResultsDirectory)
            : Path.GetFullPath(configuredResultsDirectory, Path.GetFullPath(workspaceRoot));

    private static string? ReadOption(IReadOnlyList<string> parts, string option)
    {
        for (var index = 3; index < parts.Count - 1; index++)
        {
            if (parts[index].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                return parts[index + 1];
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> TranslateMtpFilter(string filter)
    {
        var arguments = new List<string>();
        foreach (var rawToken in System.Text.RegularExpressions.Regex.Split(filter, @"[&|]"))
        {
            var token = rawToken.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(
                token,
                @"^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = System.Text.RegularExpressions.Regex.Match(
                    token,
                    @"^(?<value>[A-Za-z_][A-Za-z0-9_.]*)$");
                if (!match.Success)
                {
                    throw new ArgumentException($"Unsupported stable-slot MTP filter token '{token}'.");
                }
            }

            arguments.Add(match.Groups["op"].Value == "!~"
                ? "--filter-not-class"
                : "--filter-class");
            arguments.Add($"*{match.Groups["value"].Value}*");
        }

        return arguments;
    }

    internal static int RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool configureDotnetEnvironment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(startInfo.Environment, workingDirectory);
        if (configureDotnetEnvironment)
        {
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["UseSharedCompilation"] = "false";
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        using (ProcessTreeGuiSuppression.AcquireErrorModeForChildSpawn())
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start dotnet process.");
        }

        using (process)
        {
            var standardOutput = PipeDrain.Start(process.StandardOutput, "cli-process-stdout-drain");
            var standardError = PipeDrain.Start(process.StandardError, "cli-process-stderr-drain");
            process.WaitForExit();
            var drainDeadline = Environment.TickCount64 + PipeDrain.DefaultTimeoutMilliseconds;
            var outputDrained = standardOutput.Join(drainDeadline);
            var errorDrained = standardError.Join(drainDeadline);
            Console.Out.Write(standardOutput.Text);
            Console.Error.Write(standardError.Text);
            if (!outputDrained || !errorDrained)
            {
                Console.Error.WriteLine(PipeDrain.DescribeTimeout(
                    "CLI child process",
                    PipeDrain.DefaultTimeoutMilliseconds,
                    standardOutput,
                    standardError));
                return process.ExitCode == 0 ? 1 : process.ExitCode;
            }

            return process.ExitCode;
        }
    }
}
