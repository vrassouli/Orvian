using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Orvian.Auditing;
using Orvian.Core.Commands;
using Orvian.Persistence;
using Xunit;

namespace Orvian.Persistence.Tests;

public sealed class SqliteAuditStoreTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"orvian-audit-{Guid.NewGuid():N}.db");
    private OrvianDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new OrvianDatabase(_databasePath);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartAndCompletionSurviveStoreRecreation()
    {
        var commandId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var start = Start(commandId, operationId, startedAt);
        var writer = new SqliteAuditStore(_database);
        await writer.CommandStartedAsync(start);
        await writer.CommandCompletedAsync(start with
        {
            CompletedAt = startedAt.AddSeconds(1),
            Status = AuditStatus.Succeeded,
            ExitCode = 0,
            StandardOutputBytes = 12
        });

        var reader = new SqliteAuditStore(new OrvianDatabase(_databasePath));
        var activity = await reader.QueryAsync(new(IncludeDiagnostics: true));

        var item = Assert.Single(activity);
        Assert.Equal(commandId, item.CommandId);
        Assert.Equal("operator", item.RemoteUserName);
        Assert.Equal(AuditStatus.Succeeded, item.Status);
        Assert.Equal(
            new[] { "--token", "[REDACTED]" },
            item.RedactedArguments.AsEnumerable());
        Assert.Equal(0, item.ExitCode);
        Assert.Equal(
            "operator",
            (await reader.GetCommandAsync(commandId))!.RemoteUserName);
    }

    [Fact]
    public async Task HostTrustEventsAreDurablePagedAndFilterable()
    {
        var eventId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO host_trust_audit_events (
                    event_id, occurred_at_utc, host_profile_id, action,
                    host_name, port, resolved_address, algorithm,
                    observed_fingerprint, previous_fingerprint)
                VALUES (
                    $event_id, '2026-01-02T03:04:05.0000000+00:00', 'host-1', 1,
                    'host.example.test', 22, '192.0.2.10', 'ssh-ed25519',
                    'SHA256:new', 'SHA256:old');
                """;
            command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteAuditStore(new OrvianDatabase(_databasePath));
        var events = await store.QueryHostTrustEventsAsync(
            new(
                HostProfileId: "host-1",
                Action: HostTrustAction.ReplaceChanged,
                Limit: 1));

        var item = Assert.Single(events);
        Assert.Equal(eventId, item.EventId);
        Assert.Equal("SHA256:old", item.PreviousFingerprint);
        Assert.Equal("192.0.2.10", item.ResolvedAddress);
        Assert.Single(await store.QueryHostTrustEventsAsync(
            new(SearchText: "host.example ssh-ed25519")));
        Assert.Empty(await store.QueryHostTrustEventsAsync(
            new(SearchText: "\" OR *")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryHostTrustEventsAsync(new(Limit: 501)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryHostTrustEventsAsync(
                new(SearchText: new string('x', 201))));
    }

    [Fact]
    public async Task CompletionWithoutMatchingStartFailsClosed()
    {
        var store = new SqliteAuditStore(_database);
        var entry = Start(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            Status = AuditStatus.Failed
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommandCompletedAsync(entry));
    }

    [Fact]
    public async Task AuditStartRejectsMissingOrControlSeparatedRemoteUser()
    {
        var store = new SqliteAuditStore(_database);
        var entry = Start(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.CommandStartedAsync(entry with { RemoteUserName = " " }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.CommandStartedAsync(entry with
            {
                CommandId = Guid.NewGuid(),
                RemoteUserName = "operator\nforged"
            }));
    }

    [Fact]
    public async Task DiagnosticsAreHiddenByDefaultAndFilterable()
    {
        var store = new SqliteAuditStore(_database);
        var discovery = Start(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        await store.CommandStartedAsync(discovery);

        Assert.Empty(await store.QueryAsync(new()));
        Assert.Single(await store.QueryAsync(new(
            HostProfileId: "host-1",
            InvocationSource: InvocationSource.Discovery,
            IncludeDiagnostics: true)));
    }

    [Fact]
    public async Task ActivityQueriesArePagedAndBounded()
    {
        var store = new SqliteAuditStore(_database);
        await store.CommandStartedAsync(
            Start(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1)));
        await store.CommandStartedAsync(
            Start(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow));

        var page = await store.QueryAsync(new(
            IncludeDiagnostics: true,
            Offset: 1,
            Limit: 1));

        Assert.Single(page);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryAsync(new(IncludeDiagnostics: true, Limit: 501)));
    }

    [Fact]
    public async Task ActivityDateRangeIsAppliedInSqlAndValidated()
    {
        var store = new SqliteAuditStore(_database);
        var boundary = DateTimeOffset.Parse("2026-07-29T00:00:00Z");
        await store.CommandStartedAsync(
            Start(Guid.NewGuid(), Guid.NewGuid(), boundary.AddMinutes(-1)));
        var expected = Start(
            Guid.NewGuid(),
            Guid.NewGuid(),
            boundary.AddMinutes(1));
        await store.CommandStartedAsync(expected);

        var results = await store.QueryAsync(new(
            StartedAtOrAfter: boundary,
            StartedBefore: boundary.AddDays(1),
            IncludeDiagnostics: true));

        Assert.Equal(expected.CommandId, Assert.Single(results).CommandId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryAsync(new(
                StartedAtOrAfter: boundary,
                StartedBefore: boundary)));
    }

    [Fact]
    public async Task CommandQueriesFilterPrivilegeAndOwningOperationRisk()
    {
        var store = new SqliteAuditStore(_database);
        var timestamp = DateTimeOffset.UtcNow;
        var operation = OperationStart(Guid.NewGuid(), timestamp);
        var command = Start(
            Guid.NewGuid(),
            operation.OperationId,
            timestamp) with
        {
            Privilege = PrivilegeLevel.Elevated
        };
        await store.OperationStartedAsync(operation);
        await store.CommandStartedAsync(command);

        var results = await store.QueryAsync(new(
            Privilege: PrivilegeLevel.Elevated,
            Risk: OperationRisk.Elevated,
            IncludeDiagnostics: true));

        Assert.Equal(command.CommandId, Assert.Single(results).CommandId);
        Assert.Empty(await store.QueryAsync(new(
            Risk: OperationRisk.Low,
            IncludeDiagnostics: true)));
        Assert.Empty(await store.QueryAsync(new(
            IncludeDiagnostics: true,
            ExcludeOperationCommands: true)));
    }

    [Fact]
    public async Task OperationStartAndCompletionAreDurableAndFilterable()
    {
        var operationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var start = OperationStart(operationId, startedAt);
        var store = new SqliteAuditStore(_database);

        await store.OperationStartedAsync(start);
        await store.OperationCompletedAsync(start with
        {
            CompletedAt = startedAt.AddSeconds(2),
            Status = OperationAuditStatus.Succeeded,
            CommandCount = 1
        });

        var recreated = new SqliteAuditStore(new OrvianDatabase(_databasePath));
        var entries = await recreated.QueryOperationsAsync(
            new(PluginId: "orvian.datetime"));

        var entry = Assert.Single(entries);
        Assert.Equal(operationId, entry.OperationId);
        Assert.Equal(OperationAuditStatus.Succeeded, entry.Status);
        Assert.Equal("operator", entry.RemoteUserName);
        Assert.Equal("Change timezone", entry.Title);
        Assert.Equal(1, entry.CommandCount);
    }

    [Fact]
    public async Task OperationQueriesFilterSourceAndDateRange()
    {
        var store = new SqliteAuditStore(_database);
        var boundary = DateTimeOffset.Parse("2026-07-29T00:00:00Z");
        await store.OperationStartedAsync(
            OperationStart(Guid.NewGuid(), boundary.AddMinutes(-1)));
        var expected = OperationStart(
            Guid.NewGuid(),
            boundary.AddMinutes(1));
        await store.OperationStartedAsync(expected);

        var results = await store.QueryOperationsAsync(new(
            InvocationSource: InvocationSource.UserInterface,
            Status: OperationAuditStatus.Started,
            Privilege: PrivilegeLevel.Elevated,
            Risk: OperationRisk.Elevated,
            StartedAtOrAfter: boundary,
            StartedBefore: boundary.AddDays(1)));

        Assert.Equal(expected.OperationId, Assert.Single(results).OperationId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryOperationsAsync(new(
                StartedAtOrAfter: boundary.AddDays(1),
                StartedBefore: boundary)));
    }

    [Fact]
    public async Task AuditSearchUsesSafeMetadataLiteralTermsAndBoundedInput()
    {
        var store = new SqliteAuditStore(_database);
        var operation = OperationStart(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var command = Start(
            Guid.NewGuid(),
            operation.OperationId,
            operation.StartedAt) with
        {
            InvocationSource = InvocationSource.UserInterface
        };
        await store.OperationStartedAsync(operation);
        await store.CommandStartedAsync(command);

        Assert.Equal(
            command.CommandId,
            Assert.Single(await store.QueryAsync(new(
                SearchText: "operator probe"))).CommandId);
        Assert.Equal(
            operation.OperationId,
            Assert.Single(await store.QueryOperationsAsync(new(
                SearchText: "change timezone"))).OperationId);
        Assert.Empty(await store.QueryAsync(new(
            IncludeDiagnostics: true,
            SearchText: "actual-secret")));
        Assert.Empty(await store.QueryAsync(new(
            IncludeDiagnostics: true,
            SearchText: "\" OR *")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryAsync(new(SearchText: new string('x', 201))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryOperationsAsync(
                new(SearchText: new string('x', 201))));
    }

    [Fact]
    public async Task OperationCompletionWithoutStartFailsClosed()
    {
        var entry = OperationStart(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            Status = OperationAuditStatus.Failed
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqliteAuditStore(_database).OperationCompletedAsync(entry));
    }

    [Fact]
    public async Task AuditCompletionCannotChangeAuthenticatedUserIdentity()
    {
        var store = new SqliteAuditStore(_database);
        var command = Start(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        await store.CommandStartedAsync(command);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommandCompletedAsync(command with
            {
                RemoteUserName = "different-user",
                CompletedAt = DateTimeOffset.UtcNow,
                Status = AuditStatus.Succeeded
            }));
    }

    [Fact]
    public async Task RetentionDeletesOnlyCompletedOldRecordsAndAuditsCleanup()
    {
        var store = new SqliteAuditStore(_database);
        var now = DateTimeOffset.UtcNow;
        var oldOperation = Guid.NewGuid();
        var pendingOperation = Guid.NewGuid();
        var recentOperation = Guid.NewGuid();
        await CompleteOperationWithCommandAsync(
            store,
            oldOperation,
            now.AddDays(-100));
        var pendingStart = OperationStart(pendingOperation, now.AddDays(-100));
        await store.OperationStartedAsync(pendingStart);
        await store.CommandStartedAsync(
            Start(Guid.NewGuid(), pendingOperation, now.AddDays(-100)));
        await CompleteOperationWithCommandAsync(store, recentOperation, now);

        var result = await new SqliteAuditRetentionService(_database)
            .CleanupBatchAsync(new(now.AddDays(-90), BatchSize: 10));

        Assert.Equal(1, result.DeletedOperations);
        Assert.Equal(1, result.DeletedCommands);
        var operations = await store.QueryOperationsAsync(new(Limit: 10));
        Assert.DoesNotContain(
            operations,
            entry => entry.OperationId == oldOperation);
        Assert.Contains(
            operations,
            entry =>
                entry.OperationId == pendingOperation &&
                entry.Status == OperationAuditStatus.Started);
        Assert.Contains(
            operations,
            entry => entry.OperationId == recentOperation);
        Assert.Contains(
            operations,
            entry =>
                entry.OperationId == result.CleanupOperationId &&
                entry.PluginId == "orvian.core.retention" &&
                entry.RemoteUserName == "[local]");
        var commands = await store.QueryAsync(
            new(IncludeDiagnostics: true, Limit: 10));
        Assert.Contains(commands, entry => entry.OperationId == pendingOperation);
        Assert.Contains(commands, entry => entry.OperationId == recentOperation);
        await using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var staleSearchRows = connection.CreateCommand();
        staleSearchRows.CommandText =
            """
            SELECT COUNT(*)
            FROM operation_audit_search
            WHERE operation_id = $operation_id;
            """;
        staleSearchRows.Parameters.AddWithValue(
            "$operation_id",
            oldOperation.ToString("D"));
        Assert.Equal(0L, await staleSearchRows.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RetainedOutputRoundTripsAndCanExpireBeforeMetadata()
    {
        var store = new SqliteAuditStore(_database);
        var timestamp = DateTimeOffset.UtcNow.AddDays(-10);
        var commandId = Guid.NewGuid();
        var start = Start(commandId, Guid.NewGuid(), timestamp) with
        {
            OutputLogging = OutputLoggingMode.Redacted
        };
        await store.CommandStartedAsync(start);
        await store.CommandCompletedAsync(start with
        {
            CompletedAt = timestamp.AddSeconds(1),
            Status = AuditStatus.Succeeded,
            ExitCode = 0,
            RetainedStandardOutput = "[REDACTED]",
            RetainedStandardError = string.Empty
        });

        var retained = await store.GetCommandAsync(commandId);
        Assert.Equal("[REDACTED]", retained!.RetainedStandardOutput);
        var cleanup = await new SqliteAuditRetentionService(_database)
            .CleanupBatchAsync(
                new(
                    DateTimeOffset.UtcNow.AddDays(-90),
                    DeleteOutputBefore: DateTimeOffset.UtcNow.AddDays(-5),
                    BatchSize: 10));

        Assert.Equal(1, cleanup.PurgedCommandOutputs);
        var afterCleanup = await store.GetCommandAsync(commandId);
        Assert.NotNull(afterCleanup);
        Assert.Null(afterCleanup.RetainedStandardOutput);
        Assert.Null(afterCleanup.RetainedStandardError);
    }

    private static CommandAuditEntry Start(
        Guid commandId,
        Guid operationId,
        DateTimeOffset startedAt) =>
        new()
        {
            CommandId = commandId,
            OperationId = operationId,
            StartedAt = startedAt,
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            RemoteUserName = "operator",
            PluginId = "orvian.discovery",
            PluginVersion = "0.1",
            Executable = "probe",
            RedactedArguments = ImmutableArray.Create("--token", "[REDACTED]"),
            InvocationSource = InvocationSource.Discovery,
            Status = AuditStatus.Started
        };

    private static OperationAuditEntry OperationStart(
        Guid operationId,
        DateTimeOffset startedAt) =>
        new()
        {
            OperationId = operationId,
            StartedAt = startedAt,
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            RemoteUserName = "operator",
            PluginId = "orvian.datetime",
            PluginVersion = "0.1.0",
            Title = "Change timezone",
            Purpose = "Set the remote timezone.",
            RequiredPermission = "command.mutate.execute",
            Risk = OperationRisk.Elevated,
            Privilege = PrivilegeLevel.Elevated,
            InvocationSource = InvocationSource.UserInterface,
            ResourceLockKey = "date-time:timezone.change:host-1",
            Status = OperationAuditStatus.Started
        };

    private static async Task CompleteOperationWithCommandAsync(
        SqliteAuditStore store,
        Guid operationId,
        DateTimeOffset startedAt)
    {
        var operation = OperationStart(operationId, startedAt);
        var command = Start(Guid.NewGuid(), operationId, startedAt);
        await store.OperationStartedAsync(operation);
        await store.CommandStartedAsync(command);
        await store.CommandCompletedAsync(command with
        {
            CompletedAt = startedAt.AddSeconds(1),
            Status = AuditStatus.Succeeded,
            ExitCode = 0
        });
        await store.OperationCompletedAsync(operation with
        {
            CompletedAt = startedAt.AddSeconds(1),
            Status = OperationAuditStatus.Succeeded,
            CommandCount = 1
        });
    }
}
