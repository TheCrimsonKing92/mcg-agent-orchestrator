using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class MaterializationScriptedGitRunner
{
    private readonly (string[] ExpectedArguments, GitCli.GitResult Result)[] entries;

    public MaterializationScriptedGitRunner(
        IEnumerable<(string[] ExpectedArguments, GitCli.GitResult Result)> entries)
    {
        this.entries = entries
            .Select(entry => (entry.ExpectedArguments.ToArray(), entry.Result))
            .ToArray();
    }

    public Func<string, string[], GitCli.GitResult> Runner => Run;

    public int CallCount { get; private set; }

    public GitCli.GitResult Run(string workingDirectory, string[] arguments)
    {
        var call = $"call {CallCount} in '{workingDirectory}': [{string.Join(" ", arguments)}]";
        if (CallCount == entries.Length)
            throw new InvalidOperationException($"Unexpected git {call}; script exhausted.");

        var entry = entries[CallCount];
        if (!arguments.SequenceEqual(entry.ExpectedArguments, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Unexpected git {call}; expected [{string.Join(" ", entry.ExpectedArguments)}].");

        CallCount++;
        return entry.Result;
    }

    public void AssertConsumed()
    {
        if (CallCount != entries.Length)
        {
            var remaining = string.Join("; ", entries.Skip(CallCount)
                .Select(entry => $"[{string.Join(" ", entry.ExpectedArguments)}]"));
            throw new InvalidOperationException(
                $"Git script has {entries.Length - CallCount} unconsumed entries: {remaining}.");
        }
    }
}

internal sealed record MaterializationScriptedScenario(
    string Directory,
    string FixturePath,
    MaterializationScriptedGitRunner Runner,
    int ScriptedCallCount) : IDisposable
{
    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

internal static class MaterializationScriptedScenarios
{
    public static MaterializationScriptedScenario DrainTimeout() => Create(
    [
        (["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            new GitCli.GitResult(0, string.Empty, string.Empty)),
        (["ls-files", "--eol", "-z"],
            new GitCli.GitResult(0, "i/lf    w/crlf  attr/-text \tfixture.txt\0", string.Empty)),
        (["rev-parse", "HEAD"],
            new GitCli.GitResult(0, string.Empty, string.Empty, DrainTimedOut: true))
    ]);

    public static MaterializationScriptedScenario ScanFailure() => Create(
    [
        (["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            new GitCli.GitResult(0, string.Empty, string.Empty)),
        (["ls-files", "--eol", "-z"],
            new GitCli.GitResult(19, string.Empty, "injected tracked-file scan refusal"))
    ]);

    public static MaterializationScriptedScenario MalformedReceipt() => Create(
    [
        (["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            new GitCli.GitResult(0, string.Empty, string.Empty)),
        (["ls-files", "--eol", "-z"],
            new GitCli.GitResult(0, "i/lf    w/crlf    malformed\tfixture.txt\0", string.Empty))
    ]);

    private static MaterializationScriptedScenario Create(
        (string[] ExpectedArguments, GitCli.GitResult Result)[] entries)
    {
        var directory = Directory.CreateTempSubdirectory("mcg-materialization-script-").FullName;
        try
        {
            var fixturePath = Path.Combine(directory, "fixture.txt");
            File.WriteAllBytes(fixturePath, Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n"));
            return new MaterializationScriptedScenario(
                directory, fixturePath, new MaterializationScriptedGitRunner(entries), entries.Length);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }
}
