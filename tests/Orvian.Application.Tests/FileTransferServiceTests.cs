using Orvian.Application.Connections;
using Orvian.Application.Files;
using Orvian.Application.Hosts;
using Orvian.Auditing;
using Orvian.Connections;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class FileTransferServiceTests
{
    [Fact]
    public async Task ListAsync_AuditsBeforeRemoteAccess()
    {
        var host = HostProfileId.New();
        var connectionId = ConnectionId.New();
        var events = new List<string>();
        var fileSystem = new FakeFileSystem(() => events.Add("transport"));
        await using var transport = new ConnectionCommandTransport();
        transport.Register(new FakeConnection(connectionId, fileSystem));
        var audit = new OrderingAudit(events);
        var service = CreateService(host, connectionId, transport, audit, audit);

        var result = await service.ListAsync(host, "/var/log");

        Assert.Single(result);
        Assert.Equal(["operation-start", "command-start", "transport", "command-complete", "operation-complete"], events);
    }

    [Fact]
    public async Task ListAsync_WhenAuditStartFails_DoesNotAccessRemoteFileSystem()
    {
        var host = HostProfileId.New();
        var connectionId = ConnectionId.New();
        var accessed = false;
        await using var transport = new ConnectionCommandTransport();
        transport.Register(new FakeConnection(
            connectionId,
            new FakeFileSystem(() => accessed = true)));
        var audit = new FailingAudit();
        var service = CreateService(host, connectionId, transport, audit, audit);

        await Assert.ThrowsAsync<IOException>(() => service.ListAsync(host, "/"));
        Assert.False(accessed);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/../../etc")]
    [InlineData("/tmp\nname")]
    public async Task ListAsync_RejectsUnsafeRemotePath(string path)
    {
        var host = HostProfileId.New();
        var connectionId = ConnectionId.New();
        await using var transport = new ConnectionCommandTransport();
        transport.Register(new FakeConnection(connectionId, new FakeFileSystem(() => { })));
        var audit = new OrderingAudit([]);
        var service = CreateService(host, connectionId, transport, audit, audit);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ListAsync(host, path));
    }

    private static FileTransferService CreateService(
        HostProfileId host,
        ConnectionId connectionId,
        ConnectionCommandTransport transport,
        IAuditSink commandAudit,
        IOperationAuditSink operationAudit) =>
        new(
            new SnapshotProvider(host, connectionId),
            transport,
            new ProfileRepository(CreateProfile(host)),
            commandAudit,
            operationAudit,
            new AllowConfirmation());

    private static HostProfile CreateProfile(HostProfileId host)
    {
        var now = DateTimeOffset.UtcNow;
        return Assert.IsType<HostProfile>(HostProfile.Create(
            host, "Test", "example.test", 22, "operator",
            HostAuthenticationMethod.Password, null, [], null, true,
            HostConnectionPreferences.Default, now, now).Profile);
    }

    private sealed class SnapshotProvider(HostProfileId host, ConnectionId connectionId)
        : IConnectionSnapshotProvider
    {
        public ConnectionSnapshot GetSnapshot(HostProfileId hostProfileId) =>
            new(host, connectionId, ConnectionState.Ready, DateTimeOffset.UtcNow);
    }

    private sealed class ProfileRepository(HostProfile profile) : IHostProfileRepository
    {
        public Task<HostProfile?> GetAsync(HostProfileId id, CancellationToken cancellationToken = default) =>
            Task.FromResult<HostProfile?>(profile);
        public Task<HostProfilePage> SearchAsync(HostProfileSearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostProfilePage([profile], 1, 0, 1));
        public Task AddAsync(HostProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(HostProfile value, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(HostProfileId id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeConnection(ConnectionId id, IRemoteFileSystem fileSystem) : IRemoteConnection
    {
        public ConnectionId ConnectionId => id;
        public bool IsConnected => true;
        public bool IsFileTransferAvailable => true;
        public IRemoteFileSystem FileSystem => fileSystem;
        public Task<TransportCommandResult> ExecuteAsync(Guid commandId, CommandRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFileSystem(Action onList) : IRemoteFileSystem
    {
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            onList();
            return Task.FromResult<IReadOnlyList<RemoteFileEntry>>(
                [new("file.txt", path + "/file.txt", RemoteFileKind.File, 3, DateTimeOffset.UtcNow, "rw")]);
        }
        public Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class OrderingAudit(List<string> events) : IAuditSink, IOperationAuditSink
    {
        public Task CommandStartedAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default) { events.Add("command-start"); return Task.CompletedTask; }
        public Task CommandCompletedAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default) { events.Add("command-complete"); return Task.CompletedTask; }
        public Task OperationStartedAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default) { events.Add("operation-start"); return Task.CompletedTask; }
        public Task OperationCompletedAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default) { events.Add("operation-complete"); return Task.CompletedTask; }
    }

    private sealed class FailingAudit : IAuditSink, IOperationAuditSink
    {
        public Task CommandStartedAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default) => throw new IOException("audit unavailable");
        public Task CommandCompletedAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OperationStartedAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OperationCompletedAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowConfirmation : IOperationConfirmationService
    {
        public Task<bool> ConfirmAsync(OperationConfirmationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
