using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orvian.Auditing;
using Orvian.Core.Commands;

namespace Orvian.Persistence;

public sealed class SqliteAuditStore(OrvianDatabase database) :
    IAuditSink,
    IOperationAuditSink,
    IActivityReader,
    IOperationAuditReader,
    ICommandAuditDetailReader,
    IHostTrustAuditReader
{
    public async Task<IReadOnlyList<HostTrustAuditEvent>> QueryHostTrustEventsAsync(
        HostTrustAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 ||
            query.Limit is < 1 or > 500 ||
            query.SearchText?.Length > 200 ||
            InvalidDateRange(query.OccurredAtOrAfter, query.OccurredBefore))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (query.HostProfileId is not null)
        {
            filters.Add("host_profile_id = $host_profile_id");
            command.Parameters.AddWithValue("$host_profile_id", query.HostProfileId);
        }

        if (query.Action is not null)
        {
            filters.Add("action = $action");
            command.Parameters.AddWithValue("$action", (int)query.Action.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            filters.Add(
                """
                event_id IN (
                    SELECT event_id
                    FROM host_trust_audit_search
                    WHERE host_trust_audit_search MATCH $search)
                """);
            command.Parameters.AddWithValue(
                "$search",
                BuildFtsQuery(query.SearchText));
        }

        AddDateFilters(
            query.OccurredAtOrAfter,
            query.OccurredBefore,
            filters,
            command,
            "occurred_at_utc");
        command.CommandText =
            """
            SELECT event_id, occurred_at_utc, host_profile_id, action,
                   host_name, port, resolved_address, algorithm,
                   observed_fingerprint, previous_fingerprint
            FROM host_trust_audit_events
            """ +
            (filters.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", filters)}") +
            " ORDER BY occurred_at_utc DESC, event_id ASC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);

        var events = new List<HostTrustAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new(
                Guid.Parse(reader.GetString(0)),
                Parse(reader.GetString(1)),
                reader.GetString(2),
                (HostTrustAction)reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return events;
    }

    public async Task OperationStartedAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status != OperationAuditStatus.Started ||
            entry.CompletedAt is not null ||
            InvalidRemoteUserName(entry.RemoteUserName))
        {
            throw new ArgumentException(
                "Operation audit start entry is invalid.",
                nameof(entry));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
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
                $operation_id, $started_at_utc, NULL,
                $host_profile_id, $connection_id, $remote_user_name,
                $plugin_id, $plugin_version,
                $title, $purpose, $required_permission, $risk, $privilege,
                $invocation_source, $resource_lock_key, $status, 0, NULL);
            """;
        AddOperationIdentityParameters(command, entry);
        command.Parameters.AddWithValue("$status", (int)OperationAuditStatus.Started);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task OperationCompletedAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status == OperationAuditStatus.Started ||
            entry.CompletedAt is null ||
            entry.CommandCount < 0)
        {
            throw new ArgumentException(
                "Operation audit completion entry is invalid.",
                nameof(entry));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE operation_audit_entries
            SET completed_at_utc = $completed_at_utc,
                status = $status,
                command_count = $command_count,
                safe_failure_message = $safe_failure_message
            WHERE operation_id = $operation_id
              AND host_profile_id = $host_profile_id
              AND remote_user_name = $remote_user_name
              AND plugin_id = $plugin_id
              AND status = $started_status;
            """;
        command.Parameters.AddWithValue(
            "$operation_id",
            entry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$host_profile_id", entry.HostProfileId);
        command.Parameters.AddWithValue("$remote_user_name", entry.RemoteUserName);
        command.Parameters.AddWithValue("$plugin_id", entry.PluginId);
        command.Parameters.AddWithValue(
            "$completed_at_utc",
            Format(entry.CompletedAt.Value));
        command.Parameters.AddWithValue("$status", (int)entry.Status);
        command.Parameters.AddWithValue("$command_count", entry.CommandCount);
        command.Parameters.AddWithValue(
            "$safe_failure_message",
            (object?)entry.SafeFailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$started_status",
            (int)OperationAuditStatus.Started);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "Operation audit completion did not match one pending start entry.");
        }
    }

    public async Task CommandStartedAsync(
        CommandAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status != AuditStatus.Started ||
            entry.CompletedAt is not null ||
            InvalidRemoteUserName(entry.RemoteUserName))
        {
            throw new ArgumentException("Audit start entry is invalid.", nameof(entry));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO command_audit_entries (
                command_id, operation_id, started_at_utc, completed_at_utc,
                host_profile_id, connection_id, remote_user_name,
                plugin_id, plugin_version,
                executable, redacted_arguments_json, privilege, invocation_source,
                status, exit_code, standard_output_bytes, standard_error_bytes,
                output_truncated, failure_classification, safe_failure_message,
                output_logging_mode, retained_standard_output,
                retained_standard_error)
            VALUES (
                $command_id, $operation_id, $started_at_utc, NULL,
                $host_profile_id, $connection_id, $remote_user_name,
                $plugin_id, $plugin_version,
                $executable, $redacted_arguments_json, $privilege, $invocation_source,
                $status, NULL, 0, 0, 0, $failure_classification, NULL,
                $output_logging_mode, NULL, NULL);
            """;
        AddIdentityParameters(command, entry);
        command.Parameters.AddWithValue("$status", (int)AuditStatus.Started);
        command.Parameters.AddWithValue(
            "$failure_classification",
            (int)CommandFailureClassification.None);
        command.Parameters.AddWithValue(
            "$output_logging_mode",
            (int)entry.OutputLogging);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CommandCompletedAsync(
        CommandAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status == AuditStatus.Started || entry.CompletedAt is null)
        {
            throw new ArgumentException("Audit completion entry is invalid.", nameof(entry));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE command_audit_entries
            SET completed_at_utc = $completed_at_utc,
                status = $status,
                exit_code = $exit_code,
                standard_output_bytes = $standard_output_bytes,
                standard_error_bytes = $standard_error_bytes,
                output_truncated = $output_truncated,
                failure_classification = $failure_classification,
                safe_failure_message = $safe_failure_message,
                retained_standard_output = $retained_standard_output,
                retained_standard_error = $retained_standard_error
            WHERE command_id = $command_id
              AND operation_id = $operation_id
              AND host_profile_id = $host_profile_id
              AND remote_user_name = $remote_user_name
              AND plugin_id = $plugin_id
              AND status = $started_status;
            """;
        command.Parameters.AddWithValue("$command_id", entry.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$operation_id", entry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$host_profile_id", entry.HostProfileId);
        command.Parameters.AddWithValue("$remote_user_name", entry.RemoteUserName);
        command.Parameters.AddWithValue("$plugin_id", entry.PluginId);
        command.Parameters.AddWithValue(
            "$completed_at_utc",
            Format(entry.CompletedAt.Value));
        command.Parameters.AddWithValue("$status", (int)entry.Status);
        command.Parameters.AddWithValue("$exit_code", (object?)entry.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$standard_output_bytes",
            entry.StandardOutputBytes);
        command.Parameters.AddWithValue(
            "$standard_error_bytes",
            entry.StandardErrorBytes);
        command.Parameters.AddWithValue("$output_truncated", entry.OutputTruncated ? 1 : 0);
        command.Parameters.AddWithValue(
            "$failure_classification",
            (int)entry.FailureClassification);
        command.Parameters.AddWithValue(
            "$safe_failure_message",
            (object?)entry.SafeFailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$retained_standard_output",
            (object?)entry.RetainedStandardOutput ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$retained_standard_error",
            (object?)entry.RetainedStandardError ?? DBNull.Value);
        command.Parameters.AddWithValue("$started_status", (int)AuditStatus.Started);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "Audit completion did not match one pending start entry.");
        }
    }

    public async Task<IReadOnlyList<ActivityItem>> QueryAsync(
        ActivityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 ||
            query.Limit is < 1 or > 500 ||
            query.SearchText?.Length > 200 ||
            InvalidDateRange(query.StartedAtOrAfter, query.StartedBefore))
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "Activity offset must be nonnegative and limit must be between 1 and 500.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (query.HostProfileId is not null)
        {
            filters.Add("host_profile_id = $host_profile_id");
            command.Parameters.AddWithValue("$host_profile_id", query.HostProfileId);
        }

        if (query.PluginId is not null)
        {
            filters.Add("plugin_id = $plugin_id");
            command.Parameters.AddWithValue("$plugin_id", query.PluginId);
        }

        if (query.InvocationSource is not null)
        {
            filters.Add("invocation_source = $invocation_source");
            command.Parameters.AddWithValue(
                "$invocation_source",
                (int)query.InvocationSource.Value);
        }

        if (query.Status is not null)
        {
            filters.Add("status = $status");
            command.Parameters.AddWithValue("$status", (int)query.Status.Value);
        }

        if (query.Privilege is not null)
        {
            filters.Add("privilege = $privilege");
            command.Parameters.AddWithValue(
                "$privilege",
                (int)query.Privilege.Value);
        }

        if (query.Risk is not null)
        {
            filters.Add(
                """
                EXISTS (
                    SELECT 1
                    FROM operation_audit_entries operation
                    WHERE operation.operation_id =
                          command_audit_entries.operation_id
                      AND operation.risk = $risk)
                """);
            command.Parameters.AddWithValue("$risk", (int)query.Risk.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            filters.Add(
                """
                command_id IN (
                    SELECT command_id
                    FROM command_audit_search
                    WHERE command_audit_search MATCH $search)
                """);
            command.Parameters.AddWithValue(
                "$search",
                BuildFtsQuery(query.SearchText));
        }

        AddDateFilters(
            query.StartedAtOrAfter,
            query.StartedBefore,
            filters,
            command);

        if (!query.IncludeDiagnostics)
        {
            filters.Add("invocation_source <> $discovery_source");
            command.Parameters.AddWithValue(
                "$discovery_source",
                (int)InvocationSource.Discovery);
        }

        if (query.ExcludeOperationCommands)
        {
            filters.Add(
                """
                NOT EXISTS (
                    SELECT 1
                    FROM operation_audit_entries operation
                    WHERE operation.operation_id =
                          command_audit_entries.operation_id)
                """);
        }

        command.CommandText =
            """
            SELECT command_id, operation_id, host_profile_id, remote_user_name, plugin_id,
                   executable, redacted_arguments_json, invocation_source, status,
                   started_at_utc, completed_at_utc, exit_code, output_truncated,
                   failure_classification, safe_failure_message
            FROM command_audit_entries
            """ +
            (filters.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", filters)}") +
            " ORDER BY started_at_utc DESC, command_id ASC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);

        var items = new List<ActivityItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                JsonSerializer.Deserialize<ImmutableArray<string>>(reader.GetString(6)),
                (InvocationSource)reader.GetInt32(7),
                (AuditStatus)reader.GetInt32(8),
                Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.GetInt32(12) != 0,
                (CommandFailureClassification)reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return items;
    }

    public async Task<CommandAuditEntry?> GetCommandAsync(
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command ID is required.", nameof(commandId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT command_id, operation_id, started_at_utc, completed_at_utc,
                   host_profile_id, connection_id, remote_user_name,
                   plugin_id, plugin_version,
                   executable, redacted_arguments_json, privilege,
                   invocation_source, status, exit_code, standard_output_bytes,
                   standard_error_bytes, output_truncated,
                   failure_classification, safe_failure_message,
                   output_logging_mode, retained_standard_output,
                   retained_standard_error
            FROM command_audit_entries
            WHERE command_id = $command_id;
            """;
        command.Parameters.AddWithValue("$command_id", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new()
        {
            CommandId = Guid.Parse(reader.GetString(0)),
            OperationId = Guid.Parse(reader.GetString(1)),
            StartedAt = Parse(reader.GetString(2)),
            CompletedAt = reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
            HostProfileId = reader.GetString(4),
            ConnectionId = reader.GetString(5),
            RemoteUserName = reader.GetString(6),
            PluginId = reader.GetString(7),
            PluginVersion = reader.GetString(8),
            Executable = reader.GetString(9),
            RedactedArguments =
                JsonSerializer.Deserialize<ImmutableArray<string>>(reader.GetString(10)),
            Privilege = (PrivilegeLevel)reader.GetInt32(11),
            InvocationSource = (InvocationSource)reader.GetInt32(12),
            Status = (AuditStatus)reader.GetInt32(13),
            ExitCode = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            StandardOutputBytes = reader.GetInt64(15),
            StandardErrorBytes = reader.GetInt64(16),
            OutputTruncated = reader.GetInt32(17) != 0,
            FailureClassification =
                (CommandFailureClassification)reader.GetInt32(18),
            SafeFailureMessage = reader.IsDBNull(19) ? null : reader.GetString(19),
            OutputLogging = (OutputLoggingMode)reader.GetInt32(20),
            RetainedStandardOutput =
                reader.IsDBNull(21) ? null : reader.GetString(21),
            RetainedStandardError =
                reader.IsDBNull(22) ? null : reader.GetString(22)
        };
    }

    public async Task<IReadOnlyList<OperationAuditEntry>> QueryOperationsAsync(
        OperationAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 ||
            query.Limit is < 1 or > 500 ||
            query.SearchText?.Length > 200 ||
            InvalidDateRange(query.StartedAtOrAfter, query.StartedBefore))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (query.HostProfileId is not null)
        {
            filters.Add("host_profile_id = $host_profile_id");
            command.Parameters.AddWithValue("$host_profile_id", query.HostProfileId);
        }

        if (query.PluginId is not null)
        {
            filters.Add("plugin_id = $plugin_id");
            command.Parameters.AddWithValue("$plugin_id", query.PluginId);
        }

        if (query.Status is not null)
        {
            filters.Add("status = $status");
            command.Parameters.AddWithValue("$status", (int)query.Status.Value);
        }

        if (query.Privilege is not null)
        {
            filters.Add("privilege = $privilege");
            command.Parameters.AddWithValue(
                "$privilege",
                (int)query.Privilege.Value);
        }

        if (query.Risk is not null)
        {
            filters.Add("risk = $risk");
            command.Parameters.AddWithValue("$risk", (int)query.Risk.Value);
        }

        if (query.InvocationSource is not null)
        {
            filters.Add("invocation_source = $invocation_source");
            command.Parameters.AddWithValue(
                "$invocation_source",
                (int)query.InvocationSource.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            filters.Add(
                """
                operation_id IN (
                    SELECT operation_id
                    FROM operation_audit_search
                    WHERE operation_audit_search MATCH $search)
                """);
            command.Parameters.AddWithValue(
                "$search",
                BuildFtsQuery(query.SearchText));
        }

        AddDateFilters(
            query.StartedAtOrAfter,
            query.StartedBefore,
            filters,
            command);

        command.CommandText =
            """
            SELECT operation_id, started_at_utc, completed_at_utc,
                   host_profile_id, connection_id, remote_user_name,
                   plugin_id, plugin_version,
                   title, purpose, required_permission, risk, privilege,
                   invocation_source, resource_lock_key, status, command_count,
                   safe_failure_message
            FROM operation_audit_entries
            """ +
            (filters.Count == 0
                ? string.Empty
                : $" WHERE {string.Join(" AND ", filters)}") +
            " ORDER BY started_at_utc DESC, operation_id ASC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);

        var entries = new List<OperationAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new()
            {
                OperationId = Guid.Parse(reader.GetString(0)),
                StartedAt = Parse(reader.GetString(1)),
                CompletedAt = reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
                HostProfileId = reader.GetString(3),
                ConnectionId = reader.GetString(4),
                RemoteUserName = reader.GetString(5),
                PluginId = reader.GetString(6),
                PluginVersion = reader.GetString(7),
                Title = reader.GetString(8),
                Purpose = reader.GetString(9),
                RequiredPermission = reader.GetString(10),
                Risk = (OperationRisk)reader.GetInt32(11),
                Privilege = (PrivilegeLevel)reader.GetInt32(12),
                InvocationSource = (InvocationSource)reader.GetInt32(13),
                ResourceLockKey = reader.IsDBNull(14) ? null : reader.GetString(14),
                Status = (OperationAuditStatus)reader.GetInt32(15),
                CommandCount = reader.GetInt32(16),
                SafeFailureMessage = reader.IsDBNull(17) ? null : reader.GetString(17)
            });
        }

        return entries;
    }

    private static void AddIdentityParameters(
        SqliteCommand command,
        CommandAuditEntry entry)
    {
        command.Parameters.AddWithValue("$command_id", entry.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$operation_id", entry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$started_at_utc", Format(entry.StartedAt));
        command.Parameters.AddWithValue("$host_profile_id", entry.HostProfileId);
        command.Parameters.AddWithValue("$connection_id", entry.ConnectionId);
        command.Parameters.AddWithValue("$remote_user_name", entry.RemoteUserName);
        command.Parameters.AddWithValue("$plugin_id", entry.PluginId);
        command.Parameters.AddWithValue("$plugin_version", entry.PluginVersion);
        command.Parameters.AddWithValue("$executable", entry.Executable);
        command.Parameters.AddWithValue(
            "$redacted_arguments_json",
            JsonSerializer.Serialize(entry.RedactedArguments));
        command.Parameters.AddWithValue("$privilege", (int)entry.Privilege);
        command.Parameters.AddWithValue("$invocation_source", (int)entry.InvocationSource);
    }

    private static void AddOperationIdentityParameters(
        SqliteCommand command,
        OperationAuditEntry entry)
    {
        command.Parameters.AddWithValue(
            "$operation_id",
            entry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$started_at_utc", Format(entry.StartedAt));
        command.Parameters.AddWithValue("$host_profile_id", entry.HostProfileId);
        command.Parameters.AddWithValue("$connection_id", entry.ConnectionId);
        command.Parameters.AddWithValue("$remote_user_name", entry.RemoteUserName);
        command.Parameters.AddWithValue("$plugin_id", entry.PluginId);
        command.Parameters.AddWithValue("$plugin_version", entry.PluginVersion);
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$purpose", entry.Purpose);
        command.Parameters.AddWithValue(
            "$required_permission",
            entry.RequiredPermission);
        command.Parameters.AddWithValue("$risk", (int)entry.Risk);
        command.Parameters.AddWithValue("$privilege", (int)entry.Privilege);
        command.Parameters.AddWithValue(
            "$invocation_source",
            (int)entry.InvocationSource);
        command.Parameters.AddWithValue(
            "$resource_lock_key",
            (object?)entry.ResourceLockKey ?? DBNull.Value);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddDateFilters(
        DateTimeOffset? startedAtOrAfter,
        DateTimeOffset? startedBefore,
        ICollection<string> filters,
        SqliteCommand command,
        string columnName = "started_at_utc")
    {
        if (startedAtOrAfter is not null)
        {
            filters.Add($"{columnName} >= $started_at_or_after");
            command.Parameters.AddWithValue(
                "$started_at_or_after",
                Format(startedAtOrAfter.Value));
        }

        if (startedBefore is not null)
        {
            filters.Add($"{columnName} < $started_before");
            command.Parameters.AddWithValue(
                "$started_before",
                Format(startedBefore.Value));
        }
    }

    private static bool InvalidDateRange(
        DateTimeOffset? startedAtOrAfter,
        DateTimeOffset? startedBefore) =>
        startedAtOrAfter is not null &&
        startedBefore is not null &&
        startedAtOrAfter >= startedBefore;

    private static bool InvalidRemoteUserName(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Length > 256 ||
        value.IndexOfAny(['\0', '\r', '\n']) >= 0;

    private static string BuildFtsQuery(string searchText) =>
        string.Join(
            " AND ",
            searchText.Split(
                    ' ',
                    StringSplitOptions.TrimEntries |
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
}
