using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private fixture workspaces, fixed lock probe/time and local output writers.
public sealed class ConductorStatusOwnerDirectivesTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingStore_FinalZeroLine_AndDirectoryHashesUnchanged()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var before = Hashes(fixture.Workspace.RootDirectory);
        Assert.Equal("Owner directives: 0 active", LastLine(Status(fixture.Workspace)));
        Assert.False(File.Exists(fixture.Workspace.OperatorDirectivesStorePath));
        Assert.Equal(before, Hashes(fixture.Workspace.RootDirectory));
    }

    [Fact]
    public void TwoActive_NamesOrderedFirstLines_TruncatesAndExcludesExpiredAndRetired()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        var text = new string('x', 90);
        Append(store, 2, "pacing", "  " + text + "\r\nsecond line", At);
        Append(store, 1, "intake", "  One at a time\nignore", At.AddMinutes(-1));
        Append(store, 3, "scope", "expired", At, At);
        Append(store, 4, "scope", "retired", At);
        Assert.True(store.TryAppendRetirement(Id(4), "retire", "done", "owner", OperatorActorKind.Human, "cli", At).Appended);
        var before = Hashes(fixture.Workspace.RootDirectory);
        Assert.Equal("Owner directives: 2 active: intake: One at a time; pacing: " + text[..80], LastLine(Status(fixture.Workspace)));
        Assert.Equal(before, Hashes(fixture.Workspace.RootDirectory));
    }

    [Fact]
    public void FourActive_ShowsCountOnly()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        for (var i = 1; i <= 4; i++) Append(store, i, "intake", "Text " + i, At);
        Assert.Equal("Owner directives: 4 active", LastLine(Status(fixture.Workspace)));
    }

    [Fact]
    public void LaterHandoff_ExcludesUntilHandoffDirective()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        store.TryAppendDirective(new(Id(1), "pacing", "Until owner changes", "owner", OperatorActorKind.Human,
            "cli", At.AddMinutes(-1), null, true, null, null, null, false), "record");
        store.TryAppendHandoff(Id(2), "handoff", null, "owner", OperatorActorKind.Human, "cli", At);
        Assert.Equal("Owner directives: 0 active", LastLine(Status(fixture.Workspace)));
    }

    [Fact]
    public void CorruptStore_StatusStaysSuccessfulAndUnavailable_WithoutRepair()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Directory.CreateDirectory(fixture.Workspace.OrchestratorDirectory);
        File.WriteAllText(fixture.Workspace.OperatorDirectivesStorePath, "not sqlite");
        var before = Hashes(fixture.Workspace.RootDirectory);
        Assert.Equal("Owner directives: unavailable", LastLine(Status(fixture.Workspace)));
        Assert.Equal(before, Hashes(fixture.Workspace.RootDirectory));
    }

    [Fact]
    public void Json_StatusIsByteIdenticalBeforeAndAfterDirectiveAndHasOriginalShape()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var before = Status(fixture.Workspace, json: true);
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        Append(store, 1, "intake", "Owner text", At);
        var hashes = Hashes(fixture.Workspace.RootDirectory);
        var after = Status(fixture.Workspace, json: true);
        Assert.Equal(before, after);
        using var document = JsonDocument.Parse(after);
        Assert.Equal(["conductor", "since", "supervisorBuild", "generation", "latestTick", "goals",
            "mostRecentLanding", "workerAdoption", "pendingIntents", "providerHolds"],
            document.RootElement.GetProperty("payload").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(hashes, Hashes(fixture.Workspace.RootDirectory));
    }

    private static void Append(SqliteOperatorDirectiveStore store, int id, string kind, string text,
        DateTimeOffset recorded, DateTimeOffset? expires = null) =>
        Assert.True(store.TryAppendDirective(new(Id(id), kind, text, "owner", OperatorActorKind.Human,
            "cli", recorded, expires, false, null, null, null, false), "record-" + id));

    private static string Id(int id) => id.ToString("x32");
    private static string LastLine(string text) => text.TrimEnd('\r', '\n').Split('\n')[^1].TrimEnd('\r');

    private static string Status(OrchestratorWorkspace workspace, bool json = false)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"],
            workspace, lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => At.AddDays(-1),
            mainCommit: () => "main-commit", output: output, error: error, time: new FixedTime()));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }

    private static string[] Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }
}
