using FluentAssertions;
using Hannibal.Models;
using Hannibal.Services.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hannibal.Tests.Services.Scheduling;

/// <summary>
/// Tests for the RuleScheduler dependency graph computation.
/// Uses the A/B/C scenario from the design:
///   Rule A: alpha:/usr/lib -> beta:/usr/lib
///   Rule B: alpha:/usr/bin -> beta:/usr/bin
///   Rule C: beta:/usr     -> gamma:/usr
/// C depends on A and B because C's source (beta:/usr) overlaps A and B's destinations.
/// </summary>
public class DependencyGraphTests
{
    private static Storage MakeStorage(int id, string name) => new()
    {
        Id = id,
        UserId = "test",
        Technology = "test",
        UriSchema = name,
        Networks = "",
        IsActive = true
    };

    private static Endpoint MakeEndpoint(int id, Storage storage, string path) => new()
    {
        Id = id,
        UserId = "test",
        StorageId = storage.Id,
        Storage = storage,
        Path = path,
        Name = $"{storage.UriSchema}:{path}",
        IsActive = true
    };

    private static Rule MakeRule(int id, string name, Endpoint source, Endpoint dest) => new()
    {
        Id = id,
        Name = name,
        UserId = "test",
        SourceEndpoint = source,
        SourceEndpointId = source.Id,
        DestinationEndpoint = dest,
        DestinationEndpointId = dest.Id,
        Operation = Rule.RuleOperation.Sync,
        MaxDestinationAge = TimeSpan.FromHours(12)
    };

    private RuleScheduler CreateScheduler()
    {
        // Create a minimal scheduler for testing BuildDependencyGraph
        // We can't call ExecuteAsync (needs DB), but BuildDependencyGraph is internal
        var logger = NullLoggerFactory.Instance.CreateLogger<RuleScheduler>();
        var calcLogger = NullLoggerFactory.Instance.CreateLogger<ScheduleCalculator>();
        var calculator = new ScheduleCalculator(calcLogger);

        return new RuleScheduler(
            logger,
            null!, // serviceScopeFactory - not needed for graph tests
            null!, // hannibalHub - not needed for graph tests
            calculator);
    }

    [Fact]
    public void BuildDependencyGraph_ABC_Scenario_DetectsDependencies()
    {
        // Arrange
        var alpha = MakeStorage(1, "alpha");
        var beta = MakeStorage(2, "beta");
        var gamma = MakeStorage(3, "gamma");

        var alphaUsrLib = MakeEndpoint(1, alpha, "usr/lib");
        var betaUsrLib = MakeEndpoint(2, beta, "usr/lib");
        var alphaUsrBin = MakeEndpoint(3, alpha, "usr/bin");
        var betaUsrBin = MakeEndpoint(4, beta, "usr/bin");
        var betaUsr = MakeEndpoint(5, beta, "usr");
        var gammaUsr = MakeEndpoint(6, gamma, "usr");

        var ruleA = MakeRule(1, "A: alpha lib -> beta lib", alphaUsrLib, betaUsrLib);
        var ruleB = MakeRule(2, "B: alpha bin -> beta bin", alphaUsrBin, betaUsrBin);
        var ruleC = MakeRule(3, "C: beta usr -> gamma usr", betaUsr, gammaUsr);

        var scheduler = CreateScheduler();

        // Act
        scheduler.BuildDependencyGraph(new List<Rule> { ruleA, ruleB, ruleC });
        var graph = scheduler.GetDependencyGraph();

        // Assert
        graph[ruleA.Id].Should().BeEmpty("Rule A has no prerequisites");
        graph[ruleB.Id].Should().BeEmpty("Rule B has no prerequisites");
        graph[ruleC.Id].Should().BeEquivalentTo(new[] { ruleA.Id, ruleB.Id },
            "Rule C depends on A and B because C reads from beta:/usr which overlaps A and B's destinations");
    }

    [Fact]
    public void BuildDependencyGraph_DisjointRules_NoDependencies()
    {
        // Arrange: rules that don't overlap at all
        var s1 = MakeStorage(1, "server1");
        var s2 = MakeStorage(2, "server2");
        var s3 = MakeStorage(3, "server3");

        var rule1 = MakeRule(1, "R1",
            MakeEndpoint(1, s1, "photos"),
            MakeEndpoint(2, s2, "backup/photos"));
        var rule2 = MakeRule(2, "R2",
            MakeEndpoint(3, s1, "documents"),
            MakeEndpoint(4, s3, "backup/documents"));

        var scheduler = CreateScheduler();

        // Act
        scheduler.BuildDependencyGraph(new List<Rule> { rule1, rule2 });
        var graph = scheduler.GetDependencyGraph();

        // Assert
        graph[rule1.Id].Should().BeEmpty();
        graph[rule2.Id].Should().BeEmpty();
    }

    [Fact]
    public void BuildDependencyGraph_SamePath_CreatesDependency()
    {
        // Rule X writes to exactly the path Rule Y reads from
        var s1 = MakeStorage(1, "src");
        var s2 = MakeStorage(2, "mid");
        var s3 = MakeStorage(3, "dst");

        var ruleX = MakeRule(1, "X: src -> mid",
            MakeEndpoint(1, s1, "data"),
            MakeEndpoint(2, s2, "data"));
        var ruleY = MakeRule(2, "Y: mid -> dst",
            MakeEndpoint(3, s2, "data"),
            MakeEndpoint(4, s3, "data"));

        var scheduler = CreateScheduler();
        scheduler.BuildDependencyGraph(new List<Rule> { ruleX, ruleY });
        var graph = scheduler.GetDependencyGraph();

        graph[ruleX.Id].Should().BeEmpty();
        graph[ruleY.Id].Should().Contain(ruleX.Id,
            "Y reads from mid:/data which is exactly where X writes");
    }

    [Fact]
    public void BuildDependencyGraph_SiblingPaths_NoDependency()
    {
        // Rule writes to usr/lib, another reads from usr/bin - siblings, no overlap
        var s1 = MakeStorage(1, "src");
        var s2 = MakeStorage(2, "mid");
        var s3 = MakeStorage(3, "dst");

        var ruleX = MakeRule(1, "X",
            MakeEndpoint(1, s1, "usr/lib"),
            MakeEndpoint(2, s2, "usr/lib"));
        var ruleY = MakeRule(2, "Y",
            MakeEndpoint(3, s2, "usr/bin"),
            MakeEndpoint(4, s3, "usr/bin"));

        var scheduler = CreateScheduler();
        scheduler.BuildDependencyGraph(new List<Rule> { ruleX, ruleY });
        var graph = scheduler.GetDependencyGraph();

        graph[ruleX.Id].Should().BeEmpty();
        graph[ruleY.Id].Should().BeEmpty("sibling paths do not overlap");
    }

    [Fact]
    public void BuildDependencyGraph_LibVsLibrary_NoDependency()
    {
        // This is the false-positive bug the path fix addresses:
        // usr/lib and usr/library should NOT overlap
        var s1 = MakeStorage(1, "src");
        var s2 = MakeStorage(2, "mid");
        var s3 = MakeStorage(3, "dst");

        var ruleX = MakeRule(1, "X",
            MakeEndpoint(1, s1, "usr/lib"),
            MakeEndpoint(2, s2, "usr/lib"));
        var ruleY = MakeRule(2, "Y",
            MakeEndpoint(3, s2, "usr/library"),
            MakeEndpoint(4, s3, "usr/library"));

        var scheduler = CreateScheduler();
        scheduler.BuildDependencyGraph(new List<Rule> { ruleX, ruleY });
        var graph = scheduler.GetDependencyGraph();

        graph[ruleY.Id].Should().BeEmpty(
            "usr/lib and usr/library are different paths and should not overlap");
    }

    [Fact]
    public void BuildDependencyGraph_TransitiveChain_OnlyDirectDependencies()
    {
        // A -> B -> C chain: A writes to X, B reads from X and writes to Y, C reads from Y
        // C should depend on B (not A), B should depend on A
        var s1 = MakeStorage(1, "s1");
        var s2 = MakeStorage(2, "s2");
        var s3 = MakeStorage(3, "s3");
        var s4 = MakeStorage(4, "s4");

        var ruleA = MakeRule(1, "A",
            MakeEndpoint(1, s1, "data"),
            MakeEndpoint(2, s2, "data"));
        var ruleB = MakeRule(2, "B",
            MakeEndpoint(3, s2, "data"),
            MakeEndpoint(4, s3, "data"));
        var ruleC = MakeRule(3, "C",
            MakeEndpoint(5, s3, "data"),
            MakeEndpoint(6, s4, "data"));

        var scheduler = CreateScheduler();
        scheduler.BuildDependencyGraph(new List<Rule> { ruleA, ruleB, ruleC });
        var graph = scheduler.GetDependencyGraph();

        graph[ruleA.Id].Should().BeEmpty("A has no prerequisites");
        graph[ruleB.Id].Should().BeEquivalentTo(new[] { ruleA.Id },
            "B depends on A (reads from s2 where A writes)");
        graph[ruleC.Id].Should().BeEquivalentTo(new[] { ruleB.Id },
            "C depends on B (reads from s3 where B writes), not transitively on A");
    }

    [Fact]
    public void BuildDependencyGraph_EmptyRuleList_NoErrors()
    {
        var scheduler = CreateScheduler();
        scheduler.BuildDependencyGraph(new List<Rule>());
        var graph = scheduler.GetDependencyGraph();

        graph.Should().BeEmpty();
    }

    [Fact]
    public void BuildDependencyGraph_SingleRule_NoDependencies()
    {
        var s1 = MakeStorage(1, "src");
        var s2 = MakeStorage(2, "dst");

        var rule = MakeRule(1, "only",
            MakeEndpoint(1, s1, "data"),
            MakeEndpoint(2, s2, "data"));

        var scheduler = CreateScheduler();
        scheduler.BuildDependencyGraph(new List<Rule> { rule });
        var graph = scheduler.GetDependencyGraph();

        graph[rule.Id].Should().BeEmpty();
    }

    [Fact]
    public void BuildDependencyGraph_DifferentStorages_SamePath_NoDependency()
    {
        // Same path on different storages = no overlap
        var s1 = MakeStorage(1, "src1");
        var s2 = MakeStorage(2, "dst1");
        var s3 = MakeStorage(3, "src2");
        var s4 = MakeStorage(4, "dst2");

        var ruleX = MakeRule(1, "X",
            MakeEndpoint(1, s1, "data"),
            MakeEndpoint(2, s2, "data"));
        var ruleY = MakeRule(2, "Y",
            MakeEndpoint(3, s3, "data"),
            MakeEndpoint(4, s4, "data"));

        var scheduler = CreateScheduler();
        scheduler.BuildDependencyGraph(new List<Rule> { ruleX, ruleY });
        var graph = scheduler.GetDependencyGraph();

        graph[ruleX.Id].Should().BeEmpty();
        graph[ruleY.Id].Should().BeEmpty();
    }
}
