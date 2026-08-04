using System.Collections.Immutable;
using Orvian.Auditing;
using Orvian.Application.Connections;
using Orvian.Application.Discovery;
using Orvian.Application.Hosts;
using Orvian.Connections;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Orvian.Security;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class ConnectionWorkflowTests
{
    [Fact]
    public async Task First_seen_key_requires_decision_then_connects_and_discovers()
    {
        var profile = CreateProfile();
        var trustedKeys = new FakeTrustedKeys();
        var factory = new FakeFactory(
            new(
                ConnectionAttemptStatus.HostIdentityDecisionRequired,
                ObservedHostKey: Observation(profile)),
            new(ConnectionAttemptStatus.Connected, new FakeRemoteConnection()));
        var discovery = new FakeDiscovery();
        await using var workflow = CreateWorkflow(
            profile,
            trustedKeys,
            factory,
            discovery,
            HostKeyUserDecision.TrustFirstSeen);

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.Ready, result.Snapshot.State);
        Assert.NotNull(result.Discovery);
        Assert.Equal(2, factory.CallCount);
        Assert.NotNull(trustedKeys.Value);
        Assert.Equal("SHA256:first", trustedKeys.Value.Sha256Fingerprint);
        Assert.Equal(HostTrustAction.TrustFirstSeen, trustedKeys.AuditEvent!.Action);
        Assert.Null(trustedKeys.AuditEvent.PreviousFingerprint);
        Assert.Equal(1, discovery.CallCount);
    }

    [Fact]
    public async Task Trust_persistence_failure_fails_closed_before_second_connection()
    {
        var profile = CreateProfile();
        var trustedKeys = new FakeTrustedKeys { FailStore = true };
        var factory = new FakeFactory(
            new(
                ConnectionAttemptStatus.HostIdentityDecisionRequired,
                ObservedHostKey: Observation(profile)),
            new(ConnectionAttemptStatus.Connected, new FakeRemoteConnection()));
        await using var workflow = CreateWorkflow(
            profile,
            trustedKeys,
            factory,
            new FakeDiscovery(),
            HostKeyUserDecision.TrustFirstSeen);

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.IdentityRejected, result.Snapshot.State);
        Assert.Equal(1, factory.CallCount);
        Assert.Null(trustedKeys.Value);
        Assert.Equal(
            "The host trust decision could not be saved and audited.",
            result.SafeFailureMessage);
    }

    [Fact]
    public async Task Changed_key_rejection_never_reaches_second_connection_attempt()
    {
        var profile = CreateProfile();
        var trustedKeys = new FakeTrustedKeys
        {
            Value = HostKeyVerifier.Trust(
                Observation(profile) with { Sha256Fingerprint = "SHA256:old" },
                DateTimeOffset.UtcNow)
        };
        var factory = new FakeFactory(new ConnectionAttemptResult(
            ConnectionAttemptStatus.HostIdentityDecisionRequired,
            ObservedHostKey: Observation(profile)));
        await using var workflow = CreateWorkflow(
            profile,
            trustedKeys,
            factory,
            new FakeDiscovery(),
            HostKeyUserDecision.Reject);

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.IdentityRejected, result.Snapshot.State);
        Assert.Equal(1, factory.CallCount);
        Assert.Equal("SHA256:old", trustedKeys.Value.Sha256Fingerprint);
    }

    [Fact]
    public async Task Resolved_address_change_requires_explicit_replacement()
    {
        var profile = CreateProfile();
        var trustedKeys = new FakeTrustedKeys
        {
            Value = HostKeyVerifier.Trust(
                Observation(profile) with { ResolvedAddress = "192.0.2.9" },
                DateTimeOffset.UtcNow)
        };
        var factory = new FakeFactory(new ConnectionAttemptResult(
            ConnectionAttemptStatus.HostIdentityDecisionRequired,
            ObservedHostKey: Observation(profile)));
        await using var workflow = CreateWorkflow(
            profile,
            trustedKeys,
            factory,
            new FakeDiscovery(),
            HostKeyUserDecision.Reject);

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.IdentityRejected, result.Snapshot.State);
        Assert.Equal(
            HostKeyDecision.AssociationChanged,
            result.HostKeyVerification!.Decision);
        Assert.Equal(1, factory.CallCount);
        Assert.Equal("192.0.2.9", trustedKeys.Value.ResolvedAddress);
    }

    [Fact]
    public async Task Unavailable_credentials_fail_without_transport_attempt()
    {
        var profile = CreateProfile();
        var factory = new FakeFactory();
        await using var commandTransport = new ConnectionCommandTransport();
        await using var workflow = new ConnectionWorkflow(
            new FakeProfiles(profile),
            new FakeTrustedKeys(),
            new FakeCredentials(available: false),
            new FakeDecision(HostKeyUserDecision.Reject),
            factory,
            commandTransport,
            new FakeDiscovery(),
            new FakeDiscoverySnapshots());

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.AuthenticationFailed, result.Snapshot.State);
        Assert.Equal(0, factory.CallCount);
    }

    [Fact]
    public async Task FailedConnectionCanBeResetToDisconnected()
    {
        var profile = CreateProfile();
        await using var commandTransport = new ConnectionCommandTransport();
        await using var workflow = new ConnectionWorkflow(
            new FakeProfiles(profile),
            new FakeTrustedKeys(),
            new FakeCredentials(available: false),
            new FakeDecision(HostKeyUserDecision.Reject),
            new FakeFactory(),
            commandTransport,
            new FakeDiscovery(),
            new FakeDiscoverySnapshots());
        await workflow.ConnectAsync(profile.Id);

        await workflow.DisconnectAsync(profile.Id);

        Assert.Equal(ConnectionState.Disconnected, workflow.GetSnapshot(profile.Id).State);
    }

    [Fact]
    public async Task ReadyConnectionCanRefreshDiscovery()
    {
        var profile = CreateProfile();
        var discovery = new FakeDiscovery();
        await using var workflow = CreateWorkflow(
            profile,
            new FakeTrustedKeys
            {
                Value = HostKeyVerifier.Trust(
                    Observation(profile),
                    DateTimeOffset.UtcNow)
            },
            new FakeFactory(
                new ConnectionAttemptResult(
                    ConnectionAttemptStatus.Connected,
                    new FakeRemoteConnection())),
            discovery,
            HostKeyUserDecision.Reject);
        await workflow.ConnectAsync(profile.Id);

        var result = await workflow.RefreshDiscoveryAsync(profile.Id);

        Assert.Equal(ConnectionState.Ready, result.Snapshot.State);
        Assert.NotNull(result.Discovery);
        Assert.Equal(2, discovery.CallCount);
    }

    [Fact]
    public async Task Transient_network_failures_retry_within_profile_limit()
    {
        var profile = CreateProfile(maximumReconnectAttempts: 2);
        var factory = new FakeFactory(
            new(
                ConnectionAttemptStatus.NetworkFailed,
                SafeFailureMessage: "Temporary network failure."),
            new(
                ConnectionAttemptStatus.NetworkFailed,
                SafeFailureMessage: "Temporary network failure."),
            new(
                ConnectionAttemptStatus.Connected,
                new FakeRemoteConnection()));
        var delay = new FakeRetryDelay();
        await using var workflow = CreateWorkflow(
            profile,
            Trusted(profile),
            factory,
            new FakeDiscovery(),
            HostKeyUserDecision.Reject,
            delay);

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.Ready, result.Snapshot.State);
        Assert.Equal(3, result.ConnectionAttempts);
        Assert.Equal(3, factory.CallCount);
        Assert.Equal([1, 2], delay.RetryNumbers);
    }

    [Fact]
    public async Task Exhausted_network_retries_surface_bounded_attempt_count()
    {
        var profile = CreateProfile(maximumReconnectAttempts: 2);
        var factory = new FakeFactory(
            new(ConnectionAttemptStatus.NetworkFailed),
            new(ConnectionAttemptStatus.NetworkFailed),
            new(ConnectionAttemptStatus.NetworkFailed));
        var delay = new FakeRetryDelay();
        await using var workflow = CreateWorkflow(
            profile,
            Trusted(profile),
            factory,
            new FakeDiscovery(),
            HostKeyUserDecision.Reject,
            delay);

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.NetworkFailed, result.Snapshot.State);
        Assert.Equal(3, result.ConnectionAttempts);
        Assert.Contains(
            "after 3 attempts",
            result.Snapshot.SafeFailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_failure_is_never_retried()
    {
        var profile = CreateProfile(maximumReconnectAttempts: 5);
        var factory = new FakeFactory(
            new ConnectionAttemptResult(
                ConnectionAttemptStatus.AuthenticationFailed));
        var delay = new FakeRetryDelay();
        await using var workflow = CreateWorkflow(
            profile,
            Trusted(profile),
            factory,
            new FakeDiscovery(),
            HostKeyUserDecision.Reject,
            delay);

        var result = await workflow.ConnectAsync(profile.Id);

        Assert.Equal(ConnectionState.AuthenticationFailed, result.Snapshot.State);
        Assert.Equal(1, result.ConnectionAttempts);
        Assert.Empty(delay.RetryNumbers);
    }

    [Fact]
    public async Task Cancellation_during_retry_delay_stops_before_next_attempt()
    {
        var profile = CreateProfile(maximumReconnectAttempts: 5);
        var factory = new FakeFactory(
            new ConnectionAttemptResult(ConnectionAttemptStatus.NetworkFailed));
        using var cancellation = new CancellationTokenSource();
        var delay = new FakeRetryDelay(cancellation);
        await using var workflow = CreateWorkflow(
            profile,
            Trusted(profile),
            factory,
            new FakeDiscovery(),
            HostKeyUserDecision.Reject,
            delay);

        var result = await workflow.ConnectAsync(profile.Id, cancellation.Token);

        Assert.Equal(ConnectionState.Cancelled, result.Snapshot.State);
        Assert.Equal(1, result.ConnectionAttempts);
        Assert.Equal(1, factory.CallCount);
    }

    private static ConnectionWorkflow CreateWorkflow(
        HostProfile profile,
        FakeTrustedKeys trustedKeys,
        FakeFactory factory,
        FakeDiscovery discovery,
        HostKeyUserDecision decision,
        IConnectionRetryDelay? retryDelay = null) =>
        new(
            new FakeProfiles(profile),
            trustedKeys,
            new FakeCredentials(),
            new FakeDecision(decision),
            factory,
            new ConnectionCommandTransport(),
            discovery,
            new FakeDiscoverySnapshots(),
            retryDelay: retryDelay);

    private static HostProfile CreateProfile(int maximumReconnectAttempts = 2)
    {
        var now = DateTimeOffset.UtcNow;
        return Assert.IsType<HostProfile>(
            HostProfile.Create(
                HostProfileId.New(),
                "Server",
                "server.example.test",
                22,
                "operator",
                HostAuthenticationMethod.Password,
                "secret-ref",
                [],
                null,
                true,
                new HostConnectionPreferences(
                    HostConnectionPreferences.Default.ConnectionTimeout,
                    maximumReconnectAttempts),
                now,
                now).Profile);
    }

    private static HostKeyObservation Observation(HostProfile profile) =>
        new(
            profile.Id,
            profile.HostName,
            profile.Port,
            "192.0.2.10",
            "ssh-ed25519",
            "SHA256:first");

    private static FakeTrustedKeys Trusted(HostProfile profile) =>
        new()
        {
            Value = HostKeyVerifier.Trust(
                Observation(profile),
                DateTimeOffset.UtcNow)
        };

    private sealed class FakeProfiles(HostProfile profile) : IHostProfileRepository
    {
        public Task<HostProfile?> GetAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<HostProfile?>(profile);

        public Task<HostProfilePage> SearchAsync(
            HostProfileSearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AddAsync(
            HostProfile value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(
            HostProfile value,
            DateTimeOffset expectedUpdatedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTrustedKeys : ITrustedHostKeyRepository
    {
        public TrustedHostKey? Value { get; set; }

        public HostTrustAuditEvent? AuditEvent { get; private set; }

        public bool FailStore { get; init; }

        public Task<TrustedHostKey?> GetAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Value);

        public Task StoreAsync(
            TrustedHostKey trustedHostKey,
            HostTrustAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            if (FailStore)
            {
                throw new InvalidOperationException("Simulated persistence failure.");
            }

            Value = trustedHostKey;
            AuditEvent = auditEvent;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default)
        {
            Value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredentials(bool available = true)
        : IConnectionCredentialProvider
    {
        public Task<ConnectionAuthentication?> GetAuthenticationAsync(
            HostProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionAuthentication?>(
                available ? new PasswordConnectionAuthentication("test"u8) : null);
    }

    private sealed class FakeDecision(HostKeyUserDecision decision)
        : IHostKeyDecisionService
    {
        public Task<HostKeyUserDecision> DecideAsync(
            HostKeyVerificationResult verification,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(decision);
    }

    private sealed class FakeFactory(params ConnectionAttemptResult[] results)
        : IConnectionTransportFactory
    {
        private readonly Queue<ConnectionAttemptResult> _results = new(results);

        public int CallCount { get; private set; }

        public Task<ConnectionAttemptResult> ConnectAsync(
            ConnectionTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var result = _results.Dequeue();
            if (result.Connection is FakeRemoteConnection remote)
            {
                remote.ConnectionId = request.ConnectionId;
            }

            return Task.FromResult(result);
        }
    }

    private sealed class FakeRetryDelay(
        CancellationTokenSource? cancellation = null) : IConnectionRetryDelay
    {
        public List<int> RetryNumbers { get; } = [];

        public Task DelayAsync(
            int retryNumber,
            CancellationToken cancellationToken = default)
        {
            RetryNumbers.Add(retryNumber);
            if (cancellation is null)
            {
                return Task.CompletedTask;
            }

            cancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    private sealed class FakeRemoteConnection : IRemoteConnection
    {
        public ConnectionId ConnectionId { get; set; }

        public bool IsConnected { get; private set; } = true;

        public Task<TransportCommandResult> ExecuteAsync(
            Guid commandId,
            CommandRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TransportCommandResult(
                0,
                new(string.Empty, 0, false),
                new(string.Empty, 0, false)));

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDiscovery : IHostDiscoveryService
    {
        public int CallCount { get; private set; }

        public Task<DiscoverySnapshot> DiscoverAsync(
            DiscoveryContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DiscoverySnapshot(
                DiscoverySnapshotId.New(),
                context.HostProfileId,
                context.ConnectionId,
                now,
                now,
                OperatingSystemFamily.Linux,
                ImmutableDictionary<string, DiscoveryFact>.Empty,
                ImmutableHashSet<string>.Empty,
                []));
        }
    }

    private sealed class FakeDiscoverySnapshots : IDiscoverySnapshotRepository
    {
        public DiscoverySnapshot? Value { get; private set; }

        public Task StoreAsync(
            DiscoverySnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Value = snapshot;
            return Task.CompletedTask;
        }

        public Task<DiscoverySnapshot?> GetLatestAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Value);
    }
}
