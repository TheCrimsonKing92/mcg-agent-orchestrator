using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SpecRefinementLaunchAttempt(
    DateTimeOffset LastLaunchAt,
    int ConsecutiveFailedClaims,
    DateTimeOffset? EscalatedAt,
    string MessageId,
    string StorePath);

internal sealed class SpecRefinementLaunchAttemptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    public SpecRefinementLaunchAttemptStore(string directory) =>
        _directory = Path.GetFullPath(directory);

    public static SpecRefinementLaunchAttemptStore ForWorkspace(OrchestratorWorkspace workspace) =>
        new(workspace.SpecRefinementLaunchAttemptsDirectory);

    public SpecRefinementLaunchAttempt? Get(GoalId goalId)
    {
        var path = PathFor(goalId);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SpecRefinementLaunchAttempt>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Spec-refinement launch attempt '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Spec-refinement launch attempt '{path}' is malformed.", ex);
        }
    }

    public void Save(GoalId goalId, SpecRefinementLaunchAttempt attempt)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(goalId);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(attempt, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public bool Reset(GoalId goalId)
    {
        var path = PathFor(goalId);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    private string PathFor(GoalId goalId)
    {
        var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goalId.Value)));
        return Path.Combine(_directory, $"{fileName}.json");
    }
}
