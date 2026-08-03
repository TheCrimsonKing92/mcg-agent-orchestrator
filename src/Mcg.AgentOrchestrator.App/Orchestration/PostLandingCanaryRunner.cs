using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IPostLandingCanaryRunner
{
    Task<PostLandingCanaryOutcome> RunAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken);
}

internal sealed class PostLandingCanaryRunner : IPostLandingCanaryRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _repositoryRoot;
    private readonly string _dotnetPath;
    private readonly string _buildCacheRoot;
    private readonly Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>>? _override;

    internal PostLandingCanaryRunner(
        string repositoryRoot,
        string? dotnetPath = null,
        Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>>? runOverride = null,
        string? buildCacheRoot = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _dotnetPath = string.IsNullOrWhiteSpace(dotnetPath) ? "dotnet" : dotnetPath;
        _buildCacheRoot = string.IsNullOrWhiteSpace(buildCacheRoot)
            ? Path.Combine(Path.GetTempPath(), "mcg-post-landing-canary-build")
            : Path.GetFullPath(buildCacheRoot);
        _override = runOverride;
    }

    public Task<PostLandingCanaryOutcome> RunAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken) =>
        _override is null
            ? RunCoreAsync(request, cancellationToken)
            : _override(request, cancellationToken);

    private async Task<PostLandingCanaryOutcome> RunCoreAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
        using var fixture = PostLandingCanaryFixture.Materialize(_repositoryRoot, request.LandingSha);
        await InitializeFixtureRepositoryAsync(fixture.RootPath, cancellationToken).ConfigureAwait(false);
        var appDllPath = await ResolveOrBuildMainBinaryAsync(request.LandingSha, cancellationToken)
            .ConfigureAwait(false);
        var process = await RunProcessAsync(
            _dotnetPath,
            [
                appDllPath,
                PostLandingCanaryCommand.SubcommandName,
                fixture.RootPath
            ],
            _repositoryRoot,
            cancellationToken).ConfigureAwait(false);

        var probeLine = process.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith(PostLandingCanaryCommand.ResultPrefix, StringComparison.Ordinal));
        if (probeLine is null)
        {
            throw new PostLandingCanaryEvaluationException(
                $"canary subprocess returned exit {process.ExitCode} without a result contract: {Tail(process.Stderr)}");
        }

        PostLandingCanaryProbeResult? probe;
        try
        {
            probe = JsonSerializer.Deserialize<PostLandingCanaryProbeResult>(
                probeLine[PostLandingCanaryCommand.ResultPrefix.Length..],
                JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PostLandingCanaryEvaluationException(
                $"canary subprocess result contract was invalid: {ex.Message}");
        }

        if (probe is null)
        {
            throw new PostLandingCanaryEvaluationException(
                "canary subprocess returned an empty result contract");
        }

        return probe.Green
            ? PostLandingCanaryOutcome.Passed(probe.ExecutedTestCount, probe.Detail)
            : PostLandingCanaryOutcome.Failed(
                probe.FailureReason ?? PostLandingCanaryFailureReason.InfrastructureError,
                probe.Detail,
                probe.ExecutedTestCount);
    }

    private async Task InitializeFixtureRepositoryAsync(string fixtureRoot, CancellationToken cancellationToken)
    {
        await EnsureSucceededAsync(
            "initialize fixture repository",
            "git",
            ["init", "--quiet"],
            fixtureRoot,
            cancellationToken).ConfigureAwait(false);
        await EnsureSucceededAsync(
            "stage fixture repository",
            "git",
            ["add", "--all"],
            fixtureRoot,
            cancellationToken).ConfigureAwait(false);
        await EnsureSucceededAsync(
            "commit fixture repository",
            "git",
            [
                "-c", "user.name=MCG Canary",
                "-c", "user.email=canary@localhost",
                "commit", "--quiet", "-m", "known-green canary fixture"
            ],
            fixtureRoot,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveOrBuildMainBinaryAsync(
        string landingSha,
        CancellationToken cancellationToken)
    {
        var head = await RunProcessAsync(
            "git",
            ["-C", _repositoryRoot, "rev-parse", "HEAD"],
            _repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        if (head.ExitCode != 0 ||
            !head.Stdout.Trim().Equals(landingSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Post-landing canary expected main HEAD {landingSha}, but resolved '{head.Stdout.Trim()}'.");
        }

        var repositoryKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(_repositoryRoot)))[..16];
        var outputDirectory = Path.Combine(
            _buildCacheRoot,
            repositoryKey,
            landingSha);
        var appDllPath = Path.Combine(outputDirectory, "Mcg.AgentOrchestrator.App.dll");
        var markerPath = appDllPath + ".git-head";
        if (File.Exists(appDllPath) &&
            File.Exists(markerPath) &&
            File.ReadAllText(markerPath).Trim().Equals(landingSha, StringComparison.OrdinalIgnoreCase))
        {
            return appDllPath;
        }

        Directory.CreateDirectory(outputDirectory);
        await EnsureSucceededAsync(
            "build freshly landed main binary",
            _dotnetPath,
            [
                "build",
                Path.Combine(_repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                "--nologo",
                "--output",
                outputDirectory,
                "-v",
                "quiet",
                "-clp:ErrorsOnly"
            ],
            _repositoryRoot,
            cancellationToken,
            evaluatedArtifactFailure: true).ConfigureAwait(false);
        File.WriteAllText(markerPath, landingSha + Environment.NewLine);
        return appDllPath;
    }

    private static async Task EnsureSucceededAsync(
        string operation,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool evaluatedArtifactFailure = false)
    {
        var result = await RunProcessAsync(
            fileName,
            arguments,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail =
                $"Failed to {operation} (exit {result.ExitCode}): {Tail(result.Stdout + Environment.NewLine + result.Stderr)}";
            throw evaluatedArtifactFailure
                ? new PostLandingCanaryEvaluationException(detail)
                : new InvalidOperationException(detail);
        }
    }

    private static async Task<PostLandingCanaryProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(startInfo.Environment, workingDirectory);
        using var process = ProcessTreeGuiSuppression.Start(startInfo);
        process.StandardInput.Close();
        WorkerProcessJobs.TryRegister(process, $"post-landing-canary:{workingDirectory}");
        try
        {
            // Do not cancel pipe drains before the killed process tree closes its handles.
            // Completion of this method is the coordinator's termination confirmation.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return new PostLandingCanaryProcessResult(
                    process.ExitCode,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                var killed = false;
                try { killed = WorkerProcessJobs.TryKillOrFallback(process.Id); } catch { }
                if (!killed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                if (!process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Post-landing canary process tree rooted at pid {process.Id} did not terminate.");
                }

                throw;
            }
        }
        finally
        {
            WorkerProcessJobs.Release(process.Id);
        }
    }

    private static string Tail(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= 2000 ? normalized : normalized[^2000..];
    }

    private sealed record PostLandingCanaryProcessResult(int ExitCode, string Stdout, string Stderr);
}
