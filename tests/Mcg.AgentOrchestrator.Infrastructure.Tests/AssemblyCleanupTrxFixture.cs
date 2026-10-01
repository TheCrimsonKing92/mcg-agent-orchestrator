using System.Xml.Linq;

// Every fixture owns a unique directory; no process, environment or shared state is changed.
internal sealed class AssemblyCleanupTrxFixture : IDisposable
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cleanup-trx-" + Guid.NewGuid().ToString("N"));

    internal static XElement Result(string name, string outcome, string? message = null) => new(
        Ns + "UnitTestResult", new XAttribute("testName", name), new XAttribute("outcome", outcome),
        message is null ? null : new XElement(Ns + "Output",
            new XElement(Ns + "ErrorInfo", new XElement(Ns + "Message", message))));

    internal string WriteReceipt(IEnumerable<XElement> rows, int? executed = null)
    {
        Directory.CreateDirectory(Root);
        var results = rows.ToArray();
        var passed = results.Count(row => (string?)row.Attribute("outcome") == "Passed");
        var skipped = results.Count(row => (string?)row.Attribute("outcome") is "NotExecuted" or "Skipped");
        var failed = results.Length - passed - skipped;
        var document = new XDocument(new XElement(Ns + "TestRun",
            new XElement(Ns + "Results", results),
            new XElement(Ns + "ResultSummary", new XAttribute("outcome", failed > 0 ? "Failed" : "Passed"),
                new XElement(Ns + "Counters", new XAttribute("total", results.Length),
                    new XAttribute("executed", executed ?? results.Length - skipped),
                    new XAttribute("passed", passed), new XAttribute("failed", failed),
                    new XAttribute("notExecuted", skipped)),
                new XElement(Ns + "RunInfos", new XElement(Ns + "RunInfo",
                    new XElement(Ns + "Text", "Exit code indicates failure: '2'"))))));
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".trx");
        document.Save(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
