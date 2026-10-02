using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SpecRefinementTransientRetry(
    string MessageId,
    DateTimeOffset MessageCreatedAt,
    int Attempts,
    string LastFailureDetail,
    DateTimeOffset? ExhaustedAt);

internal sealed class SpecRefinementTransientRetryStore(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.GetFullPath(directory);

    public static SpecRefinementTransientRetryStore ForWorkspace(OrchestratorWorkspace workspace) =>
        new(Path.Combine(workspace.SpecRefinementLaunchAttemptsDirectory, "transient-retries"));

    public SpecRefinementTransientRetry? Get(GoalId goalId)
    {
        var path = PathFor(goalId);
        if (!File.Exists(path))
            return null;

        try
        {
            var retry = JsonSerializer.Deserialize<SpecRefinementTransientRetry>(File.ReadAllText(path), JsonOptions);
            if (retry is null || retry.Attempts < 0 || string.IsNullOrWhiteSpace(retry.MessageId) ||
                retry.MessageCreatedAt == default || retry.LastFailureDetail is null)
                throw new InvalidDataException($"Spec-refinement transient retry '{path}' is invalid.");
            return retry;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Spec-refinement transient retry '{path}' is malformed.", ex);
        }
    }

    public void Save(GoalId goalId, SpecRefinementTransientRetry retry)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(goalId);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(retry, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public void Reset(GoalId goalId)
    {
        var path = PathFor(goalId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathFor(GoalId goalId) => Path.Combine(_directory,
        $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goalId.Value)))}.json");
}
