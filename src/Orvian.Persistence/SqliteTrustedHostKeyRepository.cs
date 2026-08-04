using System.Net;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Orvian.Auditing;
using Orvian.Application.Hosts;
using Orvian.Core.Hosts;

namespace Orvian.Persistence;

public sealed class SqliteTrustedHostKeyRepository(OrvianDatabase database)
    : ITrustedHostKeyRepository
{
    public async Task<TrustedHostKey?> GetAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT host_profile_id, host_name, port, algorithm,
                   sha256_fingerprint, trusted_at_utc, resolved_address
            FROM trusted_host_keys
            WHERE host_profile_id = $host_profile_id;
            """;
        command.Parameters.AddWithValue("$host_profile_id", hostProfileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            new HostProfileId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task StoreAsync(
        TrustedHostKey trustedHostKey,
        HostTrustAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedHostKey);
        ArgumentNullException.ThrowIfNull(auditEvent);
        Validate(trustedHostKey);
        Validate(auditEvent, trustedHostKey);

        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO trusted_host_keys (
                host_profile_id, host_name, port, algorithm,
                sha256_fingerprint, trusted_at_utc, resolved_address)
            VALUES (
                $host_profile_id, $host_name, $port, $algorithm,
                $sha256_fingerprint, $trusted_at_utc, $resolved_address)
            ON CONFLICT(host_profile_id) DO UPDATE SET
                host_name = excluded.host_name,
                port = excluded.port,
                algorithm = excluded.algorithm,
                sha256_fingerprint = excluded.sha256_fingerprint,
                trusted_at_utc = excluded.trusted_at_utc,
                resolved_address = excluded.resolved_address;
            """;
        command.Parameters.AddWithValue(
            "$host_profile_id",
            trustedHostKey.HostProfileId.ToString());
        command.Parameters.AddWithValue("$host_name", trustedHostKey.HostName);
        command.Parameters.AddWithValue("$port", trustedHostKey.Port);
        command.Parameters.AddWithValue("$algorithm", trustedHostKey.Algorithm);
        command.Parameters.AddWithValue(
            "$sha256_fingerprint",
            trustedHostKey.Sha256Fingerprint);
        command.Parameters.AddWithValue(
            "$trusted_at_utc",
            trustedHostKey.TrustedAt.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$resolved_address",
            (object?)trustedHostKey.ResolvedAddress ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var auditCommand = connection.CreateCommand();
        auditCommand.Transaction = (SqliteTransaction)transaction;
        auditCommand.CommandText =
            """
            INSERT INTO host_trust_audit_events (
                event_id, occurred_at_utc, host_profile_id, action,
                host_name, port, resolved_address, algorithm,
                observed_fingerprint, previous_fingerprint)
            VALUES (
                $event_id, $occurred_at_utc, $host_profile_id, $action,
                $host_name, $port, $resolved_address, $algorithm,
                $observed_fingerprint, $previous_fingerprint);
            """;
        auditCommand.Parameters.AddWithValue("$event_id", auditEvent.EventId.ToString("D"));
        auditCommand.Parameters.AddWithValue(
            "$occurred_at_utc",
            auditEvent.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        auditCommand.Parameters.AddWithValue("$host_profile_id", auditEvent.HostProfileId);
        auditCommand.Parameters.AddWithValue("$action", (int)auditEvent.Action);
        auditCommand.Parameters.AddWithValue("$host_name", auditEvent.HostName);
        auditCommand.Parameters.AddWithValue("$port", auditEvent.Port);
        auditCommand.Parameters.AddWithValue(
            "$resolved_address",
            (object?)auditEvent.ResolvedAddress ?? DBNull.Value);
        auditCommand.Parameters.AddWithValue("$algorithm", auditEvent.Algorithm);
        auditCommand.Parameters.AddWithValue(
            "$observed_fingerprint",
            auditEvent.ObservedFingerprint);
        auditCommand.Parameters.AddWithValue(
            "$previous_fingerprint",
            (object?)auditEvent.PreviousFingerprint ?? DBNull.Value);
        await auditCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM trusted_host_keys WHERE host_profile_id = $host_profile_id;";
        command.Parameters.AddWithValue("$host_profile_id", hostProfileId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(TrustedHostKey trustedHostKey)
    {
        if (trustedHostKey.HostProfileId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(trustedHostKey.HostName) ||
            trustedHostKey.Port is < 1 or > 65535 ||
            (trustedHostKey.ResolvedAddress is not null &&
             !IPAddress.TryParse(trustedHostKey.ResolvedAddress, out _)) ||
            string.IsNullOrWhiteSpace(trustedHostKey.Algorithm) ||
            !trustedHostKey.Sha256Fingerprint.StartsWith(
                "SHA256:",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Trusted host-key record is invalid.",
                nameof(trustedHostKey));
        }
    }

    private static void Validate(
        HostTrustAuditEvent auditEvent,
        TrustedHostKey trustedHostKey)
    {
        var previousFingerprintIsValid =
            auditEvent.Action == HostTrustAction.TrustFirstSeen
                ? auditEvent.PreviousFingerprint is null
                : auditEvent.PreviousFingerprint?.StartsWith(
                    "SHA256:",
                    StringComparison.Ordinal) == true;
        if (auditEvent.EventId == Guid.Empty ||
            auditEvent.OccurredAt == default ||
            !string.Equals(
                auditEvent.HostProfileId,
                trustedHostKey.HostProfileId.ToString(),
                StringComparison.Ordinal) ||
            !string.Equals(auditEvent.HostName, trustedHostKey.HostName, StringComparison.Ordinal) ||
            auditEvent.Port != trustedHostKey.Port ||
            !string.Equals(
                auditEvent.ResolvedAddress,
                trustedHostKey.ResolvedAddress,
                StringComparison.Ordinal) ||
            !string.Equals(auditEvent.Algorithm, trustedHostKey.Algorithm, StringComparison.Ordinal) ||
            !string.Equals(
                auditEvent.ObservedFingerprint,
                trustedHostKey.Sha256Fingerprint,
                StringComparison.Ordinal) ||
            !previousFingerprintIsValid)
        {
            throw new ArgumentException(
                "Host-trust audit event does not match the trusted key.",
                nameof(auditEvent));
        }
    }
}
