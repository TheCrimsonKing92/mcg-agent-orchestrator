using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Seeds portable project behavior, while leaving existing policies entirely owner-controlled.
internal static class ProjectConductorPolicySeeder
{
    internal enum Outcome { Seeded, AlreadyPresent, TemplateMissing, TemplateInvalid }

    internal sealed record Result(
        Outcome Outcome,
        string? PolicyName,
        string? WrittenPath,
        IReadOnlyList<string> DroppedKeys,
        string Detail);

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "name", "maxCriterionRetries", "plannerSampleCount", "acceptanceWidth",
        "cascadeTesterCheapFirst", "autoPromoteRiskThreshold", "transitionMap"
    };

    internal static string TemplatePath(string homeRoot) =>
        Path.Combine(homeRoot, "config", "project-conductor-policy.template.json");

    internal static string? TemplatePathFor(OrchestratorWorkspace workspace, OrchestratorHome home) =>
        workspace.IsProjectScoped ? TemplatePath(home.InstallRootDirectory) : null;

    internal static Result Seed(string templatePath, string orchestratorDirectory)
    {
        var path = Path.Combine(orchestratorDirectory, "conductor-policy.json");
        if (File.Exists(path))
            return new(Outcome.AlreadyPresent, null, null, [], "Policy file already exists.");

        string template;
        try
        {
            template = File.ReadAllText(templatePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(Outcome.TemplateMissing, null, null, [], ex.Message.ReplaceLineEndings(" "));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(Outcome.TemplateInvalid, null, null, [], ex.Message.ReplaceLineEndings(" "));
        }

        string json;
        ConductorAutonomyPolicy policy;
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(template, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new(Outcome.TemplateInvalid, null, null, [], "Template must be a JSON object.");

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (AllowedKeys.Contains(property.Name))
                        property.WriteTo(writer);
                    else
                        dropped.Add(property.Name);
                }
                writer.WriteNumber("maxConcurrentPaidWorkers",
                    ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);
                writer.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(buffer.ToArray());
            policy = ConductorAutonomyPolicy.ParseJson(json, templatePath);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new(Outcome.TemplateInvalid, null, null, dropped.ToArray(),
                ex.Message.ReplaceLineEndings(" "));
        }

        Directory.CreateDirectory(orchestratorDirectory);
        var temporaryPath = Path.Combine(orchestratorDirectory, $".conductor-policy-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return new(Outcome.AlreadyPresent, null, null, [], "Policy file appeared during seeding.");
            }
            return new(Outcome.Seeded, policy.Name, path, dropped.ToArray(), "Policy seeded from template.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Preserve the publication result or original write failure.
            }
        }
    }
}
