using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;
using Hannibal.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate E work item 7 (plan-git-repo-storage.md): the Porter export/import
/// round-trip covers the two new per-rule git safety-guard overrides,
/// <c>Rule.AllowAdopt</c> and <c>Rule.AllowUnsafeRefChange</c>, exactly like
/// every other rule field (Gate G AC2 will extend this to Storages/Endpoints
/// end-to-end through the UI; this is the service-level proof the mechanism
/// itself carries the new fields). No Porter tests existed before this file.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RulePorterGitSafetyOverridesTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string StoragesRoute = "/api/hannibal/v1/storages";
    private const string EndpointsRoute = "/api/hannibal/v1/endpoints";
    private const string RulesRoute = "/api/hannibal/v1/rules";
    // Both query parameters are non-nullable, default-less [FromQuery] bool
    // in Api/Program.cs's minimal API handler, so minimal APIs treat them as
    // required - omitting either one 400s before ExportConfigAsync ever runs.
    private const string ExportRoute = "/api/hannibal/v1/config/export?includePasswords=true&includeInactive=false";
    private const string ImportRoute = "/api/hannibal/v1/config/import";

    public RulePorterGitSafetyOverridesTests(PostgresFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task Export_wipe_import_round_trips_AllowAdopt_and_AllowUnsafeRefChange()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("porter-git-overrides"), Password);

        var storageId = await _createStorageAsync(client, "git", "porterflags", "https://github.com/");
        var sourceEndpointId = await _createEndpointAsync(client, storageId, "owner/repo-one");
        var destinationEndpointId = await _createEndpointAsync(client, storageId, "owner/repo-two");

        var ruleName = $"porter-flags-rule-{Guid.NewGuid():N}";
        var createResponse = await client.PostAsJsonAsync(RulesRoute, new Rule
        {
            Name = ruleName,
            SourceEndpointId = sourceEndpointId,
            DestinationEndpointId = destinationEndpointId,
            Operation = Rule.RuleOperation.Copy,
            AllowAdopt = true,
            AllowUnsafeRefChange = true
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateRuleResult>();
        created.Should().NotBeNull();

        // Sanity: the flags actually persisted on create, before touching Porter at all.
        await using (var context = Fixture.CreateContext())
        {
            var persisted = await context.Rules.AsNoTracking().FirstAsync(r => r.Id == created!.Id);
            persisted.AllowAdopt.Should().BeTrue();
            persisted.AllowUnsafeRefChange.Should().BeTrue();
        }

        var exportResponse = await client.GetAsync(ExportRoute);
        var exportJson = await exportResponse.Content.ReadAsStringAsync();
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var exported = await exportResponse.Content.ReadFromJsonAsync<ConfigExport>();
        exported.Should().NotBeNull();
        var exportedRule = exported!.Rules.Should().ContainSingle(r => r.Name == ruleName).Subject;
        exportedRule.AllowAdopt.Should().BeTrue();
        exportedRule.AllowUnsafeRefChange.Should().BeTrue();

        // Wipe: delete the rule (the endpoints/storage stay - the import
        // below re-resolves them by name/UriSchema, exactly like a real
        // "start over on rules only" restore would).
        var deleteResponse = await client.DeleteAsync($"{RulesRoute}/{created!.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = Fixture.CreateContext())
        {
            (await context.Rules.AsNoTracking().AnyAsync(r => r.Id == created.Id)).Should().BeFalse(
                "the rule must actually be gone before import re-creates it");
        }

        var importResponse = await client.PostAsJsonAsync(ImportRoute, new ConfigImportRequest
        {
            ConfigJson = exportJson,
            MergeStrategy = MergeStrategy.SkipExisting
        });
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var importResult = await importResponse.Content.ReadFromJsonAsync<ImportResult>();
        importResult.Should().NotBeNull();
        importResult!.RulesCreated.Should().Be(1, string.Join("; ", importResult.Errors.Concat(importResult.Warnings)));

        await using (var context = Fixture.CreateContext())
        {
            var reimported = await context.Rules.AsNoTracking().SingleAsync(r => r.Name == ruleName);
            reimported.AllowAdopt.Should().BeTrue();
            reimported.AllowUnsafeRefChange.Should().BeTrue();
        }

        await Fixture.ResetAsync();
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
}
