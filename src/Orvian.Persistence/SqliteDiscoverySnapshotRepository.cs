using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Orvian.Application.Discovery;
using Orvian.Core.Hosts;
using Orvian.Discovery;

namespace Orvian.Persistence;

public sealed class SqliteDiscoverySnapshotRepository(OrvianDatabase database)
    : IDiscoverySnapshotRepository
{
    public async Task StoreAsync(
        DiscoverySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var databaseTransaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var transaction = (SqliteTransaction)databaseTransaction;

        await InsertSnapshotAsync(connection, transaction, snapshot, cancellationToken)
            .ConfigureAwait(false);
        foreach (var fact in snapshot.Facts.Values)
        {
            await InsertFactAsync(
                connection,
                transaction,
                snapshot.Id,
                fact,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var capability in snapshot.Capabilities)
        {
            await InsertCapabilityAsync(
                connection,
                transaction,
                snapshot.Id,
                capability,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var outcome in snapshot.ProbeOutcomes)
        {
            await InsertOutcomeAsync(
                connection,
                transaction,
                snapshot.Id,
                outcome,
                cancellationToken).ConfigureAwait(false);
        }

        await using var latest = connection.CreateCommand();
        latest.Transaction = transaction;
        latest.CommandText =
            """
            INSERT INTO latest_discovery_snapshots(host_profile_id, snapshot_id)
            VALUES ($host_profile_id, $snapshot_id)
            ON CONFLICT(host_profile_id) DO UPDATE SET
                snapshot_id = excluded.snapshot_id;
            """;
        latest.Parameters.AddWithValue(
            "$host_profile_id",
            snapshot.HostProfileId.ToString());
        latest.Parameters.AddWithValue("$snapshot_id", snapshot.Id.Value.ToString("D"));
        await latest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await databaseTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiscoverySnapshot?> GetLatestAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.host_profile_id, s.connection_id, s.started_at_utc,
                   s.completed_at_utc, s.operating_system
            FROM latest_discovery_snapshots l
            JOIN discovery_snapshots s ON s.id = l.snapshot_id
            WHERE l.host_profile_id = $host_profile_id;
            """;
        command.Parameters.AddWithValue("$host_profile_id", hostProfileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var snapshotId = new DiscoverySnapshotId(Guid.Parse(reader.GetString(0)));
        var connectionId = reader.GetString(2);
        var startedAt = ParseTimestamp(reader.GetString(3));
        var completedAt = ParseTimestamp(reader.GetString(4));
        var operatingSystem = (OperatingSystemFamily)reader.GetInt32(5);
        await reader.DisposeAsync().ConfigureAwait(false);

        var facts = await ReadFactsAsync(connection, snapshotId, cancellationToken)
            .ConfigureAwait(false);
        var capabilities = await ReadCapabilitiesAsync(
            connection,
            snapshotId,
            cancellationToken).ConfigureAwait(false);
        var outcomes = await ReadOutcomesAsync(connection, snapshotId, cancellationToken)
            .ConfigureAwait(false);
        return new(
            snapshotId,
            hostProfileId,
            connectionId,
            startedAt,
            completedAt,
            operatingSystem,
            facts,
            capabilities,
            outcomes);
    }

    private static async Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoverySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO discovery_snapshots (
                id, host_profile_id, connection_id, started_at_utc,
                completed_at_utc, operating_system, is_partial)
            VALUES (
                $id, $host_profile_id, $connection_id, $started_at_utc,
                $completed_at_utc, $operating_system, $is_partial);
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "$host_profile_id",
            snapshot.HostProfileId.ToString());
        command.Parameters.AddWithValue("$connection_id", snapshot.ConnectionId);
        command.Parameters.AddWithValue("$started_at_utc", FormatTimestamp(snapshot.StartedAt));
        command.Parameters.AddWithValue(
            "$completed_at_utc",
            FormatTimestamp(snapshot.CompletedAt));
        command.Parameters.AddWithValue("$operating_system", (int)snapshot.OperatingSystem);
        command.Parameters.AddWithValue("$is_partial", snapshot.IsPartial ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoverySnapshotId snapshotId,
        DiscoveryFact fact,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO discovery_facts (
                snapshot_id, fact_key, fact_value, probe_id, observed_at_utc)
            VALUES (
                $snapshot_id, $fact_key, $fact_value, $probe_id, $observed_at_utc);
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.Value.ToString("D"));
        command.Parameters.AddWithValue("$fact_key", fact.Key);
        command.Parameters.AddWithValue("$fact_value", fact.Value);
        command.Parameters.AddWithValue("$probe_id", fact.ProbeId);
        command.Parameters.AddWithValue("$observed_at_utc", FormatTimestamp(fact.ObservedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCapabilityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoverySnapshotId snapshotId,
        string capability,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO discovery_capabilities(snapshot_id, capability_id)
            VALUES ($snapshot_id, $capability_id);
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.Value.ToString("D"));
        command.Parameters.AddWithValue("$capability_id", capability);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoverySnapshotId snapshotId,
        DiscoveryProbeOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO discovery_probe_outcomes (
                snapshot_id, probe_id, status, safe_failure_message)
            VALUES (
                $snapshot_id, $probe_id, $status, $safe_failure_message);
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.Value.ToString("D"));
        command.Parameters.AddWithValue("$probe_id", outcome.ProbeId);
        command.Parameters.AddWithValue("$status", (int)outcome.Status);
        command.Parameters.AddWithValue(
            "$safe_failure_message",
            (object?)outcome.SafeFailureMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ImmutableDictionary<string, DiscoveryFact>> ReadFactsAsync(
        SqliteConnection connection,
        DiscoverySnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT fact_key, fact_value, probe_id, observed_at_utc
            FROM discovery_facts
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var facts = ImmutableDictionary.CreateBuilder<string, DiscoveryFact>(
            StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            facts.Add(
                key,
                new(key, reader.GetString(1), reader.GetString(2), ParseTimestamp(reader.GetString(3))));
        }

        return facts.ToImmutable();
    }

    private static async Task<ImmutableHashSet<string>> ReadCapabilitiesAsync(
        SqliteConnection connection,
        DiscoverySnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT capability_id
            FROM discovery_capabilities
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var values = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values.ToImmutable();
    }

    private static async Task<ImmutableArray<DiscoveryProbeOutcome>> ReadOutcomesAsync(
        SqliteConnection connection,
        DiscoverySnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT probe_id, status, safe_failure_message
            FROM discovery_probe_outcomes
            WHERE snapshot_id = $snapshot_id
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var outcomes = ImmutableArray.CreateBuilder<DiscoveryProbeOutcome>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            outcomes.Add(new(
                reader.GetString(0),
                (DiscoveryProbeStatus)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return outcomes.ToImmutable();
    }

    private static void Validate(DiscoverySnapshot snapshot)
    {
        if (snapshot.Id.Value == Guid.Empty ||
            snapshot.HostProfileId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(snapshot.ConnectionId) ||
            snapshot.CompletedAt < snapshot.StartedAt)
        {
            throw new ArgumentException("Discovery snapshot is invalid.", nameof(snapshot));
        }

        if (snapshot.Facts.Any(pair =>
                !string.Equals(pair.Key, pair.Value.Key, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Discovery fact dictionary keys must match fact keys.",
                nameof(snapshot));
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
