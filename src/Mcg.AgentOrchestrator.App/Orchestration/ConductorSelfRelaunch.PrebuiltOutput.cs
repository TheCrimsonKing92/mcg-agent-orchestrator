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

internal sealed record AppBuildCommand(IReadOnlyList<string> Arguments, string IsolatedArtifactsRoot);

internal static partial class ConductorSelfRelaunch
{
    private static int _appBuildInvocationCount;

    internal static int AppBuildInvocationCount => Volatile.Read(ref _appBuildInvocationCount);

    internal static AppBuildCommand CreateAppBuildCommand(string appProjectPath, string outputDirectory)
    {
        var outputPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        // Keep the sibling short for Windows intermediate-file path budgets and outside store retention.
        var isolatedArtifactsRoot = Path.Combine(Path.GetDirectoryName(outputPath)!, "r" + Guid.NewGuid().ToString("N")[..16]);
        return new(
            [
                "build",
                appProjectPath,
                "--nologo",
                "--output",
                outputDirectory,
                $"-p:McgIsolatedArtifactsPath={isolatedArtifactsRoot}",
                "-v",
                "quiet",
                "-clp:ErrorsOnly"
            ],
            isolatedArtifactsRoot);
    }

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
            var command = CreateAppBuildCommand(options.AppProjectPath, buildOutputDirectory);
            try
            {
                return RunProcess(options.DotnetPath, command.Arguments,
                    options.RepositoryRoot, buildTimeout, cancellationToken);
            }
            finally
            {
                try { Directory.Delete(command.IsolatedArtifactsRoot, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
        var command = CreateAppBuildCommand(
            Path.Combine(request.SourceRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
            request.OutputDirectory);
        try
        {
            var result = RunProcess(dotnetPath, command.Arguments,
                request.SourceRoot, request.Timeout, cancellationToken);
            return new(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut);
        }
        finally
        {
            try { Directory.Delete(command.IsolatedArtifactsRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
