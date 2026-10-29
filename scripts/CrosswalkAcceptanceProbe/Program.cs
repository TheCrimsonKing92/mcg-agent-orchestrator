using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Operator-only host: calls the public owned acceptance API, never the Crosswalk client directly.
if (args.Length != 4 && (args.Length != 6 || args[4] != "--data-root" || string.IsNullOrWhiteSpace(args[5])))
{
    Console.Error.WriteLine("Usage: CrosswalkAcceptanceProbe <candidate-repo> <fixture-worktree> <receipts-directory> <opaque-id> [--data-root <path>]");
    return 64;
}
var candidate = Path.GetFullPath(args[0]);
var fixture = Path.GetFullPath(args[1]);
var receipts = Path.GetFullPath(args[2]);
var dataRoot = args.Length == 6 ? args[5] : Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DATA")?.Trim();
if (string.IsNullOrWhiteSpace(dataRoot))
{
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    dataRoot = string.IsNullOrWhiteSpace(localAppData)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcg-agent-orchestrator")
        : Path.Combine(localAppData, "Mcg.AgentOrchestrator");
}
var executable = new CrosswalkExecutableResolver(dataRoot: dataRoot);
Directory.CreateDirectory(receipts);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var config = Path.Combine(fixture, "config");
Directory.CreateDirectory(config);
var engine = JsonNode.Parse(File.ReadAllText(Path.Combine(candidate, "config", "acceptance-manifest.json")))!["engine"]!.DeepClone();
engine["enforceStructuralCoverage"] = false;
var manifest = JsonSerializer.SerializeToNode(new
{
    version = 1,
    checks = new[] { new { name = "real crosswalk selection", type = "crosswalk-test", timeoutMinutes = 2, arguments = new[] { args[3] } } }
}, options)!;
manifest["engine"] = engine;
File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), manifest.ToJsonString(options));
var id = "crosswalk-" + Guid.NewGuid().ToString("N");
await using var owner = AcceptanceExecutionOwners.CreateAttempt(fixture, options: new(
    RunId: id, ResultsPrefix: Path.Combine(receipts, id), IntegrationBranch: "main"));
var result = await new GoalAcceptanceVerifier { CrosswalkExecutable = executable }
    .RunOwnedAsync(fixture, null, null, null, null, owner);
var check = result.Checks?.SingleOrDefault(check => check.Name == "real crosswalk selection");
var envelope = new
{
    candidateCommit = Git(candidate, "rev-parse", "HEAD"),
    candidateTree = Git(candidate, "rev-parse", "HEAD^{tree}"),
    candidateDirtyPaths = Git(candidate, "status", "--short"),
    infrastructureAssembly = Identity(typeof(GoalAcceptanceVerifier).Assembly.Location),
    executionAssembly = Identity(typeof(WorkerProcessRunner).Assembly.Location),
    crosswalkExecutable = new { path = check?.CrosswalkExecutablePath, sha256 = check?.CrosswalkExecutableSha256 },
    selectedId = args[3], hostExitCode = result.Passed ? 0 : 1, result
};
var receipt = Path.Combine(receipts, id + ".operator.json");
File.WriteAllText(receipt, JsonSerializer.Serialize(envelope, options));
Console.WriteLine(receipt);
return result.Passed ? 0 : 1;

static object Identity(string path) => new
{
    path = Path.GetFullPath(path), sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
};

static string Git(string root, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false,
        CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WhenAll(output, error).GetAwaiter().GetResult();
    if (process.ExitCode != 0) throw new IOException(error.Result);
    return output.Result.Trim();
}
