using System.Collections.Immutable;
using Orvian.Core.Commands;

namespace Orvian.Security;

public interface IPluginPermissionSource
{
    IReadOnlySet<string> GetDeclaredPermissions(string pluginId, Version pluginVersion);
}

public sealed class InMemoryPluginPermissionSource : IPluginPermissionSource
{
    private readonly ImmutableDictionary<(string PluginId, Version Version), ImmutableHashSet<string>>
        _permissions;

    public InMemoryPluginPermissionSource(
        IEnumerable<(string PluginId, Version Version, IEnumerable<string> Permissions)> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        _permissions = plugins.ToImmutableDictionary(
            plugin => (plugin.PluginId, plugin.Version),
            plugin => plugin.Permissions.ToImmutableHashSet(StringComparer.Ordinal));
    }

    public IReadOnlySet<string> GetDeclaredPermissions(
        string pluginId,
        Version pluginVersion) =>
        _permissions.TryGetValue((pluginId, pluginVersion), out var permissions)
            ? permissions
            : ImmutableHashSet<string>.Empty;
}

public sealed class PluginCommandPermissionPolicy(
    IPluginPermissionSource permissionSource) : ICommandPermissionPolicy
{
    private const string ReadPermission = "command.read.execute";
    private const string MutationPermission = "command.mutate.execute";
    private const string ElevationPermission = "privilege.elevated.request";

    public PermissionDecision Authorize(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var permissions = permissionSource.GetDeclaredPermissions(
            request.PluginId,
            request.PluginVersion);
        var expectedCommandPermission = request.Kind == CommandKind.Mutation
            ? MutationPermission
            : ReadPermission;

        if (!string.Equals(
                request.RequiredPermission,
                expectedCommandPermission,
                StringComparison.Ordinal))
        {
            return PermissionDecision.Deny(
                "The command requested a permission inconsistent with its operation kind.");
        }

        if (!permissions.Contains(expectedCommandPermission))
        {
            return PermissionDecision.Deny(
                $"Plugin did not declare '{expectedCommandPermission}'.");
        }

        if (request.Privilege != PrivilegeLevel.User &&
            !permissions.Contains(ElevationPermission))
        {
            return PermissionDecision.Deny(
                $"Plugin did not declare '{ElevationPermission}'.");
        }

        return PermissionDecision.Allow;
    }
}

public static class ConfirmationPolicy
{
    public static ConfirmationRequirement GetRequirement(
        OperationRisk risk,
        PrivilegeLevel privilege) =>
        risk switch
        {
            OperationRisk.Destructive => ConfirmationRequirement.ExplicitDestructive,
            OperationRisk.Elevated => ConfirmationRequirement.Required,
            OperationRisk.Low when privilege != PrivilegeLevel.User =>
                ConfirmationRequirement.Required,
            OperationRisk.Low => ConfirmationRequirement.Optional,
            OperationRisk.Informational when privilege != PrivilegeLevel.User =>
                ConfirmationRequirement.Required,
            _ => ConfirmationRequirement.None
        };
}

public static class StructuralRedactor
{
    public const string Replacement = "[REDACTED]";

    public static ImmutableArray<string> RedactArguments(
        IEnumerable<CommandArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return
        [
            .. arguments.Select(argument =>
                argument.IsSensitive ? Replacement : argument.Value)
        ];
    }
}
