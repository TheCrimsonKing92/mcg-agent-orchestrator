using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class RedirectedStreamDrainSourceGuardTests(Xunit.ITestOutputHelper output)
{
    private const int InitialAllowListCount = 20;
    private static readonly HashSet<(string File, string Method)> AllowList =
    [
        ("AcceptanceLaneClosureHasherTests.cs", "RunGit"),
        ("AcceptancePartitionVerdictCacheContentReuseTests.cs", "RunGit"),
        ("CliCommandTests.cs", "RunAppCli"),
        ("CliCommandTests.cs", "RunGitOutput"),
        ("DispatchProcessHostTests.cs", "RunGit"),
        ("GitCliTests.cs", "RunGit"),
        ("GoalsPruneTests.cs", "RunGitExitCode"),
        ("GoalWorktreeTests.cs", "RunInvokeRepoGit"),
        ("GoalWorktreeTests.cs", "RunGitOutput"),
        ("LauncherScriptTests.cs", "ResolveRunDirConcurrentPublishersExposeOnlyOneCompleteClosure"),
        ("LauncherScriptTests.cs", "AssertStartOrchestratorCommandReceipt"),
        ("LauncherScriptTests.cs", "StartOrchestratorCommandForwardsDoubleDashArgumentsToBackgroundProcess"),
        ("LauncherScriptTests.cs", "StartOrchestratorCommandLaunchFailureExitsNonzeroWithErrorJsonOnStderr"),
        ("LauncherScriptTests.cs", "StartOrchestratorCommandKeepsAppDllNamedOnlyAndForwardsRemainingArguments"),
        ("LauncherScriptTests.cs", "RunProcess"),
        ("PostLandingCanaryBuildLifecycleTests.cs", "Git"),
        ("WorkerContextArtifactsCharacterizationTests.cs", "RunGit"),
        ("WorkerDispatchTests.cs", "ReadGit"),
        ("WorkerDispatchTests.cs", "RunPowerShellCommand"),
        ("Fixtures/ConsoleIoProbe/StartupPipeProbe.cs", "StartAndReturnScope")
    ];

    [Fact]
    public void AllowListCannotGrow()
    {
        Assert.True(AllowList.Count <= InitialAllowListCount,
            $"Redirected stream drain allow-list grew from {InitialAllowListCount} to {AllowList.Count}.");
    }

    [Fact]
    public void TestProjectHasNoUnapprovedSequentialRedirectedStreamReads()
    {
        var testRoot = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        var matched = new HashSet<(string File, string Method)>();
        var failures = new List<string>();
        foreach (var path in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            var file = Path.GetRelativePath(testRoot, path).Replace('\\', '/');
            if (file.Split('/').Any(part => part is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"))
                continue;
            foreach (var (method, reason) in FindOffenders(File.ReadAllText(path)))
            {
                var key = (file, method);
                if (AllowList.Contains(key))
                    matched.Add(key);
                else
                    failures.Add($"{file}::{method}: {reason}");
            }
        }

        foreach (var stale in AllowList.Except(matched).OrderBy(entry => entry.File).ThenBy(entry => entry.Method))
            output.WriteLine($"Stale redirected-stream allow-list entry: {stale.File}::{stale.Method}");
        Assert.Empty(failures);
    }

    [Fact]
    public void SyntaxAnalysisFlagsSequentialReadsAndWaitBeforeReadButIgnoresLiteralsAndConcurrentReads()
    {
        const string sequential = "class C { void M() { var a = p.StandardOutput.ReadToEnd(); var b = p.StandardError.ReadToEnd(); } }";
        const string waitFirst = "class C { void M() { p.WaitForExit(30000); var a = p.StandardOutput.ReadToEnd(); } }";
        const string concurrent = "class C { void M() { var a = p.StandardOutput.ReadToEndAsync(); var b = p.StandardError.ReadToEndAsync(); p.WaitForExit(30000); } }";
        const string literal = "class C { void M() { var script = \"p.WaitForExit(); p.StandardOutput.ReadToEnd();\"; } }";

        Assert.Single(FindOffenders(sequential));
        Assert.Single(FindOffenders(waitFirst));
        Assert.Empty(FindOffenders(concurrent));
        Assert.Empty(FindOffenders(literal));
    }

    private static IReadOnlyList<(string Method, string Reason)> FindOffenders(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = new List<(string Method, string Reason)>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression is MemberAccessExpressionSyntax).ToArray();
            var stdoutSync = calls.Any(call => IsRead(call, "StandardOutput", synchronousOnly: true));
            var stderrSync = calls.Any(call => IsRead(call, "StandardError", synchronousOnly: true));
            var waits = calls.Where(call => CallName(call) == "WaitForExit")
                .Select(call => call.SpanStart).ToArray();
            var readAfterWait = waits.Length > 0 && calls.Any(call =>
                call.SpanStart > waits.Min() &&
                (IsRead(call, "StandardOutput") || IsRead(call, "StandardError")));
            if (stdoutSync && stderrSync || readAfterWait)
                findings.Add((method.Identifier.ValueText,
                    readAfterWait ? "WaitForExit precedes a redirected stream read" :
                        "stdout and stderr are read synchronously in one method"));
        }
        return findings;
    }

    private static bool IsRead(InvocationExpressionSyntax call, string stream, bool synchronousOnly = false)
    {
        var name = CallName(call);
        if (synchronousOnly ? name != "ReadToEnd" : !name.StartsWith("Read", StringComparison.Ordinal))
            return false;
        return call.Expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Any(access => access.Name.Identifier.ValueText == stream);
    }

    private static string CallName(InvocationExpressionSyntax call) =>
        ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText;
}
