using FluentAssertions;
using Hannibal;
using Hannibal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate A acceptance 4: proves the connection string resolution order of
/// <see cref="DependencyInjection.AddHannibalService"/> is
/// ConnectionStrings:DefaultConnection -> HANNIBAL_DB_CONNECTION -> hardcoded
/// fallback.
///
/// These tests mutate a process-wide environment variable, so they share a
/// collection to keep them from running concurrently with one another. They
/// never open a database connection - only the registered DbContextOptions is
/// inspected.
/// </summary>
[Collection(ConnectionStringResolutionTests.CollectionName)]
public class ConnectionStringResolutionTests
{
    public const string CollectionName = "Connection string resolution";

    private const string EnvironmentVariable = "HANNIBAL_DB_CONNECTION";

    private const string HardcodedFallback =
        "Host=localhost;Port=5432;Database=hannibal;Username=postgres;Password=admin";


    [Fact]
    public void DefaultConnection_configuration_wins_over_the_environment_variable()
    {
        const string fromConfiguration = "Host=config-host;Port=5433;Database=config_db;Username=u;Password=p";
        const string fromEnvironment = "Host=env-host;Port=5434;Database=env_db;Username=u;Password=p";

        _withEnvironmentVariable(fromEnvironment, () =>
        {
            var resolved = _resolveConnectionString(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = fromConfiguration
            });

            resolved.Should().Be(fromConfiguration);
        });
    }


    [Fact]
    public void Environment_variable_is_used_when_no_DefaultConnection_is_configured()
    {
        const string fromEnvironment = "Host=env-host;Port=5434;Database=env_db;Username=u;Password=p";

        _withEnvironmentVariable(fromEnvironment, () =>
        {
            var resolved = _resolveConnectionString(new Dictionary<string, string?>());

            resolved.Should().Be(fromEnvironment);
        });
    }


    [Fact]
    public void Hardcoded_fallback_is_used_when_neither_source_is_set()
    {
        _withEnvironmentVariable(null, () =>
        {
            var resolved = _resolveConnectionString(new Dictionary<string, string?>());

            resolved.Should().Be(HardcodedFallback);
        });
    }


    [Fact]
    public void Blank_DefaultConnection_falls_through_to_the_environment_variable()
    {
        const string fromEnvironment = "Host=env-host;Port=5434;Database=env_db;Username=u;Password=p";

        _withEnvironmentVariable(fromEnvironment, () =>
        {
            var resolved = _resolveConnectionString(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "   "
            });

            resolved.Should().Be(fromEnvironment);
        });
    }


    /// <summary>
    /// Gate A acceptance 5: registration must not print the connection string
    /// (and therefore the database password) to stdout.
    /// </summary>
    [Fact]
    public void Registration_never_writes_the_connection_string_to_stdout()
    {
        const string secretPassword = "sup3r-s3cret-passw0rd";
        var connectionString =
            $"Host=secret-host;Port=5432;Database=secret_db;Username=postgres;Password={secretPassword}";

        var originalOut = Console.Out;
        var captured = new StringWriter();
        string stdout;

        try
        {
            Console.SetOut(captured);
            _resolveConnectionString(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString
            });
            stdout = captured.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        stdout.Should().NotContain(secretPassword);
        stdout.Should().NotContain(connectionString);

        // The non-secret coordinates may be reported, and are useful for support.
        stdout.Should().Contain("secret-host:5432/secret_db");
    }


    private static string _resolveConnectionString(IDictionary<string, string?> configurationValues)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHannibalService(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<HannibalContext>>();
        var relationalOptions = options.Extensions
            .OfType<RelationalOptionsExtension>()
            .Single();

        relationalOptions.ConnectionString.Should().NotBeNull();
        return relationalOptions.ConnectionString!;
    }


    private static void _withEnvironmentVariable(string? value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, original);
        }
    }
}


/// <summary>
/// Disables parallelism between the environment-variable-mutating tests above.
/// </summary>
[CollectionDefinition(ConnectionStringResolutionTests.CollectionName, DisableParallelization = true)]
public sealed class ConnectionStringResolutionCollection
{
}
