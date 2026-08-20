using TestSupport.Api;
using Xunit;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Every test class carrying [Collection(PostgresCollection.Name)] shares one
/// throwaway database and therefore one CREATE/DROP cycle per run.
///
/// The definition has to live in this assembly - xUnit only discovers a
/// collection definition alongside the tests that use it - while
/// <see cref="PostgresFixture"/> itself is shared from TestSupport.Api.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL integration";
}
