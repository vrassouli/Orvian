namespace Orvian.Connections;

public enum RemoteFileKind
{
    File,
    Directory,
    SymbolicLink,
    Other
}

public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    RemoteFileKind Kind,
    long Length,
    DateTimeOffset LastWriteTime,
    string Permissions)
{
    public bool IsDirectory => Kind == RemoteFileKind.Directory;
}

public sealed record FileTransferProgress(
    string ItemName,
    int CompletedItems,
    int TotalItems,
    long ItemBytesTransferred,
    long? ItemTotalBytes,
    long TotalBytesTransferred,
    long? TotalBytes)
{
    public double? ItemPercent => ItemTotalBytes is > 0
        ? Math.Clamp(ItemBytesTransferred * 100d / ItemTotalBytes.Value, 0d, 100d)
        : null;

    public double? TotalPercent => TotalBytes is > 0
        ? Math.Clamp(TotalBytesTransferred * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

public interface IRemoteFileSystem
{
    Task<IReadOnlyList<RemoteFileEntry>> ListAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        string remotePath,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    Task UploadAsync(
        Stream source,
        string remotePath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
