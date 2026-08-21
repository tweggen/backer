using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;
using Hannibal.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate G AC2 (plan-git-repo-storage.md "### Gate G"): the Porter export/
/// import round-trip covers a full git Storage (Host/Username/Password/
/// UriSchema), its Endpoints and a git Rule (including the Gate E safety-
/// guard overrides) exactly, and the existing "don't blank the token on a
/// passwordless import" mechanism (<see cref="RulePorterGitSafetyOverridesTests"/>
/// proved it for the rule flags; this proves it for the git storage fields).
/// </summary>
[Collection(PostgresCollection.Name)]
public class GitStoragePorterRoundTripTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string StoragesRoute = "/api/hannibal/v1/storages";
    private const string EndpointsRoute = "/api/hannibal/v1/endpoints";
    private const string RulesRoute = "/api/hannibal/v1/rules";
    private const string ExportRouteWithPasswords = "/api/hannibal/v1/config/export?includePasswords=true&includeInactive=false";
    private const string ExportRouteNoPasswords = "/api/hannibal/v1/config/export?includePasswords=false&includeInactive=false";
    private const string ImportRoute = "/api/hannibal/v1/config/import";

    public GitStoragePorterRoundTripTests(PostgresFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task Export_wipe_import_reproduces_git_storage_endpoints_and_rule_exactly()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("porter-git-roundtrip"), Password);

        var uriSchema = $"gitmirror{Guid.NewGuid():N}"[..24];
        const string host = "https://github.com/";
        const string username = "octocat";
        var token = $"ghp_{Guid.NewGuid():N}";

        var storageId = await _createGitStorageAsync(client, uriSchema, host, username, token);
        var sourceEndpointId = await _createEndpointAsync(client, storageId, "owner/repo-one");
        var destinationEndpointId = await _createEndpointAsync(client, storageId, "owner/repo-two");

        var ruleName = $"git-roundtrip-rule-{Guid.NewGuid():N}";
        var createRuleResponse = await client.PostAsJsonAsync(RulesRoute, new Rule
        {
            Name = ruleName,
            SourceEndpointId = sourceEndpointId,
            DestinationEndpointId = destinationEndpointId,
            Operation = Rule.RuleOperation.Sync,
            MaxDestinationAge = TimeSpan.FromHours(6),
            MinRetryTime = TimeSpan.FromMinutes(10),
            MaxTimeAfterSourceModification = TimeSpan.FromMinutes(45),
            DailyTriggerTime = TimeSpan.FromHours(2),
            AllowAdopt = true,
            AllowUnsafeRefChange = true
        });
        createRuleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdRule = await createRuleResponse.Content.ReadFromJsonAsync<CreateRuleResult>();
        createdRule.Should().NotBeNull();

        // Export with passwords included, and check the export DTO carries
        // every git field before touching the database at all.
        var exportResponse = await client.GetAsync(ExportRouteWithPasswords);
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportJson = await exportResponse.Content.ReadAsStringAsync();
        var exported = await exportResponse.Content.ReadFromJsonAsync<ConfigExport>();
        exported.Should().NotBeNull();

        var exportedStorage = exported!.Storages.Should().ContainSingle(s => s.UriSchema == uriSchema).Subject;
        exportedStorage.Technology.Should().Be("git");
        exportedStorage.Host.Should().Be(host);
        exportedStorage.Username.Should().Be(username);
        exportedStorage.Password.Should().Be(token);

        exported.Endpoints.Should().Contain(e => e.StorageRef == uriSchema && e.Path == "owner/repo-one");
        exported.Endpoints.Should().Contain(e => e.StorageRef == uriSchema && e.Path == "owner/repo-two");

        var exportedRule = exported.Rules.Should().ContainSingle(r => r.Name == ruleName).Subject;
        exportedRule.Operation.Should().Be("Sync");
        exportedRule.AllowAdopt.Should().BeTrue();
        exportedRule.AllowUnsafeRefChange.Should().BeTrue();

        // Wipe + import in one call: MergeStrategy.ReplaceAll deletes all of
        // this user's data first, then recreates it purely from the export -
        // the strongest form of "reproduces exactly" available through the
        // public API.
        var importResponse = await client.PostAsJsonAsync(ImportRoute, new ConfigImportRequest
        {
            ConfigJson = exportJson,
            MergeStrategy = MergeStrategy.ReplaceAll
        });
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var importResult = await importResponse.Content.ReadFromJsonAsync<ImportResult>();
        importResult.Should().NotBeNull();
        importResult!.Errors.Should().BeEmpty();
        importResult.StoragesCreated.Should().Be(1);
        importResult.EndpointsCreated.Should().Be(2);
        importResult.RulesCreated.Should().Be(1);

        await using (var context = Fixture.CreateContext())
        {
            var reimportedStorage = await context.Storages.AsNoTracking()
                .SingleAsync(s => s.UriSchema == uriSchema);
            reimportedStorage.Technology.Should().Be("git");
            reimportedStorage.Host.Should().Be(host);
            reimportedStorage.Username.Should().Be(username);
            reimportedStorage.Password.Should().Be(token);

            var reimportedEndpoints = await context.Endpoints.AsNoTracking()
                .Where(e => e.StorageId == reimportedStorage.Id)
                .ToListAsync();
            reimportedEndpoints.Should().HaveCount(2);
            reimportedEndpoints.Select(e => e.Path).Should().BeEquivalentTo("owner/repo-one", "owner/repo-two");

            var reimportedRule = await context.Rules.AsNoTracking()
                .Include(r => r.SourceEndpoint)
                .Include(r => r.DestinationEndpoint)
                .SingleAsync(r => r.Name == ruleName);
            reimportedRule.Operation.Should().Be(Rule.RuleOperation.Sync);
            reimportedRule.MaxDestinationAge.Should().Be(TimeSpan.FromHours(6));
            reimportedRule.MinRetryTime.Should().Be(TimeSpan.FromMinutes(10));
            reimportedRule.MaxTimeAfterSourceModification.Should().Be(TimeSpan.FromMinutes(45));
            reimportedRule.DailyTriggerTime.Should().Be(TimeSpan.FromHours(2));
            reimportedRule.AllowAdopt.Should().BeTrue();
            reimportedRule.AllowUnsafeRefChange.Should().BeTrue();
            reimportedRule.SourceEndpoint.Path.Should().Be("owner/repo-one");
            reimportedRule.DestinationEndpoint.Path.Should().Be("owner/repo-two");
        }

        await Fixture.ResetAsync();
    }

    [SkippableFact]
    public async Task Export_with_includePasswords_false_omits_token_and_import_preserves_existing_token()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("porter-git-notoken"), Password);

        var uriSchema = $"gitnotoken{Guid.NewGuid():N}"[..24];
        const string host = "https://codeberg.org/";
        const string username = "backer-bot";
        var token = $"ghp_{Guid.NewGuid():N}";

        await _createGitStorageAsync(client, uriSchema, host, username, token);

        // includePasswords=false: the token must not appear ANYWHERE in the
        // raw export payload, not merely be absent from the deserialised
        // Password property.
        var exportResponse = await client.GetAsync(ExportRouteNoPasswords);
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportJson = await exportResponse.Content.ReadAsStringAsync();
        exportJson.Should().NotContain(token, "includePasswords=false must never leak the stored token into the export payload");

        var exported = await JsonRoundTripAsync(exportJson);
        var exportedStorage = exported.Storages.Should().ContainSingle(s => s.UriSchema == uriSchema).Subject;
        exportedStorage.Password.Should().BeNull();
        exportedStorage.Host.Should().Be(host);
        exportedStorage.Username.Should().Be(username);

        // Importing that passwordless export back over the SAME (existing)
        // storage must leave its stored token intact rather than blanking it.
        var importResponse = await client.PostAsJsonAsync(ImportRoute, new ConfigImportRequest
        {
            ConfigJson = exportJson,
            MergeStrategy = MergeStrategy.UpdateExisting
        });
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var importResult = await importResponse.Content.ReadFromJsonAsync<ImportResult>();
        importResult.Should().NotBeNull();
        importResult!.Errors.Should().BeEmpty();
        importResult.StoragesUpdated.Should().Be(1);
        importResult.StoragesCreated.Should().Be(0);

        await using (var context = Fixture.CreateContext())
        {
            var storageAfterImport = await context.Storages.AsNoTracking()
                .SingleAsync(s => s.UriSchema == uriSchema);
            storageAfterImport.Password.Should().Be(token,
                "a passwordless import must not blank an existing stored token");
        }

        await Fixture.ResetAsync();
    }

    private static Task<ConfigExport> JsonRoundTripAsync(string json)
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var export = System.Text.Json.JsonSerializer.Deserialize<ConfigExport>(json, options);
        export.Should().NotBeNull();
        return Task.FromResult(export!);
    }

    private static async Task<int> _createGitStorageAsync(
        HttpClient client, string uriSchema, string host, string username, string password)
    {
        var storage = new Storage
        {
            Technology = "git",
            UriSchema = uriSchema,
            Host = host,
            Username = username,
            Password = password
        };
        var response = await client.PostAsJsonAsync(StoragesRoute, storage);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "creating a git storage should succeed");
        var result = await response.Content.ReadFromJsonAsync<CreateStorageResult>();
        return result!.Id;
    }

    private static async Task<int> _createEndpointAsync(HttpClient client, int storageId, string path)
    {
        // Endpoint has no default-true IsActive (unlike Storage's constructor) -
        // the export's includeInactive=false filter would silently drop it
        // otherwise.
        var endpoint = new Endpoint { StorageId = storageId, Name = $"endpoint-{Guid.NewGuid():N}", Path = path, IsActive = true };
        var response = await client.PostAsJsonAsync(EndpointsRoute, endpoint);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"creating endpoint with path '{path}' should succeed");
        var result = await response.Content.ReadFromJsonAsync<CreateEndpointResult>();
        return result!.Id;
    }
}
