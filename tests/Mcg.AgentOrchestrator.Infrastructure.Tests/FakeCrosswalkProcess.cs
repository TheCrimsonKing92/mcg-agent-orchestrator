using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Owns an isolated tool path; version, detect and list never reach the OS.</summary>
internal sealed class FakeCrosswalkProcess : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("onboarding-crosswalk-");
    internal string Binary { get; }
    internal CrosswalkStackReader Reader { get; }
    internal int VersionCalls { get; private set; }
    internal List<WorkerProcessRunRequest> Calls { get; } = [];
    internal WorkerProcessRunResult DetectResult { get; set; }
    internal WorkerProcessRunResult ListResult { get; set; } = new(0, "[]", "");
    internal Exception? DetectException { get; set; }
    internal TimeSpan Timeout { get; } = TimeSpan.FromSeconds(73);

    internal FakeCrosswalkProcess(string stack = "unknown", string? framework = null)
    {
        Binary = Path.Combine(_directory.FullName, "crosswalk.exe");
        File.WriteAllText(Binary, "injected crosswalk process");
        DetectResult = new(0, JsonSerializer.Serialize(new
        {
            stack, testFramework = framework ?? "", evidence = new[] { new { path = "build.file", reason = "suite declaration" } }
        }), "");
        var resolver = new CrosswalkExecutableResolver(Binary, runVersionAsync: (_, _) =>
        {
            VersionCalls++;
            return Task.FromResult(new WorkerProcessRunResult(0, "fixture-version", ""));
        });
        Reader = new(resolver, Timeout, (request, _) =>
        {
            Calls.Add(request);
            if (request.Command.Contains(" detect ", StringComparison.Ordinal))
            {
                if (DetectException is not null) throw DetectException;
                return Task.FromResult(DetectResult);
            }
            Assert.Contains(" list ", request.Command, StringComparison.Ordinal);
            return Task.FromResult(ListResult);
        });
    }

    internal void ListTests(params string[] ids) => ListResult = new(0,
        JsonSerializer.Serialize(ids.Select(id => new { id, name = id, file = "suite.test" })), "");

    public void Dispose() => _directory.Delete(recursive: true);
}
