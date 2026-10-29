using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Only graph-relevant compatibility input reaches the authoritative evidence resolver.
internal static class WorkerCompatibilityHandoffSource
{
    internal static IReadOnlyList<ResolvedLegacyHandoffArtifact> ResolveArtifacts(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        string? generatedHandoff)
    {
        var handoff = SelectCompatibilityHandoff(goal, task, workingDirectory, generatedHandoff);
        if (handoff is null)
        {
            return Array.Empty<ResolvedLegacyHandoffArtifact>();
        }

        var authoritativeByIdentity = goal.Tasks
            .TakeWhile(candidate => candidate.Id != task.Id)
            .Where(candidate => candidate.LastVerification is not null)
            .ToDictionary(
                candidate => $"prior/{candidate.Id.Value}/verification-output",
                candidate =>
                {
                    var output = WorkerVerificationEvidence.ProjectPriorTaskOutputForContext(candidate);
                    return Encoding.UTF8.GetBytes(output);
                },
                StringComparer.Ordinal);
        var selfVerificationIdentity = $"prior/{task.Id.Value}/verification-output";
        var resolver = new LegacyHandoffCompatibilityResolver(
            identity => authoritativeByIdentity.TryGetValue(identity, out var bytes) ? bytes : null,
            workingDirectory,
            selfVerificationIdentity,
            File.ReadAllBytes);
        return resolver.ResolveArtifactsFromMarkdown(handoff);
    }

    private static string? SelectCompatibilityHandoff(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        string? generatedHandoff)
    {
        if (generatedHandoff is not null)
        {
            return generatedHandoff;
        }

        var path = Path.Combine(workingDirectory, ".orchestrator-handoff.md");
        if (!File.Exists(path))
        {
            return null;
        }

        var relevantIdentities = goal.Tasks.TakeWhile(candidate => candidate.Id != task.Id)
            .Where(candidate => candidate.LastVerification is not null)
            .Select(candidate => $"prior/{candidate.Id.Value}/verification-output")
            .ToHashSet(StringComparer.Ordinal);
        var hasPredecessorEvidence = relevantIdentities.Count > 0;
        // Self-materialization is supported only for this exact preparation's task identity.
        relevantIdentities.Add($"prior/{task.Id.Value}/verification-output");

        const string pointerPrefix = "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): ";
        var handoff = File.ReadAllText(path);
        var selectedPointers = new List<string>();
        var hasPointers = false;
        using var reader = new StringReader(handoff);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith(pointerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            hasPointers = true;
            JsonDocument representation;
            try
            {
                representation = JsonDocument.Parse(line[pointerPrefix.Length..]);
            }
            catch (JsonException) when (task.RequiredRole == AgentRole.Planner && !hasPredecessorEvidence)
            {
                // Unrecognized user content supplies no identity. A first Planner still has
                // no predecessor input, but a recognized self-pointer must reach validation.
                continue;
            }

            using (representation)
            {
                if (representation.RootElement.ValueKind == JsonValueKind.Object &&
                    representation.RootElement.TryGetProperty(
                        nameof(LegacyHandoffPointer.AuthoritativeEvidenceIdentity), out var identity) &&
                    identity.ValueKind == JsonValueKind.String &&
                    relevantIdentities.Contains(identity.GetString()!))
                {
                    selectedPointers.Add(line);
                }
            }
        }

        // Selection grants no trust: the unchanged resolver still validates each selected representation.
        // Embedded v0 evidence has no task identity, so it requires predecessor evidence in this graph.
        return hasPointers
            ? selectedPointers.Count > 0 ? string.Join(Environment.NewLine, selectedPointers) : null
            : hasPredecessorEvidence ? handoff : null;
    }
}
