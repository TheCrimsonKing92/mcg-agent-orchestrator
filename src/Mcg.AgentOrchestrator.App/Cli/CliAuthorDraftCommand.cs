using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAuthorDraftCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("author-draft", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Run(args, workspace, new(WorkerProcessRunner.RunBufferedAsync,
            new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory),
            ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath)), Console.Out, Console.Error);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        AuthorBriefDraftSeams seams, TextWriter output, TextWriter error)
    {
        string? receiptPath = null;
        string? backlogItemId = null;
        string? mainHead = null;
        int? exitCode = null;
        string? model = null;
        var kind = "failed";
        string? staleReason = null;
        IReadOnlyList<string> evidenceReferences = [];
        IReadOnlyList<AuthorBriefDraftCheck> checks = [];
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            if (!IsCommand(args) || args.Count != 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
                throw new ArgumentException(CliCommandHelp.AuthorDraftUsage);
            var item = AuthorBriefDraftBacklog.Read(workspace.BacklogStorePath, args[1]);
            backlogItemId = item.Id;
            var directory = Path.Combine(workspace.OrchestratorDirectory, "author-drafts");
            var stem = $"{item.Id[..8]}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
            Directory.CreateDirectory(directory);
            receiptPath = Path.Combine(directory, stem + ".receipt.json");
            var resolvedModel = ConductorRoundModelResolver.Resolve(seams.Catalog, ModelFunctionPurposes.ConductorAuthor);
            model = resolvedModel.Alias;
            mainHead = seams.Repository.ResolveMainHead();
            var lessons = new ConductorLessonSelector(workspace.OperatorLessonsStorePath)
                .Select(ConductorLessonSelector.AuthorTags("brief"));
            var round = AuthorBriefDraftRound.DispatchAsync(AuthorBriefDraftPrompt.Render(item, lessons, mainHead),
                workspace.ExecutionDirectory, seams.RunProcessAsync, resolvedModel).GetAwaiter().GetResult();
            exitCode = round.ExitCode;
            if (exitCode != 0) throw new InvalidOperationException($"Author model exited {exitCode}: {round.StandardError}");
            var result = AuthorBriefDraftResultParser.Parse(round.StandardOutput);
            if (result is null)
            {
                kind = "unparseable";
                throw new InvalidOperationException("Author did not return one valid draft or stale JSON object.");
            }
            if (seams.Repository.ResolveMainHead() != mainHead)
                throw new InvalidOperationException("Main HEAD changed during the drafting round; rerun against current main.");
            kind = result.Kind;
            staleReason = result.Reason;
            evidenceReferences = result.EvidenceReferences;
            if (kind == "stale")
            {
                WriteReceipt(null);
                output.WriteLine(staleReason);
                return 2;
            }
            var draftPath = Path.Combine(directory, stem + ".md");
            File.WriteAllText(draftPath, result.Markdown!);
            checks = AuthorBriefDraftChecks.Run(result.Markdown!, mainHead, seams.Repository);
            var findings = BriefLint.Lint(result.Markdown!);
            checks = [.. checks, .. findings
                .Where(finding => finding.Severity is BriefLintSeverity.BlocksDispatch or BriefLintSeverity.BlocksCliStart)
                .Select(finding => new AuthorBriefDraftCheck($"brief-lint:{finding.Kind}", false, LintDetail(finding)))];
            WriteReceipt(null);
            output.WriteLine($"Draft: {draftPath}");
            foreach (var check in checks.Where(check => !check.Passed))
                output.WriteLine($"Failed {check.Name}: {check.Detail}");
            foreach (var finding in findings.Where(finding => finding.Severity == BriefLintSeverity.Advisory))
                output.WriteLine($"BRIEF-LINT {finding.SeverityToken} {finding.Kind}: {LintDetail(finding)}");
            output.WriteLine($"goal --brief-file \"{draftPath}\" --backlog-item {item.Id} --backlog-coverage full");
            return checks.All(check => check.Passed) ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (receiptPath is not null) WriteReceipt(ex is ConductorModelRoundException { ModelAlias: null }
                ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        void WriteReceipt(string? failure) => File.WriteAllText(receiptPath!, JsonSerializer.Serialize(new
        {
            backlogItemId, mainHead, exitCode, kind, checks, staleReason, evidenceReferences, failure, model
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    }

    private static string LintDetail(BriefLintFinding finding) => string.IsNullOrEmpty(finding.Remedy)
        ? finding.Message
        : $"{finding.Message} remedy: {finding.Remedy}";
}
