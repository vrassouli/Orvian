using Microsoft.Data.Sqlite;

namespace Orvian.Persistence;

public sealed class OrvianDatabase
{
    private readonly string _connectionString;

    public OrvianDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var databaseTransaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var transaction = (SqliteTransaction)databaseTransaction;

        var version = await GetSchemaVersionAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (version < 1)
        {
            await ApplyVersionOneAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 1;
        }

        if (version < 2)
        {
            await ApplyVersionTwoAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 2;
        }

        if (version < 3)
        {
            await ApplyVersionThreeAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 3;
        }

        if (version < 4)
        {
            await ApplyVersionFourAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 4;
        }

        if (version < 5)
        {
            await ApplyVersionFiveAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 5;
        }

        if (version < 6)
        {
            await ApplyVersionSixAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 6;
        }

        if (version < 7)
        {
            await ApplyVersionSevenAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 7;
        }

        if (version < 8)
        {
            await ApplyVersionEightAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 8;
        }

        if (version < 9)
        {
            await ApplyVersionNineAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 9;
        }

        if (version < 10)
        {
            await ApplyVersionTenAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 10;
        }

        if (version < 11)
        {
            await ApplyVersionElevenAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 11;
        }

        if (version < 12)
        {
            await ApplyVersionTwelveAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            version = 12;
        }

        if (version < 13)
        {
            await ApplyVersionThirteenAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        await databaseTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            transaction: null,
            """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            """,
            cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyVersionOneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE host_profiles (
                id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL,
                host_name TEXT NOT NULL,
                port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                user_name TEXT NOT NULL,
                authentication_method INTEGER NOT NULL,
                credential_secret_reference TEXT NULL,
                notes TEXT NULL,
                is_enabled INTEGER NOT NULL,
                connection_timeout_ms INTEGER NOT NULL,
                maximum_reconnect_attempts INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE host_tags (
                host_profile_id TEXT NOT NULL,
                tag TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (host_profile_id, tag),
                FOREIGN KEY (host_profile_id) REFERENCES host_profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_host_profiles_display_name ON host_profiles(display_name);
            CREATE INDEX ix_host_profiles_host_name ON host_profiles(host_name);
            CREATE INDEX ix_host_tags_tag ON host_tags(tag);

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionTwoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE trusted_host_keys (
                host_profile_id TEXT NOT NULL PRIMARY KEY,
                host_name TEXT NOT NULL,
                port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                algorithm TEXT NOT NULL,
                sha256_fingerprint TEXT NOT NULL,
                trusted_at_utc TEXT NOT NULL,
                FOREIGN KEY (host_profile_id) REFERENCES host_profiles(id) ON DELETE CASCADE
            );

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionThreeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE discovery_snapshots (
                id TEXT NOT NULL PRIMARY KEY,
                host_profile_id TEXT NOT NULL,
                connection_id TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NOT NULL,
                operating_system INTEGER NOT NULL,
                is_partial INTEGER NOT NULL,
                FOREIGN KEY (host_profile_id) REFERENCES host_profiles(id) ON DELETE CASCADE
            );

            CREATE TABLE discovery_facts (
                snapshot_id TEXT NOT NULL,
                fact_key TEXT NOT NULL,
                fact_value TEXT NOT NULL,
                probe_id TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, fact_key),
                FOREIGN KEY (snapshot_id) REFERENCES discovery_snapshots(id) ON DELETE CASCADE
            );

            CREATE TABLE discovery_capabilities (
                snapshot_id TEXT NOT NULL,
                capability_id TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, capability_id),
                FOREIGN KEY (snapshot_id) REFERENCES discovery_snapshots(id) ON DELETE CASCADE
            );

            CREATE TABLE discovery_probe_outcomes (
                snapshot_id TEXT NOT NULL,
                probe_id TEXT NOT NULL,
                status INTEGER NOT NULL,
                safe_failure_message TEXT NULL,
                PRIMARY KEY (snapshot_id, probe_id),
                FOREIGN KEY (snapshot_id) REFERENCES discovery_snapshots(id) ON DELETE CASCADE
            );

            CREATE TABLE latest_discovery_snapshots (
                host_profile_id TEXT NOT NULL PRIMARY KEY,
                snapshot_id TEXT NOT NULL UNIQUE,
                FOREIGN KEY (host_profile_id) REFERENCES host_profiles(id) ON DELETE CASCADE,
                FOREIGN KEY (snapshot_id) REFERENCES discovery_snapshots(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_discovery_snapshots_host_completed
                ON discovery_snapshots(host_profile_id, completed_at_utc DESC);

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionFourAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE command_audit_entries (
                command_id TEXT NOT NULL PRIMARY KEY,
                operation_id TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL,
                host_profile_id TEXT NOT NULL,
                connection_id TEXT NOT NULL,
                plugin_id TEXT NOT NULL,
                plugin_version TEXT NOT NULL,
                executable TEXT NOT NULL,
                redacted_arguments_json TEXT NOT NULL,
                privilege INTEGER NOT NULL,
                invocation_source INTEGER NOT NULL,
                status INTEGER NOT NULL,
                exit_code INTEGER NULL,
                standard_output_bytes INTEGER NOT NULL,
                standard_error_bytes INTEGER NOT NULL,
                output_truncated INTEGER NOT NULL,
                failure_classification INTEGER NOT NULL,
                safe_failure_message TEXT NULL
            );

            CREATE INDEX ix_command_audit_started
                ON command_audit_entries(started_at_utc DESC);
            CREATE INDEX ix_command_audit_host_started
                ON command_audit_entries(host_profile_id, started_at_utc DESC);

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionFiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            ALTER TABLE host_profiles ADD COLUMN private_key_path TEXT NULL;

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (5, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionSixAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE operation_audit_entries (
                operation_id TEXT NOT NULL PRIMARY KEY,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL,
                host_profile_id TEXT NOT NULL,
                connection_id TEXT NOT NULL,
                plugin_id TEXT NOT NULL,
                plugin_version TEXT NOT NULL,
                title TEXT NOT NULL,
                purpose TEXT NOT NULL,
                required_permission TEXT NOT NULL,
                risk INTEGER NOT NULL,
                privilege INTEGER NOT NULL,
                invocation_source INTEGER NOT NULL,
                resource_lock_key TEXT NULL,
                status INTEGER NOT NULL,
                command_count INTEGER NOT NULL,
                safe_failure_message TEXT NULL
            );

            CREATE INDEX ix_operation_audit_started
                ON operation_audit_entries(started_at_utc DESC);
            CREATE INDEX ix_operation_audit_host_started
                ON operation_audit_entries(host_profile_id, started_at_utc DESC);
            CREATE INDEX ix_operation_audit_plugin_started
                ON operation_audit_entries(plugin_id, started_at_utc DESC);
            CREATE INDEX ix_command_audit_operation
                ON command_audit_entries(operation_id, started_at_utc);

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (6, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionSevenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            ALTER TABLE command_audit_entries
                ADD COLUMN output_logging_mode INTEGER NOT NULL DEFAULT 2;
            ALTER TABLE command_audit_entries
                ADD COLUMN retained_standard_output TEXT NULL;
            ALTER TABLE command_audit_entries
                ADD COLUMN retained_standard_error TEXT NULL;

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (7, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionEightAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE plugin_states (
                plugin_id TEXT NOT NULL PRIMARY KEY,
                observed_version TEXT NOT NULL,
                is_enabled INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
                lifecycle_state TEXT NOT NULL,
                diagnostics_json TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (8, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionNineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            ALTER TABLE trusted_host_keys ADD COLUMN resolved_address TEXT NULL;

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (9, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionTenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE host_trust_audit_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                occurred_at_utc TEXT NOT NULL,
                host_profile_id TEXT NOT NULL,
                action INTEGER NOT NULL,
                host_name TEXT NOT NULL,
                port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                resolved_address TEXT NULL,
                algorithm TEXT NOT NULL,
                observed_fingerprint TEXT NOT NULL,
                previous_fingerprint TEXT NULL
            );

            CREATE INDEX ix_host_trust_audit_occurred
                ON host_trust_audit_events(occurred_at_utc DESC);
            CREATE INDEX ix_host_trust_audit_host_occurred
                ON host_trust_audit_events(host_profile_id, occurred_at_utc DESC);

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (10, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionElevenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE application_settings (
                setting_key TEXT NOT NULL PRIMARY KEY,
                schema_version INTEGER NOT NULL CHECK (schema_version >= 1),
                value_json TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (11, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionTwelveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            ALTER TABLE command_audit_entries
                ADD COLUMN remote_user_name TEXT NOT NULL DEFAULT '[unknown]';
            ALTER TABLE operation_audit_entries
                ADD COLUMN remote_user_name TEXT NOT NULL DEFAULT '[unknown]';

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (12, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionThirteenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE VIRTUAL TABLE command_audit_search USING fts5(
                command_id UNINDEXED,
                remote_user_name,
                plugin_id,
                executable,
                redacted_arguments);

            INSERT INTO command_audit_search (
                command_id, remote_user_name, plugin_id,
                executable, redacted_arguments)
            SELECT command_id, remote_user_name, plugin_id,
                   executable, redacted_arguments_json
            FROM command_audit_entries;

            CREATE TRIGGER command_audit_search_insert
            AFTER INSERT ON command_audit_entries
            BEGIN
                INSERT INTO command_audit_search (
                    command_id, remote_user_name, plugin_id,
                    executable, redacted_arguments)
                VALUES (
                    new.command_id, new.remote_user_name, new.plugin_id,
                    new.executable, new.redacted_arguments_json);
            END;

            CREATE TRIGGER command_audit_search_delete
            AFTER DELETE ON command_audit_entries
            BEGIN
                DELETE FROM command_audit_search
                WHERE command_id = old.command_id;
            END;

            CREATE VIRTUAL TABLE operation_audit_search USING fts5(
                operation_id UNINDEXED,
                remote_user_name,
                plugin_id,
                title,
                purpose);

            INSERT INTO operation_audit_search (
                operation_id, remote_user_name, plugin_id, title, purpose)
            SELECT operation_id, remote_user_name, plugin_id, title, purpose
            FROM operation_audit_entries;

            CREATE TRIGGER operation_audit_search_insert
            AFTER INSERT ON operation_audit_entries
            BEGIN
                INSERT INTO operation_audit_search (
                    operation_id, remote_user_name, plugin_id, title, purpose)
                VALUES (
                    new.operation_id, new.remote_user_name, new.plugin_id,
                    new.title, new.purpose);
            END;

            CREATE TRIGGER operation_audit_search_delete
            AFTER DELETE ON operation_audit_entries
            BEGIN
                DELETE FROM operation_audit_search
                WHERE operation_id = old.operation_id;
            END;

            CREATE VIRTUAL TABLE host_trust_audit_search USING fts5(
                event_id UNINDEXED,
                action,
                host_name,
                resolved_address,
                algorithm,
                observed_fingerprint,
                previous_fingerprint);

            INSERT INTO host_trust_audit_search (
                event_id, action, host_name, resolved_address, algorithm,
                observed_fingerprint, previous_fingerprint)
            SELECT event_id,
                   CASE action
                       WHEN 0 THEN 'TrustFirstSeen'
                       WHEN 1 THEN 'ReplaceChanged'
                       ELSE 'Unknown'
                   END,
                   host_name, COALESCE(resolved_address, ''), algorithm,
                   observed_fingerprint, COALESCE(previous_fingerprint, '')
            FROM host_trust_audit_events;

            CREATE TRIGGER host_trust_audit_search_insert
            AFTER INSERT ON host_trust_audit_events
            BEGIN
                INSERT INTO host_trust_audit_search (
                    event_id, action, host_name, resolved_address, algorithm,
                    observed_fingerprint, previous_fingerprint)
                VALUES (
                    new.event_id,
                    CASE new.action
                        WHEN 0 THEN 'TrustFirstSeen'
                        WHEN 1 THEN 'ReplaceChanged'
                        ELSE 'Unknown'
                    END,
                    new.host_name, COALESCE(new.resolved_address, ''),
                    new.algorithm, new.observed_fingerprint,
                    COALESCE(new.previous_fingerprint, ''));
            END;

            CREATE TRIGGER host_trust_audit_search_delete
            AFTER DELETE ON host_trust_audit_events
            BEGIN
                DELETE FROM host_trust_audit_search
                WHERE event_id = old.event_id;
            END;

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (13, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
