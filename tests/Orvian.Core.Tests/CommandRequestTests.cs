using Orvian.Core.Commands;

namespace Orvian.Core.Tests;

public sealed class CommandRequestTests
{
    [Fact]
    public void Defaults_are_safe_and_explicit()
    {
        var request = new CommandRequest
        {
            PluginId = "orvian.test",
            OperationId = "test.operation",
            Executable = "uname"
        };

        Assert.Equal(PrivilegeLevel.User, request.Privilege);
        Assert.Equal(InvocationSource.UserInterface, request.InvocationSource);
        Assert.Equal(OutputLoggingMode.Full, request.OutputLogging);
        Assert.Empty(request.Arguments);
        Assert.Empty(request.SensitiveArgumentIndexes);
    }
}
