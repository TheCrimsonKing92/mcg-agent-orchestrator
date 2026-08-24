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

    [Xunit.Theory]
    [Xunit.InlineData("text", "trial-compare succeeded=False")]
    [Xunit.InlineData("json", "\"succeeded\": false")]
    public void ExecuteRendersFailedReceiptThenExitsOne(string format, string expectedOutput)
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var specPath = WriteSpec(root);
            using var output = new StringWriter();

            var exception = Xunit.Assert.Throws<CliExitException>(() => TrialCompareCliCommand.Execute(
                ["trial-compare", "--spec", specPath, "--format", format],
                new FailingTrialRootHost(),
                Path.Combine(root, "receipts"),
                output));

            Xunit.Assert.Equal(1, exception.ExitCode);
            Xunit.Assert.Contains(expectedOutput, output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void ExecuteRejectsUnknownFormatBeforeCreatingAnyRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var specPath = WriteSpec(root);
            var host = new FailingTrialRootHost();

            var exception = Xunit.Assert.Throws<ArgumentException>(() => TrialCompareCliCommand.Execute(
                ["trial-compare", "--spec", specPath, "--format", "xml"],
                host,
                Path.Combine(root, "receipts"),
                TextWriter.Null));

            Xunit.Assert.Contains("--format", exception.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, host.CreateCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string WriteSpec(string root)
    {
        var specPath = Path.Combine(root, "spec.json");
        File.WriteAllText(specPath, $$"""
            {
              "sourceRepositoryPath": "{{root.Replace("\\", "\\\\")}}",
              "baseCommit": "abc123",
              "harnesses": [
                { "name": "alpha", "fileName": "alpha.exe", "arguments": [] },
                { "name": "beta", "fileName": "beta.exe", "arguments": [] }
              ]
            }
            """);
        return specPath;
    }

    private sealed class FailingTrialRootHost : ITrialRootHost
    {
        public int CreateCalls { get; private set; }

        public ITrialRootSession Create(TrialRootRequest request)
        {
            CreateCalls++;
            throw new InvalidOperationException("fixture trial-root creation failed");
        }
    }
}
