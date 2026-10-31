using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Completion comes from installed/runtime evidence, independently of leftover model questions.
internal static class ProjectOnboardingCompletion
{
    internal static bool IsComplete(string directory, string? conductEventsLogPath, bool goalLanded)
    {
        if (goalLanded) return true;
        try { return ManifestInstalled(directory) && ConductorRan(directory, conductEventsLogPath); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return false; } // The ordinary step reader owns diagnostics for incomplete artifacts.
    }

    internal static bool ManifestInstalled(string directory)
    {
        string manifest;
        try { manifest = File.ReadAllText(Path.Combine(directory, "acceptance-manifest.json")); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return false; }
        using var document = JsonDocument.Parse(manifest);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Installed acceptance manifest must be a JSON object.");
        // Use the gate's existing format, without deriving or comparing a new manifest.
        _ = document.RootElement.Deserialize<GoalAcceptanceVerifier.AcceptanceManifest>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return true;
    }

    internal static async Task<bool> HasLandedGoalAsync(string statePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(statePath)) return false;
        try
        {
            var state = SqliteOrchestratorStateRepository.OpenReadOnly(statePath);
            return (await state.ListGoalMetadataAsync(token)).Any(goal => goal.PromotionFact is not null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
    }

    internal static bool ConductorRan(string directory, string? conductEventsLogPath) =>
        new ConductorLeaseLiveness(directory).IsRunning() || HasConductorHistory(conductEventsLogPath);

    private static bool HasConductorHistory(string? path)
    {
        if (path is null) return false;
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return false;
        var files = Directory.EnumerateFiles(directory,
            Path.GetFileNameWithoutExtension(path) + "-*" + Path.GetExtension(path)).Prepend(path);
        foreach (var file in files)
        {
            FileStream stream;
            try { stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
            using (stream)
            using (var reader = new StreamReader(stream))
                while (reader.ReadLine() is { } line)
                    if (ConductEventFileSource.TryParse(line, out var item) &&
                        item!.EventKind is "loop-start" or "startup-first-tick" or "tick" or "loop-stop") return true;
        }
        return false;
    }
}
