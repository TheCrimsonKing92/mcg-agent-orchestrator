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
        var isGo = model.Stack == ProjectStack.Go;
        var isNode = model.Stack == ProjectStack.Node;
        var isLua = model.Stack == ProjectStack.Lua;
        var isDotnet = !isGo && !isNode && !isLua;
        var nodeChecks = isNode ? NodeVerificationChecks.For(model) : default;
        var luaChecks = isLua ? LuaProjectDiscoveryAdapter.For(model, applied) : default;
        var checkNames = isDotnet ? LearnedCheckNames.Assign(checks.Select(check => check.ProjectPath).ToArray()) : default;
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
            isGo ? $"no confident go runner for {unit.UnitId}" : $"no confident VSTest or MTP runner for {unit.UnitId}")).ToList();
        if (isNode)
            blockers.AddRange(nodeChecks.Blockers);
        if (isLua)
            blockers.AddRange(luaChecks.Blockers);
        if (isDotnet)
            foreach (var check in checks.Where(check => check.Runner == "mtp"))
                if (mtpLayoutRefusals?.TryGetValue(check.ProjectPath, out var reason) == true)
                    blockers.Add(new ManifestBlocker("engine/mtpInvocations", reason));
        blockers.AddRange(questions.Select(question => new ManifestBlocker(question.FactKey, question.Question)));
        if (isDotnet && checkNames.Collisions.Count > 0)
            blockers.Add(new ManifestBlocker("checks/names",
                $"Two or more test projects produce the same check name: {string.Join(", ", checkNames.Collisions)}."));
        if (blockers.Count > 0)
            return new(null, blockers.AsReadOnly(), questions.AsReadOnly());

        var emittedChecks = new JsonArray();
        if (isGo)
        {
            foreach (var check in GoVerificationChecks.Entries)
                emittedChecks.Add(new JsonObject
                {
                    ["name"] = check.Name,
                    ["type"] = "command",
                    ["command"] = check.Command,
                    ["arguments"] = new JsonArray(check.Arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray())
                });
        }
        else if (isNode || isLua)
        {
            var ecosystemChecks = isLua
                ? luaChecks.Checks.Select(check => (check.Name, check.Type, check.Command, check.Project, check.Arguments))
                : nodeChecks.Checks.Select(check => (check.Name, check.Type, check.Command, check.Project, check.Arguments));
            foreach (var check in ecosystemChecks)
            {
                var emittedCheck = new JsonObject
                {
                    ["name"] = check.Name,
                    ["type"] = check.Type
                };
                if (check.Command is not null)
                    emittedCheck["command"] = check.Command;
                if (check.Project is not null)
                    emittedCheck["project"] = check.Project;
                emittedCheck["arguments"] = new JsonArray(check.Arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray());
                emittedChecks.Add(emittedCheck);
            }
        }
        else
        {
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
        if (isDotnet)
        {
            var mtpInvocations = new JsonArray();
            foreach (var check in checks.Where(check => check.Runner == "mtp")
                .OrderBy(check => check.ProjectPath, StringComparer.Ordinal))
                mtpInvocations.Add(MtpInvocationDeriver.Derive(check.ProjectPath));
            if (mtpInvocations.Count > 0)
                manifest["engine"]!["mtpInvocations"] = mtpInvocations;
        }

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
