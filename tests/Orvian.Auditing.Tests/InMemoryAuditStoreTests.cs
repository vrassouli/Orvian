using Orvian.Auditing;
using Orvian.Core.Commands;
using Xunit;

namespace Orvian.Auditing.Tests;

public sealed class InMemoryAuditStoreTests
{
    [Fact]
    public async Task Start_and_completion_correlate_into_one_activity_item()
    {
        var store = new InMemoryAuditStore();
        var started = CreateStarted();
        await store.CommandStartedAsync(started);
        await store.CommandCompletedAsync(started with
        {
            Status = AuditStatus.Succeeded,
            CompletedAt = started.StartedAt.AddSeconds(1),
            ExitCode = 0
        });

        var items = await store.QueryAsync(new ActivityQuery());

        var item = Assert.Single(items);
        Assert.Equal(started.CommandId, item.CommandId);
        Assert.Equal(AuditStatus.Succeeded, item.Status);
        Assert.Equal(0, item.ExitCode);
    }

    [Fact]
    public async Task Discovery_is_hidden_by_default_and_available_with_diagnostics()
    {
        var store = new InMemoryAuditStore();
        var started = CreateStarted() with
        {
            InvocationSource = InvocationSource.Discovery
        };
        await store.CommandStartedAsync(started);

        var defaultItems = await store.QueryAsync(new ActivityQuery());
        var diagnosticItems = await store.QueryAsync(
            new ActivityQuery(IncludeDiagnostics: true));

        Assert.Empty(defaultItems);
        Assert.Single(diagnosticItems);
    }

    [Fact]
    public async Task Query_filters_by_host_plugin_source_and_status()
    {
        var store = new InMemoryAuditStore();
        var matching = CreateStarted();
        var other = CreateStarted() with
        {
            CommandId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            HostProfileId = "host-2",
            PluginId = "orvian.other"
        };
        await store.CommandStartedAsync(matching);
        await store.CommandStartedAsync(other);

        var items = await store.QueryAsync(new ActivityQuery(
            HostProfileId: matching.HostProfileId,
            PluginId: matching.PluginId,
            InvocationSource: matching.InvocationSource,
            Status: AuditStatus.Started));

        Assert.Single(items);
        Assert.Equal(matching.CommandId, items[0].CommandId);
    }

    [Fact]
    public async Task Search_matches_only_safe_command_metadata_and_is_bounded()
    {
        var store = new InMemoryAuditStore();
        var started = CreateStarted() with
        {
            RedactedArguments = ["--token", "[REDACTED]"]
        };
        await store.CommandStartedAsync(started);

        Assert.Single(await store.QueryAsync(new(
            SearchText: "operator uname")));
        Assert.Empty(await store.QueryAsync(new(
            SearchText: "actual-secret")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.QueryAsync(new(SearchText: new string('x', 201))));
    }

    [Fact]
    public async Task Completion_without_start_is_rejected()
    {
        var store = new InMemoryAuditStore();
        var completion = CreateStarted() with
        {
            Status = AuditStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommandCompletedAsync(completion));
    }

    [Fact]
    public async Task Completion_cannot_change_authenticated_user_identity()
    {
        var store = new InMemoryAuditStore();
        var started = CreateStarted();
        await store.CommandStartedAsync(started);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommandCompletedAsync(started with
            {
                RemoteUserName = "different-user",
                Status = AuditStatus.Succeeded,
                CompletedAt = started.StartedAt.AddSeconds(1)
            }));
    }

    private static CommandAuditEntry CreateStarted() =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            RemoteUserName = "operator",
            PluginId = "orvian.test",
            PluginVersion = "0.1.0",
            Executable = "uname",
            RedactedArguments = ["-a"],
            InvocationSource = InvocationSource.UserInterface,
            Status = AuditStatus.Started
        };
}
