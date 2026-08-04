using System.Globalization;
using Microsoft.Data.Sqlite;
using Orvian.Auditing;
using Orvian.Core.Commands;

namespace Orvian.Persistence;

public sealed class SqliteAuditRetentionService(
    OrvianDatabase database,
    TimeProvider? timeProvider = null) : IAuditRetentionService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AuditRetentionResult> CleanupBatchAsync(
        AuditRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.BatchSize is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Retention batch size must be between 1 and 5000.");
        }

        var cleanupId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var databaseTransaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var transaction = (SqliteTransaction)databaseTransaction;

        var purgedOutputs = 0;
        if (policy.DeleteOutputBefore is not null)
        {
            await using var purge = connection.CreateCommand();
            purge.Transaction = transaction;
            purge.CommandText =
                """
                UPDATE command_audit_entries
                SET retained_standard_output = NULL,
                    retained_standard_error = NULL
                WHERE command_id IN (
                    SELECT command_id
                    FROM command_audit_entries
                    WHERE completed_at_utc IS NOT NULL
                      AND completed_at_utc < $output_cutoff
                      AND status <> $started
                      AND (retained_standard_output IS NOT NULL
                           OR retained_standard_error IS NOT NULL)
                    ORDER BY completed_at_utc
                    LIMIT $limit);
                """;
            purge.Parameters.AddWithValue(
                "$output_cutoff",
                Format(policy.DeleteOutputBefore.Value));
            purge.Parameters.AddWithValue("$started", (int)AuditStatus.Started);
            purge.Parameters.AddWithValue("$limit", policy.BatchSize);
            purgedOutputs = await purge.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var operationIds = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT operation_id
                FROM operation_audit_entries operation
                WHERE completed_at_utc IS NOT NULL
                  AND completed_at_utc < $cutoff
                  AND status <> $started
                  AND NOT EXISTS (
                      SELECT 1
                      FROM command_audit_entries command
                      WHERE command.operation_id = operation.operation_id
                        AND command.status = $command_started)
                ORDER BY completed_at_utc
                LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$cutoff", Format(policy.DeleteCompletedBefore));
            select.Parameters.AddWithValue("$started", (int)OperationAuditStatus.Started);
            select.Parameters.AddWithValue("$command_started", (int)AuditStatus.Started);
            select.Parameters.AddWithValue("$limit", policy.BatchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                operationIds.Add(reader.GetString(0));
            }
        }

        var deletedCommands = 0;
        var deletedOperations = 0;
        foreach (var operationId in operationIds)
        {
            await using var deleteCommands = connection.CreateCommand();
            deleteCommands.Transaction = transaction;
            deleteCommands.CommandText =
                """
                DELETE FROM command_audit_entries
                WHERE operation_id = $operation_id
                  AND status <> $started;
                """;
            deleteCommands.Parameters.AddWithValue("$operation_id", operationId);
            deleteCommands.Parameters.AddWithValue("$started", (int)AuditStatus.Started);
            deletedCommands += await deleteCommands
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var deleteOperation = connection.CreateCommand();
            deleteOperation.Transaction = transaction;
            deleteOperation.CommandText =
                """
                DELETE FROM operation_audit_entries
                WHERE operation_id = $operation_id
                  AND status <> $started;
                """;
            deleteOperation.Parameters.AddWithValue("$operation_id", operationId);
            deleteOperation.Parameters.AddWithValue(
                "$started",
                (int)OperationAuditStatus.Started);
            deletedOperations += await deleteOperation
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using (var deleteLegacyCommands = connection.CreateCommand())
        {
            deleteLegacyCommands.Transaction = transaction;
            deleteLegacyCommands.CommandText =
                """
                DELETE FROM command_audit_entries
                WHERE command_id IN (
                    SELECT command.command_id
                    FROM command_audit_entries command
                    WHERE command.completed_at_utc IS NOT NULL
                      AND command.completed_at_utc < $cutoff
                      AND command.status <> $started
                      AND NOT EXISTS (
                          SELECT 1
                          FROM operation_audit_entries operation
                          WHERE operation.operation_id = command.operation_id)
                    ORDER BY command.completed_at_utc
                    LIMIT $limit);
                """;
            deleteLegacyCommands.Parameters.AddWithValue(
                "$cutoff",
                Format(policy.DeleteCompletedBefore));
            deleteLegacyCommands.Parameters.AddWithValue("$started", (int)AuditStatus.Started);
            deleteLegacyCommands.Parameters.AddWithValue("$limit", policy.BatchSize);
            deletedCommands += await deleteLegacyCommands
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await InsertCleanupAuditAsync(
            connection,
            transaction,
            cleanupId,
            now,
            deletedOperations,
            deletedCommands,
            purgedOutputs,
            cancellationToken).ConfigureAwait(false);
        await databaseTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            deletedOperations,
            deletedCommands,
            purgedOutputs,
            cleanupId);
    }

    private static async Task InsertCleanupAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        DateTimeOffset timestamp,
        int deletedOperations,
        int deletedCommands,
        int purgedOutputs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO operation_audit_entries (
                operation_id, started_at_utc, completed_at_utc,
                host_profile_id, connection_id, remote_user_name,
                plugin_id, plugin_version,
                title, purpose, required_permission, risk, privilege,
                invocation_source, resource_lock_key, status, command_count,
                safe_failure_message)
            VALUES (
                $operation_id, $timestamp, $timestamp,
                'local', 'local', '[local]', 'orvian.core.retention', '1.0',
                'Audit retention cleanup', $purpose, 'host.read',
                $risk, $privilege, $invocation_source, 'audit:retention',
                $status, 0, NULL);
            """;
        command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$timestamp", Format(timestamp));
        command.Parameters.AddWithValue(
            "$purpose",
            $"Deleted {deletedOperations} operation and {deletedCommands} command audit " +
            $"records and purged output from {purgedOutputs} commands.");
        command.Parameters.AddWithValue("$risk", (int)OperationRisk.Informational);
        command.Parameters.AddWithValue("$privilege", (int)PrivilegeLevel.User);
        command.Parameters.AddWithValue(
            "$invocation_source",
            (int)InvocationSource.UserInterface);
        command.Parameters.AddWithValue("$status", (int)OperationAuditStatus.Succeeded);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
