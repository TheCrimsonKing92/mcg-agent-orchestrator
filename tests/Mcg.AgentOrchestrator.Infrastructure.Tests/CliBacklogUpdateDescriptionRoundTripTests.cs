using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every handler invocation owns a unique temp workspace and SQLite backlog.
// CaptureConsole uses the repository's scoped console capture; no process-global console mutation.
public sealed class CliBacklogUpdateDescriptionRoundTripTests : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task InlineDescriptionPreservesSpacesAndUnknownOptionWords(bool interactive)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        const string description = "alpha  beta   gamma --keep-this-word and more";
        var output = ExecuteCliAndCapture(Parse(interactive, item.Id[..8], "--description", description), kernel, workspace);

        Xunit.Assert.Equal(description, (await store.GetByExactIdAsync(item.Id))!.Body);
        Xunit.Assert.Contains($"Updated: [{item.Id}]", output);
    }

    [Xunit.Fact]
    public async Task ShellSplitDescriptionKeepsUnknownOptionWords()
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        var parts = CliArgumentParser.NormalizeArgs(
            ["backlog-update", item.Id[..8], "--description", "alpha", "beta", "gamma", "--keep-this-word", "and", "more"]);

        ExecuteCliAndCapture(parts, kernel, workspace);

        Xunit.Assert.Equal("alpha beta gamma --keep-this-word and more", (await store.GetByExactIdAsync(item.Id))!.Body);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task InlineDescriptionPreservesBoundaryWhitespaceAndNewlines(bool interactive)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        const string description = "  alpha\tbeta\r\ngamma  ";
        ExecuteCliAndCapture(Parse(interactive, item.Id[..8], "--description", description, "--priority", "high"), kernel, workspace);
        Xunit.Assert.Equal(description, (await store.GetByExactIdAsync(item.Id))!.Body);

        // The final value also bypasses SplitCommand's shared trailing-whitespace trim.
        ExecuteCliAndCapture(Parse(interactive, item.Id[..8], "--description", description), kernel, workspace);
        Xunit.Assert.Equal(description, (await store.GetByExactIdAsync(item.Id))!.Body);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "--text-file")]
    [Xunit.InlineData(false, "--text-file")]
    [Xunit.InlineData(true, "--body-file")]
    [Xunit.InlineData(false, "--body-file")]
    public async Task FileDescriptionRoundTripsAllThreeLines(bool interactive, string flag)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        var path = Path.Combine(workspace.ExecutionDirectory, "body with spaces.txt");
        const string description = "LINE-ONE-MARKER\nLINE-TWO-MARKER\nLINE-THREE-MARKER";
        await File.WriteAllTextAsync(path, description, new UTF8Encoding(false));

        var output = ExecuteCliAndCapture(Parse(interactive, item.Id[..8], flag, path), kernel, workspace);
        var stored = (await store.GetByExactIdAsync(item.Id))!.Body;

        Xunit.Assert.Equal(description, stored);
        Xunit.Assert.Equal(await File.ReadAllBytesAsync(path), Encoding.UTF8.GetBytes(stored));
        Xunit.Assert.Contains($"Updated: [{item.Id}]", output);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "--text-file", "")]
    [Xunit.InlineData(false, "--text-file", "")]
    [Xunit.InlineData(true, "--body-file", "alpha\r\n--status done\r\n  ")]
    [Xunit.InlineData(false, "--body-file", "alpha\r\n--status done\r\n  ")]
    public async Task FileDescriptionPreservesEmptyContentAndCrLf(bool interactive, string flag, string description)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        var path = Path.Combine(workspace.ExecutionDirectory, "body.txt");
        await File.WriteAllTextAsync(path, description, new UTF8Encoding(false));

        ExecuteCliAndCapture(Parse(interactive, item.Id[..8], flag, path), kernel, workspace);

        var stored = (await store.GetByExactIdAsync(item.Id))!.Body;
        Xunit.Assert.Equal(description, stored);
        Xunit.Assert.Equal(await File.ReadAllBytesAsync(path), Encoding.UTF8.GetBytes(stored));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "--text-file", "--description")]
    [Xunit.InlineData(false, "--text-file", "--description")]
    [Xunit.InlineData(true, "--body-file", "--description")]
    [Xunit.InlineData(false, "--body-file", "--description")]
    [Xunit.InlineData(true, "--text-file", "--body-file")]
    [Xunit.InlineData(false, "--text-file", "--body-file")]
    public async Task ConflictingDescriptionSourcesRejectWithoutWriting(bool interactive, string first, string second)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        var path = Path.Combine(workspace.ExecutionDirectory, "body.txt");
        await File.WriteAllTextAsync(path, "replacement");
        var parts = Parse(interactive, item.Id[..8], "--title", "partial title", first, path,
            second, second == "--description" ? "other" : path);

        var (error, output) = CaptureFailure(parts, kernel, workspace);

        Xunit.Assert.IsAssignableFrom<ArgumentException>(error);
        Xunit.Assert.Contains(first, error.Message);
        Xunit.Assert.Contains(second, error.Message);
        Xunit.Assert.DoesNotContain("Updated:", output);
        AssertUnchanged(item, await store.GetByExactIdAsync(item.Id));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "missing")]
    [Xunit.InlineData(false, "missing")]
    [Xunit.InlineData(true, "directory")]
    [Xunit.InlineData(false, "directory")]
    [Xunit.InlineData(true, "locked")]
    [Xunit.InlineData(false, "locked")]
    public async Task FileReadFailureRejectsBeforeAnyFieldWrite(bool interactive, string failure)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        var path = Path.Combine(workspace.ExecutionDirectory, "unreadable.txt");
        if (failure == "directory")
            Directory.CreateDirectory(path);
        using var locked = failure == "locked"
            ? new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None)
            : null;

        var (error, output) = CaptureFailure(
            Parse(interactive, item.Id[..8], "--title", "partial title", "--text-file", path), kernel, workspace);

        Xunit.Assert.IsAssignableFrom<InvalidOperationException>(error);
        Xunit.Assert.Contains("--text-file", error.Message);
        Xunit.Assert.Contains(path, error.Message);
        Xunit.Assert.DoesNotContain("Updated:", output);
        AssertUnchanged(item, await store.GetByExactIdAsync(item.Id));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, new[] { "--description" })]
    [Xunit.InlineData(false, new[] { "--description" })]
    [Xunit.InlineData(true, new[] { "--description", "--status", "done" })]
    [Xunit.InlineData(false, new[] { "--description", "--status", "done" })]
    [Xunit.InlineData(true, new[] { "--description", "first", "--description", "second" })]
    [Xunit.InlineData(false, new[] { "--description", "first", "--description", "second" })]
    [Xunit.InlineData(true, new[] { "--description", "--unrepresentable" })]
    [Xunit.InlineData(false, new[] { "--description", "--unrepresentable" })]
    [Xunit.InlineData(true, new[] { "--text-file" })]
    [Xunit.InlineData(false, new[] { "--body-file" })]
    public async Task InvalidValuesRejectWithoutPartialWrites(bool interactive, string[] arguments)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        var parts = Parse(interactive, item.Id[..8], ["--title", "partial title", .. arguments]);

        var (error, output) = CaptureFailure(parts, kernel, workspace);

        Xunit.Assert.IsAssignableFrom<ArgumentException>(error);
        Xunit.Assert.Contains(arguments[0], error.Message);
        Xunit.Assert.DoesNotContain("Updated:", output);
        AssertUnchanged(item, await store.GetByExactIdAsync(item.Id));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task UsageAndHandlerErrorsListBothFileFlags(bool omitTarget)
    {
        var (workspace, _, item, kernel) = await CreateFixture();
        string[] parts = omitTarget ? ["backlog-update"] : ["backlog-update", item.Id[..8]];
        var (error, output) = CaptureFailure(parts, kernel, workspace);

        Xunit.Assert.IsAssignableFrom<ArgumentException>(error);
        Xunit.Assert.Equal(CliCommandHelp.BacklogUpdateUsage, error.Message);
        foreach (var flag in new[] { "--text-file", "--body-file" })
        {
            Xunit.Assert.Contains(flag, CliCommandHelp.BacklogUpdateUsage);
            Xunit.Assert.Contains(flag, error.Message);
        }
        Xunit.Assert.DoesNotContain("Updated:", output);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "--help")]
    [Xunit.InlineData(false, "--help")]
    [Xunit.InlineData(true, "-h")]
    [Xunit.InlineData(false, "-h")]
    public async Task HelpRemainsAFlagAfterADescription(bool interactive, string help)
    {
        var (workspace, store, item, kernel) = await CreateFixture();
        var output = ExecuteCliAndCapture(Parse(interactive, item.Id[..8], "--description", "replacement", help), kernel, workspace);

        Xunit.Assert.Contains(CliCommandHelp.BacklogUpdateUsage, output);
        Xunit.Assert.DoesNotContain("Updated:", output);
        AssertUnchanged(item, await store.GetByExactIdAsync(item.Id));
    }

    private async Task<(OrchestratorWorkspace, BacklogStore, BacklogItem, AgentOrchestratorKernel)> CreateFixture()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Original title", "Original body");
        item = (await store.GetByExactIdAsync(item.Id))!;
        return (workspace, store, item, new AgentOrchestratorKernel());
    }

    private static void AssertUnchanged(BacklogItem original, BacklogItem? current) =>
        Xunit.Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(current));

    private static IReadOnlyList<string> Parse(bool interactive, string prefix, params string[] arguments) =>
        interactive
            ? CliArgumentParser.SplitCommand($"backlog-update {prefix} {string.Join(' ', arguments)}")
            : CliArgumentParser.NormalizeArgs(["backlog-update", prefix, .. arguments]);

    private static (Exception Error, string Output) CaptureFailure(
        IReadOnlyList<string> parts, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        Exception? error = null;
        var output = CaptureConsole(() => error = Xunit.Record.Exception(() => CliCommandDispatcher.ExecuteCommand(
            parts, kernel, workspace, ref agents, providers, ref profiles, ref currentGoal)));
        Xunit.Assert.NotNull(error);
        return (error, output);
    }
}
