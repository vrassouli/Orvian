using Orvian.Core.Hosts;

namespace Orvian.Application.Files;

public sealed record FileTransferLocations(
    string? LocalPath,
    string? RemotePath);

public interface IFileTransferLocationStore
{
    Task<FileTransferLocations> LoadAsync(
        HostProfileId hostId,
        CancellationToken cancellationToken = default);

    Task SaveLocalPathAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task SaveRemotePathAsync(
        HostProfileId hostId,
        string path,
        CancellationToken cancellationToken = default);
}
