using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private fixture workspaces, fixed clocks and captured output; no shared state.
public sealed class CliDirectivesCommandTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingStore_ListsEmptyWithoutWriting(bool json)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var before = Hashes(fixture.Workspace.RootDirectory);
        using var output = new StringWriter();
        Assert.Equal(0, CliDirectiveCommands.Run(json ? ["directives", "--json"] : ["directives"],
            fixture.Workspace, output, new FixedTime()));
        if (json)
        {
            using var document = JsonDocument.Parse(output.ToString());
            var payload = Envelope(document.RootElement);
            Assert.Empty(payload.GetProperty("directives").EnumerateArray());
        }
        else Assert.Equal("No owner directives recorded." + Environment.NewLine, output.ToString());
        Assert.False(File.Exists(fixture.Workspace.OperatorDirectivesStorePath));
        Assert.Equal(before, Hashes(fixture.Workspace.RootDirectory));
    }

    [Fact]
    public void Json_UsesExactTenFieldEnvelope_OrdersAndFlagsRows_WithoutWrites()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        var first = Row("aaaaaaaa111111111111111111111111", At.AddHours(-2), At.AddHours(-1));
        var second = Row("bbbbbbbb111111111111111111111111", At.AddHours(-2), null);
        var retired = Row("cccccccc111111111111111111111111", At.AddHours(-3), null);
        store.TryAppendDirective(second, "second");
        store.TryAppendDirective(first, "first");
        store.TryAppendDirective(retired, "retired");
        Assert.True(store.TryAppendRetirement(retired.Id, "retirement", "obsolete", "owner",
            OperatorActorKind.Human, "cli", At).Appended);
        var before = Hashes(fixture.Workspace.RootDirectory);
        using var output = new StringWriter();
        Assert.Equal(0, CliDirectiveCommands.Run(["directives", "--json"], fixture.Workspace, output, new FixedTime()));
        using var document = JsonDocument.Parse(output.ToString());
        var rows = Envelope(document.RootElement).GetProperty("directives").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { first.Id, second.Id }, rows.Select(row => row.GetProperty("id").GetString()).ToArray());
        foreach (var row in rows)
            Assert.Equal(["id", "kind", "text", "recordedAt", "actor", "actorKind", "channel", "expiresAt", "untilHandoff", "expired"],
                row.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("scope", rows[0].GetProperty("kind").GetString());
        Assert.Equal(first.Text, rows[0].GetProperty("text").GetString());
        Assert.Equal(first.RecordedAt.ToString("O", CultureInfo.InvariantCulture), rows[0].GetProperty("recordedAt").GetString());
        Assert.Equal("owner", rows[0].GetProperty("actor").GetString());
        Assert.Equal("Human", rows[0].GetProperty("actorKind").GetString());
        Assert.Equal("cli", rows[0].GetProperty("channel").GetString());
        Assert.Equal(first.ExpiresAt!.Value.ToString("O", CultureInfo.InvariantCulture), rows[0].GetProperty("expiresAt").GetString());
        Assert.False(rows[0].GetProperty("untilHandoff").GetBoolean());
        Assert.True(rows[0].GetProperty("expired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("expiresAt").ValueKind);
        Assert.False(rows[1].GetProperty("expired").GetBoolean());
        Assert.Equal(before, Hashes(fixture.Workspace.RootDirectory));
    }

    [Fact]
    public void Text_ListsExpiryAttributionAndExpiredMarker()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var row = Row("aaaaaaaa111111111111111111111111", At.AddHours(-2), At.AddHours(-1));
        new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath).TryAppendDirective(row, "source");
        var before = Hashes(fixture.Workspace.RootDirectory);
        using var output = new StringWriter();
        Assert.Equal(0, CliDirectiveCommands.Run(["directives"], fixture.Workspace, output, new FixedTime()));
        Assert.Equal($"{row.Id} | scope | {row.RecordedAt:O} | owner | expiry={row.ExpiresAt:O} | expired{Environment.NewLine}" +
            row.Text + Environment.NewLine, output.ToString());
        Assert.Equal(before, Hashes(fixture.Workspace.RootDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptStore_ListingFailsWithoutRepair(bool json)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Directory.CreateDirectory(fixture.Workspace.OrchestratorDirectory);
        File.WriteAllText(fixture.Workspace.OperatorDirectivesStorePath, "not sqlite");
        var before = Hashes(fixture.Workspace.RootDirectory);
        using var output = new StringWriter();
        Assert.Equal(1, CliDirectiveCommands.Run(json ? ["directives", "--json"] : ["directives"], fixture.Workspace, output));
        Assert.Equal("", output.ToString());
        Assert.Equal(before, Hashes(fixture.Workspace.RootDirectory));
    }

    private static JsonElement Envelope(JsonElement root)
    {
        Assert.Equal(["schema", "schemaVersion", "generatedAt", "payload"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("mcg.directives", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(At, DateTimeOffset.Parse(root.GetProperty("generatedAt").GetString()!, CultureInfo.InvariantCulture));
        var payload = root.GetProperty("payload");
        Assert.Equal(["directives"], payload.EnumerateObject().Select(p => p.Name).ToArray());
        return payload;
    }

    private static OperatorDirective Row(string id, DateTimeOffset recorded, DateTimeOffset? expires) =>
        new(id, "scope", "Keep the current scope", "owner", OperatorActorKind.Human, "cli", recorded,
            expires, false, null, null, null, false);

    private static string[] Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }
}
