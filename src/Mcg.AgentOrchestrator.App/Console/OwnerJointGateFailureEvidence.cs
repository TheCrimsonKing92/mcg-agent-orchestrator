using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Joint gate evidence is observational: never construct a store (its constructor migrates it).
internal sealed class OwnerJointGateFailureEvidence(string orchestratorDirectory)
{
    internal OwnerActivityTestEvidence? Read(OwnerConductEvent item, OwnerActivityTestEvidence? recorded = null)
    {
        if (item.EventKind != "acceptance-cohort" || !string.IsNullOrWhiteSpace(recorded?.FirstFailure)) return recorded;
        var sources = new Sources();
        var kind = OwnerActivityNarrator.Field(item, "kind");
        if (kind != "train") ReadCohort(item, sources);
        if (kind != "cohort") ReadTrain(item, sources);
        ReadAttempt(item, sources);
        string? failure = null;
        var tests = new List<string>();
        foreach (var (path, directory) in sources.Results)
        {
            TryRead(() =>
            {
                var result = AcceptanceTrxFailureReader.Read(Path.GetFullPath(path, directory));
                sources.Found |= result.Status != AcceptanceTrxReadStatus.Missing;
                foreach (var test in result.Failures)
                {
                    if (test.TestName is { } name) tests.Add(name);
                    failure ??= OwnerGateFailureEvidence.Describe(test);
                }
            });
        }
        foreach (var (path, directory) in sources.Outputs)
        {
            TryRead(() =>
            {
                var fullPath = Path.GetFullPath(path, directory);
                if (!File.Exists(fullPath)) return;
                sources.Found = true;
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                    if (CompilerError(line) is { } error) { failure ??= error; break; }
            });
        }
        failure ??= sources.Details.Select(CompilerError).OfType<string>().FirstOrDefault();
        var check = sources.Checks.FirstOrDefault();
        failure ??= check is not null ? "check " + check + (sources.ExitCode is { } code ? " exited " + code : " failed") :
            sources.ExitCode is { } exit ? "gate exited " + exit :
            sources.Found ? "recorded gate evidence contains no failure detail" : null;
        if (failure is null) return recorded;
        return new(recorded?.Tests.Count > 0 ? recorded.Tests : tests.Distinct().ToArray(),
            recorded?.Checks.Count > 0 ? recorded.Checks : sources.Checks, recorded?.OwnTest ?? false, failure);
    }

    private void ReadCohort(OwnerConductEvent item, Sources sources)
    {
        var receipt = OwnerActivityNarrator.Field(item, "receipt");
        var identity = OwnerActivityNarrator.Field(item, "identity");
        if (receipt is null && identity is null) return;
        var rows = Rows("cohort-acceptance.db", receipt is not null ?
            "SELECT * FROM cohort_receipts WHERE receipt_id=$id;" :
            "SELECT * FROM cohort_receipts WHERE cohort_id=$id ORDER BY completed_at DESC LIMIT 1;", receipt ?? identity!);
        foreach (var row in rows)
        {
            sources.Found = true;
            AddRow(row, "gate_test_result_paths_json", sources);
            if (row.GetValueOrDefault("infrastructure_detail") is { } detail) sources.Details.Add(detail);
            if (row.GetValueOrDefault("cohort_id") is not { } cohort) continue;
            foreach (var partition in Rows("cohort-acceptance.db",
                "SELECT * FROM cohort_partition_receipts WHERE cohort_id=$id AND lower(outcome) <> 'passed' ORDER BY member_ordinal;", cohort))
                AddRow(partition, "test_result_paths_json", sources);
        }
    }

    private void AddRow(Dictionary<string, string?> row, string pathsColumn, Sources sources)
    {
        AddStrings(row.GetValueOrDefault(pathsColumn), path => sources.Results.Add((path, orchestratorDirectory)));
        AddStrings(row.GetValueOrDefault("failed_checks_json"), check => sources.Checks.Add(check));
        sources.ExitCode ??= row.GetValueOrDefault("gate_exit_code");
    }

    private void ReadTrain(OwnerConductEvent item, Sources sources)
    {
        var receipt = OwnerActivityNarrator.Field(item, "receipt");
        var identity = OwnerActivityNarrator.Field(item, "identity");
        if (receipt is not null || identity is not null)
            foreach (var row in Rows("merge-train-acceptance.db", receipt is not null ?
                "SELECT * FROM merge_train_receipts WHERE receipt_id=$id;" :
                "SELECT * FROM merge_train_receipts WHERE train_id=$id;", receipt ?? identity!))
            {
                sources.Found = true;
                identity ??= row.GetValueOrDefault("train_id");
                ReadJson(row.GetValueOrDefault("payload_json"), root => AddJson(root, orchestratorDirectory, sources));
            }
        if (identity is null && receipt?.StartsWith("merge-train-receipt-", StringComparison.Ordinal) == true)
            identity = "merge-train-v2-" + receipt["merge-train-receipt-".Length..];
        if (!SafeSegment(identity)) return;
        var directory = Path.Combine(orchestratorDirectory, "merge-train-evidence", identity!);
        TryRead(() =>
        {
            if (!Directory.Exists(directory)) return;
            // The retained copies are authoritative even if original result paths have disappeared.
            var files = Directory.EnumerateFiles(directory, "*.trx").Order(StringComparer.Ordinal).ToArray();
            sources.Results.InsertRange(0, files.Select(path => (path, directory)));
            var verdict = Path.Combine(directory, "gate-verdict.json");
            if (!File.Exists(verdict)) return;
            sources.Found = true;
            ReadJson(File.ReadAllText(verdict), root => AddJson(root, directory, sources));
        });
    }

    private void ReadAttempt(OwnerConductEvent item, Sources sources)
    {
        var attempt = OwnerActivityNarrator.Field(item, "attempt");
        if (!SafeSegment(attempt)) return;
        var directory = Path.Combine(orchestratorDirectory, "grouped-gate-attempts", attempt!);
        var metadata = Path.Combine(directory, attempt + ".attempt.json");
        TryRead(() =>
        {
            if (!File.Exists(metadata)) return;
            // Metadata alone is not a receipt, results file or captured output.
            ReadJson(File.ReadAllText(metadata), root => AddJson(root, directory, sources));
        });
    }

    private static void AddJson(JsonElement root, string directory, Sources sources)
    {
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "failedchecks":
                    AddStrings(property.Value.GetRawText(), check => sources.Checks.Add(check)); break;
                case "gatetestresultpaths":
                case "testresultpaths":
                    AddStrings(property.Value.GetRawText(), path => sources.Results.Add((path, directory))); break;
                case "gateexitcode":
                    if (property.Value.ValueKind == JsonValueKind.Number) sources.ExitCode ??= property.Value.GetRawText();
                    break;
                case "stdoutpath":
                case "stderrpath":
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } path)
                        sources.Outputs.Add((path, directory));
                    break;
            }
        }
    }

    private List<Dictionary<string, string?>> Rows(string database, string sql, string id)
    {
        var rows = new List<Dictionary<string, string?>>();
        var path = Path.Combine(orchestratorDirectory, database);
        TryRead(() =>
        {
            if (!File.Exists(path)) return;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < reader.FieldCount; index++)
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                rows.Add(row);
            }
        });
        return rows;
    }

    private static void AddStrings(string? json, Action<string> add) => ReadJson(json, root =>
    {
        if (root.ValueKind != JsonValueKind.Array) return;
        foreach (var value in root.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) add(text);
    });

    private static void ReadJson(string? json, Action<JsonElement> read) => TryRead(() =>
    {
        if (json is null) return;
        using var document = JsonDocument.Parse(json);
        read(document.RootElement);
    });

    private static void TryRead(Action read)
    {
        try { read(); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or InvalidCastException) { }
    }

    private static bool SafeSegment(string? value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Contains("..", StringComparison.Ordinal) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains('/') && !value.Contains('\\');

    private static string? CompilerError(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"(?:^|[\\/\s])([^\\/\s]+\.cs\(\d+,\d+\):\s*error CS\d+:.*)");
            if (!match.Success) continue;
            var error = Regex.Replace(match.Groups[1].Value, @"\s+\[[^\]]+\.csproj\]\s*$", "");
            return Regex.Replace(error.Trim(), @"\s+", " ");
        }
        return null;
    }

    private sealed class Sources
    {
        internal readonly List<(string Path, string Directory)> Results = [];
        internal readonly List<(string Path, string Directory)> Outputs = [];
        internal readonly List<string> Checks = [];
        internal readonly List<string> Details = [];
        internal string? ExitCode;
        internal bool Found;
    }
}
