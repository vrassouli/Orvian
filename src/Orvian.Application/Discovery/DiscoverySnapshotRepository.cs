using Orvian.Core.Hosts;
using Orvian.Discovery;

namespace Orvian.Application.Discovery;

public interface IDiscoverySnapshotRepository
{
    Task StoreAsync(
        DiscoverySnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<DiscoverySnapshot?> GetLatestAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default);
}
