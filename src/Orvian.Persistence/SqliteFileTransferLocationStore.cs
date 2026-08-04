using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orvian.Application.Files;
using Orvian.Core.Hosts;

namespace Orvian.Persistence;

public sealed class SqliteFileTransferLocationStore(OrvianDatabase database)
    : IFileTransferLocationStore
{
    private const string LocalPathKey = "file_transfer.local_path";
    private const string RemotePathPrefix = "file_transfer.remote_path.";
    private const int SchemaVersion = 1;
    private const int MaximumPathLength = 4096;

    public async Task<FileTransferLocations> LoadAsync(
        HostProfileId hostId,
        CancellationToken cancellationToken = default)
    {
        var remoteKey = RemoteKey(hostId);
        string? local = null;
        string? remote = null;
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT setting_key, schema_version, value_json FROM application_settings " +
            "WHERE setting_key IN ($local, $remote);";
        command.Parameters.AddWithValue("$local", LocalPathKey);
        command.Parameters.AddWithValue("$remote", remoteKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(1) > SchemaVersion)
            {
                continue;
            }

            try
            {
                var value = JsonSerializer.Deserialize<string>(reader.GetString(2));
                if (!IsSafePath(value))
                {
                    continue;
                }

                if (string.Equals(reader.GetString(0), LocalPathKey, StringComparison.Ordinal))
                {
                    local = value;
                }
                else
                {
                    remote = value;
                }
            }
            catch (JsonException)
            {
                // A corrupt noncritical location is ignored without rewriting it.
            }
        }

        return new(local, remote);
    }

    public Task SaveLocalPathAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        SaveAsync(LocalPathKey, path, cancellationToken);

    public Task SaveRemotePathAsync(
        HostProfileId hostId,
        string path,
        CancellationToken cancellationToken = default) =>
        SaveAsync(RemoteKey(hostId), path, cancellationToken);

    private async Task SaveAsync(
        string key,
        string path,
        CancellationToken cancellationToken)
    {
        if (!IsSafePath(path))
        {
            throw new ArgumentException("The file-transfer path is invalid.", nameof(path));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO application_settings " +
            "(setting_key, schema_version, value_json, updated_at_utc) " +
            "VALUES ($key, $version, $value, $updated) " +
            "ON CONFLICT(setting_key) DO UPDATE SET " +
            "schema_version = excluded.schema_version, " +
            "value_json = excluded.value_json, updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$version", SchemaVersion);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(path));
        command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string RemoteKey(HostProfileId hostId) =>
        RemotePathPrefix + hostId.ToString();

    private static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= MaximumPathLength &&
        path.IndexOfAny(['\0', '\r', '\n']) < 0;
}
