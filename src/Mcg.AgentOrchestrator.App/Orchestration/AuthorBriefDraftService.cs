using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorBriefDraftOutcome(string Kind, int ExitCode, string? MainHead,
    string? DraftPath, string? ReceiptPath, IReadOnlyList<AuthorBriefDraftCheck> Checks, string? Failure = null);

internal static class AuthorBriefDraftService
{
    internal static AuthorBriefDraftOutcome Run(string prefix, OrchestratorWorkspace workspace,
        AuthorBriefDraftSeams seams, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default, string draftsDirectory = "author-drafts",
        Func<DateTimeOffset>? utcNow = null)
    {
        string? draftPath = null;
        string? failureDetail = null;
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
            var item = AuthorBriefDraftBacklog.Read(workspace.BacklogStorePath, prefix);
            backlogItemId = item.Id;
            var directory = Path.Combine(workspace.OrchestratorDirectory, draftsDirectory);
            var stem = $"{item.Id[..8]}-{(utcNow ?? (() => DateTimeOffset.UtcNow))():yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
            Directory.CreateDirectory(directory);
            receiptPath = Path.Combine(directory, stem + ".receipt.json");
            var resolvedModel = ConductorRoundModelResolver.Resolve(seams.Catalog, ModelFunctionPurposes.ConductorAuthor);
            model = resolvedModel.Alias;
            mainHead = seams.Repository.ResolveMainHead();
            var lessons = new ConductorLessonSelector(workspace.OperatorLessonsStorePath)
                .Select(ConductorLessonSelector.AuthorTags("brief"));
            var round = AuthorBriefDraftRound.DispatchAsync(AuthorBriefDraftPrompt.Render(item, lessons, mainHead),
                workspace.ExecutionDirectory, seams.RunProcessAsync, resolvedModel, cancellationToken).GetAwaiter().GetResult();
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
                return Outcome(2);
            }
            draftPath = Path.Combine(directory, stem + ".md");
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
            return Outcome(checks.All(check => check.Passed) ? 0 : 1);
        }
        catch (Exception ex)
        {
            failureDetail = $"{ex.GetType().Name}: {ex.Message}";
            if (receiptPath is not null) WriteReceipt(ex is ConductorModelRoundException { ModelAlias: null }
                ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            error.WriteLine($"Error: {ex.Message}");
            return Outcome(1);
        }

        AuthorBriefDraftOutcome Outcome(int code) => new(
            failureDetail is null && kind is "draft" or "stale" ? kind : "failed",
            code, mainHead, draftPath, receiptPath, checks, failureDetail);

        void WriteReceipt(string? failure) => File.WriteAllText(receiptPath!, JsonSerializer.Serialize(new
        {
            backlogItemId, mainHead, exitCode, kind, checks, staleReason, evidenceReferences, failure, model
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    }

    private static string LintDetail(BriefLintFinding finding) => string.IsNullOrEmpty(finding.Remedy)
        ? finding.Message
        : $"{finding.Message} remedy: {finding.Remedy}";
}
