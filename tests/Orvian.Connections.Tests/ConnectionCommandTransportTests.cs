using Orvian.Connections;
using Orvian.Core.Commands;
using Xunit;

namespace Orvian.Connections.Tests;

public sealed class ConnectionCommandTransportTests
{
    [Fact]
    public async Task Registered_connection_executes_command()
    {
        await using var transport = new ConnectionCommandTransport();
        var connection = new FakeConnection();
        transport.Register(connection);

        var result = await transport.ExecuteAsync(
            Guid.NewGuid(),
            Request(connection.ConnectionId),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, connection.ExecutionCount);
    }

    [Fact]
    public async Task Unknown_connection_is_rejected()
    {
        await using var transport = new ConnectionCommandTransport();

        await Assert.ThrowsAsync<RemoteCommandInterruptionException>(
            () => transport.ExecuteAsync(
                Guid.NewGuid(),
                Request(ConnectionId.New()),
                CancellationToken.None));
    }

    [Fact]
    public async Task Commands_on_same_connection_are_serialized()
    {
        await using var transport = new ConnectionCommandTransport();
        var connection = new FakeConnection(blockFirst: true);
        transport.Register(connection);
        var first = transport.ExecuteAsync(
            Guid.NewGuid(),
            Request(connection.ConnectionId),
            CancellationToken.None);
        await connection.FirstStarted.Task;

        var second = transport.ExecuteAsync(
            Guid.NewGuid(),
            Request(connection.ConnectionId),
            CancellationToken.None);
        Assert.Equal(1, connection.MaximumConcurrency);

        connection.ReleaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, connection.MaximumConcurrency);
        Assert.Equal(2, connection.ExecutionCount);
    }

    [Fact]
    public async Task Remove_disconnects_disposes_and_rejects_later_commands()
    {
        await using var transport = new ConnectionCommandTransport();
        var connection = new FakeConnection();
        transport.Register(connection);

        await transport.RemoveAsync(connection.ConnectionId);

        Assert.True(connection.WasDisconnected);
        Assert.True(connection.WasDisposed);
        await Assert.ThrowsAsync<RemoteCommandInterruptionException>(
            () => transport.ExecuteAsync(
                Guid.NewGuid(),
                Request(connection.ConnectionId),
                CancellationToken.None));
    }

    private static CommandRequest Request(ConnectionId connectionId) =>
        new()
        {
            OperationId = Guid.NewGuid(),
            HostProfileId = "host-1",
            ConnectionId = connectionId.ToString(),
            PluginId = "orvian.test",
            PluginVersion = new Version(0, 1),
            RequiredPermission = "command.read.execute",
            Executable = "uname"
        };

    private sealed class FakeConnection(bool blockFirst = false) : IRemoteConnection
    {
        private int _concurrency;

        public ConnectionId ConnectionId { get; } = ConnectionId.New();

        public bool IsConnected { get; private set; } = true;

        public int ExecutionCount { get; private set; }

        public int MaximumConcurrency { get; private set; }

        public bool WasDisconnected { get; private set; }

        public bool WasDisposed { get; private set; }

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TransportCommandResult> ExecuteAsync(
            Guid commandId,
            CommandRequest request,
            CancellationToken cancellationToken)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            ExecutionCount++;
            try
            {
                if (blockFirst && ExecutionCount == 1)
                {
                    FirstStarted.SetResult();
                    await ReleaseFirst.Task.WaitAsync(cancellationToken);
                }

                return new(0, new("ok", 2, false), new(string.Empty, 0, false));
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            WasDisconnected = true;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
