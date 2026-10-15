using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildProjectScopeProcessSpawningTests
{
    [Xunit.Fact]
    public void TestsOnlyFollowupAfterRenamePublishesFreshCompleteCoverageAndAdmitsWithoutFallback()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var isolatedRoot = Path.Combine(fixture.Root, "outputs");
        fixture.Write(".gitignore", ".orchestrator-context/\noutputs/\n**/bin/\n**/obj/\n");
        foreach (var name in new[] { "Core", "Execution", "App", "Tests" }) fixture.Change(name);
        fixture.Commit();
        var helper = Path.Combine(RepositoryRoot(), "scripts", "Invoke-WorkerBuildCheck.ps1");
        var receiptPath = Path.Combine(isolatedRoot, "goals", fixture.Prefix, "artifacts", "worker-build-receipt.json");
        var initial = Build(helper, fixture.Root, isolatedRoot, WorkerBuildProjectScopeTests.Fixture.AllProjects);
        Assert.True(initial.ExitCode == 0, initial.Stdout + initial.Stderr);
        using var before = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
        fixture.Git("mv", "Tests/Tests.cs", "Tests/RenamedTests.cs");
        fixture.Write("Tests/RenamedTests.cs", "public class RenamedTests { public int Changed => 1; }");
        Assert.False(WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root).Matches);

        var followup = Build(helper, fixture.Root, isolatedRoot, ["Tests/Tests.csproj"]);

        Assert.True(followup.ExitCode == 0, followup.Stdout + followup.Stderr);
        using var after = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
        Assert.Equal(WorkerBuildProjectScope.Resolve(fixture.Root, []).StableMetadataDigest,
            after.RootElement.GetProperty("scopeStableDigest").GetString());
        Assert.NotEqual(before.RootElement.GetProperty("treeDigest").GetString(), after.RootElement.GetProperty("treeDigest").GetString());
        Assert.Equal(WorkerBuildProjectScopeTests.Fixture.AllProjects,
            after.RootElement.GetProperty("projects").EnumerateArray().Select(element => element.GetString()!.Replace('\\', '/')));
        var executions = after.RootElement.GetProperty("projectResults").EnumerateArray().ToArray();
        Assert.Equal(4, executions.Length);
        Assert.All(executions, execution =>
        {
            Assert.Equal(0, execution.GetProperty("exitCode").GetInt32());
            Assert.True(File.Exists(execution.GetProperty("logPath").GetString()));
        });
        var fallbacks = 0;
        var admission = OrchestratorBuildEvidenceCheck.Resolve(fixture.Root, AgentRole.Developer, [], [], false,
            () => WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root), _ => { fallbacks++; return new(false, -1, "unexpected fallback"); });
        Assert.False(admission.FailsRound, admission.Diagnostic);
        Assert.Equal(0, fallbacks);
        fixture.SetMetadata(fixture.Metadata() with { IntegrationBranch = "other" });
        Assert.Equal("scope-mismatch", WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root).Reason);
    }

    [Xunit.Fact]
    public void RequiredRootCompilerFailureCannotPublishSuccess()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var isolatedRoot = Path.Combine(fixture.Root, "outputs");
        fixture.Write(".gitignore", ".orchestrator-context/\noutputs/\n**/bin/\n**/obj/\n");
        fixture.Write("Core/Core.cs", "this is not valid C#");
        var result = Build(Path.Combine(RepositoryRoot(), "scripts", "Invoke-WorkerBuildCheck.ps1"),
            fixture.Root, isolatedRoot, ["Tests/Tests.csproj"]);
        Assert.NotEqual(0, result.ExitCode);
        var receiptPath = Path.Combine(isolatedRoot, "goals", fixture.Prefix, "artifacts", "worker-build-receipt.json");
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
        Assert.Equal("failure", receipt.RootElement.GetProperty("buildOutcome").GetString());
        Assert.Equal("build-failed", WorkerBuildReceipt.Evaluate(receiptPath, fixture.Root).Reason);
    }

    private static TestChildProcessCapture.Result Build(string helper, string root, string isolatedRoot, string[] projects)
    {
        var start = new ProcessStartInfo(WorkerShell.Executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper }.Concat(projects)) start.ArgumentList.Add(argument);
        start.Environment["MCG_DOTNET_ISOLATED_ROOT"] = isolatedRoot;
        return TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(3), start: info =>
        {
            var process = Process.Start(info);
            process?.StandardInput.Close();
            return process;
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void SourceOrMetadataMutationDuringBuildCannotPublishSuccess(bool mutateMetadata)
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var isolatedRoot = Path.Combine(fixture.Root, "outputs");
        fixture.Write(".gitignore", ".orchestrator-context/\noutputs/\n**/bin/\n**/obj/\n");
        fixture.Change("Core");
        var mutationPath = mutateMetadata ? WorkerBuildProjectScope.MetadataPath(fixture.Root) : Path.Combine(fixture.Root, "Core", "Core.cs");
        // Replace metadata with valid JSON or append a valid C# comment after the scope was captured.
        var escapedPath = System.Security.SecurityElement.Escape(mutationPath);
        var payload = mutateMetadata ? JsonSerializer.Serialize(fixture.Metadata() with { DispatchIdentity = Guid.NewGuid().ToString("N") }) : "// source changed during build";
        var escapedPayload = System.Security.SecurityElement.Escape(payload);
        fixture.Write("Core/Core.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name=\"MutateInput\" BeforeTargets=\"BeforeBuild\"><WriteLinesToFile File=\"{escapedPath}\" Lines=\"{escapedPayload}\" Overwrite=\"{mutateMetadata.ToString().ToLowerInvariant()}\" /></Target></Project>");

        var result = Build(Path.Combine(RepositoryRoot(), "scripts", "Invoke-WorkerBuildCheck.ps1"), fixture.Root, isolatedRoot, ["Tests/Tests.csproj"]);

        Assert.NotEqual(0, result.ExitCode);
        var receiptPath = Path.Combine(isolatedRoot, "goals", fixture.Prefix, "artifacts", "worker-build-receipt.json");
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
        Assert.Equal("failure", receipt.RootElement.GetProperty("buildOutcome").GetString());
        Assert.All(receipt.RootElement.GetProperty("projectResults").EnumerateArray(), item => Assert.Equal(0, item.GetProperty("exitCode").GetInt32()));
        Assert.Contains(mutateMetadata ? "scope identity changed" : "worktree identity changed", result.Stdout, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root) ? root : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
}
