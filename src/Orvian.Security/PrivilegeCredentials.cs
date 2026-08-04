using Orvian.Core.Hosts;

namespace Orvian.Security;

public interface IPrivilegeCredentialCache
{
    void Store(HostProfileId hostProfileId, ReadOnlySpan<char> secret);

    SecretValue? Retrieve(HostProfileId hostProfileId);

    void Delete(HostProfileId hostProfileId);
}

public sealed class SessionPrivilegeCredentialCache : IPrivilegeCredentialCache, IDisposable
{
    private readonly SessionCredentialCache _cache = new();

    public void Store(HostProfileId hostProfileId, ReadOnlySpan<char> secret) =>
        _cache.Store(hostProfileId, secret);

    public SecretValue? Retrieve(HostProfileId hostProfileId) =>
        _cache.Retrieve(hostProfileId);

    public void Delete(HostProfileId hostProfileId) =>
        _cache.Delete(hostProfileId);

    public void Dispose() =>
        _cache.Dispose();
}
