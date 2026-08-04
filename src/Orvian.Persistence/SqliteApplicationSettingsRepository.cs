using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orvian.Application.Settings;

namespace Orvian.Persistence;

public sealed class SqliteApplicationSettingsRepository(OrvianDatabase database)
    : IApplicationSettingsRepository
{
    private const string AppearanceKey = "appearance";
    private const string CultureKey = "culture";
    private const string ConnectionTimeoutKey = "connection.default_timeout_ms";
    private const string ReconnectAttemptsKey = "connection.default_reconnect_attempts";
    private const string OutputRetentionKey = "retention.output_days";
    private const string AuditRetentionKey = "retention.audit_days";
    private const string ClearCredentialsKey = "security.clear_session_credentials_on_disconnect";

    public async Task<ApplicationSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT setting_key, schema_version, value_json
            FROM application_settings
            WHERE setting_key IN (
                $appearance, $culture, $timeout, $retries,
                $output_retention, $audit_retention, $clear_credentials);
            """;
        command.Parameters.AddWithValue("$appearance", AppearanceKey);
        command.Parameters.AddWithValue("$culture", CultureKey);
        command.Parameters.AddWithValue("$timeout", ConnectionTimeoutKey);
        command.Parameters.AddWithValue("$retries", ReconnectAttemptsKey);
        command.Parameters.AddWithValue("$output_retention", OutputRetentionKey);
        command.Parameters.AddWithValue("$audit_retention", AuditRetentionKey);
        command.Parameters.AddWithValue("$clear_credentials", ClearCredentialsKey);
        var incompatibleVersion = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(1) > ApplicationSettings.SchemaVersion)
            {
                incompatibleVersion = true;
                continue;
            }

            values[reader.GetString(0)] = reader.GetString(2);
        }

        if (incompatibleVersion)
        {
            return new(
                ApplicationSettings.Default,
                UsedDefaults: true,
                "Some settings were written by a newer Remotune version. Conservative defaults are active and the newer records were preserved.");
        }

        try
        {
            var defaults = ApplicationSettings.Default;
            var settings = new ApplicationSettings(
                Read(values, AppearanceKey, defaults.Appearance),
                Read(values, CultureKey, defaults.CultureName),
                TimeSpan.FromMilliseconds(Read(
                    values,
                    ConnectionTimeoutKey,
                    (long)defaults.DefaultConnectionTimeout.TotalMilliseconds)),
                Read(values, ReconnectAttemptsKey, defaults.DefaultMaximumReconnectAttempts),
                Read(values, OutputRetentionKey, defaults.OutputRetentionDays),
                Read(values, AuditRetentionKey, defaults.AuditRetentionDays),
                Read(
                    values,
                    ClearCredentialsKey,
                    defaults.ClearSessionCredentialsOnDisconnect));
            ApplicationSettings.Validate(settings);
            return new(settings, UsedDefaults: false);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or OverflowException)
        {
            return new(
                ApplicationSettings.Default,
                UsedDefaults: true,
                "Invalid noncritical settings were ignored. Conservative defaults are active until settings are saved again.");
        }
    }

    public async Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ApplicationSettings.Validate(settings);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var values = new (string Key, string Json)[]
        {
            (AppearanceKey, JsonSerializer.Serialize(settings.Appearance)),
            (CultureKey, JsonSerializer.Serialize(settings.CultureName)),
            (
                ConnectionTimeoutKey,
                JsonSerializer.Serialize((long)settings.DefaultConnectionTimeout.TotalMilliseconds)),
            (ReconnectAttemptsKey, JsonSerializer.Serialize(settings.DefaultMaximumReconnectAttempts)),
            (OutputRetentionKey, JsonSerializer.Serialize(settings.OutputRetentionDays)),
            (AuditRetentionKey, JsonSerializer.Serialize(settings.AuditRetentionDays)),
            (
                ClearCredentialsKey,
                JsonSerializer.Serialize(settings.ClearSessionCredentialsOnDisconnect))
        };

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var (key, json) in values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO application_settings (
                    setting_key, schema_version, value_json, updated_at_utc)
                VALUES ($key, $version, $json, $updated_at_utc)
                ON CONFLICT(setting_key) DO UPDATE SET
                    schema_version = excluded.schema_version,
                    value_json = excluded.value_json,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$version", ApplicationSettings.SchemaVersion);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$updated_at_utc", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Read<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue) =>
        values.TryGetValue(key, out var json)
            ? JsonSerializer.Deserialize<T>(json)!
            : defaultValue;
}
