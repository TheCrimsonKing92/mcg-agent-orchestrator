using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Builds a gate artifact from snapshot facts and live owner answers, without I/O.</summary>
public static class LearnedAcceptanceManifest
{
    public static LearnedAcceptanceManifestResult Build(ProjectModel model, IReadOnlyList<OwnerAnswer> answers,
        IReadOnlyDictionary<string, string>? mtpLayoutRefusals = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(answers);
        var applied = OwnerAnswerApplier.Apply(model, answers.OrderBy(answer => answer.FactKey, StringComparer.Ordinal)
            .ThenBy(answer => answer.RecordedUtc).ThenBy(answer => answer.Value, StringComparer.Ordinal).ToArray());
        var (checks, unresolved) = LearnedTestCheckDeriver.Derive(model, applied);
        var checkNames = LearnedCheckNames.Assign(checks.Select(check => check.ProjectPath).ToArray());
        var learned = LearnedEngineSettingsDeriver.Derive(model);
        var settings = OwnerAnsweredEngineSettings.Overlay(learned, applied);
        var questions = settings.OwnerQuestions.Concat(applied.OpenQuestions)
            .Where(question => question.FactKey.StartsWith("engine/", StringComparison.Ordinal))
            .DistinctBy(question => question.FactKey, StringComparer.Ordinal).ToList();

        // A removed question is not proof that the required numeric fact was materialized.
        EnsureTimeoutQuestion(OwnerAnswerValidator.DefaultTimeoutKey, settings.DefaultTimeoutMinutes);
        EnsureTimeoutQuestion(OwnerAnswerValidator.BuildServerShutdownKey, settings.BuildServerShutdownMinutes);
        EnsureTimeoutQuestion(OwnerAnswerValidator.DiscoveryKey, settings.DiscoveryMinutes);

        var blockers = unresolved.Select(unit => new ManifestBlocker(unit.FactKey,
            $"no confident VSTest or MTP runner for {unit.UnitId}")).ToList();
        foreach (var check in checks.Where(check => check.Runner == "mtp"))
            if (mtpLayoutRefusals?.TryGetValue(check.ProjectPath, out var reason) == true)
                blockers.Add(new ManifestBlocker("engine/mtpInvocations", reason));
        blockers.AddRange(questions.Select(question => new ManifestBlocker(question.FactKey, question.Question)));
        if (checkNames.Collisions.Count > 0)
            blockers.Add(new ManifestBlocker("checks/names",
                $"Two or more test projects produce the same check name: {string.Join(", ", checkNames.Collisions)}."));
        if (blockers.Count > 0)
            return new(null, blockers.AsReadOnly(), questions.AsReadOnly());

        var emittedChecks = new JsonArray();
        var checkTimeouts = OwnerAnsweredCheckTimeouts.Read(applied);
        foreach (var check in checks.OrderBy(check => check.ProjectPath.Replace('\\', '/'), StringComparer.Ordinal))
        {
            var path = check.ProjectPath.Replace('\\', '/');
            var emittedCheck = new JsonObject
            {
                ["name"] = checkNames.Names[path],
                ["type"] = "dotnet-test",
                ["runner"] = check.Runner,
                ["project"] = path
            };
            if (checkTimeouts.TryGetValue(path, out var timeout))
                emittedCheck["timeoutMinutes"] = timeout.Minutes;
            emittedCheck["arguments"] = new JsonArray();
            emittedChecks.Add(emittedCheck);
        }
        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["engine"] = new JsonObject
            {
                ["maxConcurrentShards"] = settings.MaxConcurrentShards,
                ["enforceStructuralCoverage"] = false,
                ["timeouts"] = new JsonObject
                {
                    ["defaultMinutes"] = settings.DefaultTimeoutMinutes,
                    ["buildServerShutdownMinutes"] = settings.BuildServerShutdownMinutes,
                    ["discoveryMinutes"] = settings.DiscoveryMinutes
                }
            },
            ["checks"] = emittedChecks,
            ["forbiddenChangedPathGlobs"] = new JsonArray()
        };
        var mtpInvocations = new JsonArray();
        foreach (var check in checks.Where(check => check.Runner == "mtp")
            .OrderBy(check => check.ProjectPath, StringComparer.Ordinal))
            mtpInvocations.Add(MtpInvocationDeriver.Derive(check.ProjectPath));
        if (mtpInvocations.Count > 0)
            manifest["engine"]!["mtpInvocations"] = mtpInvocations;

        var json = manifest.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, IndentSize = 2, NewLine = "\n"
        }) + "\n";
        return new(json, blockers.AsReadOnly(), questions.AsReadOnly());

        void EnsureTimeoutQuestion(string key, int? minutes)
        {
            if (minutes is > 0 || questions.Any(question => question.FactKey == key))
                return;
            questions.Add(learned.OwnerQuestions.First(question => question.FactKey == key));
        }
    }
}
