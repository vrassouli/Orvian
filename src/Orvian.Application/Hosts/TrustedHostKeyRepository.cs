using Orvian.Auditing;
using Orvian.Core.Hosts;

namespace Orvian.Application.Hosts;

public interface ITrustedHostKeyRepository
{
    Task<TrustedHostKey?> GetAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default);

    Task StoreAsync(
        TrustedHostKey trustedHostKey,
        HostTrustAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default);
}
