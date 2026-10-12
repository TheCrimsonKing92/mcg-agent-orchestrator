using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Renders the gate invocation for a project with the standard artifacts build layout.</summary>
public static class MtpInvocationDeriver
{
    public static JsonObject Derive(string projectPath) => new()
    {
        ["project"] = projectPath,
        ["executablePathTemplate"] = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
        ["firewallExecutablePathTemplate"] = "bin/{projectName}/{configuration}/{projectName}.exe",
        ["arguments"] = new JsonArray("{executable}", "--no-ansi", "--progress", "off",
            "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename",
            "{trxFileName}", "--long-running", "120", "--minimum-expected-tests", "1")
    };
}
