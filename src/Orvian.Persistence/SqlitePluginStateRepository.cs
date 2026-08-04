using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orvian.Application.Plugins;

namespace Orvian.Persistence;

public sealed class SqlitePluginStateRepository(OrvianDatabase database)
    : IPluginStateRepository
{
    public async Task<PersistedPluginState?> GetAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT observed_version, is_enabled, lifecycle_state,
                   diagnostics_json, updated_at_utc
            FROM plugin_states
            WHERE plugin_id = $plugin_id;
            """;
        command.Parameters.AddWithValue("$plugin_id", pluginId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var diagnostics = JsonSerializer.Deserialize<ImmutableArray<PersistedPluginDiagnostic>>(
            reader.GetString(3));
        return new(
            pluginId,
            reader.GetString(0),
            reader.GetInt64(1) == 1,
            reader.GetString(2),
            diagnostics.IsDefault ? [] : diagnostics,
            DateTimeOffset.Parse(
                reader.GetString(4),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

    public async Task UpsertAsync(
        PersistedPluginState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.PluginId);

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plugin_states(
                plugin_id, observed_version, is_enabled, lifecycle_state,
                diagnostics_json, updated_at_utc)
            VALUES(
                $plugin_id, $observed_version, $is_enabled, $lifecycle_state,
                $diagnostics_json, $updated_at_utc)
            ON CONFLICT(plugin_id) DO UPDATE SET
                observed_version = excluded.observed_version,
                is_enabled = excluded.is_enabled,
                lifecycle_state = excluded.lifecycle_state,
                diagnostics_json = excluded.diagnostics_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$plugin_id", state.PluginId);
        command.Parameters.AddWithValue("$observed_version", state.ObservedVersion);
        command.Parameters.AddWithValue("$is_enabled", state.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$lifecycle_state", state.LifecycleState);
        command.Parameters.AddWithValue(
            "$diagnostics_json",
            JsonSerializer.Serialize(state.Diagnostics));
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            state.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
