using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Adapts terminal top-level Go test events to the gate's existing TRX evidence contract.</summary>
internal static class GoTestJsonGateReceipt
{
    internal const string AdapterTypeName = "executor://go-test/v1";
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    internal static string[] PrepareArguments(
        AcceptanceManifestCheck check, string[] arguments, string? resultsPrefix) =>
        IsEligible(check, resultsPrefix) && !arguments.Contains("-json", StringComparer.Ordinal)
            ? [.. arguments[..2], "-json", .. arguments[2..]]
            : arguments;

    internal static AcceptanceCheckResult Attach(
        AcceptanceManifestCheck check, string[] arguments, GoalAcceptanceVerifier.CommandResult result,
        string worktreePath, string? resultsPrefix, Func<string, string?> tail, AcceptanceCheckResult built)
    {
        if (!IsEligible(check, resultsPrefix) || !arguments.Contains("-json", StringComparer.Ordinal))
            return built;

        var decoded = new StringBuilder();
        var rows = ReadTopLevelRows(result.Output, decoded);
        if (rows.Count == 0)
            return built;

        var results = new XElement(Trx + "Results");
        var definitions = new XElement(Trx + "TestDefinitions");
        var packages = new Dictionary<string, (string? Clause, string Directory)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!packages.TryGetValue(row.Package, out var source))
                packages.Add(row.Package, source = ResolvePackage(worktreePath, row.Package));
            var id = Guid.NewGuid().ToString();
            var name = $"{source.Clause ?? row.Package}.{row.Test}";
            results.Add(new XElement(Trx + "UnitTestResult", new XAttribute("testId", id),
                new XAttribute("testName", name), new XAttribute("outcome", row.Failed ? "Failed" : "Passed")));
            definitions.Add(new XElement(Trx + "UnitTest", new XAttribute("id", id),
                new XAttribute("name", name), new XAttribute("storage", row.Package),
                new XElement(Trx + "TestMethod", new XAttribute("adapterTypeName", AdapterTypeName),
                    new XAttribute("codeBase", source.Directory), new XAttribute("className", row.Package),
                    new XAttribute("name", row.Test))));
        }
        var failed = rows.Count(row => row.Failed);
        var documentToWrite = new XDocument(new XElement(Trx + "TestRun", new XAttribute("name", "go test"),
            results, definitions, new XElement(Trx + "ResultSummary", new XAttribute("outcome", "Completed"),
                new XElement(Trx + "Counters", new XAttribute("total", rows.Count),
                    new XAttribute("executed", rows.Count), new XAttribute("passed", rows.Count - failed),
                    new XAttribute("failed", failed)))));
        var prefix = Path.GetFullPath(resultsPrefix!);
        var directory = Path.GetDirectoryName(prefix)!;
        var path = Path.Combine(directory, AcceptanceArtifactFileNames.BoundFileName(
            $"{Path.GetFileName(prefix)}.{AcceptanceArtifactFileNames.Slug(check.Name)}", ".trx"));
        Directory.CreateDirectory(directory);
        documentToWrite.Save(path);
        return WithNormalizedResult(built with
        {
            TestResultPaths = [path],
            ExecutedTestCount = rows.Count,
            FailingTestIdentities = rows.Where(row => row.Failed).Select(row => $"{row.Package}.{row.Test}").ToArray(),
            OutputTail = built.OutputTail is null ? null : tail(decoded.ToString())
        }, path);
    }

    private static AcceptanceCheckResult WithNormalizedResult(AcceptanceCheckResult receipt, string trxPath)
    {
        var target = NormalizedTestResultWriter.PathFor(trxPath);
        try
        {
            var read = new TrxTestResultSource().Read(trxPath);
            if (read.Status != TestResultReadStatus.Readable)
                return receipt with
                {
                    NormalizedTestResultWriteFailure = new(target, NormalizedTestResultWriteFailure.SourceNotReadable,
                        read.Detail ?? read.Status.ToString())
                };
            NormalizedTestResultWriter.Write(target, "go-test-json", read.Records);
            return receipt with { NormalizedTestResultPaths = [target] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var firstLine = ex.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? "No message provided.";
            return receipt with
            {
                NormalizedTestResultWriteFailure = new(target, NormalizedTestResultWriteFailure.WriteFailed,
                    $"{ex.GetType().Name}: {firstLine}")
            };
        }
    }

    internal static IReadOnlyList<(string Package, string Test, bool Failed)> ReadTopLevelRows(
        string output, StringBuilder? decoded)
    {
        var positions = new Dictionary<(string Package, string Test), int>();
        var rows = new List<(string Package, string Test, bool Failed)>();
        foreach (var (line, terminator) in Lines(output))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    decoded?.Append(line).Append(terminator);
                    continue;
                }
                decoded?.Append(StringProperty(value, "Output"));
                var package = StringProperty(value, "Package");
                var test = StringProperty(value, "Test");
                var action = StringProperty(value, "Action");
                if (string.IsNullOrEmpty(package) || string.IsNullOrEmpty(test) || test.Contains('/') ||
                    action is not ("pass" or "fail"))
                    continue;

                var key = (package, test);
                if (positions.TryGetValue(key, out var index))
                    rows[index] = (package, test, rows[index].Failed || action == "fail");
                else
                {
                    positions.Add(key, rows.Count);
                    rows.Add((package, test, action == "fail"));
                }
            }
            catch (JsonException)
            {
                decoded?.Append(line).Append(terminator);
            }
        }
        return rows;
    }

    private static bool IsEligible(AcceptanceManifestCheck check, string? resultsPrefix) =>
        GoPackageFocusedEvidence.IsGoTestCommand(check) && !GoPackageFocusedEvidence.IsEvidenceCheck(check) &&
        !string.IsNullOrEmpty(resultsPrefix);

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static IEnumerable<(string Line, string Terminator)> Lines(string output)
    {
        for (var start = 0; start < output.Length;)
        {
            var end = output.IndexOfAny(['\r', '\n'], start);
            if (end < 0) end = output.Length;
            var next = end;
            if (next < output.Length && output[next] == '\r') next++;
            if (next < output.Length && output[next] == '\n') next++;
            yield return (output[start..end], output[end..next]);
            start = next;
        }
    }

    private static (string? Clause, string Directory) ResolvePackage(string worktreePath, string importPath)
    {
        try
        {
            var moduleText = WithoutComments(File.ReadAllText(Path.Combine(worktreePath, "go.mod")));
            var moduleMatch = Regex.Match(moduleText, "(?m)^\\s*module\\s+(\"[^\"\\r\\n]+\"|`[^`\\r\\n]+`|[^\\s]+)");
            if (!moduleMatch.Success) return (null, string.Empty);
            var module = moduleMatch.Groups[1].Value.Trim('"', '`');
            if (importPath != module && !importPath.StartsWith(module + "/", StringComparison.Ordinal))
                return (null, string.Empty);
            var relative = importPath == module ? string.Empty : importPath[(module.Length + 1)..];
            if (relative.Contains('\\') || relative.Split('/').Any(part => part is "." or ".."))
                return (null, string.Empty);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
            var directory = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (directory != root && !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (null, string.Empty);
            var testFile = Directory.EnumerateFiles(directory, "*_test.go", SearchOption.TopDirectoryOnly)
                .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal).FirstOrDefault();
            if (testFile is null) return (null, string.Empty);
            var packageMatch = Regex.Match(WithoutComments(File.ReadAllText(testFile)),
                @"\A\s*package\s+([\p{L}_][\p{L}\p{Nd}_]*)\b");
            if (!packageMatch.Success) return (null, string.Empty);
            var clause = packageMatch.Groups[1].Value;
            return (clause.EndsWith("_test", StringComparison.Ordinal) ? clause[..^5] : clause, relative);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, string.Empty);
        }
    }

    private static string WithoutComments(string source) =>
        Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", " ");
}
