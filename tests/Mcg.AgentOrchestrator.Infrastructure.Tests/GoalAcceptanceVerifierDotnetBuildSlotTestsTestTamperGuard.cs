using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsTestTamperGuard : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_deleted_fact_method")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsDeletedFactMethod()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }"
        ]);

        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        // Suite still passed — advisory does not gate
        Assert.True(result.Passed);
        Assert.Equal(0, result.ExitCode);

        // Tamper check is advisory and failed
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.True(tamperCheck.OutputTail is not null);
        Assert.Contains("FooTests.cs", tamperCheck.OutputTail!, StringComparison.Ordinal);

        // Git diff was the last call
        var lastCall = calls.Last();
        Assert.Equal("git", lastCall[0]);
        Assert.Equal("diff", lastCall[1]);
        Assert.True(lastCall.Any(a => a.Equals("main...HEAD", StringComparison.Ordinal)));
        Assert.True(lastCall.Any(a => a.Equals("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public async Task TestTamperGuard_DeclaredRemoval_Passes()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }"
        ]);
        var criteria = """
            [
              {
                "name": "test-removal: Example.Tests.FooTests.SomeTest",
                "type": "test-removal",
                "testIdentity": "Example.Tests.FooTests.SomeTest"
              }
            ]
            """;

        var result = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(diff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"],
            criteria);

        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Passed, tamperCheck.OutputTail);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
        Assert.DoesNotContain(result.Checks, check => check.Name.StartsWith("test-removal:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task TestTamperGuard_DeclaredRemovalFromClassNotNamedAfterFile_Passes()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs",
            "@@ -10,5 +10,0 @@ public sealed class GoalAcceptanceVerifierDotnetBuildSlotTests : GoalAcceptanceVerifierTestBase",
            "-    [Xunit.Fact]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }"
        ]);
        var criteria = """
            [
              {
                "name": "test-removal: Example.Tests.GoalAcceptanceVerifierDotnetBuildSlotTests.SomeTest",
                "type": "test-removal",
                "testIdentity": "Example.Tests.GoalAcceptanceVerifierDotnetBuildSlotTests.SomeTest"
              }
            ]
            """;

        var result = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(diff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs"],
            criteria);

        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Passed, tamperCheck.OutputTail);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact]
    public async Task TestTamperGuard_DeclaredRemovalFromWhollyDeletedSplitFile_Passes()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PortfolioCommands.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PortfolioCommands.cs",
            "deleted file mode 100644",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PortfolioCommands.cs",
            "+++ /dev/null",
            "@@ -1,9 +0,0 @@",
            "-public sealed class CliCommandTestsPortfolioCommands : CliCommandTestBase",
            "-{",
            "-    [Xunit.Fact]",
            "-    public void EpicAndProjectCommandsAssignMembersAndPrintRollups()",
            "-    {",
            "-        Assert.True(something);",
            "-    }",
            "-}"
        ]);
        var criteria = """
            [
              {
                "name": "test-removal: CliCommandTestsPortfolioCommands.EpicAndProjectCommandsAssignMembersAndPrintRollups",
                "type": "test-removal",
                "testIdentity": "CliCommandTestsPortfolioCommands.EpicAndProjectCommandsAssignMembersAndPrintRollups"
              }
            ]
            """;

        var result = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(diff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PortfolioCommands.cs"],
            criteria);

        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Passed, tamperCheck.OutputTail);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact]
    public async Task TestTamperGuard_DeclaredRemovalWithLambdaAssertions_Passes()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LandingExecutorTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LandingExecutorTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LandingExecutorTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LandingExecutorTests.cs",
            "@@ -10,6 +10,0 @@ public sealed class LandingExecutorTests",
            "-    [Xunit.Fact]",
            "-    public void RemoteMirrorTwoRemotesPushesReachableAndDefersUnreachable()",
            "-    {",
            "-        Assert.Contains(outcomes, outcome => outcome.Succeeded);",
            "-        Assert.Contains(outcomes, outcome => outcome.Failed);",
            "-    }"
        ]);
        var criteria = """
            [
              {
                "name": "test-removal: Example.Tests.LandingExecutorTests.RemoteMirrorTwoRemotesPushesReachableAndDefersUnreachable",
                "type": "test-removal",
                "testIdentity": "Example.Tests.LandingExecutorTests.RemoteMirrorTwoRemotesPushesReachableAndDefersUnreachable"
              }
            ]
            """;

        var result = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(diff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LandingExecutorTests.cs"],
            criteria);

        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Passed, tamperCheck.OutputTail);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact]
    public async Task TestTamperGuard_WrongClassDeclaration_Fails()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@ public sealed class FooTests",
            "-    [Xunit.Fact]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }"
        ]);
        var criteria = """
            [
              {
                "name": "test-removal: Example.Tests.OtherTests.SomeTest",
                "type": "test-removal",
                "testIdentity": "Example.Tests.OtherTests.SomeTest"
              }
            ]
            """;

        var result = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(diff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"],
            criteria);

        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.False(tamperCheck.Passed);
        Assert.Contains("tests -1/+0", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("net -1 assertion", tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task TestTamperGuard_DeclaredRemoval_DoesNotMaskWeakening()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }",
            "@@ -30,1 +25,0 @@ public void ExistingTest()",
            "-        Assert.True(stillRequired);"
        ]);
        var criteria = """
            [
              {
                "name": "test-removal: Example.Tests.FooTests.SomeTest",
                "type": "test-removal",
                "testIdentity": "Example.Tests.FooTests.SomeTest"
              }
            ]
            """;

        var result = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(diff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"],
            criteria);

        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.False(tamperCheck.Passed);
        Assert.Equal("1 test degradation signal(s)", tamperCheck.ResultSummary);
        Assert.Contains("net -1 assertion", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.DoesNotContain("test method(s) removed", tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task TestTamperGuardAllowsQualifiedFactRename()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,2 +10,2 @@",
            "-    [Xunit.Fact(DisplayName = \"old test\")]",
            "-    public void OldTest()",
            "+    [Xunit.Fact]",
            "+    public void RenamedTest()"
        ]);

        var result = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(diff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Passed);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Theory]
    [Xunit.InlineData("[Fact]")]
    [Xunit.InlineData("[Fact(DisplayName = \"test\")]")]
    [Xunit.InlineData("[Theory]")]
    [Xunit.InlineData("[Theory(DisplayName = \"test\")]")]
    [Xunit.InlineData("[Xunit.Fact]")]
    [Xunit.InlineData("[Xunit.Fact(DisplayName = \"test\")]")]
    [Xunit.InlineData("[Xunit.Theory]")]
    [Xunit.InlineData("[Xunit.Theory(DisplayName = \"test\")]")]
    [Xunit.InlineData("[Fact, Trait(\"category\", \"guard\")]")]
    public async Task TestTamperGuardCountsSupportedAttributes(string attribute)
    {
        var removedDiff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,2 +10,0 @@",
            $"-    {attribute}",
            "-    public void RemovedTest()"
        ]);

        var removedResult = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(removedDiff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);
        var removedCheck = removedResult.Checks!.Single(c => c.Name == "test tamper guard");

        Assert.False(removedCheck.Passed);
        Assert.Contains("tests -1/+0", removedCheck.OutputTail!, StringComparison.Ordinal);

        var balancedDiff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,2 +10,2 @@",
            "-    [Xunit.Fact(DisplayName = \"old test\")]",
            "-    public void RemovedTest()",
            $"+    {attribute}",
            "+    public void AddedTest()"
        ]);

        var balancedResult = await RunTamperGuardAsync(
            CreateTamperGuardVerifier(balancedDiff),
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);
        var balancedCheck = balancedResult.Checks!.Single(c => c.Name == "test tamper guard");

        Assert.True(balancedCheck.Passed);
        Assert.Equal("no test degradation detected", balancedCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_passes_when_assertions_added")]
    public async Task GoalAcceptanceVerifierTestTamperGuardPassesWhenAssertionsAdded()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,0 +10,5 @@",
            "+    [Xunit.Fact(DisplayName = \"new test\")]",
            "+    public void NewTest()",
            "+    {",
            "+        Assert.True(something);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.True(tamperCheck.Passed);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_allows_mechanical_test_split")]
    public async Task GoalAcceptanceVerifierTestTamperGuardAllowsMechanicalTestSplit()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }",
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "--- /dev/null",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "@@ -0,0 +1,5 @@",
            "+    [Xunit.Fact(DisplayName = \"some test\")]",
            "+    public void SomeTest()",
            "+    {",
            "+        Assert.True(something);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs"
            ]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.True(tamperCheck.Passed);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_diff_wide_net_removal")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsDiffWideNetRemoval()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,10 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"first test\")]",
            "-    public void FirstTest()",
            "-    {",
            "-        Assert.True(first);",
            "-    }",
            "-    [Xunit.Fact(DisplayName = \"second test\")]",
            "-    public void SecondTest()",
            "-    {",
            "-        Assert.True(second);",
            "-    }",
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "--- /dev/null",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "@@ -0,0 +1,5 @@",
            "+    [Xunit.Fact(DisplayName = \"first test\")]",
            "+    public void FirstTest()",
            "+    {",
            "+        Assert.True(first);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs"
            ]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.Equal("2 test degradation signal(s)", tamperCheck.ResultSummary);
        Assert.Contains("diff-wide net -1 assertion(s) removed", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("diff-wide 1 test method(s) removed", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("FooTests.cs: assertions -2/+0, tests -2/+0", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("SplitFooTests.cs: assertions -0/+1, tests -0/+1", tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_tautology_assertion")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsTautologyAssertion()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -12,1 +12,1 @@",
            "-        Assert.True(something);",
            "+        Assert.True(true);"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.True(tamperCheck.OutputTail is not null);
        Assert.Contains("tautology", tamperCheck.OutputTail!, StringComparison.OrdinalIgnoreCase);
        var tautologyAssertion = "Assert." + "True(true)";
        Assert.Contains(tautologyAssertion, tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_tautology_across_files")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsTautologyAcrossFiles()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }",
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "--- /dev/null",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "@@ -0,0 +1,5 @@",
            "+    [Xunit.Fact(DisplayName = \"some test\")]",
            "+    public void SomeTest()",
            "+    {",
            "+        Assert.True(true);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs"
            ]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.Equal("1 test degradation signal(s)", tamperCheck.ResultSummary);
        Assert.Contains("SplitFooTests.cs", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("tautology", tamperCheck.OutputTail!, StringComparison.OrdinalIgnoreCase);
        var tautologyAssertion = "Assert." + "True(true)";
        Assert.Contains(tautologyAssertion, tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_absent_when_no_test_files_in_diff")]
    public async Task GoalAcceptanceVerifierTestTamperGuardAbsentWhenNoTestFilesInDiff()
    {
        var calls = new List<string[]>();
        var root = CreatePartitionedInfrastructureManifestWorkspace();

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            return Task.FromResult(args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: [
                "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs",
                "README.md"
            ]);

        Assert.True(result.Passed);
        Assert.False(result.Checks?.Any(c => c.Name == "test tamper guard") == true);
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = CountChangeScopedInfrastructureTestLanes(root);
        Assert.Equal(laneCount, infrastructureCalls.Length);
        foreach (var call in infrastructureCalls)
            Assert.True(
                call.Contains("--filter-class") || call.Contains("--filter-not-class"),
                "Each infrastructure shard must carry a translated MTP class filter.");
        // Each MTP shard runs a `dotnet build` before its executable, and there is no git diff
        // call because no test files changed: lane count * (build + executable).
        Assert.Equal(laneCount * 2, calls.Count);
        Assert.DoesNotContain(calls, call => call.Length >= 2 && call[0] == "git" && call[1] == "diff");
        DeleteDirectoryWithRetry(root);
    }

    private static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "acceptance-manifest.json"),
            AcceptanceManifestTestDefaults.WithEngine(manifest));
        return root;
    }

    private static async Task<AcceptanceVerificationResult> RunTamperGuardAsync(
        GoalAcceptanceVerifier verifier,
        IReadOnlyList<string> changedFiles,
        string? criteriaJson = null)
    {
        var root = CreatePartitionedInfrastructureManifestWorkspace();
        try
        {
            if (!string.IsNullOrWhiteSpace(criteriaJson))
            {
                var orchestratorDirectory = Path.Combine(root, ".orchestrator");
                Directory.CreateDirectory(orchestratorDirectory);
                File.WriteAllText(
                    Path.Combine(orchestratorDirectory, "goal-acceptance-criteria.json"),
                    criteriaJson);
            }

            return await verifier.RunAsync(root, changedFiles: changedFiles);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static GoalAcceptanceVerifier CreateTamperGuardVerifier(string diff) => new((args, _, _) =>
    {
        if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
        {
            WriteMtpTrx(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
        }

        if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
        {
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
        }

        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
    });

}
