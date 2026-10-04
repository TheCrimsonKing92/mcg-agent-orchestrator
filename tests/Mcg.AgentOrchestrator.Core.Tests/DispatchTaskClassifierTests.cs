using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixed text and path inputs only.
public sealed class DispatchTaskClassifierTests
{
    [Fact(DisplayName = "Task classes follow engine, move, documentation precedence")]
    public void ClassesAndPrecedenceAreOrderIndependent()
    {
        var engine = PostLandingCanaryTrigger.EnginePathPrefixes[0] + ".cs";
        Assert.Equal(DispatchTaskClass.GateEngine,
            DispatchTaskClassifier.Classify("pure move", [engine, "docs/guide.md"]));
        Assert.Equal(DispatchTaskClass.GateEngine,
            DispatchTaskClassifier.Classify($"pure move `{engine}`", ["docs/guide.md"]));
        Assert.Equal(DispatchTaskClass.PureMove,
            DispatchTaskClassifier.Classify("PURE-MOVE", ["src/Widget.cs"]));
        Assert.Equal(DispatchTaskClass.PureMove,
            DispatchTaskClassifier.Classify("behavior-preserving", ["docs/guide.md"]));
        Assert.Equal(DispatchTaskClass.DocsOnly,
            DispatchTaskClassifier.Classify("Read `./README.md`", ["docs/data.json", "notes.MD"]));
        Assert.Equal(DispatchTaskClass.Other, DispatchTaskClassifier.Classify("Update label"));
        Assert.Equal(DispatchTaskClass.Other, DispatchTaskClassifier.Classify("Update", ["src/Widget.cs"]));
        foreach (var paths in new[]
                 {
                     new[] { engine, "docs/guide.md", engine },
                     new[] { "docs/guide.md", engine },
                     new[] { engine, engine, "docs/guide.md" }
                 })
            Assert.Equal(DispatchTaskClass.GateEngine, DispatchTaskClassifier.Classify("pure move", paths));
        Assert.Equal(DispatchTaskClass.DocsOnly,
            DispatchTaskClassifier.Classify("", ["docs/a.txt", "README.md", "docs/a.txt"]));
        Assert.Equal(DispatchTaskClass.DocsOnly,
            DispatchTaskClassifier.Classify("", ["README.md", "docs/a.txt"]));
    }

    [Theory(DisplayName = "Each named move marker is case insensitive")]
    [InlineData("pure move")]
    [InlineData("pure-move")]
    [InlineData("behavior-preserving")]
    [InlineData("decomposition slice")]
    public void EveryMarkerSelectsPureMove(string marker) => Assert.Equal(DispatchTaskClass.PureMove,
        DispatchTaskClassifier.Classify(marker.ToUpperInvariant(), ["src/File.cs"]));

    [Fact(DisplayName = "Only relative path citations participate in classification")]
    public void PathsNormalizeAnchorsAndRejectCommandsAndAbsolutePaths()
    {
        Assert.Equal(DispatchTaskClass.DocsOnly,
            DispatchTaskClassifier.Classify("`./docs\\guide.md#anchor` `README.md:12` `docs/a.md::Heading`"));
        Assert.Equal(DispatchTaskClass.Other,
            DispatchTaskClassifier.Classify("`low` `git ls-files <path>` `https://site/readme.md` `C:/README.md` `/README.md` `../README.md`"));
        Assert.Equal(DispatchTaskClass.Other, DispatchTaskClassifier.Classify(null));
        Assert.Equal(DispatchTaskClass.Other, DispatchTaskClassifier.Classify("`src/a.cs`", ["docs/b.md"]));
    }
}
