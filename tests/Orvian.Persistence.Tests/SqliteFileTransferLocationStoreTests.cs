using Microsoft.Data.Sqlite;
using Orvian.Core.Hosts;
using Orvian.Persistence;
using Xunit;

namespace Orvian.Persistence.Tests;

public sealed class SqliteFileTransferLocationStoreTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"orvian-file-locations-{Guid.NewGuid():N}.db");
    private OrvianDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new OrvianDatabase(_databasePath);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LocalPathIsGlobalAndRemotePathIsScopedPerHost()
    {
        var store = new SqliteFileTransferLocationStore(_database);
        var first = HostProfileId.New();
        var second = HostProfileId.New();

        await store.SaveLocalPathAsync("/local/work");
        await store.SaveRemotePathAsync(first, "/srv/first");
        await store.SaveRemotePathAsync(second, "/srv/second");

        Assert.Equal(new("/local/work", "/srv/first"), await store.LoadAsync(first));
        Assert.Equal(new("/local/work", "/srv/second"), await store.LoadAsync(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\npath")]
    public async Task InvalidPathIsRejectedWithoutChangingSavedValue(string invalid)
    {
        var store = new SqliteFileTransferLocationStore(_database);
        var host = HostProfileId.New();
        await store.SaveRemotePathAsync(host, "/safe");

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveRemotePathAsync(host, invalid));

        Assert.Equal("/safe", (await store.LoadAsync(host)).RemotePath);
    }
}
