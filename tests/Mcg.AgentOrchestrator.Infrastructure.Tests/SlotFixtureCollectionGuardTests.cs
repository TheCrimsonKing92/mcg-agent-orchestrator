using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel safe: inspects source and metadata; private slot-fixture controls are never instantiated.
public sealed class SlotFixtureCollectionGuardTests
{
    private static readonly Type[] ScenarioUsers =
    [
        typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneResourceUsage), typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteSlotInstrumentation),
        typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteTypedSlotClaims), typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorClassClaim),
        typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLanePolicyConductEvent), typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteShadowPreemption)
    ];
    private static readonly IReadOnlyDictionary<string, (string Collection, string Reason)> CollectionExceptions =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [nameof(GoalAcceptanceLaneIsolatedRootGuardTests)] =
                ("IsolatedProcessSpawning", "This collection also supplies IsolatedDotnetRootFixture."),
            [nameof(GoalWorktreeAcceptanceContentionTestsScenarioHostRootIsolation)] =
                (TestCollections.DotnetBuildSlots, "Its build-slot lane intentionally exercises the shared pool.")
        };

    [Xunit.Fact]
    public void Live_users_have_job_accounting_or_current_named_exceptions()
    {
        var repository = VerifiedRepositoryRoot.Find();
        // The guard and its private controls only inspect metadata; they never instantiate a slot fixture.
        var candidates = typeof(SlotFixtureCollectionGuardTests).Assembly.GetTypes()
            .Where(type => type != typeof(SlotFixtureCollectionGuardTests) &&
                type.DeclaringType != typeof(SlotFixtureCollectionGuardTests))
            .Where(IsLiveTestClass).ToArray();
        var sources = AcceptanceTestClassSourcesMemo.Get(repository).ToDictionary(item => item.FullName);
        var snapshot = RepositorySourceSnapshotCache.Get(repository);
        string? Source(Type type)
        {
            if (!sources.TryGetValue(type.FullName!, out var source)) return null;
            var hierarchy = new HashSet<string>(StringComparer.Ordinal);
            for (var parent = type; parent is not null; parent = parent.BaseType)
                hierarchy.Add(parent.FullName!);
            return string.Join("\n", source.SourcePaths.Select(path =>
            {
                var syntax = snapshot.GetSyntaxTree(path).GetRoot();
                Assert.DoesNotContain(syntax.GetDiagnostics(), item => item.Severity == DiagnosticSeverity.Error);
                // Keep usings (aliases/static imports) and this class's hierarchy, not unrelated siblings.
                return string.Join("\n", syntax.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Select(node => node.ToFullString()).Concat(syntax.DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .Where(node => hierarchy.Contains(LaneIsolatedRootScanner.ClassName(node)))
                        .Select(node => node.RemoveNodes(node.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                            SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString())));
            }));
        }

        var users = SlotFixtureUsers(candidates, typeof(GoalAcceptanceVerifierDotnetBuildSlotTests), Source);
        Assert.DoesNotContain(typeof(SlotFixtureCollectionGuardTests), users);
        foreach (var expected in ScenarioUsers.Concat(CollectionExceptions.Keys.Select(name =>
                     Assert.Single(candidates, type => type.Name == name))))
            Assert.Contains(expected, users);
        Assert.Empty(StaleExceptions(CollectionExceptions, candidates, users));
        Assert.Empty(users.Where(type => DeclaredCollection(type) != TestCollections.JobAccounting &&
            !CollectionExceptions.ContainsKey(type.Name)));
        var lanes = AcceptanceGateEngineSettings.Load(repository).InfrastructureTestLanes;
        foreach (var user in users.Where(type => DeclaredCollection(type) == TestCollections.JobAccounting))
            Assert.Contains(AcceptanceLaneMembership.LanesIncluding(lanes, user.FullName!),
                lane => lane.Name == "Goal acceptance build slots" || lane.Name == "Goal acceptance verifier");
    }

    [Xunit.Fact]
    public void Scenario_users_declare_job_accounting_and_use_build_slot_lane()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var lanes = AcceptanceGateEngineSettings.Load(repository).InfrastructureTestLanes;
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, repository);
        foreach (var type in ScenarioUsers)
        {
            Assert.Equal(TestCollections.JobAccounting, DeclaredCollection(type));
            foreach (var membership in new[] { lanes, resolved })
                Assert.Equal("Goal acceptance build slots", Assert.Single(
                    AcceptanceLaneMembership.LanesIncluding(membership, type.FullName!)).Name);
        }
    }

    [Xunit.Fact]
    public void Controls_reject_unattributed_derivers_and_nested_fixture_owners()
    {
        Type[] candidates = [typeof(Controls.UnattributedSlotUser), typeof(Controls.GoalAcceptanceVerifierDotnetBuildSlotTestsAttributedSlotUser),
            typeof(Controls.NestedSlotUser)];
        Assert.All(candidates, type => Assert.False(IsLiveTestClass(type)));
        var violations = FindViolations(candidates, typeof(GoalAcceptanceVerifierDotnetBuildSlotTests), _ => null);
        Assert.Equal(new[] { typeof(Controls.NestedSlotUser), typeof(Controls.UnattributedSlotUser) }, violations);
        // The lane-contract guard enumerates hidden classes too, even though xunit cannot discover them.
        var lanes = AcceptanceGateEngineSettings.Load(VerifiedRepositoryRoot.Find()).InfrastructureTestLanes;
        Assert.Equal("Goal acceptance build slots", Assert.Single(AcceptanceLaneMembership.LanesIncluding(
            lanes, typeof(Controls.GoalAcceptanceVerifierDotnetBuildSlotTestsAttributedSlotUser).FullName!)).Name);
        var stale = new Dictionary<string, (string, string)>
        {
            ["MissingUser"] = ("OtherCollection", "Synthetic missing class.")
        };
        Assert.Equal(["MissingUser"], StaleExceptions(stale, candidates, candidates));
    }

    [Xunit.Theory]
    [Xunit.InlineData("class Sample { object M() => new GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteSlotInstrumentation.Scenario(1); }", true)]
    [Xunit.InlineData("using Alias = GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteTypedSlotClaims.JobAccountingScenario; class Sample { Alias field; }", true)]
    [Xunit.InlineData("using static GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteSlotInstrumentation; class Sample { Scenario field; }", true)]
    [Xunit.InlineData("class Sample { JobAccountingScenario field; }", true)]
    [Xunit.InlineData("class Sample : GoalAcceptanceVerifierDotnetBuildSlotTests { }", true)]
    [Xunit.InlineData("class Sample { Scenario field; }", false)]
    [Xunit.InlineData("// GoalAcceptanceVerifierDotnetBuildSlotTests\nclass Sample { string s = \"GoalAcceptanceVerifierDotnetBuildSlotTests\"; }", false)]
    [Xunit.InlineData("class Sample { object t = typeof(GoalAcceptanceVerifierDotnetBuildSlotTests); string n = nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteSlotInstrumentation.Scenario); }", false)]
    [Xunit.InlineData("class Sample { object M() => new GoalAcceptanceVerifierTestsGateChildReap.FakeGateChildReapSeam(); }", false)]
    public void Source_matching_requires_fixture_references(string source, bool expected)
    {
        var violations = FindViolations([typeof(Controls.SourceOnlyUser)],
            typeof(GoalAcceptanceVerifierDotnetBuildSlotTests), _ => source);
        Assert.Equal(expected ? new[] { typeof(Controls.SourceOnlyUser) } : [], violations);
    }

    [Xunit.Fact]
    public void Stale_checks_reject_nonusers_changed_collections_and_blank_reasons()
    {
        Type[] candidates = [typeof(Controls.UnattributedSlotUser), typeof(Controls.GoalAcceptanceVerifierDotnetBuildSlotTestsAttributedSlotUser),
            typeof(Controls.SourceOnlyUser)];
        var users = SlotFixtureUsers(candidates, typeof(GoalAcceptanceVerifierDotnetBuildSlotTests), _ => null);
        var exceptions = new Dictionary<string, (string, string)>
        {
            [nameof(Controls.SourceOnlyUser)] = ("Other", "No fixture use."),
            [nameof(Controls.GoalAcceptanceVerifierDotnetBuildSlotTestsAttributedSlotUser)] = (TestCollections.JobAccounting, "Now isolated."),
            [nameof(Controls.UnattributedSlotUser)] = ("Other", "Collection changed.")
        };
        Assert.Equal(exceptions.Keys.Order(StringComparer.Ordinal), StaleExceptions(exceptions, candidates, users));
        exceptions = new() { [nameof(Controls.UnattributedSlotUser)] = ("", " ") };
        Assert.Equal([nameof(Controls.UnattributedSlotUser)], StaleExceptions(exceptions, candidates, users));
        exceptions[nameof(Controls.UnattributedSlotUser)] = ("", "Synthetic valid exception.");
        Assert.Empty(StaleExceptions(exceptions, candidates, users));
    }

    private static bool IsLiveTestClass(Type type) => type.IsClass && !type.IsAbstract && type.IsVisible &&
        !type.ContainsGenericParameters && type.GetMethods().Any(method => method.GetCustomAttributesData()
            .Any(attribute => typeof(Xunit.FactAttribute).IsAssignableFrom(attribute.AttributeType)));

    private static string? DeclaredCollection(Type type) => type.GetCustomAttributesData()
        .SingleOrDefault(attribute => attribute.AttributeType == typeof(Xunit.CollectionAttribute))?
        .ConstructorArguments.FirstOrDefault().Value as string;

    // Pure detection: visibility is deliberately left to the live caller so hidden controls exercise it too.
    private static Type[] FindViolations(IReadOnlyList<Type> candidates, Type fixtureBase, Func<Type, string?> source) =>
        SlotFixtureUsers(candidates, fixtureBase, source)
            .Where(type => !string.Equals(DeclaredCollection(type), TestCollections.JobAccounting, StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.Ordinal).ToArray();

    private static Type[] SlotFixtureUsers(IReadOnlyList<Type> candidates, Type fixtureBase, Func<Type, string?> source)
    {
        var assemblyTypes = fixtureBase.Assembly.GetTypes();
        var fixtures = assemblyTypes.Where(fixtureBase.IsAssignableFrom).ToArray();
        var uniqueNames = assemblyTypes.GroupBy(type => type.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return candidates.Where(type => fixtureBase.IsAssignableFrom(type) || fixtures.Any(fixture =>
                fixture.DeclaringType == type) ||
            source(type) is { } text && NamesFixture(CSharpSyntaxTree.ParseText(text).GetRoot(), fixtures, uniqueNames))
            .ToArray();
    }

    private static IEnumerable<Type> EnclosingTypes(Type type)
    {
        for (var parent = type.DeclaringType; parent is not null; parent = parent.DeclaringType)
            yield return parent;
    }

    private static bool NamesFixture(SyntaxNode syntax, Type[] fixtures, HashSet<string> uniqueNames)
    {
        var imports = syntax.DescendantNodes().OfType<UsingDirectiveSyntax>().ToArray();
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in fixtures)
        {
            var qualified = string.Join(".", EnclosingTypes(fixture).Reverse().Select(type => type.Name).Append(fixture.Name));
            references.Add(qualified);
            references.Add(fixture.FullName!.Replace('+', '.'));
            if (!fixture.IsNested || uniqueNames.Contains(fixture.Name) || imports.Any(import =>
                    import.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) && import.Name?.ToString() ==
                    qualified[..^(fixture.Name.Length + 1)]))
                references.Add(fixture.Name);
        }
        foreach (var node in syntax.DescendantNodes().Where(node =>
                     node is IdentifierNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax))
        {
            if (node.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax ||
                node.Ancestors().Any(parent => parent is TypeOfExpressionSyntax ||
                    parent is InvocationExpressionSyntax call && call.Expression.ToString() == "nameof")) continue;
            var name = string.Concat(node.DescendantTokens().Select(token => token.ValueText))
                .Replace("global::", "", StringComparison.Ordinal);
            if (references.Contains(name)) return true;
        }
        return false;
    }

    private static string[] StaleExceptions(IReadOnlyDictionary<string, (string Collection, string Reason)> exceptions,
        IReadOnlyList<Type> candidates, IReadOnlyList<Type> users) => exceptions.Where(entry =>
        {
            var matches = candidates.Where(type => type.Name == entry.Key).ToArray();
            return matches.Length != 1 || !users.Contains(matches[0]) ||
                DeclaredCollection(matches[0]) == TestCollections.JobAccounting ||
                (DeclaredCollection(matches[0]) ?? "") != entry.Value.Collection || string.IsNullOrWhiteSpace(entry.Value.Reason);
        }).Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();

    private static class Controls
    {
        // Abstract intermediate preserves the existing direct-subclass collection parity guard.
        public abstract class SyntheticSlotFixture : GoalAcceptanceVerifierDotnetBuildSlotTests;

        public sealed class UnattributedSlotUser : SyntheticSlotFixture
        {
            [Xunit.Fact] public void Sample() { }
        }

        [Xunit.Collection(TestCollections.JobAccounting)]
        public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsAttributedSlotUser : SyntheticSlotFixture
        {
            [Xunit.Fact] public void Sample() { }
        }

        public sealed class NestedSlotUser
        {
            private sealed class NestedFixture : SyntheticSlotFixture;
            [Xunit.Fact] public void Sample() { }
        }

        public sealed class SourceOnlyUser
        {
            [Xunit.Fact] public void Sample() { }
        }
    }
}
