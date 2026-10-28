using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The supported Node verification contract, shared by emission and comparison.</summary>
public static class NodeVerificationChecks
{
    public const string PackageManagerFact = "node-package-manager";
    public const string FrameworkFact = "node-test-framework";
    public const string TypeScriptConfigFact = "node-typescript-config";
    public const string PackageManagerQuestion = "environment/node-package-manager";
    public const string FrameworkQuestion = "environment/node-test-framework";
    public const string RegistryQuestion = "environment/node-registry-access";
    public const string RegistryHazard = "node-install-needs-registry-network";
    public const string AbsentTypeScriptConfig = "absent: no TypeScript type-check, because tsconfig.json is absent at the root";

    public sealed record Check(string Name, string Type, string? Command, string? Project, IReadOnlyList<string> Arguments);

    public static IReadOnlyList<Check> Entries { get; } = Array.AsReadOnly(new[]
    {
        new Check("npm ci", "command", "npm", null, Array.AsReadOnly(new[] { "ci" })),
        new Check("npx tsc --noEmit", "command", "npx", null, Array.AsReadOnly(new[] { "tsc", "--noEmit" })),
        new Check("crosswalk-test .", "crosswalk-test", null, ".", Array.Empty<string>())
    });

    public static (IReadOnlyList<Check> Checks, IReadOnlyList<ManifestBlocker> Blockers) For(ProjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var blockers = model.OwnerQuestions.Where(question =>
                question.FactKey is PackageManagerQuestion or FrameworkQuestion ||
                question.FactKey.StartsWith("discovery/files/package.json/", StringComparison.Ordinal))
            .Select(question => new ManifestBlocker(question.FactKey, question.Question)).ToList();
        Require(PackageManagerFact, "npm", PackageManagerQuestion,
            "Which package manager should verification use? Only an unambiguous npm lockfile is supported.");
        Require(FrameworkFact, "vitest", FrameworkQuestion,
            "Which test framework should verification use? Only an unambiguous Vitest devDependency is supported.");
        var config = model.EnvironmentNeeds.FirstOrDefault(need => need.Name == TypeScriptConfigFact)?.Value;
        if (config is not { Confidence: FactConfidence.High or FactConfidence.Medium } ||
            config.Value is not ("tsconfig.json" or AbsentTypeScriptConfig))
            AddQuestion("environment/node-typescript-config", "Confirm whether root tsconfig.json exists.");

        return blockers.Count > 0 ? ([], blockers.AsReadOnly())
            : (config!.Value == "tsconfig.json" ? Entries : Entries.Where(check => check.Command != "npx").ToArray(), []);

        void Require(string name, string supported, string key, string question)
        {
            var fact = model.EnvironmentNeeds.FirstOrDefault(need => need.Name == name)?.Value;
            if (fact is not { Confidence: FactConfidence.High or FactConfidence.Medium } || fact.Value != supported)
                AddQuestion(key, question);
        }

        void AddQuestion(string key, string question)
        {
            if (blockers.All(blocker => blocker.FactKey != key))
            {
                var ownerQuestion = model.OwnerQuestions.FirstOrDefault(item => item.FactKey == key)
                    ?? new ProjectOwnerQuestion(key, $"{key}: {question}", new FactSource("package.json", 1));
                blockers.Add(new(ownerQuestion.FactKey, ownerQuestion.Question));
            }
        }
    }
}
