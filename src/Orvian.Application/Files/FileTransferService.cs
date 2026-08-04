using System.Collections.Immutable;
using Orvian.Application.Connections;
using Orvian.Application.Hosts;
using Orvian.Auditing;
using Orvian.Connections;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;

namespace Orvian.Application.Files;

public enum FileTransferItemStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}

public sealed record FileTransferItemResult(
    string Name,
    FileTransferItemStatus Status,
    string? SafeFailureMessage = null);

public sealed record FileTransferBatchResult(
    Guid OperationId,
    ImmutableArray<FileTransferItemResult> Items)
{
    public bool IsSuccess => Items.Length > 0 &&
        Items.All(item => item.Status == FileTransferItemStatus.Succeeded);

    public bool IsPartial => Items.Any(item => item.Status == FileTransferItemStatus.Succeeded) &&
        Items.Any(item => item.Status != FileTransferItemStatus.Succeeded);
}

public sealed record LocalTransferSource(string FullPath, bool IsDirectory);

public sealed class FileTransferService(
    IConnectionSnapshotProvider connections,
    ConnectionCommandTransport transport,
    IHostProfileRepository profiles,
    IAuditSink commandAudit,
    IOperationAuditSink operationAudit,
    IOperationConfirmationService confirmations,
    TimeProvider? timeProvider = null)
{
    public const string PluginId = "orvian.file-transfer";
    public static readonly Version PluginVersion = new(0, 1);
    public const string ReadPermission = "file.read";
    public const string WritePermission = "file.write";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(
        HostProfileId hostId,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(hostId, cancellationToken).ConfigureAwait(false);
        var path = RemotePath.Normalize(remotePath);
        var operationId = Guid.NewGuid();
        return await ExecuteReadAsync(
            context,
            operationId,
            "List remote directory",
            "sftp.list",
            [path],
            (fileSystem, token) => fileSystem.ListAsync(path, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileTransferBatchResult> DownloadAsync(
        HostProfileId hostId,
        IReadOnlyList<RemoteFileEntry> entries,
        string localDirectory,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var context = await ResolveContextAsync(hostId, cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(localDirectory);
        Directory.CreateDirectory(root);
        var operationId = Guid.NewGuid();
        var operationStart = await StartOperationAsync(
            context,
            operationId,
            "Download files",
            "Download selected remote files to this computer.",
            ReadPermission,
            OperationRisk.Informational,
            entries.Count,
            cancellationToken).ConfigureAwait(false);
        var results = new List<FileTransferItemResult>(entries.Count);
        long aggregate = 0;
        long? aggregateTotal = entries.All(entry => entry.Kind == RemoteFileKind.File)
            ? entries.Sum(entry => entry.Length)
            : null;
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                try
                {
                    await DownloadEntryAsync(
                        context,
                        operationId,
                        entry,
                        root,
                        index,
                        entries.Count,
                        value =>
                        {
                            var total = aggregate + value;
                            progress?.Report(new(
                                entry.Name,
                                index,
                                entries.Count,
                                value,
                                entry.Kind == RemoteFileKind.File ? entry.Length : null,
                                total,
                                aggregateTotal));
                        },
                        cancellationToken).ConfigureAwait(false);
                    aggregate += entry.Kind == RemoteFileKind.File ? entry.Length : 0;
                    results.Add(new(entry.Name, FileTransferItemStatus.Succeeded));
                }
                catch (OperationCanceledException)
                {
                    results.Add(new(entry.Name, FileTransferItemStatus.Cancelled));
                    throw;
                }
                catch (Exception)
                {
                    results.Add(new(
                        entry.Name,
                        FileTransferItemStatus.Failed,
                        "The item could not be downloaded."));
                }
            }

            return await CompleteOperationAsync(
                operationStart,
                results,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CompleteOperationAsync(operationStart, results, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<FileTransferBatchResult> UploadAsync(
        HostProfileId hostId,
        IReadOnlyList<LocalTransferSource> sources,
        string remoteDirectory,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var context = await ResolveContextAsync(hostId, cancellationToken).ConfigureAwait(false);
        var remoteRoot = RemotePath.Normalize(remoteDirectory);
        var operationId = Guid.NewGuid();
        var operationStart = await StartConfirmedOperationAsync(
            context,
            operationId,
            "Upload files",
            "Upload selected local files to the remote host. Existing files may be replaced.",
            sources.Count,
            cancellationToken).ConfigureAwait(false);
        var results = new List<FileTransferItemResult>(sources.Count);
        long aggregate = 0;
        long? aggregateTotal = sources.All(source => !source.IsDirectory)
            ? sources.Sum(source => new FileInfo(source.FullPath).Length)
            : null;
        try
        {
            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source.FullPath));
                try
                {
                    await UploadEntryAsync(
                        context,
                        operationId,
                        Path.GetFullPath(source.FullPath),
                        source.IsDirectory,
                        RemotePath.Combine(remoteRoot, name),
                        index,
                        sources.Count,
                        value => progress?.Report(new(
                            name,
                            index,
                            sources.Count,
                            value,
                            source.IsDirectory ? null : new FileInfo(source.FullPath).Length,
                            aggregate + value,
                            aggregateTotal)),
                        cancellationToken).ConfigureAwait(false);
                    if (!source.IsDirectory)
                    {
                        aggregate += new FileInfo(source.FullPath).Length;
                    }
                    results.Add(new(name, FileTransferItemStatus.Succeeded));
                }
                catch (OperationCanceledException)
                {
                    results.Add(new(name, FileTransferItemStatus.Cancelled));
                    throw;
                }
                catch (Exception)
                {
                    results.Add(new(name, FileTransferItemStatus.Failed, "The item could not be uploaded."));
                }
            }
            return await CompleteOperationAsync(operationStart, results, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CompleteOperationAsync(operationStart, results, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public Task<FileTransferBatchResult> DeleteAsync(
        HostProfileId hostId,
        IReadOnlyList<RemoteFileEntry> entries,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        MutateEntriesAsync(
            hostId,
            entries,
            "Delete remote files",
            "Permanently delete the selected remote files and folders.",
            async (context, operationId, entry, token) =>
                await DeleteEntryAsync(context, operationId, entry, token).ConfigureAwait(false),
            progress,
            cancellationToken);

    public async Task CreateDirectoryAsync(
        HostProfileId hostId,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(hostId, cancellationToken).ConfigureAwait(false);
        var path = RemotePath.Normalize(remotePath);
        await ExecuteMutationAsync(
            context,
            "Create remote folder",
            "Create a folder on the remote host.",
            "sftp.mkdir",
            [path],
            (fs, token) => fs.CreateDirectoryAsync(path, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(
        HostProfileId hostId,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(hostId, cancellationToken).ConfigureAwait(false);
        var source = RemotePath.Normalize(sourcePath);
        var destination = RemotePath.Normalize(destinationPath);
        await ExecuteMutationAsync(
            context,
            "Move remote item",
            "Move or rename an item on the remote host.",
            "sftp.rename",
            [source, destination],
            (fs, token) => fs.RenameAsync(source, destination, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileTransferBatchResult> MutateEntriesAsync(
        HostProfileId hostId,
        IReadOnlyList<RemoteFileEntry> entries,
        string title,
        string purpose,
        Func<TransferContext, Guid, RemoteFileEntry, CancellationToken, Task> action,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(hostId, cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var start = await StartConfirmedOperationAsync(
            context, operationId, title, purpose, entries.Count, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<FileTransferItemResult>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            try
            {
                await action(context, operationId, entry, cancellationToken).ConfigureAwait(false);
                results.Add(new(entry.Name, FileTransferItemStatus.Succeeded));
                progress?.Report(new(entry.Name, index + 1, entries.Count, 1, 1, index + 1, entries.Count));
            }
            catch (OperationCanceledException)
            {
                results.Add(new(entry.Name, FileTransferItemStatus.Cancelled));
                break;
            }
            catch (Exception)
            {
                results.Add(new(entry.Name, FileTransferItemStatus.Failed, "The remote item operation failed."));
            }
        }
        return await CompleteOperationAsync(start, results, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DownloadEntryAsync(
        TransferContext context,
        Guid operationId,
        RemoteFileEntry entry,
        string localRoot,
        int index,
        int total,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        var target = SafeLocalChild(localRoot, entry.Name);
        if (entry.Kind == RemoteFileKind.SymbolicLink)
        {
            throw new NotSupportedException("Symbolic links are not followed during download.");
        }
        if (entry.Kind == RemoteFileKind.Directory)
        {
            Directory.CreateDirectory(target);
            var children = await ListAsync(context.HostId, entry.FullPath, cancellationToken)
                .ConfigureAwait(false);
            foreach (var child in children)
            {
                await DownloadEntryAsync(context, operationId, child, target, index, total, report, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        var temporary = target + ".orvian-part";
        await using var output = new FileStream(
            temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await ExecuteAuditedAsync(
                context, operationId, "sftp.download", [entry.FullPath, entry.Name], ReadPermission,
                (fs, token) => fs.DownloadAsync(entry.FullPath, output, new Progress<long>(report), token),
                cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, true);
        }
        catch
        {
            output.Close();
            File.Delete(temporary);
            throw;
        }
    }

    private async Task UploadEntryAsync(
        TransferContext context,
        Guid operationId,
        string localPath,
        bool isDirectory,
        string remotePath,
        int index,
        int total,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        if (isDirectory)
        {
            await TryCreateDirectoryAsync(context, operationId, remotePath, cancellationToken)
                .ConfigureAwait(false);
            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                await UploadEntryAsync(
                    context, operationId, child, Directory.Exists(child),
                    RemotePath.Combine(remotePath, Path.GetFileName(child)), index, total, report,
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        await using var input = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await ExecuteAuditedAsync(
            context, operationId, "sftp.upload", [Path.GetFileName(localPath), remotePath], WritePermission,
            (fs, token) => fs.UploadAsync(input, remotePath, new Progress<long>(report), token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteEntryAsync(
        TransferContext context,
        Guid operationId,
        RemoteFileEntry entry,
        CancellationToken cancellationToken)
    {
        if (RemotePath.Normalize(entry.FullPath) == "/")
        {
            throw new InvalidOperationException("The remote root cannot be deleted.");
        }
        if (entry.Kind == RemoteFileKind.Directory)
        {
            var children = await ListAsync(context.HostId, entry.FullPath, cancellationToken)
                .ConfigureAwait(false);
            foreach (var child in children)
            {
                await DeleteEntryAsync(context, operationId, child, cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAuditedAsync(
                context, operationId, "sftp.rmdir", [entry.FullPath], WritePermission,
                (fs, token) => fs.DeleteDirectoryAsync(entry.FullPath, token), cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        await ExecuteAuditedAsync(
            context, operationId, "sftp.delete", [entry.FullPath], WritePermission,
            (fs, token) => fs.DeleteFileAsync(entry.FullPath, token), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryCreateDirectoryAsync(
        TransferContext context,
        Guid operationId,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAuditedAsync(
                context, operationId, "sftp.mkdir", [path], WritePermission,
                (fs, token) => fs.CreateDirectoryAsync(path, token), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            var parent = RemotePath.Parent(path);
            var existing = await ListAsync(context.HostId, parent, cancellationToken).ConfigureAwait(false);
            if (!existing.Any(item => item.IsDirectory && item.FullPath == path)) throw;
        }
    }

    private async Task ExecuteMutationAsync(
        TransferContext context,
        string title,
        string purpose,
        string executable,
        ImmutableArray<string> arguments,
        Func<IRemoteFileSystem, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var start = await StartConfirmedOperationAsync(
            context, operationId, title, purpose, 1, cancellationToken).ConfigureAwait(false);
        var result = new List<FileTransferItemResult>();
        try
        {
            await ExecuteAuditedAsync(
                context, operationId, executable, arguments, WritePermission, action, cancellationToken)
                .ConfigureAwait(false);
            result.Add(new(arguments[^1], FileTransferItemStatus.Succeeded));
        }
        catch (Exception)
        {
            result.Add(new(arguments[^1], FileTransferItemStatus.Failed, "The remote file operation failed."));
            throw;
        }
        finally
        {
            await CompleteOperationAsync(start, result, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<T> ExecuteReadAsync<T>(
        TransferContext context,
        Guid operationId,
        string title,
        string executable,
        ImmutableArray<string> arguments,
        Func<IRemoteFileSystem, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var start = await StartOperationAsync(
            context, operationId, title, title, ReadPermission, OperationRisk.Informational, 1,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ExecuteAuditedAsync(
                context, operationId, executable, arguments, ReadPermission, action, cancellationToken)
                .ConfigureAwait(false);
            await CompleteOperationAsync(
                start, [new(executable, FileTransferItemStatus.Succeeded)], CancellationToken.None)
                .ConfigureAwait(false);
            return result;
        }
        catch
        {
            await CompleteOperationAsync(
                start, [new(executable, FileTransferItemStatus.Failed)], CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteAuditedAsync(
        TransferContext context,
        Guid operationId,
        string executable,
        ImmutableArray<string> arguments,
        string permission,
        Func<IRemoteFileSystem, CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        await ExecuteAuditedAsync(
            context, operationId, executable, arguments, permission,
            async (fs, token) => { await action(fs, token).ConfigureAwait(false); return true; },
            cancellationToken).ConfigureAwait(false);

    private async Task<T> ExecuteAuditedAsync<T>(
        TransferContext context,
        Guid operationId,
        string executable,
        ImmutableArray<string> arguments,
        string permission,
        Func<IRemoteFileSystem, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var start = new CommandAuditEntry
        {
            CommandId = commandId,
            OperationId = operationId,
            StartedAt = _timeProvider.GetUtcNow(),
            HostProfileId = context.HostId.ToString(),
            ConnectionId = context.ConnectionId.ToString(),
            RemoteUserName = context.RemoteUserName,
            PluginId = PluginId,
            PluginVersion = PluginVersion.ToString(),
            Executable = executable,
            RedactedArguments = arguments,
            Privilege = PrivilegeLevel.User,
            InvocationSource = InvocationSource.UserInterface,
            Status = AuditStatus.Started,
            OutputLogging = OutputLoggingMode.MetadataOnly
        };
        await commandAudit.CommandStartedAsync(start, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await transport.UseFileSystemAsync(
                context.ConnectionId, action, cancellationToken).ConfigureAwait(false);
            await commandAudit.CommandCompletedAsync(
                start with { CompletedAt = _timeProvider.GetUtcNow(), Status = AuditStatus.Succeeded },
                CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await commandAudit.CommandCompletedAsync(
                start with { CompletedAt = _timeProvider.GetUtcNow(), Status = AuditStatus.Cancelled,
                    SafeFailureMessage = "The file operation was cancelled." }, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await commandAudit.CommandCompletedAsync(
                start with { CompletedAt = _timeProvider.GetUtcNow(), Status = AuditStatus.Failed,
                    SafeFailureMessage = "The remote file operation failed." }, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<OperationAuditEntry> StartConfirmedOperationAsync(
        TransferContext context,
        Guid operationId,
        string title,
        string purpose,
        int itemCount,
        CancellationToken cancellationToken)
    {
        var start = await StartOperationAsync(
            context, operationId, title, purpose, WritePermission, OperationRisk.Destructive,
            itemCount, cancellationToken).ConfigureAwait(false);
        var intent = new OperationIntent
        {
            OperationId = operationId,
            HostProfileId = context.HostId.ToString(),
            ConnectionId = context.ConnectionId.ToString(),
            PluginId = PluginId,
            PluginVersion = PluginVersion,
            InvocationSource = InvocationSource.UserInterface,
            Title = title,
            Purpose = purpose,
            Risk = OperationRisk.Destructive,
            Privilege = PrivilegeLevel.User,
            RequiredPermission = WritePermission,
            ResourceLockKey = "files:" + context.HostId
        };
        var confirmed = await confirmations.ConfirmAsync(
            new(intent, ConfirmationRequirement.ExplicitDestructive, [$"{itemCount} selected item(s)"]),
            cancellationToken).ConfigureAwait(false);
        if (!confirmed)
        {
            await operationAudit.OperationCompletedAsync(
                start with { CompletedAt = _timeProvider.GetUtcNow(), Status = OperationAuditStatus.Denied,
                    SafeFailureMessage = "The file operation was not confirmed." }, CancellationToken.None)
                .ConfigureAwait(false);
            throw new OperationCanceledException("The file operation was not confirmed.");
        }
        return start;
    }

    private async Task<OperationAuditEntry> StartOperationAsync(
        TransferContext context,
        Guid operationId,
        string title,
        string purpose,
        string permission,
        OperationRisk risk,
        int itemCount,
        CancellationToken cancellationToken)
    {
        var start = new OperationAuditEntry
        {
            OperationId = operationId,
            StartedAt = _timeProvider.GetUtcNow(),
            HostProfileId = context.HostId.ToString(),
            ConnectionId = context.ConnectionId.ToString(),
            RemoteUserName = context.RemoteUserName,
            PluginId = PluginId,
            PluginVersion = PluginVersion.ToString(),
            Title = title,
            Purpose = purpose,
            RequiredPermission = permission,
            Risk = risk,
            Privilege = PrivilegeLevel.User,
            InvocationSource = InvocationSource.UserInterface,
            ResourceLockKey = "files:" + context.HostId,
            Status = OperationAuditStatus.Started,
            CommandCount = itemCount
        };
        await operationAudit.OperationStartedAsync(start, cancellationToken).ConfigureAwait(false);
        return start;
    }

    private async Task<FileTransferBatchResult> CompleteOperationAsync(
        OperationAuditEntry start,
        IReadOnlyList<FileTransferItemResult> results,
        CancellationToken cancellationToken)
    {
        var status = results.Count == 0
            ? OperationAuditStatus.Cancelled
            : results.All(item => item.Status == FileTransferItemStatus.Succeeded)
                ? OperationAuditStatus.Succeeded
                : results.Any(item => item.Status == FileTransferItemStatus.Succeeded)
                    ? OperationAuditStatus.PartiallySucceeded
                    : results.Any(item => item.Status == FileTransferItemStatus.Cancelled)
                        ? OperationAuditStatus.Cancelled
                        : OperationAuditStatus.Failed;
        await operationAudit.OperationCompletedAsync(
            start with { CompletedAt = _timeProvider.GetUtcNow(), Status = status,
                CommandCount = results.Count,
                SafeFailureMessage = status is OperationAuditStatus.Succeeded ? null :
                    "One or more file operations did not complete." }, cancellationToken)
            .ConfigureAwait(false);
        return new(start.OperationId, [.. results]);
    }

    private async Task<TransferContext> ResolveContextAsync(
        HostProfileId hostId,
        CancellationToken cancellationToken)
    {
        var snapshot = connections.GetSnapshot(hostId);
        if (!snapshot.IsConnected)
        {
            throw new InvalidOperationException("File transfer requires a connected host.");
        }
        var profile = await profiles.GetAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected host no longer exists.");
        return new(hostId, snapshot.ConnectionId, profile.UserName);
    }

    private static string SafeLocalChild(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("The remote item name is not valid on this computer.");
        }
        var candidate = Path.GetFullPath(Path.Combine(root, name));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The remote item would escape the selected folder.");
        }
        return candidate;
    }

    private sealed record TransferContext(
        HostProfileId HostId,
        ConnectionId ConnectionId,
        string RemoteUserName);
}

internal static class RemotePath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Remote paths must be absolute and contain no control characters.", nameof(path));
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (normalized.Count == 0) throw new ArgumentException("Remote path escapes the root.", nameof(path));
                normalized.RemoveAt(normalized.Count - 1);
                continue;
            }
            normalized.Add(segment);
        }
        return normalized.Count == 0 ? "/" : "/" + string.Join('/', normalized);
    }

    public static string Combine(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(child) || child.Contains('/') || child is "." or "..")
            throw new ArgumentException("Remote item names must be a single path segment.", nameof(child));
        return Normalize(parent == "/" ? "/" + child : parent + "/" + child);
    }

    public static string Parent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "/") return "/";
        var index = normalized.LastIndexOf('/');
        return index == 0 ? "/" : normalized[..index];
    }
}
