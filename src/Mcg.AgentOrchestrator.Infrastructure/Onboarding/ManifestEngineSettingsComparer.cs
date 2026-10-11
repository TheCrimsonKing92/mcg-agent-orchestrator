using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcceptanceManifest = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifest;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reports resolved manifest facts against the gate's undeclared defaults.</summary>
public static class ManifestEngineSettingsComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ManifestFactComparison> Compare(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Acceptance manifest must be a JSON object.");

        var hand = AcceptanceGateEngineSettings.Parse(manifestJson);
        var learned = new AcceptanceGateEngineSettings();
        var facts = new List<ManifestFactComparison>
        {
            CompareValue("engine.maxConcurrentShards", hand.MaxConcurrentShards, learned.MaxConcurrentShards),
            CompareValue("engine.enforceStructuralCoverage", hand.EnforceStructuralCoverage, learned.EnforceStructuralCoverage),
            CompareValue("engine.timeouts.defaultMinutes", hand.ResolveCheckTimeout(null).TotalMinutes,
                learned.ResolveCheckTimeout(null).TotalMinutes),
            CompareValue("engine.timeouts.buildServerShutdownMinutes", hand.ResolveBuildServerShutdownTimeout().TotalMinutes,
                learned.ResolveBuildServerShutdownTimeout().TotalMinutes),
            CompareValue("engine.timeouts.discoveryMinutes", hand.ResolveDiscoveryTimeout().TotalMinutes,
                learned.ResolveDiscoveryTimeout().TotalMinutes)
        };

        if (root.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in engine.EnumerateObject())
            {
                var member = typeof(AcceptanceGateEngineSettings).GetProperty(property.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (member is null || member.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                    facts.Add(new($"engine.{property.Name}", ManifestFactResultKind.NotConsumed,
                        property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText(),
                        "none", "The gate ignores this declared engine key."));
            }
        }

        var manifest = root.Deserialize<AcceptanceManifest>(JsonOptions)!;
        var defaultManifest = new AcceptanceManifest();
        var learnedGlobs = defaultManifest.ForbiddenChangedPathGlobs.Select(glob => glob.Trim()).ToArray();
        var handGlobs = (manifest.ForbiddenChangedPathGlobs ?? defaultManifest.ForbiddenChangedPathGlobs)
            .Select(glob => glob.Trim()).ToArray();
        facts.Add(CompareRendered("forbiddenChangedPathGlobs", RenderList(handGlobs, ","),
            RenderList(learnedGlobs, ","), handGlobs.SequenceEqual(learnedGlobs, StringComparer.Ordinal)));

        // The gate injects a check when checks is absent; this report covers only declared checks.
        if (root.EnumerateObject().Any(property => property.Name.Equals("checks", StringComparison.OrdinalIgnoreCase)))
        {
            if (manifest.Checks is null)
                throw new JsonException("Acceptance manifest checks must be an array.");
            foreach (var check in manifest.Checks)
            {
                if (check is null)
                    throw new JsonException("Acceptance manifest checks must contain objects.");
                if (check.Type?.Trim().Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) == true)
                    AddCheckFacts(facts, check);
            }
        }

        return facts;
    }

    private static void AddCheckFacts(List<ManifestFactComparison> facts, AcceptanceManifestCheck check)
    {
        var defaults = new AcceptanceManifestCheck();
        var key = $"check[{(string.IsNullOrWhiteSpace(check.Project) ? check.Name : check.Project)}]";
        var mtp = AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check);
        if (check.Arguments is null)
            throw new JsonException("Acceptance check arguments must be an array.");
        var (handFilter, forwarded) = ExtractArguments(check.Arguments, mtp);
        var (learnedFilter, defaultArguments) = ExtractArguments(defaults.Arguments, mtp);
        facts.Add(CompareValue($"{key}.filter", handFilter, learnedFilter));
        facts.Add(CompareValue($"{key}.timeoutMinutes", check.TimeoutMinutes, defaults.TimeoutMinutes));
        facts.Add(CompareValue($"{key}.advisory", check.Advisory, defaults.Advisory));
        facts.Add(CompareValue($"{key}.estimatedSerialSeconds", check.EstimatedSerialSeconds, defaults.EstimatedSerialSeconds));
        facts.Add(new($"{key}.arguments", ManifestFactResultKind.Match, RenderList(forwarded, " "),
            RenderList(defaultArguments, " "), "The gate forwards these declared arguments."));
        if (mtp)
        {
            var dropped = FindDroppedMtpArguments(check.Arguments);
            if (dropped.Count > 0)
                facts.Add(new($"{key}.arguments", ManifestFactResultKind.NotConsumed, RenderList(dropped, " "),
                    RenderList(defaultArguments, " "), "The gate ignores these declared arguments for MTP."));
        }
    }

    private static (string? Filter, List<string> Arguments) ExtractArguments(IReadOnlyList<string> arguments, bool mtp)
    {
        var forwarded = new List<string>();
        var filter = mtp
            ? AcceptanceCheckCommandBuilder.ExtractMtpCompatibleArguments(arguments, forwarded)
            : AcceptanceCheckCommandBuilder.ExtractFilterArguments(arguments, forwarded);
        if (!mtp && !string.IsNullOrWhiteSpace(filter))
            forwarded.RemoveRange(forwarded.Count - 2, 2); // The VSTest extractor appends the filter pair.
        return (string.IsNullOrWhiteSpace(filter) ? null : filter, forwarded);
    }

    private static List<string> FindDroppedMtpArguments(IReadOnlyList<string> arguments)
    {
        var dropped = new List<string>();
        var prefix = new List<string>();
        var forwardedCount = 0;
        var filterValue = false;
        var probe = "__manifest_filter_probe__";
        while (arguments.Contains(probe, StringComparer.Ordinal)) probe += "_";
        foreach (var argument in arguments)
        {
            prefix.Add(argument);
            var (_, forwarded) = ExtractArguments(prefix, mtp: true);
            // Prefix counts preserve token identity, including repeated values in dropped and forwarded options.
            var consumedFilter = false;
            if (!filterValue && argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
                consumedFilter = ExtractArguments([.. prefix, probe], mtp: true).Filter == probe;
            if (forwarded.Count == forwardedCount && !filterValue && !consumedFilter)
                dropped.Add(argument);
            forwardedCount = forwarded.Count;
            filterValue = consumedFilter;
        }
        return dropped;
    }

    private static ManifestFactComparison CompareValue<T>(string fact, T hand, T learned) =>
        CompareRendered(fact, Render(hand), Render(learned), EqualityComparer<T>.Default.Equals(hand, learned));

    private static ManifestFactComparison CompareRendered(string fact, string hand, string learned, bool match) =>
        new(fact, match ? ManifestFactResultKind.Match : ManifestFactResultKind.DiffersFromGateDefault, hand, learned,
            match ? "Agrees with the gate default." : "The learned value comes from the gate's undeclared default.");

    private static string Render<T>(T value) => value switch
    {
        null => "none",
        bool boolean => boolean ? "true" : "false",
        string text => string.IsNullOrWhiteSpace(text) ? "none" : text,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!
    };

    private static string RenderList(IReadOnlyList<string> values, string separator) =>
        values.Count == 0 ? "none" : string.Join(separator, values);
}
