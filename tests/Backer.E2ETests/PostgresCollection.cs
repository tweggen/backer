using TestSupport.Api;
using Xunit;

namespace Backer.E2ETests;

/// <summary>
/// One throwaway database for the whole full-loop run. The definition has to
/// live in this assembly - xUnit only discovers a collection definition
/// alongside the tests that use it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL full loop";
}
