using Orvian.Connections;
using Orvian.Core.Hosts;
using Xunit;

namespace Orvian.Connections.Tests;

public sealed class ConnectionStateMachineTests
{
    [Fact]
    public void Successful_connection_follows_explicit_security_stages()
    {
        var machine = new ConnectionStateMachine(HostProfileId.New());
        var states = new List<ConnectionState>();
        machine.Changed += (_, snapshot) => states.Add(snapshot.State);

        machine.Transition(ConnectionState.Resolving);
        machine.Transition(ConnectionState.Connecting);
        machine.Transition(ConnectionState.Negotiating);
        machine.Transition(ConnectionState.VerifyingHostIdentity);
        machine.Transition(ConnectionState.Authenticating);
        machine.Transition(ConnectionState.Connected);
        machine.Transition(ConnectionState.Discovering);
        machine.Transition(ConnectionState.Ready);

        Assert.Equal(
        [
            ConnectionState.Resolving,
            ConnectionState.Connecting,
            ConnectionState.Negotiating,
            ConnectionState.VerifyingHostIdentity,
            ConnectionState.Authenticating,
            ConnectionState.Connected,
            ConnectionState.Discovering,
            ConnectionState.Ready
        ],
            states);
        Assert.True(machine.Snapshot.IsConnected);
    }

    [Fact]
    public void Authentication_cannot_skip_host_identity_verification()
    {
        var machine = new ConnectionStateMachine(HostProfileId.New());
        machine.Transition(ConnectionState.Resolving);
        machine.Transition(ConnectionState.Connecting);
        machine.Transition(ConnectionState.Negotiating);

        Assert.Throws<InvalidOperationException>(
            () => machine.Transition(ConnectionState.Authenticating));
    }

    [Fact]
    public void Failure_requires_safe_message_and_can_return_to_disconnected()
    {
        var machine = new ConnectionStateMachine(HostProfileId.New());
        machine.Transition(ConnectionState.Resolving);

        Assert.Throws<ArgumentException>(
            () => machine.Transition(ConnectionState.NetworkFailed));

        machine.Transition(ConnectionState.NetworkFailed, "The host could not be resolved.");
        machine.Transition(ConnectionState.Disconnected);
        Assert.Equal(ConnectionState.Disconnected, machine.Snapshot.State);
    }

    [Fact]
    public void Reconnect_creates_new_connection_identity()
    {
        var machine = new ConnectionStateMachine(HostProfileId.New());
        machine.Transition(ConnectionState.Resolving);
        var first = machine.Snapshot.ConnectionId;
        machine.Transition(ConnectionState.Cancelled);
        machine.Transition(ConnectionState.Disconnected);

        machine.Transition(ConnectionState.Resolving);

        Assert.NotEqual(first, machine.Snapshot.ConnectionId);
    }

    [Fact]
    public void Disposed_connection_has_no_outgoing_transition()
    {
        var machine = new ConnectionStateMachine(HostProfileId.New());
        machine.Transition(ConnectionState.Disposed);

        Assert.Throws<InvalidOperationException>(
            () => machine.Transition(ConnectionState.Disconnected));
    }
}
