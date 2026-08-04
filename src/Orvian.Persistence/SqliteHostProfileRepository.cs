using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Orvian.Auditing;
using Orvian.Application.Hosts;
using Orvian.Core.Hosts;

namespace Orvian.Persistence;

public sealed class SqliteHostProfileRepository(OrvianDatabase database)
    : IHostProfileRepository, IAuditExecutionIdentityResolver
{
    public async Task<AuditExecutionIdentity?> ResolveAsync(
        string hostProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(hostProfileId, out var id) || id == Guid.Empty)
        {
            return null;
        }

        var profile = await GetAsync(new(id), cancellationToken).ConfigureAwait(false);
        return profile is null ? null : new(profile.UserName);
    }

    public async Task<HostProfile?> GetAsync(
        HostProfileId id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE h.id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadSingleAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostProfilePage> SearchAsync(
        HostProfileSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "Offset must be nonnegative and limit must be between 1 and 500.");
        }

        query = query with
        {
            Tags = query.Tags.IsDefault ? [] : query.Tags,
            Capabilities = query.Capabilities.IsDefault ? [] : query.Capabilities
        };
        if (query.Tags.Length > 64 || query.Capabilities.Length > 64 ||
            query.Tags.Any(string.IsNullOrWhiteSpace) ||
            query.Capabilities.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Host filters contain too many values or an empty value.",
                nameof(query));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var where = BuildWhere(query);

        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM host_profiles h {where.Sql};";
        AddSearchParameters(count, query, where.SearchPattern);
        var total = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText =
            SelectColumns + $" {where.Sql} ORDER BY h.display_name COLLATE NOCASE, h.id LIMIT $limit OFFSET $offset;";
        AddSearchParameters(command, query, where.SearchPattern);
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var profiles = ImmutableArray.CreateBuilder<HostProfile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return new(profiles.ToImmutable(), total, query.Offset, query.Limit);
    }

    public async Task AddAsync(
        HostProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await InsertOrUpdateAsync(
            connection,
            (SqliteTransaction)transaction,
            profile,
            isUpdate: false,
            expectedUpdatedAt: null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        HostProfile profile,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await InsertOrUpdateAsync(
            connection,
            (SqliteTransaction)transaction,
            profile,
            isUpdate: true,
            expectedUpdatedAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        HostProfileId id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM host_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOrUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostProfile profile,
        bool isUpdate,
        DateTimeOffset? expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = isUpdate
            ? """
              UPDATE host_profiles SET
                display_name=$display_name, host_name=$host_name, port=$port,
                user_name=$user_name, authentication_method=$authentication_method,
                credential_secret_reference=$credential_secret_reference,
                private_key_path=$private_key_path, notes=$notes,
                is_enabled=$is_enabled, connection_timeout_ms=$connection_timeout_ms,
                maximum_reconnect_attempts=$maximum_reconnect_attempts,
                updated_at_utc=$updated_at_utc
              WHERE id=$id AND updated_at_utc=$expected_updated_at_utc;
              """
            : """
              INSERT INTO host_profiles (
                id, display_name, host_name, port, user_name, authentication_method,
                credential_secret_reference, private_key_path, notes, is_enabled, connection_timeout_ms,
                maximum_reconnect_attempts, created_at_utc, updated_at_utc)
              VALUES (
                $id, $display_name, $host_name, $port, $user_name, $authentication_method,
                $credential_secret_reference, $private_key_path, $notes, $is_enabled, $connection_timeout_ms,
                $maximum_reconnect_attempts, $created_at_utc, $updated_at_utc);
              """;
        AddProfileParameters(command, profile);
        if (expectedUpdatedAt is not null)
        {
            command.Parameters.AddWithValue(
                "$expected_updated_at_utc",
                FormatTimestamp(expectedUpdatedAt.Value));
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (isUpdate && affected != 1)
        {
            throw new HostProfileConcurrencyException(profile.Id);
        }

        if (isUpdate)
        {
            await using var deleteTags = connection.CreateCommand();
            deleteTags.Transaction = transaction;
            deleteTags.CommandText =
                "DELETE FROM host_tags WHERE host_profile_id = $host_profile_id;";
            deleteTags.Parameters.AddWithValue("$host_profile_id", profile.Id.ToString());
            await deleteTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var tag in profile.Tags)
        {
            await using var insertTag = connection.CreateCommand();
            insertTag.Transaction = transaction;
            insertTag.CommandText =
                "INSERT INTO host_tags(host_profile_id, tag) VALUES ($host_profile_id, $tag);";
            insertTag.Parameters.AddWithValue("$host_profile_id", profile.Id.ToString());
            insertTag.Parameters.AddWithValue("$tag", tag);
            await insertTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddProfileParameters(SqliteCommand command, HostProfile profile)
    {
        command.Parameters.AddWithValue("$id", profile.Id.ToString());
        command.Parameters.AddWithValue("$display_name", profile.DisplayName);
        command.Parameters.AddWithValue("$host_name", profile.HostName);
        command.Parameters.AddWithValue("$port", profile.Port);
        command.Parameters.AddWithValue("$user_name", profile.UserName);
        command.Parameters.AddWithValue(
            "$authentication_method",
            (int)profile.AuthenticationMethod);
        command.Parameters.AddWithValue(
            "$credential_secret_reference",
            (object?)profile.CredentialSecretReference ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$private_key_path",
            (object?)profile.PrivateKeyPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)profile.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_enabled", profile.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$connection_timeout_ms",
            checked((long)profile.ConnectionPreferences.ConnectionTimeout.TotalMilliseconds));
        command.Parameters.AddWithValue(
            "$maximum_reconnect_attempts",
            profile.ConnectionPreferences.MaximumReconnectAttempts);
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(profile.CreatedAt));
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(profile.UpdatedAt));
    }

    private static (string Sql, string? SearchPattern) BuildWhere(
        HostProfileSearchQuery query)
    {
        var conditions = new List<string>();
        string? searchPattern = null;
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            searchPattern = $"%{EscapeLike(query.SearchText.Trim())}%";
            conditions.Add(
                "(h.display_name LIKE $search ESCAPE '\\' OR h.host_name LIKE $search ESCAPE '\\' OR h.user_name LIKE $search ESCAPE '\\')");
        }

        if (query.IsEnabled is not null)
        {
            conditions.Add("h.is_enabled = $is_enabled");
        }
        else if (!query.IncludeDisabled)
        {
            conditions.Add("h.is_enabled = 1");
        }

        for (var index = 0; index < query.Tags.Length; index++)
        {
            conditions.Add(
                $"EXISTS (SELECT 1 FROM host_tags ht{index} WHERE ht{index}.host_profile_id=h.id AND ht{index}.tag=$tag{index})");
        }

        if (query.OperatingSystem is not null)
        {
            conditions.Add(
                """
                EXISTS (
                    SELECT 1
                    FROM latest_discovery_snapshots lds_os
                    JOIN discovery_snapshots ds_os ON ds_os.id = lds_os.snapshot_id
                    WHERE lds_os.host_profile_id = h.id
                      AND ds_os.operating_system = $operating_system)
                """);
        }

        for (var index = 0; index < query.Capabilities.Length; index++)
        {
            conditions.Add(
                $"""
                 EXISTS (
                     SELECT 1
                     FROM latest_discovery_snapshots lds_cap{index}
                     JOIN discovery_capabilities dc{index}
                       ON dc{index}.snapshot_id = lds_cap{index}.snapshot_id
                     WHERE lds_cap{index}.host_profile_id = h.id
                       AND dc{index}.capability_id = $capability{index})
                 """);
        }

        return (conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions), searchPattern);
    }

    private static void AddSearchParameters(
        SqliteCommand command,
        HostProfileSearchQuery query,
        string? searchPattern)
    {
        if (searchPattern is not null)
        {
            command.Parameters.AddWithValue("$search", searchPattern);
        }

        if (query.IsEnabled is not null)
        {
            command.Parameters.AddWithValue(
                "$is_enabled",
                query.IsEnabled.Value ? 1 : 0);
        }

        for (var index = 0; index < query.Tags.Length; index++)
        {
            command.Parameters.AddWithValue($"$tag{index}", query.Tags[index]);
        }

        if (query.OperatingSystem is not null)
        {
            command.Parameters.AddWithValue(
                "$operating_system",
                (int)query.OperatingSystem.Value);
        }

        for (var index = 0; index < query.Capabilities.Length; index++)
        {
            command.Parameters.AddWithValue(
                $"$capability{index}",
                query.Capabilities[index]);
        }
    }

    private static async Task<HostProfile?> ReadSingleAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken) =>
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;

    private static HostProfile ReadProfile(SqliteDataReader reader)
    {
        var result = HostProfile.Create(
            new HostProfileId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            (HostAuthenticationMethod)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(14)
                ? []
                : reader.GetString(14).Split('\u001f', StringSplitOptions.RemoveEmptyEntries),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetBoolean(8),
            new(TimeSpan.FromMilliseconds(reader.GetInt64(9)), reader.GetInt32(10)),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            reader.IsDBNull(13) ? null : reader.GetString(13));
        return result.Profile ??
            throw new InvalidDataException(
                "Persisted host profile failed domain validation: " +
                string.Join(", ", result.Errors.Select(error => error.Code)));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private const string SelectColumns =
        """
        SELECT h.id, h.display_name, h.host_name, h.port, h.user_name,
               h.authentication_method, h.credential_secret_reference, h.notes,
               h.is_enabled, h.connection_timeout_ms, h.maximum_reconnect_attempts,
               h.created_at_utc, h.updated_at_utc, h.private_key_path,
               (SELECT group_concat(tag, char(31)) FROM host_tags WHERE host_profile_id=h.id)
        FROM host_profiles h
        """;
}

public sealed class HostProfileConcurrencyException(HostProfileId id)
    : InvalidOperationException($"Host profile '{id}' was modified by another operation.");
