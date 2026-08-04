using System.Net;
using System.Net.Sockets;
using System.Text;
using Orvian.Connections;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Security;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Orvian.Ssh;

public sealed class SshNetConnectionFactory : IConnectionTransportFactory
{
    public async Task<ConnectionAttemptResult> ConnectAsync(
        ConnectionTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        IPAddress? resolvedAddress;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(
                profile.HostName,
                cancellationToken).ConfigureAwait(false);
            resolvedAddress = addresses.FirstOrDefault();
            if (resolvedAddress is null)
            {
                return new(
                    ConnectionAttemptStatus.NetworkFailed,
                    SafeFailureMessage: "The host name did not resolve to an address.");
            }
        }
        catch (OperationCanceledException)
        {
            return new(ConnectionAttemptStatus.Cancelled);
        }
        catch (SocketException)
        {
            return new(
                ConnectionAttemptStatus.NetworkFailed,
                SafeFailureMessage: "The host name could not be resolved.");
        }

        using var authenticationMethod = CreateAuthenticationMethod(
            profile.UserName,
            request.Authentication);
        var connectionInfo = new ConnectionInfo(
            profile.HostName,
            profile.Port,
            profile.UserName,
            authenticationMethod)
        {
            Timeout = profile.ConnectionPreferences.ConnectionTimeout
        };
        var client = new SshClient(connectionInfo);
        HostKeyObservation? observed = null;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            observed = new(
                profile.Id,
                profile.HostName,
                profile.Port,
                resolvedAddress.ToString(),
                eventArgs.HostKeyName,
                $"SHA256:{eventArgs.FingerPrintSHA256}");
            eventArgs.CanTrust =
                HostKeyVerifier.Verify(request.TrustedHostKey, observed).MayContinueWithoutPrompt;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!client.IsConnected)
            {
                client.Dispose();
                return new(
                    ConnectionAttemptStatus.Failed,
                    SafeFailureMessage: "SSH negotiation did not establish a connection.");
            }

            SftpClient? sftpClient = new(connectionInfo);
            sftpClient.HostKeyReceived += (_, eventArgs) =>
            {
                var sftpObservation = new HostKeyObservation(
                    profile.Id,
                    profile.HostName,
                    profile.Port,
                    resolvedAddress.ToString(),
                    eventArgs.HostKeyName,
                    $"SHA256:{eventArgs.FingerPrintSHA256}");
                eventArgs.CanTrust = HostKeyVerifier
                    .Verify(request.TrustedHostKey, sftpObservation)
                    .MayContinueWithoutPrompt;
            };
            try
            {
                await sftpClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SshException or SocketException or IOException)
            {
                sftpClient.Dispose();
                sftpClient = null;
            }

            return new(
                ConnectionAttemptStatus.Connected,
                new SshNetRemoteConnection(request.ConnectionId, client, sftpClient));
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            return new(ConnectionAttemptStatus.Cancelled);
        }
        catch (SshAuthenticationException)
        {
            client.Dispose();
            return new(
                ConnectionAttemptStatus.AuthenticationFailed,
                SafeFailureMessage: "SSH authentication failed.");
        }
        catch (SshConnectionException) when (observed is not null)
        {
            client.Dispose();
            return new(
                ConnectionAttemptStatus.HostIdentityDecisionRequired,
                ObservedHostKey: observed,
                SafeFailureMessage: request.TrustedHostKey is null
                    ? "The host identity is not trusted yet."
                    : "The host identity differs from the trusted key.");
        }
        catch (Exception exception) when (
            exception is SshException or SocketException or IOException)
        {
            client.Dispose();
            return new(
                ConnectionAttemptStatus.NetworkFailed,
                SafeFailureMessage: "The SSH connection could not be established.");
        }
    }

    private static AuthenticationMethod CreateAuthenticationMethod(
        string userName,
        ConnectionAuthentication authentication) =>
        authentication switch
        {
            PasswordConnectionAuthentication password =>
                new PasswordAuthenticationMethod(userName, password.Password.ToArray()),
            PrivateKeyConnectionAuthentication privateKey =>
                new PrivateKeyAuthenticationMethod(
                    userName,
                    new PrivateKeyFile(
                        privateKey.PrivateKeyPath,
                        privateKey.Passphrase.ToString())),
            _ => throw new NotSupportedException(
                $"Authentication type '{authentication.GetType().Name}' is not supported.")
        };
}

internal sealed class SshNetRemoteConnection(
    ConnectionId connectionId,
    SshClient client,
    SftpClient? sftpClient) : IRemoteConnection
{
    private const int StandardOutputLimit = 1024 * 1024;
    private const int StandardErrorLimit = 256 * 1024;
    private int _disposed;

    public ConnectionId ConnectionId { get; } = connectionId;

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && client.IsConnected;

    public bool IsFileTransferAvailable =>
        IsConnected && sftpClient is { IsConnected: true };

    public IRemoteFileSystem FileSystem =>
        sftpClient is { IsConnected: true }
            ? new SshNetRemoteFileSystem(sftpClient)
            : throw new NotSupportedException(
                "The remote host does not provide an SFTP subsystem.");

    public async Task<TransportCommandResult> ExecuteAsync(
        Guid commandId,
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!client.IsConnected)
        {
            throw new RemoteCommandInterruptionException(
                "The SSH connection is no longer available.");
        }

        using var command = client.CreateCommand(PosixArgumentEncoder.Encode(request));
        command.CommandTimeout = request.Timeout;
        try
        {
            var execution = command.ExecuteAsync(cancellationToken);
            var standardInput = WriteStandardInputAsync(
                command,
                request.StandardInput,
                cancellationToken);
            var standardOutput = ReadBoundedAsync(
                command.OutputStream,
                StandardOutputLimit,
                cancellationToken);
            var standardError = ReadBoundedAsync(
                command.ExtendedOutputStream,
                StandardErrorLimit,
                cancellationToken);
            await Task.WhenAll(
                execution,
                standardInput,
                standardOutput,
                standardError).ConfigureAwait(false);

            return new(
                command.ExitStatus ?? -1,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SshOperationTimeoutException)
        {
            throw new TimeoutException("The SSH command timed out.");
        }
        catch (SshConnectionException)
        {
            throw new RemoteCommandInterruptionException(
                "The SSH connection was interrupted during command execution.");
        }
    }

    private static async Task WriteStandardInputAsync(
        SshCommand command,
        SensitiveStandardInput? standardInput,
        CancellationToken cancellationToken)
    {
        if (standardInput is null)
        {
            return;
        }

        var characters = standardInput.Memory;
        var bytes = new byte[Encoding.UTF8.GetMaxByteCount(characters.Length)];
        try
        {
            var count = Encoding.UTF8.GetBytes(characters.Span, bytes);
            await using var input = command.CreateInputStream();
            await input.WriteAsync(
                bytes.AsMemory(0, count),
                cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (client.IsConnected)
        {
            client.Disconnect();
        }

        if (sftpClient?.IsConnected == true)
        {
            sftpClient.Disconnect();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (client.IsConnected)
        {
            await Task.Run(client.Disconnect).ConfigureAwait(false);
        }

        client.Dispose();
        if (sftpClient?.IsConnected == true)
        {
            await Task.Run(sftpClient.Disconnect).ConfigureAwait(false);
        }
        sftpClient?.Dispose();
    }

    private static async Task<CapturedOutput> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        using var retained = new MemoryStream(Math.Min(limit, 16 * 1024));
        var buffer = new byte[16 * 1024];
        long observed = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            observed += read;
            var remaining = limit - checked((int)retained.Length);
            if (remaining > 0)
            {
                retained.Write(buffer, 0, Math.Min(read, remaining));
            }
        }

        var content = RemoteOutputSanitizer.Sanitize(
            Encoding.UTF8.GetString(
                retained.GetBuffer(),
                0,
                checked((int)retained.Length)));
        return new(content, observed, observed > limit);
    }
}

internal sealed class SshNetRemoteFileSystem(SftpClient client) : IRemoteFileSystem
{
    public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<RemoteFileEntry>();
        await foreach (var item in client.ListDirectoryAsync(path, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (item.Name is "." or "..")
            {
                continue;
            }

            var kind = item.IsSymbolicLink
                ? RemoteFileKind.SymbolicLink
                : item.IsDirectory
                    ? RemoteFileKind.Directory
                    : item.IsRegularFile
                        ? RemoteFileKind.File
                        : RemoteFileKind.Other;
            entries.Add(new(
                item.Name,
                item.FullName,
                kind,
                item.Length,
                item.LastWriteTimeUtc,
                item.Attributes.ToString() ?? string.Empty));
        }

        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task DownloadAsync(
        string remotePath,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        client.DownloadFileAsync(
            remotePath,
            new ProgressWriteStream(destination, progress),
            cancellationToken);

    public Task UploadAsync(
        Stream source,
        string remotePath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        client.UploadFileAsync(
            new ProgressReadStream(source, progress),
            remotePath,
            cancellationToken);

    public Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        client.CreateDirectoryAsync(path, cancellationToken);

    public Task DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        client.DeleteFileAsync(path, cancellationToken);

    public Task DeleteDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        client.DeleteDirectoryAsync(path, cancellationToken);

    public Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        client.RenameFileAsync(sourcePath, destinationPath, cancellationToken);
}

internal sealed class ProgressReadStream(Stream inner, IProgress<long>? progress) : Stream
{
    private long _transferred;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Report(read);
        return read;
    }
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Report(read);
        return read;
    }
    private void Report(int count)
    {
        if (count > 0) progress?.Report(Interlocked.Add(ref _transferred, count));
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class ProgressWriteStream(Stream inner, IProgress<long>? progress) : Stream
{
    private long _transferred;
    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        progress?.Report(Interlocked.Add(ref _transferred, count));
    }
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        progress?.Report(Interlocked.Add(ref _transferred, buffer.Length));
    }
}
