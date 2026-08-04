using System.Collections.Immutable;
using Orvian.Core.Hosts;
using Orvian.Discovery;

namespace Orvian.Application.Hosts;

public sealed record HostProfileSearchQuery(
    string? SearchText,
    ImmutableArray<string> Tags,
    bool IncludeDisabled,
    int Offset,
    int Limit,
    OperatingSystemFamily? OperatingSystem = null,
    ImmutableArray<string> Capabilities = default,
    bool? IsEnabled = null);

public sealed record HostProfilePage(
    ImmutableArray<HostProfile> Items,
    int TotalCount,
    int Offset,
    int Limit);

public interface IHostProfileRepository
{
    Task<HostProfile?> GetAsync(
        HostProfileId id,
        CancellationToken cancellationToken = default);

    Task<HostProfilePage> SearchAsync(
        HostProfileSearchQuery query,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        HostProfile profile,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        HostProfile profile,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        HostProfileId id,
        CancellationToken cancellationToken = default);
}
