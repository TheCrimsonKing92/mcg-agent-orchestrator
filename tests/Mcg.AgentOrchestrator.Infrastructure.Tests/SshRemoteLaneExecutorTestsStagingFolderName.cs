using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every filesystem test owns its fake's temporary root.
public sealed class SshRemoteLaneExecutorTestsStagingFolderName
{
    [Fact]
    public void NameHasFixedWidthAndHashesExecutorLaneAndProbe()
    {
        const string executor = "one";
        const string key = "infrastructure-tests-cli-lane";
        var name = SshRemoteLaneExecutor.StagingFolderName(executor, key, 0);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("one\ninfrastructure-tests-cli-lane"));
        Assert.Matches("^r-[0-9a-f]{8}$", name);
        Assert.Equal("r-" + Convert.ToHexString(digest)[..8].ToLowerInvariant(), name);
        Assert.Equal(name, SshRemoteLaneExecutor.StagingFolderName(executor, key, 0));
        Assert.NotEqual(name, SshRemoteLaneExecutor.StagingFolderName("two", key, 0));
        Assert.NotEqual(name, SshRemoteLaneExecutor.StagingFolderName(executor, "another-lane", 0));
        Assert.NotEqual(name, SshRemoteLaneExecutor.StagingFolderName(executor, key, 1));
        var probed = SshRemoteLaneExecutor.StagingFolderName(executor, key, 17);
        var probeDigest = SHA256.HashData(Encoding.UTF8.GetBytes("one\ninfrastructure-tests-cli-lane\n17"));
        Assert.Matches("^r-[0-9a-f]{8}$", probed);
        Assert.Equal("r-" + Convert.ToHexString(probeDigest)[..8].ToLowerInvariant(), probed);
    }

    [Fact]
    public async Task PreparationWritesIdentityFirstAndReusesWithoutRewriting()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var request = FakeSshRemoteLaneTransport.Request;
        var key = SshRemoteLaneExecutor.LaneKey(request.Lane);
        var attempt = Path.Combine(fake.Root, "attempt");
        var folder = SshRemoteLaneExecutor.PrepareStaging(attempt, request.ExecutorId, request.Lane, key);
        Assert.True(Path.IsPathFullyQualified(folder));
        Assert.Equal(Path.Combine(attempt, SshRemoteLaneExecutor.StagingFolderName(request.ExecutorId, key, 0)), folder);
        var manifest = Assert.Single(Directory.GetFileSystemEntries(folder));
        Assert.Equal(Path.Combine(folder, "lane.json"), manifest);
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal(request.ExecutorId, json.RootElement.GetProperty("executorId").GetString());
        Assert.Equal(request.Lane, json.RootElement.GetProperty("lane").GetString());
        Assert.Equal(key, json.RootElement.GetProperty("laneKey").GetString());

        // Different display text still belongs to the same executor/key and must not be rewritten.
        var original = File.ReadAllBytes(manifest);
        Assert.Equal(folder, SshRemoteLaneExecutor.PrepareStaging(attempt, request.ExecutorId, request.Lane, key));
        Assert.Equal(folder, SshRemoteLaneExecutor.PrepareStaging(attempt, request.ExecutorId, "another display name", key));
        Assert.Equal(original, File.ReadAllBytes(manifest));
        Assert.Single(Directory.GetFileSystemEntries(folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"executorId\":\"other\",\"lane\":\"Lane\",\"laneKey\":\"lane\"}")]
    [InlineData("{\"executorId\":\"one\",\"lane\":\"Lane\",\"laneKey\":\"other\"}")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"executorId\":null,\"lane\":\"Lane\",\"laneKey\":\"lane\"}")]
    [InlineData("{\"executorId\":\"one\",\"lane\":\"Lane\"}")]
    [InlineData("{\"executorId\":123,\"lane\":\"Lane\",\"laneKey\":\"lane\"}")]
    public async Task TakenFolderAdvancesProbeAndPreservesEveryExistingFile(string? manifest)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var attempt = Path.Combine(fake.Root, "attempt");
        var taken = Path.Combine(attempt, SshRemoteLaneExecutor.StagingFolderName("one", "lane", 0));
        Directory.CreateDirectory(Path.Combine(taken, "nested"));
        File.WriteAllText(Path.Combine(taken, "nested", "existing.txt"), "retain these bytes");
        if (manifest is not null) File.WriteAllText(Path.Combine(taken, "lane.json"), manifest);
        var before = Directory.GetFiles(taken, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        var folder = SshRemoteLaneExecutor.PrepareStaging(attempt, "one", "Lane", "lane");

        Assert.Equal(Path.Combine(attempt, SshRemoteLaneExecutor.StagingFolderName("one", "lane", 1)), folder);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(taken, "*", SearchOption.AllDirectories).Order());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(folder, SshRemoteLaneExecutor.PrepareStaging(attempt, "one", "Lane", "lane"));
        Assert.True(File.Exists(Path.Combine(folder, "lane.json")));
    }

    [Fact]
    public async Task SubmissionCopiesJobFromPreparedFolderWithManifestAlreadyPresent()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var request = FakeSshRemoteLaneTransport.Request;
        var attempt = Path.Combine(fake.Root, "attempt");
        var key = SshRemoteLaneExecutor.LaneKey(request.Lane);
        var sawManifestAtCopy = false;
        async Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            if (args[3] == "job.json")
            {
                Assert.True(File.Exists(Path.Combine(directory, "lane.json")));
                sawManifestAtCopy = true;
            }
            return await fake.Transport(args, directory, bound, token);
        }
        var executor = new SshRemoteLaneExecutor(new([new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor")],
            [request.Lane], null), fake.Root, Path.Combine(attempt, "result"), fake.Clock, Transport, fake.Git, TimeSpan.Zero);

        var submission = await executor.SubmitAsync(request, TestContext.Current.CancellationToken);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        var folder = SshRemoteLaneExecutor.PrepareStaging(attempt, request.ExecutorId, request.Lane, key);
        var copy = Assert.Single(fake.Calls, call => call.Arguments[3] == "job.json");
        Assert.True(sawManifestAtCopy);
        Assert.Equal(folder, copy.Directory);
        Assert.True(File.Exists(Path.Combine(folder, "lane.json")));
    }
}
