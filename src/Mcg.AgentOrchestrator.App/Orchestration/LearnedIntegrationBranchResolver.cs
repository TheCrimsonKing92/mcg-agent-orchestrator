using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class LearnedIntegrationBranchResolver
{
    public static IntegrationBranchResolution Resolve(string? registryBranch, bool useLearned, string projectModelPath)
    {
        var storedBranch = string.IsNullOrWhiteSpace(registryBranch) ? null : registryBranch.Trim();
        if (!useLearned)
            return new(storedBranch ?? TrunkBranchName.Default,
                storedBranch is null ? IntegrationBranchResolution.Default : IntegrationBranchResolution.Registry, null);

        var learned = ReadBranch(projectModelPath, out var modelNote);
        if (storedBranch is not null)
        {
            string? note = null;
            if (learned is { Confidence: FactConfidence.High, Value: { } learnedBranch } &&
                !string.Equals(storedBranch, learnedBranch, StringComparison.Ordinal))
                note = $"Registry integration branch '{storedBranch}' differs from learned branch '{OneLine(learnedBranch)}' (High); using the registry value.";
            return new(storedBranch, IntegrationBranchResolution.Registry, note);
        }

        if (modelNote is not null) return DefaultResolution(modelNote);
        if (learned is null) return DefaultResolution("Stored project model has no learned integration branch.");
        if (learned.Confidence != FactConfidence.High)
            return DefaultResolution($"Learned integration branch is {learned.Confidence} confidence.");

        try
        {
            return new(TrunkBranchName.Validate(learned.Value), IntegrationBranchResolution.Learned, null);
        }
        catch (ArgumentException)
        {
            return DefaultResolution("Learned integration branch is not a valid branch name.");
        }
    }

    private static ProjectFact<string>? ReadBranch(string path, out string? note)
    {
        note = null;
        try
        {
            return ProjectModelJson.Deserialize(File.ReadAllText(path)).IntegrationBranch;
        }
        catch (FileNotFoundException)
        {
            note = "No stored project model.";
        }
        catch (DirectoryNotFoundException)
        {
            note = "No stored project model.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            note = "Stored project model is unreadable or invalid.";
        }
        return null;
    }

    private static IntegrationBranchResolution DefaultResolution(string reason) =>
        new(TrunkBranchName.Default, IntegrationBranchResolution.Default, $"{reason} Using {TrunkBranchName.Default}.");

    private static string OneLine(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
}
