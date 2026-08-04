using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class SessionCredentialCacheTests
{
    [Fact]
    public void StoredCredentialsAreReturnedAsDisposableCopies()
    {
        using var cache = new SessionCredentialCache();
        var hostId = HostProfileId.New();
        cache.Store(hostId, "temporary".AsSpan());

        var first = cache.Retrieve(hostId);
        using var second = cache.Retrieve(hostId);

        Assert.Equal("temporary", first!.Memory.ToString());
        first.Dispose();
        Assert.Equal("temporary", second!.Memory.ToString());
    }

    [Fact]
    public void DeleteRemovesCredential()
    {
        using var cache = new SessionCredentialCache();
        var hostId = HostProfileId.New();
        cache.Store(hostId, "temporary".AsSpan());

        cache.Delete(hostId);

        Assert.Null(cache.Retrieve(hostId));
    }

    [Fact]
    public void DisposedCacheRejectsAccess()
    {
        var cache = new SessionCredentialCache();
        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => cache.Store(HostProfileId.New(), "temporary".AsSpan()));
    }
}
