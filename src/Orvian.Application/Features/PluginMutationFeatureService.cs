using System.Collections.Immutable;
using Orvian.Application.Connections;
using Orvian.Application.Discovery;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Plugin.Abstractions;

namespace Orvian.Application.Features;

public sealed record PluginMutationDescriptor(
    string FeatureId,
    string MutationId,
    string Title,
    string Purpose,
    string ParameterName,
    string ParameterLabel,
    IReadOnlyList<PluginMutationParameter> Parameters,
    PluginMutationRisk Risk,
    bool RequiresElevation,
    bool DisplaysOutput = false);

public sealed record PluginMutationResult(
    bool IsSuccess,
    OperationResult? Operation = null,
    string? SafeFailureMessage = null);

public sealed class PluginMutationFeatureService(
    IPluginFeatureCatalog features,
    IDiscoverySnapshotRepository discoverySnapshots,
    IConnectionSnapshotProvider connections,
    IOperationCoordinator operations)
{
    public async Task<ImmutableArray<PluginMutationDescriptor>> GetAvailableAsync(
        string featureId,
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        var discovery = await discoverySnapshots
            .GetLatestAsync(hostProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (discovery is null)
        {
            return [];
        }

        return
        [
            .. SelectCompatible(featureId, discovery.Capabilities)
                .GroupBy(
                    item => item.Provider.MutationId,
                    StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(item => item.Provider.Priority)
                    .ThenBy(item => item.Provider.ProviderId, StringComparer.Ordinal)
                    .First())
                .OrderBy(item => item.Provider.Title, StringComparer.Ordinal)
                .Select(item => new PluginMutationDescriptor(
                    item.Provider.FeatureId,
                    item.Provider.MutationId,
                    item.Provider.Title,
                    item.Provider.Purpose,
                    item.Provider.ParameterName,
                    item.Provider.ParameterLabel,
                    item.Provider.Parameters,
                    item.Provider.Risk,
                    item.Provider.RequiresElevation,
                    item.Provider.DisplaysOutput))
        ];
    }

    public async Task<PluginMutationResult> ExecuteAsync(
        string featureId,
        string mutationId,
        HostProfileId hostProfileId,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);
        ArgumentNullException.ThrowIfNull(parameters);
        var connection = connections.GetSnapshot(hostProfileId);
        if (!connection.IsConnected)
        {
            return new(false, SafeFailureMessage:
                "Connect the host before applying this change.");
        }

        var discovery = await discoverySnapshots
            .GetLatestAsync(hostProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (discovery is null)
        {
            return new(false, SafeFailureMessage:
                "Host capabilities are unavailable until discovery completes.");
        }

        var registered = SelectCompatible(featureId, discovery.Capabilities)
            .Where(item => string.Equals(
                item.Provider.MutationId,
                mutationId,
                StringComparison.Ordinal))
            .OrderByDescending(item => item.Provider.Priority)
            .ThenBy(item => item.Provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (registered is null)
        {
            return new(false, SafeFailureMessage:
                "No compatible mutation provider is available for this host.");
        }

        ImmutableArray<PluginMutationRequest> providerRequests;
        try
        {
            providerRequests = registered.Provider is IMultiCommandMutationFeatureProvider multi
                ? multi.CreateRequests(parameters)
                : [registered.Provider.CreateRequest(parameters)];
            if (providerRequests.IsDefaultOrEmpty || providerRequests.Length > 8)
            {
                throw new InvalidOperationException(
                    "Mutation providers must create between one and eight commands.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            return new(false, SafeFailureMessage:
                "The requested change contains invalid input.");
        }
        catch (Exception)
        {
            return new(false, SafeFailureMessage:
                "The feature provider could not construct the change.");
        }

        if (providerRequests.Any(request => request.Risk != registered.Provider.Risk))
        {
            return new(false, SafeFailureMessage:
                "The feature provider returned inconsistent risk metadata.");
        }

        var operationId = Guid.NewGuid();
        var privilege = registered.Provider.RequiresElevation
            ? PrivilegeLevel.Elevated
            : PrivilegeLevel.User;
        var intent = new OperationIntent
        {
            OperationId = operationId,
            HostProfileId = hostProfileId.ToString(),
            ConnectionId = connection.ConnectionId.ToString(),
            PluginId = registered.PluginId,
            PluginVersion = registered.PluginVersion,
            Title = providerRequests[0].Title,
            Purpose = providerRequests[0].Purpose,
            RequiredPermission = PluginPermissions.CommandMutateExecute,
            Risk = MapRisk(registered.Provider.Risk),
            Privilege = privilege,
            InvocationSource = InvocationSource.UserInterface,
            ResourceLockKey = $"{hostProfileId}:{featureId}"
        };
        var commands = providerRequests.Select(providerRequest => new CommandRequest
            {
                OperationId = operationId,
                HostProfileId = intent.HostProfileId,
                ConnectionId = intent.ConnectionId,
                PluginId = intent.PluginId,
                PluginVersion = intent.PluginVersion,
                RequiredPermission = intent.RequiredPermission,
                Executable = providerRequest.Executable,
                Arguments =
                [
                    .. providerRequest.Arguments.Select(argument =>
                        new CommandArgument(argument.Value, argument.IsSensitive))
                ],
                Kind = CommandKind.Mutation,
                Privilege = privilege,
                InvocationSource = intent.InvocationSource,
                OutputLogging = OutputLoggingMode.Redacted,
                Timeout = providerRequest.Timeout,
                Reason = providerRequest.Purpose
            }).ToImmutableArray();
        var result = await operations.ExecuteAsync(
            new(intent, commands),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new(true, result)
            : new(
                false,
                result,
                result.Status == OperationStatus.Denied
                    ? "The change was not authorized."
                    : result.Commands.LastOrDefault()?.SafeFailureMessage ??
                        "The remote change failed.");
    }

    private IEnumerable<RegisteredMutationFeatureProvider> SelectCompatible(
        string featureId,
        IReadOnlySet<string> capabilities) =>
        features.MutationSnapshot.Where(item =>
            string.Equals(
                item.Provider.FeatureId,
                featureId,
                StringComparison.Ordinal) &&
            item.Provider.RequiredCapabilities.All(capabilities.Contains));

    private static OperationRisk MapRisk(PluginMutationRisk risk) =>
        risk switch
        {
            PluginMutationRisk.Low => OperationRisk.Low,
            PluginMutationRisk.Elevated => OperationRisk.Elevated,
            PluginMutationRisk.Destructive => OperationRisk.Destructive,
            _ => OperationRisk.Elevated
        };
}
