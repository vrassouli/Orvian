using Orvian.Core.Commands;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class CommandSecurityTests
{
    [Fact]
    public void Read_permission_does_not_authorize_mutation()
    {
        var policy = CreatePolicy("command.read.execute");

        var decision = policy.Authorize(
            CreateRequest() with
            {
                Kind = CommandKind.Mutation,
                RequiredPermission = "command.mutate.execute"
            });

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void Elevated_command_requires_explicit_elevation_permission()
    {
        var policy = CreatePolicy("command.read.execute");

        var decision = policy.Authorize(
            CreateRequest() with
            {
                Privilege = PrivilegeLevel.Elevated,
                Reason = "Inspect privileged status."
            });

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void Request_cannot_downgrade_required_permission()
    {
        var policy = CreatePolicy("command.read.execute", "command.mutate.execute");

        var decision = policy.Authorize(
            CreateRequest() with
            {
                Kind = CommandKind.Mutation,
                RequiredPermission = "command.read.execute"
            });

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void Structural_redaction_does_not_reveal_value_or_length()
    {
        var redacted = StructuralRedactor.RedactArguments(
        [
            new("--token"),
            new("very-long-secret-value", IsSensitive: true),
            new("x", IsSensitive: true)
        ]);

        Assert.Equal(
            ["--token", "[REDACTED]", "[REDACTED]"],
            redacted.ToArray());
    }

    [Theory]
    [InlineData(OperationRisk.Informational, PrivilegeLevel.User, ConfirmationRequirement.None)]
    [InlineData(OperationRisk.Elevated, PrivilegeLevel.User, ConfirmationRequirement.Required)]
    [InlineData(
        OperationRisk.Destructive,
        PrivilegeLevel.RootOnly,
        ConfirmationRequirement.ExplicitDestructive)]
    public void Confirmation_is_derived_from_risk_and_privilege(
        OperationRisk risk,
        PrivilegeLevel privilege,
        ConfirmationRequirement expected)
    {
        Assert.Equal(expected, ConfirmationPolicy.GetRequirement(risk, privilege));
    }

    private static PluginCommandPermissionPolicy CreatePolicy(params string[] permissions) =>
        new(new InMemoryPluginPermissionSource(
        [
            ("orvian.test", new Version(0, 1), permissions.AsEnumerable())
        ]));

    private static CommandRequest CreateRequest() =>
        new()
        {
            OperationId = Guid.NewGuid(),
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            PluginId = "orvian.test",
            PluginVersion = new Version(0, 1),
            RequiredPermission = "command.read.execute",
            Executable = "uname"
        };
}
