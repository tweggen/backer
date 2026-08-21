using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hannibal.Data;

/// <summary>
/// Brings the database schema up to date at service startup.
///
/// <para>This replaces the old pair of <c>Database.Migrate()</c> (no retry -
/// a PostgreSQL that was still starting killed the container) and
/// <c>EnsureCreatedAsync</c> (which creates schema without migration history
/// and thereby poisons the database for every future <c>Migrate()</c>).
/// See <c>docs/plan-db-migration-strategy.md</c> for the full story.</para>
/// </summary>
public static class StartupMigrator
{
    /// <summary>
    /// Total time to keep retrying transient connection failures before giving
    /// up. Bounded on purpose: an unreachable database must fail the start
    /// with a clear exception instead of hanging the host forever.
    /// </summary>
    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    public static async Task MigrateAsync(
        HannibalContext context,
        ILogger logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? _defaultTimeout);
        var attempt = 0;
        bool databaseExists;
        IReadOnlyList<string> applied = Array.Empty<string>();

        /*
         * Phase 1 - reach the server. Only genuinely transient failures are
         * retried; a real error (wrong password, broken migration) surfaces
         * immediately. A missing database is not an error: Migrate() below
         * creates it. The probe is an explicit connection open, not
         * GetAppliedMigrationsAsync - the history repository swallows a
         * missing database into "no migrations applied", which is
         * indistinguishable from the poisoned state checked in phase 2.
         */
        while (true)
        {
            attempt++;
            try
            {
                await context.Database.OpenConnectionAsync(cancellationToken);
                await context.Database.CloseConnectionAsync();
                databaseExists = true;
                break;
            }
            catch (Exception e) when (_isMissingDatabase(e))
            {
                databaseExists = false;
                break;
            }
            catch (Exception e) when (_isTransient(e))
            {
                if (DateTime.UtcNow + _retryDelay >= deadline)
                {
                    throw new TimeoutException(
                        $"The database was not reachable after {attempt} attempt(s). "
                        + "The connection source in use was logged at startup by the "
                        + "Hannibal service registration. See inner exception for the "
                        + "last failure.",
                        e);
                }

                logger.LogWarning(
                    "Database not reachable yet (attempt {Attempt}): {Message}. Retrying...",
                    attempt, e.Message);
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        /*
         * Phase 2 - refuse the poisoned state explicitly. A database that has
         * tables but no recorded migrations was created by EnsureCreated (or
         * restored without its history table); Migrate() would die on
         * "relation already exists", which diagnoses nothing.
         */
        if (databaseExists)
        {
            applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken))
                .ToList();
        }

        if (databaseExists && applied.Count == 0
                           && await _hasAnyTableAsync(context, cancellationToken))
        {
            throw new InvalidOperationException(
                "The database contains tables but no migration history "
                + "(__EFMigrationsHistory is missing or empty), so migrations cannot "
                + "be applied to it. Do not wipe it. Follow the baseline runbook in "
                + "docs/plan-db-migration-strategy.md (Gate M2) to record the "
                + "migrations whose schema is already present.");
        }

        /*
         * Phase 3 - say what is about to happen, then do it. A deploy that
         * migrates silently is a deploy that fails undiagnosably.
         */
        var pending = databaseExists
            ? (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList()
            : context.Database.GetMigrations().ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation(
                "Database schema is up to date ({Applied} migration(s) applied).",
                applied.Count);
            return;
        }

        logger.LogInformation(
            databaseExists
                ? "Applying {Count} pending migration(s): {Migrations}"
                : "Creating the database and applying {Count} migration(s): {Migrations}",
            pending.Count, string.Join(", ", pending));

        await context.Database.MigrateAsync(cancellationToken);

        var nowApplied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToList();
        logger.LogInformation(
            "Migration complete; {Applied} migration(s) applied in total.",
            nowApplied.Count);
    }


    /// <summary>
    /// True when the failure means "the database does not exist yet"
    /// (PostgreSQL invalid_catalog_name) - a fresh install, not an error.
    /// </summary>
    internal static bool _isMissingDatabase(Exception e)
        => _chain(e).OfType<PostgresException>()
            .Any(p => p.SqlState == PostgresErrorCodes.InvalidCatalogName);

    /// <summary>
    /// True for failures worth retrying: the server is not reachable or not
    /// ready yet. False for anything the server actually answered with (wrong
    /// credentials, a failed statement) - retrying those diagnoses nothing.
    /// </summary>
    internal static bool _isTransient(Exception e)
        => _chain(e).Any(x => x switch
        {
            PostgresException p => p.SqlState == PostgresErrorCodes.CannotConnectNow,
            NpgsqlException n => n.IsTransient,
            SocketException => true,
            TimeoutException => true,
            _ => false
        });

    private static IEnumerable<Exception> _chain(Exception e)
    {
        for (Exception? current = e; current != null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static async Task<bool> _hasAnyTableAsync(
        HannibalContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables "
                + "WHERE table_schema = 'public' AND table_type = 'BASE TABLE')";
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        finally
        {
            if (wasClosed)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }
}
