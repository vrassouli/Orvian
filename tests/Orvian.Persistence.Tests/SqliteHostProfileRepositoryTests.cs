using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Orvian.Auditing;
using Orvian.Application.Hosts;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Xunit;

namespace Orvian.Persistence.Tests;

public sealed class SqliteHostProfileRepositoryTests
{
    [Fact]
    public async Task Clean_database_migrates_and_round_trips_profile()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile(tags: ["Production", "Linux"]);

        await fixture.Repository.AddAsync(profile);
        var loaded = await fixture.Repository.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal(profile.DisplayName, loaded.DisplayName);
        Assert.Equal(profile.HostName, loaded.HostName);
        Assert.Equal(profile.CredentialSecretReference, loaded.CredentialSecretReference);
        Assert.Equal(profile.Tags.ToArray(), loaded.Tags.ToArray());
        Assert.Equal(
            profile.UserName,
            (await fixture.Repository.ResolveAsync(profile.Id.ToString()))!.RemoteUserName);
        Assert.Null(await fixture.Repository.ResolveAsync("not-a-host-id"));
    }

    [Fact]
    public async Task Initialization_is_idempotent()
    {
        using var fixture = await DatabaseFixture.CreateAsync();

        await fixture.Database.InitializeAsync();
        await fixture.Database.InitializeAsync();

        await using var connection = new SqliteConnection(
            $"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=1;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task VersionFourDatabaseMigratesThroughLatestSchemaWithoutRecreation()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"orvian-v4-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE schema_migrations (
                        version INTEGER NOT NULL PRIMARY KEY,
                        applied_at_utc TEXT NOT NULL);
                    INSERT INTO schema_migrations VALUES (4, '2026-01-01T00:00:00Z');
                    CREATE TABLE host_profiles (
                        id TEXT NOT NULL PRIMARY KEY,
                        display_name TEXT NOT NULL);
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
                        safe_failure_message TEXT NULL);
                    CREATE TABLE trusted_host_keys (
                        host_profile_id TEXT NOT NULL PRIMARY KEY,
                        host_name TEXT NOT NULL,
                        port INTEGER NOT NULL,
                        algorithm TEXT NOT NULL,
                        sha256_fingerprint TEXT NOT NULL,
                        trusted_at_utc TEXT NOT NULL);
                    INSERT INTO host_profiles VALUES ('existing', 'Existing host');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var database = new OrvianDatabase(databasePath);
            await database.InitializeAsync();

            await using var verification = new SqliteConnection($"Data Source={databasePath}");
            await verification.OpenAsync();
            await using var columns = verification.CreateCommand();
            columns.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('host_profiles') WHERE name='private_key_path';";
            Assert.Equal(1L, await columns.ExecuteScalarAsync());
            await using var row = verification.CreateCommand();
            row.CommandText =
                "SELECT display_name FROM host_profiles WHERE id='existing';";
            Assert.Equal("Existing host", await row.ExecuteScalarAsync());
            await using var auditColumns = verification.CreateCommand();
            auditColumns.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('command_audit_entries')
                WHERE name IN (
                    'output_logging_mode',
                    'retained_standard_output',
                    'retained_standard_error');
                """;
            Assert.Equal(3L, await auditColumns.ExecuteScalarAsync());
            await using var latestMigration = verification.CreateCommand();
            latestMigration.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(13L, await latestMigration.ExecuteScalarAsync());
            await using var searchTables = verification.CreateCommand();
            searchTables.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                      'command_audit_search',
                      'operation_audit_search',
                      'host_trust_audit_search');
                """;
            Assert.Equal(3L, await searchTables.ExecuteScalarAsync());
            await using var hostKeyAddressColumn = verification.CreateCommand();
            hostKeyAddressColumn.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('trusted_host_keys')
                WHERE name = 'resolved_address';
                """;
            Assert.Equal(1L, await hostKeyAddressColumn.ExecuteScalarAsync());
            await using var trustAuditTable = verification.CreateCommand();
            trustAuditTable.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'host_trust_audit_events';
                """;
            Assert.Equal(1L, await trustAuditTable.ExecuteScalarAsync());
            await using var settingsTable = verification.CreateCommand();
            settingsTable.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'application_settings';
                """;
            Assert.Equal(1L, await settingsTable.ExecuteScalarAsync());
            await using var auditIdentityColumns = verification.CreateCommand();
            auditIdentityColumns.CommandText =
                """
                SELECT
                    (SELECT COUNT(*)
                     FROM pragma_table_info('command_audit_entries')
                     WHERE name = 'remote_user_name') +
                    (SELECT COUNT(*)
                     FROM pragma_table_info('operation_audit_entries')
                     WHERE name = 'remote_user_name');
                """;
            Assert.Equal(2L, await auditIdentityColumns.ExecuteScalarAsync());
            await using var outputDefault = verification.CreateCommand();
            outputDefault.CommandText =
                """
                INSERT INTO command_audit_entries (
                    command_id, operation_id, started_at_utc, host_profile_id,
                    connection_id, plugin_id, plugin_version, executable,
                    redacted_arguments_json, privilege, invocation_source,
                    status, standard_output_bytes, standard_error_bytes,
                    output_truncated, failure_classification)
                VALUES (
                    'default-output-mode', 'legacy',
                    '2026-01-01T00:00:00Z', 'existing', 'connection-1',
                    'orvian.test', '0.1.0', 'probe', '[]', 0, 0, 0,
                    0, 0, 0, 0);
                SELECT output_logging_mode
                FROM command_audit_entries
                WHERE command_id = 'default-output-mode';
                """;
            Assert.Equal(2L, await outputDefault.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Search_is_paged_and_filters_text_tags_and_enabled_state()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Repository.AddAsync(CreateProfile(
            displayName: "Production API",
            tags: ["production", "linux"]));
        await fixture.Repository.AddAsync(CreateProfile(
            displayName: "Development API",
            tags: ["development", "linux"]));
        await fixture.Repository.AddAsync(CreateProfile(
            displayName: "Disabled Production",
            tags: ["production"],
            isEnabled: false));

        var page = await fixture.Repository.SearchAsync(new(
            SearchText: "API",
            Tags: ["production"],
            IncludeDisabled: false,
            Offset: 0,
            Limit: 10));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("Production API", Assert.Single(page.Items).DisplayName);
    }

    [Fact]
    public async Task Search_filters_latest_operating_system_capabilities_and_disabled_only()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var matching = CreateProfile(
            displayName: "Disabled Linux",
            tags: ["production"],
            isEnabled: false);
        var other = CreateProfile(
            displayName: "Enabled macOS",
            tags: ["production"]);
        await fixture.Repository.AddAsync(matching);
        await fixture.Repository.AddAsync(other);
        await fixture.DiscoverySnapshots.StoreAsync(
            CreateDiscoverySnapshot(matching.Id) with
            {
                OperatingSystem = OperatingSystemFamily.Linux,
                Capabilities =
                    ImmutableHashSet.Create("shell.posix", "init.systemd")
            });
        await fixture.DiscoverySnapshots.StoreAsync(
            CreateDiscoverySnapshot(other.Id) with
            {
                OperatingSystem = OperatingSystemFamily.MacOs,
                Capabilities = ImmutableHashSet.Create("shell.posix")
            });

        var page = await fixture.Repository.SearchAsync(new(
            SearchText: null,
            Tags: ["production"],
            IncludeDisabled: true,
            Offset: 0,
            Limit: 10,
            OperatingSystem: OperatingSystemFamily.Linux,
            Capabilities: ["shell.posix", "init.systemd"],
            IsEnabled: false));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(matching.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_rejects_unbounded_or_empty_structured_filters()
    {
        using var fixture = await DatabaseFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Repository.SearchAsync(new(
                SearchText: null,
                Tags: [],
                IncludeDisabled: true,
                Offset: 0,
                Limit: 10,
                Capabilities: [.. Enumerable.Repeat("capability.test", 65)])));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Repository.SearchAsync(new(
                SearchText: null,
                Tags: [" "],
                IncludeDisabled: true,
                Offset: 0,
                Limit: 10)));
    }

    [Fact]
    public async Task Update_uses_optimistic_concurrency_and_rolls_back_tags()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var original = CreateProfile(tags: ["old"]);
        await fixture.Repository.AddAsync(original);
        var updatedAt = original.UpdatedAt.AddSeconds(1);
        var updated = CreateProfile(
            id: original.Id,
            displayName: "Updated",
            tags: ["new"],
            createdAt: original.CreatedAt,
            updatedAt: updatedAt);

        await Assert.ThrowsAsync<HostProfileConcurrencyException>(
            () => fixture.Repository.UpdateAsync(
                updated,
                original.UpdatedAt.AddMinutes(-1)));

        var loaded = await fixture.Repository.GetAsync(original.Id);
        Assert.Equal(original.DisplayName, loaded!.DisplayName);
        Assert.Equal(["old"], loaded.Tags.ToArray());
    }

    [Fact]
    public async Task Deleting_profile_cascades_tags()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile(tags: ["production"]);
        await fixture.Repository.AddAsync(profile);

        await fixture.Repository.DeleteAsync(profile.Id);

        Assert.Null(await fixture.Repository.GetAsync(profile.Id));
        await using var connection = new SqliteConnection(
            $"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_tags;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Credential_reference_is_stored_but_secret_value_has_no_column()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile(credentialReference: "opaque-reference");
        await fixture.Repository.AddAsync(profile);

        await using var connection = new SqliteConnection(
            $"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(host_profiles);";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("credential_secret_reference", columns);
        Assert.DoesNotContain("password", columns);
        Assert.DoesNotContain("private_key", columns);
        Assert.DoesNotContain("passphrase", columns);
    }

    [Fact]
    public async Task PrivateKeyPathRoundTripsWithoutKeyMaterial()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = HostProfile.Create(
            HostProfileId.New(),
            "Key host",
            "key.example.test",
            22,
            "operator",
            HostAuthenticationMethod.PrivateKey,
            null,
            [],
            null,
            true,
            HostConnectionPreferences.Default,
            now,
            now,
            "/Users/operator/.ssh/id_ed25519").Profile!;

        await fixture.Repository.AddAsync(profile);
        var loaded = await fixture.Repository.GetAsync(profile.Id);

        Assert.Equal(profile.PrivateKeyPath, loaded!.PrivateKeyPath);
        Assert.Null(loaded.CredentialSecretReference);
    }

    [Fact]
    public async Task Trusted_host_key_round_trips_and_can_be_explicitly_replaced()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile();
        await fixture.Repository.AddAsync(profile);
        var first = new TrustedHostKey(
            profile.Id,
            profile.HostName,
            profile.Port,
            "ssh-ed25519",
            "SHA256:first",
            DateTimeOffset.UtcNow,
            "192.0.2.10");
        await fixture.TrustedKeys.StoreAsync(
            first,
            CreateTrustAuditEvent(first, HostTrustAction.TrustFirstSeen));

        var replacement = first with
        {
            Algorithm = "ecdsa-sha2-nistp256",
            Sha256Fingerprint = "SHA256:replacement",
            TrustedAt = first.TrustedAt.AddMinutes(1)
        };
        await fixture.TrustedKeys.StoreAsync(
            replacement,
            CreateTrustAuditEvent(
                replacement,
                HostTrustAction.ReplaceChanged,
                first.Sha256Fingerprint));

        Assert.Equal(replacement, await fixture.TrustedKeys.GetAsync(profile.Id));
    }

    [Fact]
    public async Task Host_trust_update_and_audit_event_are_atomic()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile();
        await fixture.Repository.AddAsync(profile);
        var first = new TrustedHostKey(
            profile.Id,
            profile.HostName,
            profile.Port,
            "ssh-ed25519",
            "SHA256:first",
            DateTimeOffset.UtcNow);
        var firstEvent = CreateTrustAuditEvent(first, HostTrustAction.TrustFirstSeen);
        await fixture.TrustedKeys.StoreAsync(first, firstEvent);
        var replacement = first with
        {
            Sha256Fingerprint = "SHA256:replacement",
            TrustedAt = first.TrustedAt.AddMinutes(1)
        };

        await Assert.ThrowsAsync<SqliteException>(
            () => fixture.TrustedKeys.StoreAsync(
                replacement,
                CreateTrustAuditEvent(
                    replacement,
                    HostTrustAction.ReplaceChanged,
                    first.Sha256Fingerprint) with
                {
                    EventId = firstEvent.EventId
                }));

        Assert.Equal(first, await fixture.TrustedKeys.GetAsync(profile.Id));
    }

    [Fact]
    public async Task Deleting_host_cascades_trusted_identity()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile();
        await fixture.Repository.AddAsync(profile);
        var trusted = new TrustedHostKey(
            profile.Id,
            profile.HostName,
            profile.Port,
            "ssh-ed25519",
            "SHA256:known",
            DateTimeOffset.UtcNow);
        await fixture.TrustedKeys.StoreAsync(
            trusted,
            CreateTrustAuditEvent(trusted, HostTrustAction.TrustFirstSeen));

        await fixture.Repository.DeleteAsync(profile.Id);

        Assert.Null(await fixture.TrustedKeys.GetAsync(profile.Id));
    }

    private static HostTrustAuditEvent CreateTrustAuditEvent(
        TrustedHostKey trusted,
        HostTrustAction action,
        string? previousFingerprint = null) =>
        new(
            Guid.NewGuid(),
            trusted.TrustedAt,
            trusted.HostProfileId.ToString(),
            action,
            trusted.HostName,
            trusted.Port,
            trusted.ResolvedAddress,
            trusted.Algorithm,
            trusted.Sha256Fingerprint,
            previousFingerprint);

    [Fact]
    public async Task Discovery_snapshot_round_trips_with_provenance_and_partial_state()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile();
        await fixture.Repository.AddAsync(profile);
        var snapshot = CreateDiscoverySnapshot(profile.Id, partial: true);

        await fixture.DiscoverySnapshots.StoreAsync(snapshot);
        var loaded = await fixture.DiscoverySnapshots.GetLatestAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Id, loaded.Id);
        Assert.Equal(snapshot.OperatingSystem, loaded.OperatingSystem);
        Assert.Equal("arm64", loaded.Facts["kernel.arch"].Value);
        Assert.Equal("core.uname.architecture", loaded.Facts["kernel.arch"].ProbeId);
        Assert.Contains("shell.posix", loaded.Capabilities);
        Assert.True(loaded.IsPartial);
    }

    [Fact]
    public async Task New_snapshot_atomically_replaces_latest_pointer_without_deleting_history()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var profile = CreateProfile();
        await fixture.Repository.AddAsync(profile);
        var first = CreateDiscoverySnapshot(profile.Id);
        var second = CreateDiscoverySnapshot(profile.Id) with
        {
            Id = DiscoverySnapshotId.New(),
            ConnectionId = Guid.NewGuid().ToString(),
            CompletedAt = first.CompletedAt.AddMinutes(1)
        };

        await fixture.DiscoverySnapshots.StoreAsync(first);
        await fixture.DiscoverySnapshots.StoreAsync(second);

        Assert.Equal(
            second.Id,
            (await fixture.DiscoverySnapshots.GetLatestAsync(profile.Id))!.Id);
        await using var connection = new SqliteConnection(
            $"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM discovery_snapshots WHERE host_profile_id=$host;";
        command.Parameters.AddWithValue("$host", profile.Id.ToString());
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    private static HostProfile CreateProfile(
        HostProfileId? id = null,
        string displayName = "Server",
        IEnumerable<string>? tags = null,
        bool isEnabled = true,
        string? credentialReference = "secret-ref",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        var created = createdAt ?? DateTimeOffset.UtcNow;
        var updated = updatedAt ?? created;
        var result = HostProfile.Create(
            id ?? HostProfileId.New(),
            displayName,
            $"{Guid.NewGuid():N}.example.test",
            22,
            "operator",
            HostAuthenticationMethod.Password,
            credentialReference,
            tags ?? [],
            null,
            isEnabled,
            HostConnectionPreferences.Default,
            created,
            updated);
        return Assert.IsType<HostProfile>(result.Profile);
    }

    private static DiscoverySnapshot CreateDiscoverySnapshot(
        HostProfileId hostProfileId,
        bool partial = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            DiscoverySnapshotId.New(),
            hostProfileId,
            Guid.NewGuid().ToString(),
            now,
            now.AddSeconds(1),
            OperatingSystemFamily.MacOs,
            ImmutableDictionary<string, DiscoveryFact>.Empty.Add(
                "kernel.arch",
                new("kernel.arch", "arm64", "core.uname.architecture", now)),
            ImmutableHashSet<string>.Empty.Add("shell.posix"),
            partial
                ? [new("core.systemd", DiscoveryProbeStatus.CommandFailed, "Not available.")]
                : [new("core.uname.architecture", DiscoveryProbeStatus.Succeeded)]);
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private DatabaseFixture(string directory, string databasePath)
        {
            DirectoryPath = directory;
            DatabasePath = databasePath;
            Database = new(databasePath);
            Repository = new(Database);
            TrustedKeys = new(Database);
            DiscoverySnapshots = new(Database);
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public OrvianDatabase Database { get; }

        public SqliteHostProfileRepository Repository { get; }

        public SqliteTrustedHostKeyRepository TrustedKeys { get; }

        public SqliteDiscoverySnapshotRepository DiscoverySnapshots { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"orvian-persistence-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var fixture = new DatabaseFixture(
                directory,
                Path.Combine(directory, "orvian.db"));
            await fixture.Database.InitializeAsync();
            return fixture;
        }

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
