using Mcg.AgentOrchestrator.App.Cli;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class TrialCompareCliCommandTests
{
    [Xunit.Fact]
    public void ParseSpecAcceptsNamedHarnessesAndOperatorOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var receipts = Path.Combine(root, "receipts");
            var json = $$"""
                {
                  "sourceRepositoryPath": "{{root.Replace("\\", "\\\\")}}",
                  "baseCommit": "abc123",
                  "protectedPaths": ["protected.txt"],
                  "harnesses": [
                    { "name": "alpha", "fileName": "alpha.exe", "arguments": ["one"], "environment": { "MODE": "a" } },
                    { "name": "beta", "fileName": "beta.exe", "arguments": ["two"], "environment": { "MODE": "b" } }
                  ]
                }
                """;

            var request = TrialCompareCliCommand.ParseSpec(
                json,
                ["trial-compare", "--receipts", receipts, "--timeout-seconds", "45"],
                Path.Combine(root, "default-receipts"));

            Xunit.Assert.Equal("abc123", request.BaseCommit);
            Xunit.Assert.Equal(2, request.Harnesses.Count);
            Xunit.Assert.Equal(Path.GetFullPath(receipts), request.ReceiptsDirectory);
            Xunit.Assert.Equal(TimeSpan.FromSeconds(45), request.LaunchTimeout);
            Xunit.Assert.Equal("a", request.Harnesses[0].Environment!["MODE"]);
            Xunit.Assert.Equal("b", request.Harnesses[1].Environment!["MODE"]);
            Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["trial-compare"]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
