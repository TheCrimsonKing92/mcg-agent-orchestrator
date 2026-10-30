using Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class HomeBuildAdoptionFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"home-adoption-{Guid.NewGuid():N}");
    internal string RecordPath => HomeBuildAdoptionRecord.RecordPath(Root);
    internal ConductorSupervisorBuildIdentity Running => new(new string('a', 40), Path.Combine(Root, "old"));
    internal HomeBuildAdoptionRecord Candidate { get; }

    internal HomeBuildAdoptionFixture()
    {
        var run = Path.Combine(Root, "run");
        Directory.CreateDirectory(run);
        var dll = Path.Combine(run, "Mcg.AgentOrchestrator.App.dll");
        File.WriteAllText(dll, "fixture output");
        File.WriteAllText(dll + ".git-head", new string('b', 40));
        Candidate = new(1, Root, 2, new string('b', 40), run, dll, "adopted");
        Publish(Candidate);
    }

    internal void Publish(HomeBuildAdoptionRecord record) => HomeBuildAdoptionRecord.Write(RecordPath, record);
    internal HomeBuildAdoptionDetector Detector(Func<HomeBuildAdoptionRecord?>? read = null) =>
        new(Root, Running, readRecord: read);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
