using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceLaneMembershipTests
{
    [Xunit.Fact]
    public void LocalHostOnlyBuildSlotTestsBelongOnlyToTheirConductorLane()
    {
        const string localLaneName = "Dotnet build slots local-only";
        const string hostKey = "host:DotnetBuildSlotsHostOnly";
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var descriptors = RunnableClasses();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var localLane = Xunit.Assert.Single(lanes, lane => lane.Name == localLaneName);
        var expectedTypes = new[]
        {
            typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyFocusedRunner),
            typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyLockAttribution),
            typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffStdout),
            typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffSuppressionFailure),
            typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyHandleProbe)
        };
        var expectedMethods = new[]
        {
            "FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt",
            "LockAttributionRestartManagerNamesFileHolder",
            "ConductorLoopHandoffWindowsLauncherInheritsRedirectedStdoutHandle",
            "ConductorLoopHandoffSuppressionFailureStillStartsSuccessor",
            "LockAttributionHandleProbeReturnsResultsForRealHeldFile"
        };
        var testMethods = descriptors.SelectMany(descriptor => typeof(AcceptanceLaneMembershipTests).Assembly
                .GetType(descriptor.FullName)!.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<Xunit.FactAttribute>(inherit: true) is not null)
            .ToArray();
        var namedMethods = testMethods.Where(method => expectedMethods.Contains(method.Name)).ToArray();
        Xunit.Assert.Equal(expectedMethods.Order(), namedMethods.Select(method => method.Name).Order());
        Xunit.Assert.Equal("LockAttribution_restart_manager_names_file_holder",
            namedMethods.Single(method => method.Name == expectedMethods[1])
                .GetCustomAttribute<Xunit.FactAttribute>()!.DisplayName);
        Xunit.Assert.Equal("ConductorLoopHandoff_windows_launcher_inherits_redirected_stdout_handle",
            namedMethods.Single(method => method.Name == expectedMethods[2])
                .GetCustomAttribute<Xunit.FactAttribute>()!.DisplayName);
        Xunit.Assert.Equal("LockAttribution_handle_probe_returns_results_for_real_held_file",
            namedMethods.Single(method => method.Name == expectedMethods[4])
                .GetCustomAttribute<Xunit.FactAttribute>()!.DisplayName);
        Xunit.Assert.Equal(expectedTypes.Select(type => type.FullName).Order(),
            descriptors.Where(descriptor => AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName)
                    .Any(lane => lane.Name == localLaneName))
                .Select(descriptor => descriptor.FullName).Order());
        foreach (var type in expectedTypes)
        {
            var method = Xunit.Assert.Single(testMethods.Where(method => method.DeclaringType == type));
            Xunit.Assert.Contains(method.Name, expectedMethods);
            Xunit.Assert.True(HasLocalHostOnlyTrait(method));
            // Check both the written filters and collection-resolved filters. Reverting the
            // manifest places these tests back in the remote-capable lane and fails here.
            foreach (var membership in new[] { lanes, resolved })
                Xunit.Assert.Equal(localLaneName, Xunit.Assert.Single(
                    AcceptanceLaneMembership.LanesIncluding(membership, type.FullName!)).Name);
        }
        Xunit.Assert.Equal(expectedMethods.Order(), testMethods.Where(HasLocalHostOnlyTrait)
            .Select(method => method.Name).Order());
        Xunit.Assert.Equal(["xunit:DotnetBuildSlots", hostKey], localLane.ExclusiveResourceKeys);
        Xunit.Assert.Equal(localLaneName, Xunit.Assert.Single(lanes,
            lane => lane.ExclusiveResourceKeys.Contains(hostKey)).Name);
        Xunit.Assert.Equal(["--filter-class", "*DotnetBuildEnvironmentManagerTestsLocalHostOnly*"],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter(localLane.Filter).ToArray());
        var reducedArguments = AcceptanceCheckCommandBuilder.TranslateMtpFilter(
            Xunit.Assert.Single(lanes, lane => lane.Name == "Dotnet build slots").Filter).ToArray();
        Xunit.Assert.Contains(Enumerable.Range(0, reducedArguments.Length / 2), index =>
            reducedArguments[index * 2] == "--filter-not-trait" &&
            reducedArguments[index * 2 + 1] == "Category=LocalHostOnly");
        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "config", "acceptance-manifest.json")));
        var conductor = Xunit.Assert.Single(manifest.RootElement.GetProperty("engine")
            .GetProperty("localTestPartitions").EnumerateArray(),
            partition => partition.GetProperty("name").GetString() == "Conductor");
        Xunit.Assert.Contains(localLaneName, conductor.GetProperty("laneNames").EnumerateArray()
            .Select(name => name.GetString()));
    }

    [Xunit.Fact]
    public void IsolatedRootScannerKeepsBreakawayHandoffClassesInLocalOnlyLane()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var assembly = typeof(AcceptanceLaneMembershipTests).Assembly;
        var reducedClasses = LaneIsolatedRootScanner.ResolveLaneClasses(root, "Dotnet build slots", assembly);
        var localClasses = LaneIsolatedRootScanner.ResolveLaneClasses(root, "Dotnet build slots local-only", assembly);
        foreach (var type in new[]
                 {
                     typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffStdout),
                     typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffSuppressionFailure)
                 })
        {
            Xunit.Assert.DoesNotContain(type, reducedClasses);
            Xunit.Assert.Contains(type, localClasses);
        }
    }

    [Xunit.Fact]
    public void WildcardRemoteSelectionKeepsHostOnlyBuildSlotsLocal()
    {
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot())
            .InfrastructureTestLanes;
        var root = Path.Combine(Path.GetTempPath(), "build-slot-eligibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configurationPath = Path.Combine(root, "executors.json");
            File.WriteAllText(configurationPath,
                """{"executors":[{"id":"fixture-executor"}],"lanes":["*"],"machineLocalResourceKeys":["xunit:DotnetBuildSlots"]}""");
            var configuration = RemoteLaneExecutorConfiguration.Load(configurationPath)
                .ResolveLanes(lanes.Select(lane => "infrastructure tests: " + lane.Name));
            Xunit.Assert.True(configuration.Enabled);
            Xunit.Assert.DoesNotContain("host:DotnetBuildSlotsHostOnly", configuration.MachineLocalResourceKeys);
            using var coordinator = new RemoteLaneCoordinator(configuration,
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
                root, null, new FakeRemoteLaneExecutor(), TimeProvider.System, TimeSpan.FromSeconds(1), null);
            foreach (var laneName in new[] { "Dotnet build slots", "Dotnet build slots local-only" })
            {
                var lane = Xunit.Assert.Single(lanes, lane => lane.Name == laneName);
                var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
                {
                    Name = "infrastructure tests: " + lane.Name,
                    Type = "dotnet-test",
                    Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    Arguments = ["--filter", lane.Filter],
                    ExclusiveResourceKeys = lane.ExclusiveResourceKeys
                };
                Xunit.Assert.Contains(check.Name, configuration.Lanes);
                Xunit.Assert.True(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _));
                Xunit.Assert.Equal(laneName == "Dotnet build slots", coordinator.IsEligible(check));
            }
            var refusal = Xunit.Assert.Single(RemoteExecutorHealthLedger.ReadAll(
                RemoteExecutorHealthLedger.ResolveStorePath(root)));
            Xunit.Assert.Equal("infrastructure tests: Dotnet build slots local-only", refusal.Lane);
            Xunit.Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, refusal.Outcome);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void BuildSlotDocumentationKeepsHostTestsLocalAndRemoteClearancePending()
    {
        var row = File.ReadLines(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(),
                "docs", "acceptance-gate-resource-isolation.md"))
            .Single(line => line.StartsWith("| `DotnetBuildSlots` /", StringComparison.Ordinal));
        Xunit.Assert.Contains("FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt", row);
        Xunit.Assert.Contains("LockAttribution_restart_manager_names_file_holder", row);
        Xunit.Assert.Contains("ConductorLoopHandoffWindowsLauncherInheritsRedirectedStdoutHandle", row);
        Xunit.Assert.Contains("ConductorLoopHandoffSuppressionFailureStillStartsSuccessor", row);
        Xunit.Assert.Contains("LockAttribution_handle_probe_returns_results_for_real_held_file", row);
        Xunit.Assert.Contains("Category=LocalHostOnly", row);
        Xunit.Assert.Contains("`Dotnet build slots local-only`", row);
        Xunit.Assert.Contains("never remote-eligible", row);
        Xunit.Assert.Contains("`host:DotnetBuildSlotsHostOnly`, which must never be added to the machine-local list", row);
        Xunit.Assert.Contains("an accepted remote run of the reduced lane", row);
        Xunit.Assert.Contains("executed count, summed with the local-only lane, equals the local count", row);
        Xunit.Assert.DoesNotContain("cleared for the machine-local list after the P4a hold", row);
    }

    private static bool HasLocalHostOnlyTrait(MethodInfo method) =>
        method.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(Xunit.TraitAttribute) &&
            attribute.ConstructorArguments.Count == 2 &&
            attribute.ConstructorArguments[0].Value as string == "Category" &&
            attribute.ConstructorArguments[1].Value as string == "LocalHostOnly");

    [Xunit.Fact]
    public void ExactClassFilterKeepsTheFullNameWithoutWildcards()
    {
        Xunit.Assert.Equal(
            ["--filter-class", "Example.Outer+Nested", "--filter-not-class", "Example.Other"],
            AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(
                "FullyQualifiedName=Example.Outer+Nested&FullyQualifiedName!=Example.Other").ToArray());
    }

    [Xunit.Theory]
    [Xunit.InlineData("ProcessSpawning", "Process spawning")]
    [Xunit.InlineData("DotnetBuildSlots", "Dotnet build slots")]
    public void NewCollectionMemberUsesOwningLaneWithoutManifestEdit(string collection, string laneName)
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var scratch = Path.Combine(Path.GetTempPath(), "acceptance-lane-" + Guid.NewGuid().ToString("N"));
        var testDirectory = Path.Combine(scratch, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(testDirectory);
        try
        {
            File.WriteAllText(Path.Combine(testDirectory, "QuokkaHarborSerializedFixture.cs"),
                $"[Xunit.Collection(\"{collection}\")] public class QuokkaHarborSerializedFixture {{ [Xunit.Fact] public void Example() {{ }} }}");
            var className = "QuokkaHarborSerializedFixture";
            var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
            Xunit.Assert.Equal("Remainder", Xunit.Assert.Single(
                AcceptanceLaneMembership.LanesIncluding(baseline, className)).Name);

            var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, scratch);
            Xunit.Assert.Equal(laneName, Xunit.Assert.Single(
                AcceptanceLaneMembership.LanesIncluding(resolved, className)).Name);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ExistingRunnableClassesKeepTheirSubstringLaneMembership()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var descriptors = RunnableClasses();
        var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var mismatches = new List<AcceptanceLaneMembershipMismatch>();
        foreach (var descriptor in descriptors)
        {
            var baselineNames = AcceptanceLaneMembership.LanesIncluding(baseline, descriptor.FullName)
                .Select(lane => lane.Name).Order().ToArray();
            var resolvedNames = AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName)
                .Select(lane => lane.Name).Order().ToArray();
            if (!baselineNames.SequenceEqual(resolvedNames))
            {
                mismatches.Add(new AcceptanceLaneMembershipMismatch(
                    descriptor.FullName, descriptor.Collection, baselineNames, resolvedNames));
            }
        }

        if (mismatches.Count > 0)
        {
            Xunit.Assert.Fail(AcceptanceLaneMembershipDiagnostics.Describe(
                mismatches, nameof(ExistingRunnableClassesKeepTheirSubstringLaneMembership)));
        }
    }

    [Xunit.Fact]
    public void OwnedCollectionMemberAlsoMatchingAnotherLaneStillUsesItsOwner()
    {
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
        var descriptor = new AcceptanceTestClassDescriptor("WorkerShellTestsQuokka", "ProcessSpawning");
        var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
        Xunit.Assert.DoesNotContain(AcceptanceLaneMembership.LanesIncluding(baseline, descriptor.FullName),
            lane => lane.Name == "Process spawning");

        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, [descriptor]);
        Xunit.Assert.Contains(AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName),
            lane => lane.Name == "Process spawning");
    }

    [Xunit.Fact]
    public void SourceScanAndReflectionFindTheSameOwnedCollectionMembers()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var owned = lanes.SelectMany(lane => lane.OwnedCollections).ToHashSet(StringComparer.Ordinal);
        var reflected = RunnableClasses().Where(item => item.Collection is not null && owned.Contains(item.Collection))
            .OrderBy(item => item.FullName).ToArray();
        var scanned = AcceptanceTestClassSourceScanner.Scan(root)
            .Where(item => item.Collection is not null && owned.Contains(item.Collection))
            .OrderBy(item => item.FullName).ToArray();
        Xunit.Assert.Equal(reflected, scanned);
    }

    [Xunit.Fact]
    public void RemainderExcludesEveryOwnedCollectionMember()
    {
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
        var descriptors = RunnableClasses();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
        var owned = lanes.SelectMany(lane => lane.OwnedCollections).ToHashSet(StringComparer.Ordinal);
        var mismatches = new List<AcceptanceLaneMembershipMismatch>();
        foreach (var descriptor in descriptors.Where(item => item.Collection is not null && owned.Contains(item.Collection)))
        {
            var resolvedLanes = AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName);
            if (resolvedLanes.Any(lane => lane.Name == "Remainder"))
            {
                mismatches.Add(new AcceptanceLaneMembershipMismatch(
                    descriptor.FullName, descriptor.Collection,
                    AcceptanceLaneMembership.LanesIncluding(baseline, descriptor.FullName)
                        .Select(lane => lane.Name).ToArray(),
                    resolvedLanes.Select(lane => lane.Name).ToArray()));
            }
        }

        if (mismatches.Count > 0)
        {
            Xunit.Assert.Fail(AcceptanceLaneMembershipDiagnostics.Describe(
                mismatches, nameof(RemainderExcludesEveryOwnedCollectionMember)));
        }
    }

    private static AcceptanceTestClassDescriptor[] RunnableClasses() =>
        typeof(AcceptanceLaneMembershipTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .Select(type => new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name))
            .ToArray();
}
