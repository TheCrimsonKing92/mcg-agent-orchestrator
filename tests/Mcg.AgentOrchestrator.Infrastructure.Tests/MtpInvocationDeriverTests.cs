using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its JSON nodes.
public sealed class MtpInvocationDeriverTests
{
    [Fact]
    public void Derive_StandardProject_MatchesGateInvocationContract()
    {
        const string project = "tests/A/A.csproj";
        var invocation = MtpInvocationDeriver.Derive(project);
        Assert.Equal(new[] { "project", "executablePathTemplate", "firewallExecutablePathTemplate", "arguments" },
            invocation.Select(property => property.Key));
        Assert.Equal(project, invocation["project"]!.GetValue<string>());
        Assert.Equal("bin/{projectName}/{configuration}/{projectName}{executableExtension}",
            invocation["executablePathTemplate"]!.GetValue<string>());
        Assert.Equal("bin/{projectName}/{configuration}/{projectName}.exe",
            invocation["firewallExecutablePathTemplate"]!.GetValue<string>());
        Assert.Equal(new[] { "{executable}", "--no-ansi", "--progress", "off", "--results-directory",
            "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}",
            "--long-running", "120", "--minimum-expected-tests", "1" },
            invocation["arguments"]!.AsArray().Select(argument => argument!.GetValue<string>()));

        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["engine"] = new JsonObject { ["mtpInvocations"] = new JsonArray(invocation) },
            ["checks"] = new JsonArray(new JsonObject
            {
                ["name"] = "A", ["type"] = "dotnet-test", ["runner"] = "mtp", ["project"] = project
            })
        };
        var settings = AcceptanceGateEngineSettings.Parse(manifest.ToJsonString());
        Assert.Equal(project, Assert.Single(settings.MtpInvocations).Project);
        Assert.Same(settings.MtpInvocations[0], settings.ResolveMtpInvocation(project));
    }
}
