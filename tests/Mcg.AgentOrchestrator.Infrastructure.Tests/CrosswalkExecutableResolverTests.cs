using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private files and injected version execution; no real processes or ambient configuration.
public sealed class CrosswalkExecutableResolverTests : IDisposable
{
    private readonly DirectoryInfo _sandbox = Directory.CreateTempSubdirectory("crosswalk-resolver-'");
    private int _versionCalls;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultFile_ResolvesAbsolutePathRawByteHashAndVersion(string? explicitPath)
    {
        var path = CreateDefault();
        WorkerProcessRunRequest? request = null;
        var resolver = new CrosswalkExecutableResolver(explicitPath, _sandbox.FullName, (value, token) =>
        {
            request = value;
            Assert.Equal(CancellationToken.None, token);
            return Task.FromResult(new WorkerProcessRunResult(0, "  crosswalk 8a2e58d\nsecond line\r\n", "ignored"));
        });

        var resolution = resolver.Resolve();

        Assert.Equal("detected", resolution.Status);
        Assert.Null(resolution.DiagnosticCategory);
        Assert.Equal(new CrosswalkExecutableIdentity(path, "crosswalk 8a2e58d\nsecond line", Hash(path)), resolution.Identity);
        Assert.Equal("& '" + path.Replace("'", "''") + "' --version; exit $LASTEXITCODE", request!.Command);
        Assert.Equal(Path.GetDirectoryName(path), request.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout);
        Assert.Null(request.StandardInput);
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("blank")]
    [InlineData("timeout")]
    [InlineData("throw")]
    [InlineData("faulted")]
    [InlineData("limited")]
    public void VersionFailure_PreservesPathAndHashWithUnknownVersion(string failure)
    {
        var path = CreateDefault();
        var resolver = new CrosswalkExecutableResolver(dataRoot: _sandbox.FullName, runVersionAsync: (_, _) => failure switch
        {
            "throw" => throw new IOException(),
            "faulted" => Task.FromException<WorkerProcessRunResult>(new OperationCanceledException()),
            _ => Task.FromResult(new WorkerProcessRunResult(failure == "nonzero" ? 2 : 0,
                failure == "blank" ? " \r\n" : "unusable version", "", failure == "timeout", failure == "limited"))
        });

        Assert.Equal(new CrosswalkExecutableIdentity(path, "unknown", Hash(path)), resolver.Resolve().Identity);
        Assert.Equal("detected", resolver.Resolve().Status);
    }

    [Fact]
    public async Task ConcurrentResolution_CachesOneIdentityAcrossFileReplacement()
    {
        var path = CreateDefault();
        var hash = Hash(path);
        var resolver = Resolver();
        var resolutions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(resolver.Resolve)))
            .WaitAsync(TimeSpan.FromSeconds(30)); // Hang guard: every resolution caller returned.
        Assert.All(resolutions, resolution => Assert.Same(resolutions[0], resolution));
        Assert.Equal(1, _versionCalls);
        File.WriteAllBytes(path, [9, 8, 7]);
        Assert.Same(resolutions[0], resolver.Resolve());
        Assert.Equal(hash, resolver.Resolve().Identity!.Sha256);
        Assert.NotEqual(Hash(path), resolver.Resolve().Identity!.Sha256);
        Assert.Equal(Hash(path), Resolver().Resolve().Identity!.Sha256);
        Assert.Equal(2, _versionCalls);
    }

    [Fact]
    public void MissingDefault_CachesNotConfiguredUntilNewResolver()
    {
        var resolver = Resolver();
        var missing = resolver.Resolve();
        Assert.Equal("not-configured", missing.Status);
        Assert.Equal(CrosswalkDiagnosticCategory.NotConfigured, missing.DiagnosticCategory);
        Assert.Contains(Path.Combine(_sandbox.FullName, "tools", "crosswalk", "crosswalk.exe"), missing.ConfigurationDetail);
        Assert.Contains("missing", missing.ConfigurationDetail);
        Assert.Contains("orchestrator data root", missing.ConfigurationDetail);
        Assert.Null(missing.Identity);
        Assert.Equal(0, _versionCalls);
        CreateDefault();
        Assert.Same(missing, resolver.Resolve());
        Assert.NotNull(Resolver().Resolve().Identity);
        Assert.Equal(1, _versionCalls);
    }

    [Fact]
    public void NoDataRootOrExplicitPath_IsNotConfigured()
    {
        var result = new CrosswalkExecutableResolver().Resolve();
        Assert.Equal("not-configured", result.Status);
        Assert.Contains("No data root or explicit crosswalk executable path", result.ConfigurationDetail);
        Assert.Contains("no path was attempted", result.ConfigurationDetail);
        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPath_WinsOverDefaultAndNormalizesRelativePath(bool relative)
    {
        CreateDefault();
        var path = Path.Combine(_sandbox.FullName, "explicit.exe");
        File.WriteAllBytes(path, [5, 4, 3, 2, 1]);
        var selected = relative ? Path.GetRelativePath(Environment.CurrentDirectory, path) : path;
        var result = Resolver(selected).Resolve();
        Assert.Equal(new CrosswalkExecutableIdentity(path, "test-version", Hash(path)), result.Identity);
        Assert.Equal(1, _versionCalls);
    }

    [Fact]
    public void ExplicitMissing_DoesNotFallBackToPresentDefault()
    {
        CreateDefault();
        var resolver = Resolver(Path.Combine(_sandbox.FullName, "missing.exe"));
        var result = resolver.Resolve();
        Assert.Equal("missing", result.Status);
        Assert.Equal(CrosswalkDiagnosticCategory.MissingBinary, result.DiagnosticCategory);
        Assert.Contains(Path.Combine(_sandbox.FullName, "missing.exe"), result.ConfigurationDetail);
        Assert.Contains("missing at explicit path", result.ConfigurationDetail);
        Assert.Null(result.Identity);
        Assert.Equal(0, _versionCalls);
        Assert.Same(result, resolver.Resolve());
    }

    [Fact]
    public void UnreadableFile_ReturnsOperationalFailureWithoutIdentity()
    {
        var path = CreateDefault();
        using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Resolver().Resolve();
        Assert.Equal("start-failed", result.Status);
        Assert.Equal(CrosswalkDiagnosticCategory.StartFailed, result.DiagnosticCategory);
        Assert.Equal("IOException", result.Detail);
        Assert.Null(result.Identity);
        Assert.Equal(0, _versionCalls);
    }

    private CrosswalkExecutableResolver Resolver(string? path = null) => new(path, _sandbox.FullName, (_, _) =>
    {
        Interlocked.Increment(ref _versionCalls);
        return Task.FromResult(new WorkerProcessRunResult(0, "test-version", ""));
    });

    private string CreateDefault()
    {
        var path = Path.Combine(_sandbox.FullName, "tools", "crosswalk", "crosswalk.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 255, 13, 10, 128, 42]);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public void Dispose() => _sandbox.Delete(recursive: true);
}
