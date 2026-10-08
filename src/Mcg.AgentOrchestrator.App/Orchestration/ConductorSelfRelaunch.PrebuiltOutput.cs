using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

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
        var isolatedArtifactsRoot = AppBuildArtifactsRoot(appProjectPath);
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

    private static string AppBuildArtifactsRoot(string appProjectPath)
    {
        var projectPath = Path.GetFullPath(appProjectPath);
        if (OperatingSystem.IsWindows()) projectPath = projectPath.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectPath)))[..16];
        // Mutable build cache belongs to the source tree, independently of successor/store retention.
        return Path.Combine(OrchestratorTempRoot.GetRoot(), "tmp", "relaunch-build", key);
    }

    private static CapturedProcessResult RunCachedAppBuild(
        string dotnetPath, string appProjectPath, string outputDirectory,
        string repositoryRoot, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cacheRoot = AppBuildArtifactsRoot(appProjectPath);
        var cachedOutput = Path.Combine(cacheRoot, "output");
        var command = CreateAppBuildCommand(appProjectPath, cachedOutput);
        Directory.CreateDirectory(cacheRoot);
        var elapsed = Stopwatch.StartNew();
        // Direct and store builds share intermediates: hold the cross-process lock through copying.
        FileStream cacheLock;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= timeout)
                return new(-1, "", "Timed out waiting for isolated App build cache.", TimedOut: true);
            try
            {
                cacheLock = File.Open(Path.Combine(cacheRoot, "build.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
            {
                var delay = timeout == Timeout.InfiniteTimeSpan ? 25 : Math.Min(25, Math.Max(1, (timeout - elapsed.Elapsed).TotalMilliseconds));
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delay)))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }

        using (cacheLock)
        {
            // Remove stale final payloads while retaining obj; MSBuild copies up-to-date assemblies back.
            if (Directory.Exists(cachedOutput)) Directory.Delete(cachedOutput, recursive: true);
            var remaining = timeout == Timeout.InfiniteTimeSpan ? timeout : timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                return new(-1, "", "Timed out waiting for isolated App build cache.", TimedOut: true);
            var result = RunProcess(dotnetPath, command.Arguments, repositoryRoot, remaining, cancellationToken);
            if (result.ExitCode == 0 && !result.TimedOut)
                CopyAppBuildOutput(cachedOutput, outputDirectory, cancellationToken, overwrite: true);
            return result;
        }
    }

    private static void CopyAppBuildOutput(string source, string destinationRoot, CancellationToken cancellationToken,
        bool skipStoreMarker = false, bool overwrite = false)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(source, sourceFile);
            if (skipStoreMarker && relativePath == LandingAppBuildStore.CompleteMarkerName) continue;
            var destination = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, overwrite);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
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
            return RunCachedAppBuild(options.DotnetPath, options.AppProjectPath, buildOutputDirectory,
                options.RepositoryRoot, buildTimeout, cancellationToken);
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
            CopyAppBuildOutput(source, buildOutputDirectory, cancellationToken, skipStoreMarker);
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
        var result = RunCachedAppBuild(dotnetPath,
            Path.Combine(request.SourceRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
            request.OutputDirectory, request.SourceRoot, request.Timeout, cancellationToken);
        return new(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut);
    }
}
