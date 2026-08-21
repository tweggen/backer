using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate B (docs/plan-git-repo-storage.md): the server classifies which
/// engine a rule's endpoint pair needs and rejects every rule no engine can
/// run - mixed git/non-git pairs because they are unsupportable, git+git
/// pairs because the engine does not exist yet (Gate D flips that one to
/// allowed), and git+git pairs that are the same repository twice, even
/// across two different Storage rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RuleEngineValidationTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string StoragesRoute = "/api/hannibal/v1/storages";
    private const string EndpointsRoute = "/api/hannibal/v1/endpoints";
    private const string RulesRoute = "/api/hannibal/v1/rules";

    public RuleEngineValidationTests(PostgresFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task Mixed_git_and_onedrive_rule_is_rejected_naming_both_technologies()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("mixed-pair"), Password);

        var gitStorageId = await _createStorageAsync(client, "git", "gitmix", "https://github.com/");
        var gitEndpointId = await _createEndpointAsync(client, gitStorageId, "owner/repo");

        var oneDriveStorageId = await _createStorageAsync(client, "onedrive", "odmix", host: null);
        var oneDriveEndpointId = await _createEndpointAsync(client, oneDriveStorageId, "some/folder");

        var response = await _postRuleAsync(client, gitEndpointId, oneDriveEndpointId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("git");
        body.Should().Contain("onedrive");
    }

    [SkippableFact]
    public async Task Git_plus_git_rule_is_rejected_because_the_engine_does_not_exist_yet()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("git-plus-git"), Password);

        var storageId = await _createStorageAsync(client, "git", "gitengine", "https://github.com/");
        var sourceEndpointId = await _createEndpointAsync(client, storageId, "owner/repo-one");
        var destinationEndpointId = await _createEndpointAsync(client, storageId, "owner/repo-two");

        var response = await _postRuleAsync(client, sourceEndpointId, destinationEndpointId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not yet available");
    }

    [SkippableFact]
    public async Task Self_mirror_across_two_different_storages_is_rejected_with_the_same_repository_message()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("self-mirror"), Password);

        // Two different Storage rows, different UriSchema, whose Host
        // normalises to the same URL - this is exactly the blind spot the
        // endpoint-in-use check cannot see (HannibalServiceJobs.cs:90-95).
        var storageOneId = await _createStorageAsync(client, "git", "selfmirror1", "https://github.com/");
        var storageTwoId = await _createStorageAsync(client, "git", "selfmirror2", "https://GITHUB.com");

        var sourceEndpointId = await _createEndpointAsync(client, storageOneId, "owner/repo");
        var destinationEndpointId = await _createEndpointAsync(client, storageTwoId, "owner/repo");

        var response = await _postRuleAsync(client, sourceEndpointId, destinationEndpointId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("same repository");
        body.Should().NotContain("not yet available",
            "the self-mirror message must win over the generic 'engine not available' message");
    }

    [SkippableFact]
    public async Task Rclone_technology_rule_still_creates_successfully()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("rclone-rule-ok"), Password);

        var storageId = await _createStorageAsync(client, "local", "localrule", host: null);
        var sourceEndpointId = await _createEndpointAsync(client, storageId, "C:/source");
        var destinationEndpointId = await _createEndpointAsync(client, storageId, "C:/destination");

        var response = await _postRuleAsync(client, sourceEndpointId, destinationEndpointId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<CreateRuleResult>();
        result.Should().NotBeNull();
        result!.Id.Should().BeGreaterThan(0);
    }

    private static async Task<int> _createStorageAsync(HttpClient client, string technology, string uriSchema, string? host)
    {
        var storage = new Storage { Technology = technology, UriSchema = uriSchema, Host = host ?? "" };
        var response = await client.PostAsJsonAsync(StoragesRoute, storage);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"creating a '{technology}' storage should succeed");
        var result = await response.Content.ReadFromJsonAsync<CreateStorageResult>();
        return result!.Id;
    }

    private static async Task<int> _createEndpointAsync(HttpClient client, int storageId, string path)
    {
        var endpoint = new Endpoint { StorageId = storageId, Name = $"endpoint-{Guid.NewGuid():N}", Path = path };
        var response = await client.PostAsJsonAsync(EndpointsRoute, endpoint);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"creating endpoint with path '{path}' should succeed");
        var result = await response.Content.ReadFromJsonAsync<CreateEndpointResult>();
        return result!.Id;
    }

    private static async Task<HttpResponseMessage> _postRuleAsync(HttpClient client, int sourceEndpointId, int destinationEndpointId)
    {
        var rule = new Rule
        {
            Name = $"rule-{Guid.NewGuid():N}",
            SourceEndpointId = sourceEndpointId,
            DestinationEndpointId = destinationEndpointId,
            Operation = Rule.RuleOperation.Copy
        };
        return await client.PostAsJsonAsync(RulesRoute, rule);
    }
}
