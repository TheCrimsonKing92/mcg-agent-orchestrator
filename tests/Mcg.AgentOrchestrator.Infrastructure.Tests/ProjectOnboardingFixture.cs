// Each caller owns a unique fixture copy; no process-global state or shared repository writes.
internal sealed class ProjectOnboardingFixture : IDisposable
{
    public string Root { get; }

    public ProjectOnboardingFixture(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), "project-discovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var source = Path.Combine(VerifiedRepositoryRoot.Find(), "tests", "project-onboarding-fixture", name);
        foreach (var template in Directory.EnumerateFiles(source, "*.template", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(Root, Path.GetRelativePath(source, template)[..^".template".Length]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(template, destination);
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
