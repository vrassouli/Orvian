using System.Collections.Immutable;
using Orvian.Application.Connections;
using Orvian.Application.Discovery;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Plugin.Abstractions;
using Orvian.Security;

namespace Orvian.Application.Features;

public enum PluginFeatureReadStatus
{
    Succeeded,
    Disconnected,
    Unsupported,
    CommandFailed,
    OutputTruncated,
    ParsingFailed
}

public sealed record PluginFeatureReadResult(
    PluginFeatureReadStatus Status,
    string? ProviderId = null,
    PluginFeatureReadModel? Model = null,
    CommandResult? Command = null,
    string? SafeFailureMessage = null)
{
    public bool IsSuccess => Status == PluginFeatureReadStatus.Succeeded;
}

public sealed record PluginReadActionDescriptor(
    string ActionId,
    string Title,
    string Purpose,
    IReadOnlyList<PluginMutationParameter> Parameters);

public sealed class PluginReadFeatureService(
    IPluginFeatureCatalog features,
    IDiscoverySnapshotRepository discoverySnapshots,
    IConnectionSnapshotProvider connections,
    ICommandExecutor commands,
    IOperationCoordinator? operations = null)
{
    public Task<PluginFeatureReadResult> ReadAsync(
        string featureId,
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(featureId, null, hostProfileId, null, cancellationToken);

    public Task<PluginFeatureReadResult> ExecuteActionAsync(
        string featureId,
        string actionId,
        HostProfileId hostProfileId,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(featureId, actionId, hostProfileId, parameters, cancellationToken);

    public async Task<ImmutableArray<PluginReadActionDescriptor>> GetActionsAsync(
        string featureId,
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        var discovery = await discoverySnapshots.GetLatestAsync(hostProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (discovery is null) return [];
        return [.. features.Snapshot
            .Where(item => item.Provider is IParameterizedReadFeatureProvider &&
                item.Provider.FeatureId == featureId &&
                item.Provider.RequiredCapabilities.All(discovery.Capabilities.Contains))
            .Select(item => (IParameterizedReadFeatureProvider)item.Provider)
            .GroupBy(item => item.ActionId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Priority)
                .ThenBy(item => item.ProviderId, StringComparer.Ordinal).First())
            .Select(item => new PluginReadActionDescriptor(
                item.ActionId, item.Title, item.Purpose, item.Parameters))];
    }

    private async Task<PluginFeatureReadResult> ReadCoreAsync(
        string featureId,
        string? actionId,
        HostProfileId hostProfileId,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        var connection = connections.GetSnapshot(hostProfileId);
        if (!connection.IsConnected)
        {
            return new(
                PluginFeatureReadStatus.Disconnected,
                SafeFailureMessage: "Connect the host before loading this feature.");
        }

        var discovery = await discoverySnapshots
            .GetLatestAsync(hostProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (discovery is null)
        {
            return new(
                PluginFeatureReadStatus.Unsupported,
                SafeFailureMessage:
                    "Host capabilities are unavailable until discovery completes.");
        }

        var registered = features.Snapshot
            .Where(item =>
                string.Equals(
                    item.Provider.FeatureId,
                    featureId,
                    StringComparison.Ordinal) &&
                (actionId is null
                    ? item.Provider is not IParameterizedReadFeatureProvider
                    : item.Provider is IParameterizedReadFeatureProvider action &&
                      string.Equals(action.ActionId, actionId, StringComparison.Ordinal)) &&
                item.Provider.RequiredCapabilities.All(
                    discovery.Capabilities.Contains))
            .OrderByDescending(item => item.Provider.Priority)
            .ThenBy(item => item.Provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (registered is null)
        {
            return new(
                PluginFeatureReadStatus.Unsupported,
                SafeFailureMessage:
                    "No compatible provider is available for this host.");
        }

        ImmutableArray<PluginReadRequest> providerRequests;
        try
        {
            providerRequests = registered.Provider switch
            {
                IParameterizedReadFeatureProvider action =>
                    [action.CreateRequest(parameters ??
                        throw new ArgumentException("Read action parameters are required."))],
                IMultiCommandReadFeatureProvider multi => multi.CreateRequests(),
                _ => [registered.Provider.CreateRequest()]
            };
            if (providerRequests.IsDefaultOrEmpty ||
                providerRequests.Length > 8)
            {
                throw new InvalidOperationException(
                    "Read providers must create between one and eight commands.");
            }
        }
        catch (Exception)
        {
            return new(
                PluginFeatureReadStatus.ParsingFailed,
                registered.Provider.ProviderId,
                SafeFailureMessage:
                    "The feature provider could not construct its read request.");
        }

        var operationId = Guid.NewGuid();
        var commandRequests = providerRequests
            .Select(providerRequest => new CommandRequest
            {
                OperationId = operationId,
                HostProfileId = hostProfileId.ToString(),
                ConnectionId = connection.ConnectionId.ToString(),
                PluginId = registered.PluginId,
                PluginVersion = registered.PluginVersion,
                RequiredPermission = PluginPermissions.CommandReadExecute,
                Executable = providerRequest.Executable,
                Arguments =
                [
                    .. providerRequest.Arguments.Select(argument =>
                        new CommandArgument(argument.Value, argument.IsSensitive))
                ],
                Kind = CommandKind.ReadOnly,
                InvocationSource = InvocationSource.UserInterface,
                OutputLogging = OutputLoggingMode.Redacted,
                Timeout = providerRequest.Timeout
            })
            .ToImmutableArray();
        ImmutableArray<CommandResult> commandResults;
        if (operations is not null)
        {
            var operation = await operations.ExecuteAsync(
                new(
                    new OperationIntent
                    {
                        OperationId = operationId,
                        HostProfileId = hostProfileId.ToString(),
                        ConnectionId = connection.ConnectionId.ToString(),
                        PluginId = registered.PluginId,
                        PluginVersion = registered.PluginVersion,
                        Title = $"Read {registered.Provider.FeatureId}",
                        Purpose = "Load current remote feature state.",
                        RequiredPermission = PluginPermissions.CommandReadExecute,
                        Risk = OperationRisk.Informational,
                        Privilege = PrivilegeLevel.User,
                        InvocationSource = InvocationSource.UserInterface
                    },
                    commandRequests),
                cancellationToken).ConfigureAwait(false);
            commandResults = operation.Commands;
        }
        else
        {
            var builder = ImmutableArray.CreateBuilder<CommandResult>(
                commandRequests.Length);
            foreach (var commandRequest in commandRequests)
            {
                var command = await commands.ExecuteAsync(
                    commandRequest,
                    cancellationToken).ConfigureAwait(false);
                builder.Add(command);
                if (!command.IsSuccess)
                {
                    break;
                }
            }

            commandResults = builder.ToImmutable();
        }

        var failedCommand = commandResults.FirstOrDefault(command => !command.IsSuccess);
        if (failedCommand is not null || commandResults.Length != commandRequests.Length)
        {
            var command = failedCommand ?? commandResults.LastOrDefault();
            return new(
                PluginFeatureReadStatus.CommandFailed,
                registered.Provider.ProviderId,
                Command: command,
                SafeFailureMessage:
                    command?.SafeFailureMessage ??
                    "The feature operation could not execute all commands.");
        }

        if (commandResults.Any(command =>
                command.StandardOutput.IsTruncated ||
                command.StandardError.IsTruncated))
        {
            return new(
                PluginFeatureReadStatus.OutputTruncated,
                registered.Provider.ProviderId,
                Command: commandResults[^1],
                SafeFailureMessage:
                    "The feature output exceeded its safety limit and was not parsed.");
        }

        PluginFeatureParseResult parsed;
        try
        {
            var outputs = commandResults
                .Select(command => new PluginCommandOutput(
                    command.ExitCode ?? -1,
                    command.StandardOutput.Content,
                    command.StandardError.Content,
                    IsTruncated: false))
                .ToImmutableArray();
            parsed = registered.Provider is IMultiCommandReadFeatureProvider multi
                ? multi.Parse(outputs)
                : registered.Provider.Parse(outputs[0]);
        }
        catch (Exception)
        {
            parsed = PluginFeatureParseResult.Failure(
                "The feature returned malformed output.");
        }

        return parsed.IsSuccess
            ? new(
                PluginFeatureReadStatus.Succeeded,
                registered.Provider.ProviderId,
                parsed.Model,
                commandResults[^1])
            : new(
                PluginFeatureReadStatus.ParsingFailed,
                registered.Provider.ProviderId,
                Command: commandResults[^1],
                SafeFailureMessage:
                    parsed.SafeFailureMessage ??
                    "The feature returned malformed output.");
    }
}

public sealed class PluginCatalogPermissionSource(
    IPluginFeatureCatalog features,
    IPluginPermissionSource builtInPermissions) : IPluginPermissionSource
{
    public IReadOnlySet<string> GetDeclaredPermissions(
        string pluginId,
        Version pluginVersion)
    {
        var registered = features.Snapshot.FirstOrDefault(item =>
            string.Equals(item.PluginId, pluginId, StringComparison.Ordinal) &&
            item.PluginVersion == pluginVersion);
        if (registered is not null)
        {
            return registered.Permissions;
        }

        var mutation = features.MutationSnapshot.FirstOrDefault(item =>
            string.Equals(item.PluginId, pluginId, StringComparison.Ordinal) &&
            item.PluginVersion == pluginVersion);
        return mutation?.Permissions ??
            builtInPermissions.GetDeclaredPermissions(pluginId, pluginVersion);
    }
}
