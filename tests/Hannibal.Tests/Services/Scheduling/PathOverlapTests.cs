using FluentAssertions;
using Hannibal.Models;
using Hannibal.Services;

namespace Hannibal.Tests.Services.Scheduling;

/// <summary>
/// Tests for endpoint key normalization and path overlap detection.
/// </summary>
public class PathOverlapTests
{
    [Theory]
    [InlineData("usr/lib", "usr/lib", true)]       // Same path
    [InlineData("usr", "usr/lib", true)]            // Parent contains child
    [InlineData("usr/lib", "usr", true)]            // Child within parent
    [InlineData("usr/lib", "usr/bin", false)]       // Siblings, no overlap
    [InlineData("usr/lib", "usr/library", false)]   // Previously false positive! "usr/lib" vs "usr/library"
    [InlineData("", "", true)]                       // Both root
    [InlineData("a/b/c", "a/b/c/d/e", true)]       // Deep nesting
    [InlineData("a/b/c/d/e", "a/b/c", true)]       // Deep nesting reversed
    public void PathsOverlap_WithSameStorage_DetectsCorrectly(string pathA, string pathB, bool expected)
    {
        var keyA = HannibalService.EndpointKey(1, pathA);
        var keyB = HannibalService.EndpointKey(1, pathB);

        var result = HannibalService.PathsOverlap(keyA, keyB);

        result.Should().Be(expected,
            because: $"paths '{pathA}' and '{pathB}' on same storage should {(expected ? "" : "not ")}overlap");
    }

    [Theory]
    [InlineData(1, "usr/lib", 2, "usr/lib")]    // Same path, different storage
    [InlineData(1, "usr", 2, "usr/lib")]         // Parent/child, different storage
    public void PathsOverlap_DifferentStorages_NeverOverlap(int storageA, string pathA, int storageB, string pathB)
    {
        var keyA = HannibalService.EndpointKey(storageA, pathA);
        var keyB = HannibalService.EndpointKey(storageB, pathB);

        var result = HannibalService.PathsOverlap(keyA, keyB);

        result.Should().BeFalse(
            because: "endpoints on different storages should never overlap");
    }

    [Fact]
    public void EndpointKey_NormalizesTrailingSlash()
    {
        var key1 = HannibalService.EndpointKey(1, "usr/lib");
        var key2 = HannibalService.EndpointKey(1, "usr/lib/");

        key1.Should().Be(key2, because: "trailing slash should be normalized");
    }

    [Fact]
    public void EndpointKey_TrimsWhitespace()
    {
        var key1 = HannibalService.EndpointKey(1, "usr/lib");
        var key2 = HannibalService.EndpointKey(1, "  usr/lib  ");

        key1.Should().Be(key2, because: "whitespace should be trimmed");
    }

    [Fact]
    public void EndpointKey_FromEndpoint_MatchesStaticMethod()
    {
        var endpoint = new Endpoint
        {
            StorageId = 5,
            Path = "usr/lib",
            Storage = new Storage { Id = 5 }
        };

        var fromEndpoint = HannibalService.EndpointKey(endpoint);
        var fromStatic = HannibalService.EndpointKey(5, "usr/lib");

        fromEndpoint.Should().Be(fromStatic);
    }
}
