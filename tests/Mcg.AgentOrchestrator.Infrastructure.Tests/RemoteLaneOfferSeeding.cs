using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteLaneOfferSeeding
{
    // Seed placement inputs separately from the current attempt's outcome evidence.
    internal static void PrepareFixture(string root, GoalAcceptanceVerifierTestOverrides overrides)
    {
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var historyPath = Path.Combine(root, "offer-history.jsonl");
        foreach (var lane in manifest["engine"]!["infrastructureTestLanes"]!.AsArray())
        {
            lane!["estimatedSerialSeconds"] = 120;
            SeedRemote(historyPath, "infrastructure tests: " + lane["name"]!.GetValue<string>(),
                lane["filter"]!.GetValue<string>(), 100);
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        overrides.RemoteLaneOfferHistoryPathForTests = historyPath;
    }

    internal static void SeedRemote(string path, string lane, string filter, double seconds, int samples = 3)
    {
        var start = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
        var end = start.AddSeconds(seconds);
        var binding = new RemoteLaneBinding("seed-executor", lane, "commit", "tree", "main",
            GoalAcceptanceVerifier.ShortHash(filter), "manifest");
        for (var index = 0; index < samples; index++)
            RemoteExecutorHealthLedger.Append(path, new(end, binding.ExecutorId, "seed-attempt", lane,
                RemoteLaneOutcomeCode.Accepted, null, binding, binding, Attempt: new(
                    [new("push", 0, false, start, start, "")], null,
                    [new("fetch", 0, false, end, end, "")], null, null, null)));
    }
}
