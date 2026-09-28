using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorSelfRelaunchSharedAppPayload
{
    private static readonly Lazy<string> SharedDirectory = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string DirectoryPath => SharedDirectory.Value;

    internal static IReadOnlyDictionary<string, string> SnapshotHashes()
    {
        var root = DirectoryPath;
        static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                HashFile,
                StringComparer.OrdinalIgnoreCase);
    }

    internal static void AssertHashesEqual(IReadOnlyDictionary<string, string> before)
    {
        var after = SnapshotHashes();
        Assert.Equal(before.Count, after.Count);
        foreach (var (path, hash) in before)
        {
            Assert.True(after.TryGetValue(path, out var currentHash), $"Shared payload file disappeared: {path}");
            Assert.Equal(hash, currentHash);
        }
    }

    internal static void CreatePrivateCopy(string destination)
    {
        var source = DirectoryPath;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    private static string Build()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-self-relaunch-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var project = Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        var dotnet = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet";
        try
        {
            var build = ConductorSelfRelaunch.RunProcessForTests(
                dotnet,
                ["build", project, "--nologo", "--output", directory, "-v", "quiet", "-clp:ErrorsOnly"],
                repositoryRoot,
                TimeSpan.FromMinutes(3));
            if (build.ExitCode != 0 || build.TimedOut)
            {
                throw new InvalidOperationException(
                    $"Shared App payload build failed exit={build.ExitCode} timedOut={build.TimedOut}: {build.Stderr}");
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(directory);
            return directory;
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
