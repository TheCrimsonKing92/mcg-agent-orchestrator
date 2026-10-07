namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial record ConductorSelfRelaunchOptions
{
    internal string? PrebuiltAppOutputDirectory { get; init; }
    internal LandingAppBuildStore? LandingAppBuildStore { get; init; }
}

internal sealed partial record ConductorSuccessorStagingOptions
{
    internal string? PrebuiltAppOutputDirectory { get; init; }
    internal LandingAppBuildStore? LandingAppBuildStore { get; init; }
}

internal static partial class ConductorSelfRelaunch
{
    private static int _appBuildInvocationCount;

    internal static int AppBuildInvocationCount => Volatile.Read(ref _appBuildInvocationCount);

    private static CapturedProcessResult ProduceBuildOutput(
        ConductorSuccessorStagingOptions options,
        string buildOutputDirectory,
        TimeSpan buildTimeout,
        CancellationToken cancellationToken,
        bool skipStoreMarker = false)
    {
        if (options.PrebuiltAppOutputDirectory is null)
        {
            Interlocked.Increment(ref _appBuildInvocationCount);
            return RunProcess(
                options.DotnetPath,
                [
                    "build",
                    options.AppProjectPath,
                    "--nologo",
                    "--output",
                    buildOutputDirectory,
                    "-v",
                    "quiet",
                    "-clp:ErrorsOnly"
                ],
                options.RepositoryRoot,
                buildTimeout,
                cancellationToken);
        }

        var source = options.PrebuiltAppOutputDirectory;
        var appDllName = Path.GetFileName(options.AppDllPath);
        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, appDllName)))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "build",
                $"Prebuilt App output at {source} is missing or does not contain {appDllName}.");
        }

        try
        {
            foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(source, sourceFile);
                if (skipStoreMarker && relativePath == LandingAppBuildStore.CompleteMarkerName) continue;
                var destination = Path.Combine(buildOutputDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceFile, destination);
                File.SetAttributes(destination, FileAttributes.Normal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConductorSelfRelaunchPreparationException(
                "build",
                $"Prebuilt App output at {source} could not be copied: {ex.Message}");
        }

        return new CapturedProcessResult(0, $"prebuilt:{source}", "");
    }

    internal static LandingAppBuildResult RunAppBuildProcess(
        string dotnetPath, LandingAppBuildRequest request, CancellationToken cancellationToken)
    {
        var result = RunProcess(dotnetPath,
            [
                "build",
                Path.Combine(request.SourceRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                "--nologo",
                "--output",
                request.OutputDirectory,
                "-v",
                "quiet",
                "-clp:ErrorsOnly"
            ], request.SourceRoot, request.Timeout, cancellationToken);
        return new(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut);
    }
}
