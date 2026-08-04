using System.Collections.Concurrent;
using Orvian.Auditing;
using Orvian.Application.Discovery;
using Orvian.Application.Hosts;
using Orvian.Connections;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Orvian.Security;

namespace Orvian.Application.Connections;

public enum HostKeyUserDecision
{
    Reject,
    TrustFirstSeen,
    ReplaceChanged
}

public interface IConnectionCredentialProvider
{
    Task<ConnectionAuthentication?> GetAuthenticationAsync(
        HostProfile profile,
        CancellationToken cancellationToken = default);
}

public interface IHostKeyDecisionService
{
    Task<HostKeyUserDecision> DecideAsync(
        HostKeyVerificationResult verification,
        CancellationToken cancellationToken = default);
}

public interface IConnectionSnapshotProvider
{
    ConnectionSnapshot GetSnapshot(HostProfileId hostProfileId);
}

public sealed record ConnectionWorkflowResult(
    ConnectionSnapshot Snapshot,
    DiscoverySnapshot? Discovery = null,
    HostKeyVerificationResult? HostKeyVerification = null,
    string? SafeFailureMessage = null,
    int ConnectionAttempts = 0);

public interface IConnectionRetryDelay
{
    Task DelayAsync(
        int retryNumber,
        CancellationToken cancellationToken = default);
}

public sealed class BoundedConnectionRetryDelay : IConnectionRetryDelay
{
    public Task DelayAsync(
        int retryNumber,
        CancellationToken cancellationToken = default)
    {
        if (retryNumber is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(retryNumber));
        }

        var delay = TimeSpan.FromMilliseconds(
            Math.Min(2_000, 250 * (1 << (retryNumber - 1))));
        return Task.Delay(delay, cancellationToken);
    }
}

public sealed class ConnectionWorkflow : IConnectionSnapshotProvider, IAsyncDisposable
{
    private readonly IHostProfileRepository _profiles;
    private readonly ITrustedHostKeyRepository _trustedKeys;
    private readonly IConnectionCredentialProvider _credentials;
    private readonly IHostKeyDecisionService _hostKeyDecisions;
    private readonly IConnectionTransportFactory _transportFactory;
    private readonly ConnectionCommandTransport _commandTransport;
    private readonly IHostDiscoveryService _discovery;
    private readonly IDiscoverySnapshotRepository _discoverySnapshots;
    private readonly TimeProvider _timeProvider;
    private readonly IConnectionRetryDelay _retryDelay;
    private readonly ConcurrentDictionary<HostProfileId, ConnectionStateMachine> _states = new();
    private readonly ConcurrentDictionary<HostProfileId, SemaphoreSlim> _hostLocks = new();

    public ConnectionWorkflow(
        IHostProfileRepository profiles,
        ITrustedHostKeyRepository trustedKeys,
        IConnectionCredentialProvider credentials,
        IHostKeyDecisionService hostKeyDecisions,
        IConnectionTransportFactory transportFactory,
        ConnectionCommandTransport commandTransport,
        IHostDiscoveryService discovery,
        IDiscoverySnapshotRepository discoverySnapshots,
        TimeProvider? timeProvider = null,
        IConnectionRetryDelay? retryDelay = null)
    {
        _profiles = profiles;
        _trustedKeys = trustedKeys;
        _credentials = credentials;
        _hostKeyDecisions = hostKeyDecisions;
        _transportFactory = transportFactory;
        _commandTransport = commandTransport;
        _discovery = discovery;
        _discoverySnapshots = discoverySnapshots;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryDelay = retryDelay ?? new BoundedConnectionRetryDelay();
    }

    public ConnectionSnapshot GetSnapshot(HostProfileId hostProfileId) =>
        GetState(hostProfileId).Snapshot;

    public async Task<ConnectionWorkflowResult> ConnectAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        var hostLock = _hostLocks.GetOrAdd(hostProfileId, _ => new(1, 1));
        await hostLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var state = GetState(hostProfileId);
        ConnectionRetryTracker? retryTracker = null;
        try
        {
            if (state.Snapshot.State != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException(
                    "Host must be disconnected before starting a new connection.");
            }

            var profile = await _profiles.GetAsync(hostProfileId, cancellationToken)
                .ConfigureAwait(false) ??
                throw new InvalidOperationException("Host profile does not exist.");
            if (!profile.IsEnabled)
            {
                throw new InvalidOperationException("Disabled host profiles cannot connect.");
            }

            state.Transition(ConnectionState.Resolving);
            state.Transition(ConnectionState.Connecting);
            state.Transition(ConnectionState.Negotiating);
            state.Transition(ConnectionState.VerifyingHostIdentity);

            using var authentication = await _credentials
                .GetAuthenticationAsync(profile, cancellationToken)
                .ConfigureAwait(false);
            if (authentication is null)
            {
                state.Transition(ConnectionState.Authenticating);
                state.Transition(
                    ConnectionState.AuthenticationFailed,
                    "Authentication credentials are unavailable.");
                return new(state.Snapshot);
            }

            var trusted = await _trustedKeys.GetAsync(hostProfileId, cancellationToken)
                .ConfigureAwait(false);
            retryTracker = new ConnectionRetryTracker(
                profile.ConnectionPreferences.MaximumReconnectAttempts,
                _transportFactory,
                _retryDelay);
            var attempt = await retryTracker.ConnectAsync(
                new(profile, state.Snapshot.ConnectionId, authentication, trusted),
                cancellationToken).ConfigureAwait(false);

            HostKeyVerificationResult? verification = null;
            if (attempt.Status == ConnectionAttemptStatus.HostIdentityDecisionRequired &&
                attempt.ObservedHostKey is not null)
            {
                verification = HostKeyVerifier.Verify(trusted, attempt.ObservedHostKey);
                var decision = await _hostKeyDecisions
                    .DecideAsync(verification, cancellationToken)
                    .ConfigureAwait(false);
                if (!DecisionPermitsTrust(verification.Decision, decision))
                {
                    state.Transition(
                        ConnectionState.IdentityRejected,
                        "The host identity was not trusted.");
                    return new(
                        state.Snapshot,
                        HostKeyVerification: verification,
                        ConnectionAttempts: retryTracker.AttemptCount);
                }

                var previousFingerprint = trusted?.Sha256Fingerprint;
                trusted = HostKeyVerifier.Trust(
                    attempt.ObservedHostKey,
                    _timeProvider.GetUtcNow());
                var auditEvent = new HostTrustAuditEvent(
                    Guid.NewGuid(),
                    trusted.TrustedAt,
                    trusted.HostProfileId.ToString(),
                    decision == HostKeyUserDecision.TrustFirstSeen
                        ? HostTrustAction.TrustFirstSeen
                        : HostTrustAction.ReplaceChanged,
                    trusted.HostName,
                    trusted.Port,
                    trusted.ResolvedAddress,
                    trusted.Algorithm,
                    trusted.Sha256Fingerprint,
                    previousFingerprint);
                try
                {
                    await _trustedKeys.StoreAsync(
                        trusted,
                        auditEvent,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    state.Transition(
                        ConnectionState.IdentityRejected,
                        "The host trust decision could not be saved and audited.");
                    return new(
                        state.Snapshot,
                        HostKeyVerification: verification,
                        SafeFailureMessage:
                            "The host trust decision could not be saved and audited.",
                        ConnectionAttempts: retryTracker.AttemptCount);
                }

                state.Transition(ConnectionState.Authenticating);
                attempt = await retryTracker.ConnectAsync(
                    new(profile, state.Snapshot.ConnectionId, authentication, trusted),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                state.Transition(ConnectionState.Authenticating);
            }

            if (attempt.Status != ConnectionAttemptStatus.Connected ||
                attempt.Connection is null)
            {
                TransitionFailure(state, attempt, retryTracker.AttemptCount);
                return new(
                    state.Snapshot,
                    HostKeyVerification: verification,
                    ConnectionAttempts: retryTracker.AttemptCount);
            }

            state.Transition(ConnectionState.Connected);
            _commandTransport.Register(attempt.Connection);
            state.Transition(ConnectionState.Discovering);
            DiscoverySnapshot? discovery = null;
            string? discoveryFailure = null;
            try
            {
                discovery = await _discovery.DiscoverAsync(
                    new(
                        hostProfileId,
                        state.Snapshot.ConnectionId.ToString(),
                        "orvian.discovery",
                        new Version(0, 1)),
                    cancellationToken).ConfigureAwait(false);
                await _discoverySnapshots.StoreAsync(discovery, cancellationToken)
                    .ConfigureAwait(false);
                state.Transition(ConnectionState.Ready);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                state.Transition(ConnectionState.Connected);
            }
            catch (Exception)
            {
                state.Transition(ConnectionState.Connected);
                discoveryFailure =
                    "Discovery completed or connected, but its cached snapshot could not be saved.";
            }

            return new(
                state.Snapshot,
                discovery,
                verification,
                discoveryFailure,
                retryTracker.AttemptCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (state.Snapshot.State is (
                    ConnectionState.Resolving or
                    ConnectionState.Connecting or
                    ConnectionState.Negotiating or
                    ConnectionState.VerifyingHostIdentity or
                    ConnectionState.Authenticating))
            {
                state.Transition(ConnectionState.Cancelled);
            }

            return new(
                state.Snapshot,
                ConnectionAttempts: retryTracker?.AttemptCount ?? 0);
        }
        finally
        {
            hostLock.Release();
        }
    }

    public async Task DisconnectAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        var hostLock = _hostLocks.GetOrAdd(hostProfileId, _ => new(1, 1));
        await hostLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = GetState(hostProfileId);
            if (state.Snapshot.IsConnected)
            {
                await _commandTransport.RemoveAsync(
                    state.Snapshot.ConnectionId,
                    cancellationToken).ConfigureAwait(false);
            }

            if (state.Snapshot.State != ConnectionState.Disconnected)
            {
                state.Transition(ConnectionState.Disconnected);
            }
        }
        finally
        {
            hostLock.Release();
        }
    }

    public async Task<ConnectionWorkflowResult> RefreshDiscoveryAsync(
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        var hostLock = _hostLocks.GetOrAdd(hostProfileId, _ => new(1, 1));
        await hostLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = GetState(hostProfileId);
            if (state.Snapshot.State is not (
                    ConnectionState.Ready or ConnectionState.Connected))
            {
                throw new InvalidOperationException(
                    "Discovery refresh requires a connected host.");
            }

            state.Transition(ConnectionState.Discovering);
            try
            {
                var discovery = await _discovery.DiscoverAsync(
                    new(
                        hostProfileId,
                        state.Snapshot.ConnectionId.ToString(),
                        "orvian.discovery",
                        new Version(0, 1)),
                    cancellationToken).ConfigureAwait(false);
                await _discoverySnapshots.StoreAsync(discovery, cancellationToken)
                    .ConfigureAwait(false);
                state.Transition(ConnectionState.Ready);
                return new(state.Snapshot, discovery);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                state.Transition(ConnectionState.Connected);
                throw;
            }
            catch (Exception)
            {
                state.Transition(ConnectionState.Connected);
                return new(
                    state.Snapshot,
                    SafeFailureMessage:
                        "Discovery refresh failed; the previous cached snapshot remains available.");
            }
        }
        finally
        {
            hostLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _commandTransport.DisposeAsync().ConfigureAwait(false);
        foreach (var hostLock in _hostLocks.Values)
        {
            hostLock.Dispose();
        }

        _hostLocks.Clear();
    }

    private ConnectionStateMachine GetState(HostProfileId hostProfileId) =>
        _states.GetOrAdd(hostProfileId, id => new(id, _timeProvider));

    private static bool DecisionPermitsTrust(
        HostKeyDecision verification,
        HostKeyUserDecision decision) =>
        (verification == HostKeyDecision.Unknown &&
         decision == HostKeyUserDecision.TrustFirstSeen) ||
        (verification is HostKeyDecision.Changed or HostKeyDecision.AssociationChanged &&
         decision == HostKeyUserDecision.ReplaceChanged);

    private static void TransitionFailure(
        ConnectionStateMachine state,
        ConnectionAttemptResult attempt,
        int attemptCount)
    {
        var message = attempt.SafeFailureMessage ?? "The SSH connection failed.";
        switch (attempt.Status)
        {
            case ConnectionAttemptStatus.AuthenticationFailed:
                state.Transition(ConnectionState.AuthenticationFailed, message);
                break;
            case ConnectionAttemptStatus.Cancelled:
                state.Transition(ConnectionState.Cancelled);
                break;
            case ConnectionAttemptStatus.HostIdentityDecisionRequired:
                state.Transition(ConnectionState.IdentityRejected, message);
                break;
            default:
                state.Transition(
                    ConnectionState.NetworkFailed,
                    attemptCount > 1
                        ? $"{message} Connection failed after {attemptCount} attempts."
                        : message);
                break;
        }
    }

    private sealed class ConnectionRetryTracker(
        int maximumRetries,
        IConnectionTransportFactory transportFactory,
        IConnectionRetryDelay retryDelay)
    {
        private int _retriesUsed;

        public int AttemptCount { get; private set; }

        public async Task<ConnectionAttemptResult> ConnectAsync(
            ConnectionTransportRequest request,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new(ConnectionAttemptStatus.Cancelled);
                }

                AttemptCount++;
                ConnectionAttemptResult result;
                try
                {
                    result = await transportFactory.ConnectAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return new(ConnectionAttemptStatus.Cancelled);
                }

                if (result.Status != ConnectionAttemptStatus.NetworkFailed ||
                    _retriesUsed >= maximumRetries)
                {
                    return result;
                }

                _retriesUsed++;
                try
                {
                    await retryDelay.DelayAsync(_retriesUsed, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return new(ConnectionAttemptStatus.Cancelled);
                }
            }
        }
    }
}
