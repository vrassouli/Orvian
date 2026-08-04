using System.Collections.Concurrent;
using Orvian.Core.Commands;

namespace Orvian.Connections;

public sealed class ConnectionCommandTransport : ICommandTransport, IAsyncDisposable
{
    private readonly ConcurrentDictionary<ConnectionId, SessionEntry> _connections = new();

    public void Register(IRemoteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.IsConnected)
        {
            throw new InvalidOperationException("Only connected sessions can be registered.");
        }

        if (!_connections.TryAdd(connection.ConnectionId, new(connection)))
        {
            throw new InvalidOperationException(
                $"Connection '{connection.ConnectionId}' is already registered.");
        }
    }

    public async Task RemoveAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryRemove(connectionId, out var entry))
        {
            return;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await entry.Connection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await entry.Connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
            entry.Gate.Dispose();
        }
    }

    public async Task<TransportCommandResult> ExecuteAsync(
        Guid commandId,
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(request.ConnectionId, out var connectionGuid))
        {
            throw new RemoteCommandInterruptionException(
                "The command references an invalid connection identity.");
        }

        var connectionId = new ConnectionId(connectionGuid);
        if (!_connections.TryGetValue(connectionId, out var entry) ||
            !entry.Connection.IsConnected)
        {
            throw new RemoteCommandInterruptionException(
                "The selected SSH connection is not available.");
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!entry.Connection.IsConnected)
            {
                throw new RemoteCommandInterruptionException(
                    "The SSH connection was interrupted before execution.");
            }

            return await entry.Connection.ExecuteAsync(
                commandId,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<T> UseFileSystemAsync<T>(
        ConnectionId connectionId,
        Func<IRemoteFileSystem, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_connections.TryGetValue(connectionId, out var entry) ||
            !entry.Connection.IsConnected)
        {
            throw new RemoteCommandInterruptionException(
                "The selected SSH connection is not available.");
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!entry.Connection.IsConnected)
            {
                throw new RemoteCommandInterruptionException(
                    "The SSH connection was interrupted before file access.");
            }

            if (!entry.Connection.IsFileTransferAvailable)
            {
                throw new NotSupportedException(
                    "The remote host does not provide an SFTP subsystem.");
            }

            return await operation(entry.Connection.FileSystem, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public Task UseFileSystemAsync(
        ConnectionId connectionId,
        Func<IRemoteFileSystem, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        UseFileSystemAsync(
            connectionId,
            async (fileSystem, token) =>
            {
                await operation(fileSystem, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var connectionId in _connections.Keys.ToArray())
        {
            await RemoveAsync(connectionId).ConfigureAwait(false);
        }
    }

    private sealed record SessionEntry(IRemoteConnection Connection)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
