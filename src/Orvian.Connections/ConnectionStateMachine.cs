using Orvian.Core.Hosts;

namespace Orvian.Connections;

public readonly record struct ConnectionId(Guid Value)
{
    public static ConnectionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public enum ConnectionState
{
    Disconnected,
    Resolving,
    Connecting,
    Negotiating,
    VerifyingHostIdentity,
    Authenticating,
    Connected,
    Discovering,
    Ready,
    Cancelled,
    NetworkFailed,
    IdentityRejected,
    AuthenticationFailed,
    Interrupted,
    Disposed
}

public sealed record ConnectionSnapshot(
    HostProfileId HostProfileId,
    ConnectionId ConnectionId,
    ConnectionState State,
    DateTimeOffset ChangedAt,
    string? SafeFailureMessage = null)
{
    public bool IsConnected =>
        State is ConnectionState.Connected or ConnectionState.Discovering or ConnectionState.Ready;
}

public sealed class ConnectionStateMachine
{
    private static readonly IReadOnlyDictionary<ConnectionState, IReadOnlySet<ConnectionState>>
        AllowedTransitions = new Dictionary<ConnectionState, IReadOnlySet<ConnectionState>>
        {
            [ConnectionState.Disconnected] = Set(ConnectionState.Resolving, ConnectionState.Disposed),
            [ConnectionState.Resolving] = Set(
                ConnectionState.Connecting,
                ConnectionState.Cancelled,
                ConnectionState.NetworkFailed,
                ConnectionState.Disposed),
            [ConnectionState.Connecting] = Set(
                ConnectionState.Negotiating,
                ConnectionState.Cancelled,
                ConnectionState.NetworkFailed,
                ConnectionState.Disposed),
            [ConnectionState.Negotiating] = Set(
                ConnectionState.VerifyingHostIdentity,
                ConnectionState.Cancelled,
                ConnectionState.NetworkFailed,
                ConnectionState.Disposed),
            [ConnectionState.VerifyingHostIdentity] = Set(
                ConnectionState.Authenticating,
                ConnectionState.IdentityRejected,
                ConnectionState.NetworkFailed,
                ConnectionState.Cancelled,
                ConnectionState.Disposed),
            [ConnectionState.Authenticating] = Set(
                ConnectionState.Connected,
                ConnectionState.AuthenticationFailed,
                ConnectionState.IdentityRejected,
                ConnectionState.Cancelled,
                ConnectionState.NetworkFailed,
                ConnectionState.Disposed),
            [ConnectionState.Connected] = Set(
                ConnectionState.Discovering,
                ConnectionState.Ready,
                ConnectionState.Interrupted,
                ConnectionState.Disconnected,
                ConnectionState.Disposed),
            [ConnectionState.Discovering] = Set(
                ConnectionState.Ready,
                ConnectionState.Connected,
                ConnectionState.Interrupted,
                ConnectionState.Disconnected,
                ConnectionState.Disposed),
            [ConnectionState.Ready] = Set(
                ConnectionState.Discovering,
                ConnectionState.Interrupted,
                ConnectionState.Disconnected,
                ConnectionState.Disposed),
            [ConnectionState.Cancelled] = Set(ConnectionState.Disconnected, ConnectionState.Disposed),
            [ConnectionState.NetworkFailed] = Set(ConnectionState.Disconnected, ConnectionState.Disposed),
            [ConnectionState.IdentityRejected] = Set(ConnectionState.Disconnected, ConnectionState.Disposed),
            [ConnectionState.AuthenticationFailed] = Set(ConnectionState.Disconnected, ConnectionState.Disposed),
            [ConnectionState.Interrupted] = Set(ConnectionState.Disconnected, ConnectionState.Disposed),
            [ConnectionState.Disposed] = Set()
        };

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private ConnectionSnapshot _snapshot;

    public ConnectionStateMachine(
        HostProfileId hostProfileId,
        TimeProvider? timeProvider = null)
    {
        if (hostProfileId.Value == Guid.Empty)
        {
            throw new ArgumentException("Host profile ID is required.", nameof(hostProfileId));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshot = new(
            hostProfileId,
            ConnectionId.New(),
            ConnectionState.Disconnected,
            _timeProvider.GetUtcNow());
    }

    public event EventHandler<ConnectionSnapshot>? Changed;

    public ConnectionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public ConnectionSnapshot Transition(
        ConnectionState next,
        string? safeFailureMessage = null)
    {
        ConnectionSnapshot snapshot;
        lock (_gate)
        {
            if (!AllowedTransitions[_snapshot.State].Contains(next))
            {
                throw new InvalidOperationException(
                    $"Connection cannot transition from {_snapshot.State} to {next}.");
            }

            if (IsFailure(next) && string.IsNullOrWhiteSpace(safeFailureMessage))
            {
                throw new ArgumentException(
                    "Terminal failures require a safe failure message.",
                    nameof(safeFailureMessage));
            }

            var connectionId = _snapshot.ConnectionId;
            if (_snapshot.State == ConnectionState.Disconnected &&
                next == ConnectionState.Resolving)
            {
                connectionId = ConnectionId.New();
            }

            snapshot = _snapshot = new(
                _snapshot.HostProfileId,
                connectionId,
                next,
                _timeProvider.GetUtcNow(),
                IsFailure(next) ? safeFailureMessage : null);
        }

        Changed?.Invoke(this, snapshot);
        return snapshot;
    }

    private static bool IsFailure(ConnectionState state) =>
        state is ConnectionState.NetworkFailed or
            ConnectionState.IdentityRejected or
            ConnectionState.AuthenticationFailed or
            ConnectionState.Interrupted;

    private static IReadOnlySet<ConnectionState> Set(params ConnectionState[] states) =>
        states.ToHashSet();
}
