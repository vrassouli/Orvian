using Orvian.Core.Commands;
using Xunit;

namespace Orvian.Core.Tests;

public sealed class CommandRequestTests
{
    [Fact]
    public void Defaults_are_safe_and_explicit()
    {
        var request = new CommandRequest
        {
            OperationId = Guid.NewGuid(),
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            PluginId = "orvian.test",
            PluginVersion = new Version(0, 1),
            RequiredPermission = "command.read.execute",
            Executable = "uname"
        };

        Assert.Equal(CommandKind.ReadOnly, request.Kind);
        Assert.Equal(PrivilegeLevel.User, request.Privilege);
        Assert.Equal(InvocationSource.UserInterface, request.InvocationSource);
        Assert.Equal(OutputLoggingMode.Full, request.OutputLogging);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
        Assert.Empty(request.Arguments);
    }
}
