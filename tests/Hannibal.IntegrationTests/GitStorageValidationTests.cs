using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate A AC2/AC3 (docs/plan-git-repo-storage.md): a git Storage and its
/// Endpoints round-trip through the API, and the new server-side validation -
/// the codebase's first - rejects malformed input with a 4xx naming the
/// offending field, while every pre-existing technology is unaffected.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GitStorageValidationTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string StoragesRoute = "/api/hannibal/v1/storages";
    private const string EndpointsRoute = "/api/hannibal/v1/endpoints";

    public GitStorageValidationTests(PostgresFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task A_git_storage_and_two_endpoints_round_trip_through_the_api()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("git-roundtrip"), Password);

        var storage = new Storage
        {
            Technology = "git",
            UriSchema = "ghmain",
            Host = "https://github.com/",
            Username = "git-user",
            Password = "ghp_faketoken",
            Networks = ""
        };

        var createStorageResponse = await client.PostAsJsonAsync(StoragesRoute, storage);
        createStorageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var storageResult = await createStorageResponse.Content.ReadFromJsonAsync<CreateStorageResult>();
        storageResult.Should().NotBeNull();

        var fetchedStorage = await (await client.GetAsync($"{StoragesRoute}/{storageResult!.Id}"))
            .Content.ReadFromJsonAsync<Storage>();
        fetchedStorage.Should().NotBeNull();
        fetchedStorage!.Technology.Should().Be("git");
        fetchedStorage.UriSchema.Should().Be("ghmain");
        fetchedStorage.Host.Should().Be("https://github.com/");
        fetchedStorage.Username.Should().Be("git-user");
        fetchedStorage.Password.Should().Be("ghp_faketoken");

        var endpointOne = new Endpoint
        {
            StorageId = storageResult.Id,
            Name = "git-endpoint-one",
            Path = "owner/repo-one",
            Comment = "first mirror"
        };
        var endpointTwo = new Endpoint
        {
            StorageId = storageResult.Id,
            Name = "git-endpoint-two",
            Path = "owner/repo-two",
            Comment = "second mirror"
        };

        foreach (var endpoint in new[] { endpointOne, endpointTwo })
        {
            var createEndpointResponse = await client.PostAsJsonAsync(EndpointsRoute, endpoint);
            createEndpointResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                $"creating endpoint '{endpoint.Name}' should succeed");
        }

        var allEndpoints = await (await client.GetAsync(EndpointsRoute))
            .Content.ReadFromJsonAsync<List<Endpoint>>();
        allEndpoints.Should().NotBeNull();

        var fetchedOne = allEndpoints!.Should().ContainSingle(e => e.Name == "git-endpoint-one").Subject;
        fetchedOne.Path.Should().Be("owner/repo-one");
        fetchedOne.Comment.Should().Be("first mirror");
        fetchedOne.StorageId.Should().Be(storageResult.Id);

        var fetchedTwo = allEndpoints.Should().ContainSingle(e => e.Name == "git-endpoint-two").Subject;
        fetchedTwo.Path.Should().Be("owner/repo-two");
        fetchedTwo.Comment.Should().Be("second mirror");
        fetchedTwo.StorageId.Should().Be(storageResult.Id);
    }

    [SkippableFact]
    public async Task Unknown_technology_is_rejected_with_a_400_naming_the_field()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("git-unknown-tech"), Password);

        var storage = new Storage { Technology = "carrier-pigeon", UriSchema = "cp1" };

        var response = await client.PostAsJsonAsync(StoragesRoute, storage);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Technology");
        body.Should().Contain("carrier-pigeon");
    }

    [SkippableFact]
    public async Task Git_storage_with_empty_host_is_rejected_with_a_400_naming_the_field()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("git-empty-host"), Password);

        var storage = new Storage { Technology = "git", UriSchema = "emptyhost", Host = "" };

        var response = await client.PostAsJsonAsync(StoragesRoute, storage);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Host");
    }

    [SkippableFact]
    public async Task Git_storage_with_colliding_uri_schema_is_rejected_with_a_400_naming_the_field()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("git-collide"), Password);

        var first = new Storage
        {
            Technology = "git", UriSchema = "collideschema", Host = "https://github.com/"
        };
        (await client.PostAsJsonAsync(StoragesRoute, first)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = new Storage
        {
            Technology = "git", UriSchema = "collideschema", Host = "https://codeberg.org/"
        };
        var response = await client.PostAsJsonAsync(StoragesRoute, second);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("UriSchema");
    }

    [SkippableFact]
    public async Task Git_endpoint_path_without_exactly_one_slash_is_rejected_for_a_url_host()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("git-path-slash"), Password);

        var storageId = await _createGitStorageAsync(client, "pathslash", "https://github.com/");

        var endpoint = new Endpoint { StorageId = storageId, Name = "bad-path-slash", Path = "no-slash-here" };
        var response = await client.PostAsJsonAsync(EndpointsRoute, endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Path");
    }

    [SkippableFact]
    public async Task Git_endpoint_path_containing_dotdot_is_rejected()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("git-path-dotdot"), Password);

        var storageId = await _createGitStorageAsync(client, "pathdotdot", "https://github.com/");

        var endpoint = new Endpoint { StorageId = storageId, Name = "bad-path-dotdot", Path = "owner/../etc" };
        var response = await client.PostAsJsonAsync(EndpointsRoute, endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Path");
    }

    [SkippableTheory]
    [InlineData("onedrive")]
    [InlineData("dropbox")]
    [InlineData("googledrive")]
    [InlineData("nextcloud")]
    [InlineData("smb")]
    [InlineData("local")]
    public async Task Each_existing_technology_still_creates_successfully(string technology)
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(
            UniqueEmail($"existing-tech-{technology}"), Password);

        var storage = new Storage { Technology = technology, UriSchema = $"schema-{technology}" };

        var response = await client.PostAsJsonAsync(StoragesRoute, storage);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"technology '{technology}' predates validation and must keep working");
    }

    private static async Task<int> _createGitStorageAsync(HttpClient client, string uriSchema, string host)
    {
        var storage = new Storage { Technology = "git", UriSchema = uriSchema, Host = host };
        var response = await client.PostAsJsonAsync(StoragesRoute, storage);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<CreateStorageResult>();
        return result!.Id;
    }
}
