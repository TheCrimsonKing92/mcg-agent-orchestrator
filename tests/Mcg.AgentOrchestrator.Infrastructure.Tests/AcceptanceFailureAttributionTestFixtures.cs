using System.Xml.Linq;

internal static class AcceptanceFailureAttributionTestFixtures
{
    internal static void WriteFailedTrx(
        string path,
        params (string Identity, string DisplayName)[] failures)
    {
        var indexed = failures.Select((failure, index) => (failure, Id: $"test-{index + 1}")).ToArray();
        new XDocument(
            new XElement(
                "TestRun",
                new XElement(
                    "TestDefinitions",
                    indexed.Select(item => Definition(item.Id, item.failure.Identity))),
                new XElement(
                    "Results",
                    indexed.Select(item => new XElement(
                        "UnitTestResult",
                        new XAttribute("testId", item.Id),
                        new XAttribute("testName", item.failure.DisplayName),
                        new XAttribute("outcome", "Failed"))))))
            .Save(path);
    }

    private static XElement Definition(string id, string identity)
    {
        var separator = identity.LastIndexOf('.');
        return new XElement(
            "UnitTest",
            new XAttribute("id", id),
            new XElement(
                "TestMethod",
                new XAttribute("className", identity[..separator]),
                new XAttribute("name", identity[(separator + 1)..])));
    }
}
