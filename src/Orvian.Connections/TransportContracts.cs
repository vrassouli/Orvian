using Orvian.Core.Commands;
using Orvian.Core.Hosts;

namespace Orvian.Connections;

public enum ConnectionAttemptStatus
{
    Connected,
    HostIdentityDecisionRequired,
    AuthenticationFailed,
    NetworkFailed,
    Cancelled,
    Failed
}

public sealed record ConnectionAttemptResult(
    ConnectionAttemptStatus Status,
    IRemoteConnection? Connection = null,
    HostKeyObservation? ObservedHostKey = null,
    string? SafeFailureMessage = null);

public abstract class ConnectionAuthentication : IDisposable
{
    public abstract void Dispose();
}

public sealed class PasswordConnectionAuthentication : ConnectionAuthentication
{
    private byte[]? _password;

    public PasswordConnectionAuthentication(ReadOnlySpan<byte> password)
    {
        _password = password.ToArray();
    }

    public ReadOnlyMemory<byte> Password =>
        _password ?? throw new ObjectDisposedException(nameof(PasswordConnectionAuthentication));

    public override void Dispose()
    {
        if (_password is null)
        {
            return;
        }

        Array.Clear(_password);
        _password = null;
    }
}

public sealed class PrivateKeyConnectionAuthentication : ConnectionAuthentication
{
    private char[]? _passphrase;

    public PrivateKeyConnectionAuthentication(
        string privateKeyPath,
        ReadOnlySpan<char> passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        PrivateKeyPath = privateKeyPath;
        _passphrase = passphrase.ToArray();
    }

    public string PrivateKeyPath { get; }

    public ReadOnlyMemory<char> Passphrase =>
        _passphrase ??
        throw new ObjectDisposedException(nameof(PrivateKeyConnectionAuthentication));

    public override void Dispose()
    {
        if (_passphrase is null)
        {
            return;
        }

        Array.Clear(_passphrase);
        _passphrase = null;
    }
}

public sealed record ConnectionTransportRequest(
    HostProfile Profile,
    ConnectionId ConnectionId,
    ConnectionAuthentication Authentication,
    TrustedHostKey? TrustedHostKey);

public interface IConnectionTransportFactory
{
    Task<ConnectionAttemptResult> ConnectAsync(
        ConnectionTransportRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRemoteConnection : IAsyncDisposable
{
    ConnectionId ConnectionId { get; }

    bool IsConnected { get; }

    bool IsFileTransferAvailable => false;

    IRemoteFileSystem FileSystem =>
        throw new NotSupportedException("This connection does not provide file transfer.");

    Task<TransportCommandResult> ExecuteAsync(
        Guid commandId,
        CommandRequest request,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
