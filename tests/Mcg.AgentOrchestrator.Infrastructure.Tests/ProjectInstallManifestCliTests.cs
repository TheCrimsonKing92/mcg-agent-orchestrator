using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private temp roots; only this class changes install's test hooks, reset in finally.
// xUnit runs cases within this class sequentially; other CLI commands do not use the hooks.
public sealed class ProjectInstallManifestCliTests
{
    [Fact]
    public void Install_EmittedManifest_ReplacesLiveAndPreservesPrintedBackup()
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        var prior = File.ReadAllBytes(fixture.Live);
        var emitted = File.ReadAllBytes(fixture.Emitted);
        Assert.False(prior.SequenceEqual(emitted));

        var (exit, lines) = fixture.Install(fixture.Emitted);

        Assert.Equal(0, exit);
        var match = Regex.Match(Assert.Single(lines),
            $"^Installed manifest: {Regex.Escape(fixture.Live)} \\(2 checks; previous saved as (?<backup>.+)\\)$");
        Assert.True(match.Success, string.Join(Environment.NewLine, lines));
        var backup = match.Groups["backup"].Value;
        Assert.Equal(fixture.ModelDirectory, Path.GetDirectoryName(backup));
        Assert.Matches(@"^acceptance-manifest\.json\.bak-\d{8}T\d{6}Z$", Path.GetFileName(backup));
        Assert.Equal(prior, File.ReadAllBytes(backup));
        Assert.Equal(emitted, File.ReadAllBytes(fixture.Live));
        Assert.Equal(backup, Assert.Single(Directory.GetFiles(fixture.ModelDirectory, "*.bak-*")));
        Assert.Equal(fixture.Live, AcceptanceManifestLocator.Resolve(fixture.Source.Root, fixture.ModelDirectory));
        Assert.Empty(Directory.GetFiles(fixture.ModelDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("substitution")]
    [InlineData("truncation")]
    [InlineData("extension")]
    [InlineData("bom")]
    public void Install_ChangedBytes_RefusesAtFirstDifferingOffsetWithoutWriting(string scenario)
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        var bytes = File.ReadAllBytes(fixture.Emitted);
        int offset;
        switch (scenario)
        {
            case "substitution": offset = 7; bytes[offset] ^= 1; break;
            case "truncation": offset = bytes.Length - 1; bytes = bytes[..offset]; break;
            case "extension": offset = bytes.Length; bytes = [.. bytes, 0]; break;
            default: offset = 0; bytes = [0xef, 0xbb, 0xbf, .. bytes]; break;
        }
        File.WriteAllBytes(fixture.Emitted, bytes);
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Install(fixture.Emitted);

        Assert.Equal(1, exit);
        Assert.Equal($"Refused: --from differs from the current learned manifest at byte offset {offset}", Assert.Single(lines));
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    [Fact]
    public void Install_AnswerChangedAfterEmit_RefusesStaleEmissionWithoutWriting()
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        using var output = new StringWriter();
        Assert.Equal(0, ProjectAnswerCommand.Execute(
            ["project", "answer", "sample", "engine/timeouts/discoveryMinutes", "9"],
            fixture.Registry, fixture.Home.Root, null, output, () => DateTimeOffset.UtcNow.AddMinutes(5)));
        var current = Path.Combine(fixture.Scratch.Root, "current.json");
        Assert.Equal(0, fixture.Execute(["project", "emit-manifest", "sample", "--out", current]).Exit);
        var oldBytes = File.ReadAllBytes(fixture.Emitted);
        var currentBytes = File.ReadAllBytes(current);
        Assert.False(oldBytes.SequenceEqual(currentBytes));
        var offset = Enumerable.Range(0, Math.Min(oldBytes.Length, currentBytes.Length))
            .First(index => oldBytes[index] != currentBytes[index]);
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Install(fixture.Emitted);

        Assert.Equal(1, exit);
        Assert.Equal($"Refused: --from differs from the current learned manifest at byte offset {offset}", Assert.Single(lines));
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    [Fact]
    public void Install_UnansweredFacts_ReportsEmitBlockersWithoutWriting()
    {
        using var fixture = new Fixture();
        fixture.Discover();
        File.WriteAllText(fixture.Emitted, "{}");
        var emit = fixture.Execute(["project", "emit-manifest", "sample", "--out", Path.Combine(fixture.Scratch.Root, "blocked.json")]);
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Install(fixture.Emitted);

        Assert.Equal(1, exit);
        Assert.Equal("Refused: 3 facts the gate needs are unanswered", lines[0]);
        Assert.Equal(1, emit.Exit);
        Assert.Equal(emit.Lines, lines);
        Assert.Equal(3, lines.Count(line => line.StartsWith("Blocked ", StringComparison.Ordinal)));
        Assert.Equal(3, lines.Count(line => line.StartsWith("OwnerQuestion ", StringComparison.Ordinal)));
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Install_MissingSourceOrDirectory_RefusesWithoutWriting(bool directory)
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        var source = directory ? fixture.Scratch.Root : Path.Combine(fixture.Scratch.Root, "missing.json");
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Install(source);

        Assert.Equal(1, exit);
        Assert.Equal($"Refused: --from file does not exist {source}", Assert.Single(lines));
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    [Fact]
    public void Install_DefaultHomeProject_RefusesAndPreservesHomeConfig()
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        var config = Path.Combine(fixture.Home.Root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), "owner-protected home bytes");
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Execute(["project", "install-manifest", "DEFAULT", "--from", fixture.Emitted]);

        Assert.Equal(1, exit);
        Assert.Equal("Refused: the default home project has no installable live manifest", Assert.Single(lines));
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    [Fact]
    public void Install_LiveSourceAlias_RefusesBeforeByteComparisonWithoutWriting()
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        var before = fixture.Snapshot();
        var source = Path.Combine(fixture.ModelDirectory.ToUpperInvariant(), ".", "ACCEPTANCE-MANIFEST.JSON");

        var (exit, lines) = fixture.Install(source);

        Assert.Equal(1, exit);
        Assert.Equal($"Refused: --from is the live manifest {fixture.Live}", Assert.Single(lines));
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    [Fact]
    public void Install_BackupNameAlreadyExists_RefusesWithoutWriting()
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        var backup = fixture.Live + ".bak-20261011T225000Z";
        File.WriteAllText(backup, "existing backup bytes");
        var before = fixture.Snapshot();
        ProjectInstallManifestCommand.UtcNowForTests = () => new DateTimeOffset(2026, 10, 11, 22, 50, 0, TimeSpan.Zero);
        try
        {
            var (exit, lines) = fixture.Install(fixture.Emitted);

            Assert.Equal(1, exit);
            Assert.Equal($"Refused: backup already exists {backup}", Assert.Single(lines));
            fixture.AssertUnchanged(before);
            Assert.Equal(backup, Assert.Single(Directory.GetFiles(fixture.ModelDirectory, "*.bak-*")));
            Assert.Empty(Directory.GetFiles(fixture.ModelDirectory, "*.tmp"));
        }
        finally
        {
            ProjectInstallManifestCommand.UtcNowForTests = null;
        }
    }

    [Fact]
    public void Install_NoPreviousManifest_CreatesLiveWithoutBackupUsingActiveProject()
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        File.Delete(fixture.Live);

        var (exit, lines) = fixture.Execute(["project", "install-manifest", "--from=" + fixture.Emitted], "sample");

        Assert.Equal(0, exit);
        Assert.Equal($"Installed manifest: {fixture.Live} (2 checks; no previous manifest)", Assert.Single(lines));
        Assert.Equal(File.ReadAllBytes(fixture.Emitted), File.ReadAllBytes(fixture.Live));
        fixture.AssertNoInstallArtifacts();
    }

    [Fact]
    public void Install_ReplaceFailure_PreservesLiveAndBackupAndRemovesTemporary()
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        var prior = File.ReadAllBytes(fixture.Live);
        var reachedReplace = false;
        ProjectInstallManifestCommand.ReplaceFaultForTests = () =>
        {
            reachedReplace = true;
            Assert.Equal(prior, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.ModelDirectory, "*.bak-*"))));
            Assert.Equal(File.ReadAllBytes(fixture.Emitted),
                File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.ModelDirectory, "*.tmp"))));
            throw new IOException("simulated replace failure");
        };
        try
        {
            var (exit, lines) = fixture.Install(fixture.Emitted);

            Assert.True(reachedReplace);
            Assert.Equal(1, exit);
            Assert.Equal($"Refused: could not install {fixture.Live}: simulated replace failure", Assert.Single(lines));
            Assert.Equal(prior, File.ReadAllBytes(fixture.Live));
            Assert.Equal(prior, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.ModelDirectory, "*.bak-*"))));
            Assert.Empty(Directory.GetFiles(fixture.ModelDirectory, "*.tmp"));
        }
        finally
        {
            ProjectInstallManifestCommand.ReplaceFaultForTests = null;
        }
    }

    [Theory]
    [InlineData("missing-model")]
    [InlineData("malformed-model")]
    [InlineData("invalid-schema")]
    [InlineData("unreadable-answers")]
    public void Install_UnreadableInputs_UsesEmitMessagesWithoutWriting(string scenario)
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        switch (scenario)
        {
            case "missing-model": File.Delete(fixture.ModelPath); break;
            case "malformed-model": File.WriteAllText(fixture.ModelPath, "{"); break;
            case "invalid-schema": File.WriteAllText(fixture.ModelPath, "{}"); break;
            default: File.WriteAllText(fixture.AnswersPath, "{"); break;
        }
        var before = fixture.Snapshot();
        var emit = fixture.Execute(["project", "emit-manifest", "sample", "--out", Path.Combine(fixture.Scratch.Root, "another.json")]);

        var (exit, lines) = fixture.Install(fixture.Emitted);

        Assert.Equal(1, exit);
        Assert.Equal(1, emit.Exit);
        Assert.Equal(emit.Lines, lines);
        Assert.StartsWith(scenario == "unreadable-answers" ? "Owner answers unreadable: " : "Refused: ", Assert.Single(lines));
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    [Theory]
    [InlineData("missing-from")]
    [InlineData("missing-value")]
    [InlineData("empty-value")]
    [InlineData("option-as-value")]
    [InlineData("unknown-flag")]
    [InlineData("duplicate-from")]
    [InlineData("two-names")]
    [InlineData("unregistered")]
    public void Install_InvalidArguments_ThrowsUsageWithoutWriting(string scenario)
    {
        using var fixture = new Fixture();
        fixture.PrepareEmission();
        string[] options = scenario switch
        {
            "missing-from" => ["sample"],
            "missing-value" => ["sample", "--from"],
            "empty-value" => ["sample", "--from="],
            "option-as-value" => ["sample", "--from", "--force"],
            "unknown-flag" => ["sample", "--from", fixture.Emitted, "--force"],
            "duplicate-from" => ["sample", "--from", fixture.Emitted, "--from=" + fixture.Emitted],
            "two-names" => ["sample", "sample", "--from", fixture.Emitted],
            _ => ["unknown-project", "--from", fixture.Emitted]
        };
        var before = fixture.Snapshot();
        using var output = new StringWriter();

        var exception = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(
            ["project", "install-manifest", .. options], fixture.Registry, fixture.Home.Root, null, output));

        Assert.Contains(ProjectInstallManifestCommand.Usage, exception.Message);
        Assert.Empty(output.ToString());
        fixture.AssertUnchanged(before);
        fixture.AssertNoInstallArtifacts();
    }

    private sealed class Fixture : IDisposable
    {
        public ProjectOnboardingFixture Home { get; } = new("no-solution");
        public ProjectOnboardingFixture Source { get; } = new("answer-key-repository");
        public ProjectOnboardingFixture Scratch { get; } = new("no-solution");
        public OrchestratorProjectRegistry Registry { get; }
        public string ModelDirectory { get; }
        public string ModelPath => Path.Combine(ModelDirectory, "project-model.json");
        public string AnswersPath => Path.Combine(ModelDirectory, OwnerAnswerFile.FileName);
        public string Live => Path.Combine(ModelDirectory, "acceptance-manifest.json");
        public string Emitted => Path.Combine(Scratch.Root, "learned.json");

        public Fixture()
        {
            Registry = new(Path.Combine(Home.Root, "registry"), Path.Combine(Home.Root, "data"));
            ModelDirectory = Registry.CreateProject("sample", Source.Root).ResolveWorkspace().OrchestratorDirectory;
            Directory.CreateDirectory(ModelDirectory);
            File.Copy(Path.Combine(Source.Root, "hand-acceptance-manifest.json"), Live);
        }

        public void Discover() => Assert.Equal(0, Execute(["project", "discover", "sample"]).Exit);
        public void PrepareEmission()
        {
            Discover();
            foreach (var (key, value) in new[] { ("defaultMinutes", "17"), ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
                Assert.Equal(0, Execute(["project", "answer", "sample", "engine/timeouts/" + key, value]).Exit);
            var (exit, lines) = Execute(["project", "emit-manifest", "sample", "--out", Emitted]);
            Assert.Equal(0, exit);
            Assert.Equal($"Emitted manifest: {Emitted} (2 checks)", Assert.Single(lines));
        }
        public (int Exit, string[] Lines) Install(string source) => Execute(["project", "install-manifest", "sample", "--from", source]);
        public (int Exit, string[] Lines) Execute(string[] parts, string? activeProject = null)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, Registry, Home.Root, activeProject, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        public Dictionary<string, string> Snapshot() => new[] { Home.Root, Source.Root, Scratch.Root }
            .SelectMany(root => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            .ToDictionary(path => path, path => Directory.Exists(path) ? "directory"
                : $"{new FileInfo(path).Length}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
        public void AssertUnchanged(Dictionary<string, string> before)
        {
            var after = Snapshot();
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
        }
        public void AssertNoInstallArtifacts()
        {
            Assert.Empty(Directory.GetFiles(ModelDirectory, "*.bak-*"));
            Assert.Empty(Directory.GetFiles(ModelDirectory, "*.tmp"));
        }
        public void Dispose()
        {
            Scratch.Dispose();
            Source.Dispose();
            Home.Dispose();
        }
    }
}
