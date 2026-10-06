using System.Text.Json;
using ReferenceRow = Mcg.AgentOrchestrator.Infrastructure.AcceptanceLaneReuseShadowMiss.ReferenceRow;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaneReuseShadowReferenceReader
{
    internal static string? ResolveMainTreeSha(string worktreePath, string mainSha)
    {
        try
        {
            var result = GitCli.Run(worktreePath, "rev-parse", mainSha + "^{tree}");
            return result.Succeeded ? result.Output.Trim() : null;
        }
        catch (Exception ex) { Log("main-tree", ex); return null; }
    }

    internal static (IReadOnlyList<ReferenceRow> Rows, int UnreadableCount) Read(
        string recordRoot, string currentGoalDirectory, string currentAttemptId, string? mainTreeSha)
    {
        var rows = new List<ReferenceRow>();
        var unreadable = 0;
        try
        {
            if (!Directory.Exists(recordRoot)) return (rows, unreadable);
            foreach (var directory in Directory.EnumerateDirectories(recordRoot))
            {
                var goalId = Path.GetFileName(directory);
                try
                {
                    foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                    {
                        var attemptId = Path.GetFileNameWithoutExtension(path);
                        if (goalId == currentGoalDirectory && attemptId == currentAttemptId) continue;
                        try
                        {
                            using var document = JsonDocument.Parse(File.ReadAllText(path));
                            var record = document.RootElement;
                            var tree = record.GetProperty("candidate_tree_sha").GetString();
                            if (tree is null) throw new JsonException("Missing candidate tree.");
                            // Validate the complete record before adding any row or filtering its tree.
                            var recordRows = new List<ReferenceRow>();
                            foreach (var lane in record.GetProperty("lanes").EnumerateArray())
                            {
                                var name = lane.GetProperty("lane").GetString();
                                var partition = lane.GetProperty("partition_id").GetString();
                                var executed = lane.GetProperty("executed").GetBoolean();
                                var verdict = lane.GetProperty("verdict").GetString();
                                if (name is null || partition is null || verdict is not (null or "GREEN" or "RED"))
                                    throw new JsonException("Invalid lane reference.");
                                recordRows.Add(new(goalId, attemptId, name, partition, executed, verdict));
                            }
                            if (mainTreeSha is not null && tree == mainTreeSha) rows.AddRange(recordRows);
                        }
                        catch (Exception ex) { unreadable++; Log("reference-record", ex); }
                    }
                }
                catch (Exception ex) { Log("reference-directory", ex); }
            }
        }
        catch (Exception ex) { Log("reference", ex); }
        return (rows, unreadable);
    }

    private static void Log(string phase, Exception exception)
    {
        try { Console.WriteLine($"LANE_REUSE_SHADOW_UNAVAILABLE {phase}:{exception.GetType().Name}"); }
        catch (Exception) { }
    }
}
