using Orvian.Application.Discovery;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Security;

namespace Orvian.Application.Execution;

public sealed class CapabilityPrivilegeCommandPreparer(
    IDiscoverySnapshotRepository discoverySnapshots,
    IPrivilegeCredentialCache credentials) : IPrivilegeCommandPreparer
{
    public async ValueTask<PrivilegePreparationResult> PrepareAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Privilege == PrivilegeLevel.User)
        {
            return PrivilegePreparationResult.Allow(request);
        }

        if (!Guid.TryParse(request.HostProfileId, out var hostIdValue))
        {
            return PrivilegePreparationResult.Deny(
                "Privilege preparation requires a valid host identity.");
        }

        var hostId = new HostProfileId(hostIdValue);
        var discovery = await discoverySnapshots
            .GetLatestAsync(hostId, cancellationToken)
            .ConfigureAwait(false);
        if (discovery is null)
        {
            return PrivilegePreparationResult.Deny(
                "Privilege capabilities are unavailable until discovery completes.");
        }

        if (discovery.Capabilities.Contains("privilege.root"))
        {
            return PrivilegePreparationResult.Allow(request);
        }

        if (discovery.Capabilities.Contains("privilege.sudo"))
        {
            using var credential = credentials.Retrieve(hostId);
            if (credential is null)
            {
                return PrivilegePreparationResult.Allow(
                    Wrap(request, "sudo", ["-n", "--"]));
            }

            var input = new char[credential.Memory.Length + 1];
            credential.Memory.Span.CopyTo(input);
            input[^1] = '\n';
            try
            {
                return PrivilegePreparationResult.Allow(
                    Wrap(
                        request,
                        "sudo",
                        ["-S", "-p", string.Empty, "--"],
                        new SensitiveStandardInput(input)));
            }
            finally
            {
                Array.Clear(input);
            }
        }

        if (discovery.Capabilities.Contains("privilege.doas"))
        {
            return PrivilegePreparationResult.Allow(
                Wrap(request, "doas", ["-n"]));
        }

        return PrivilegePreparationResult.Deny(
            "No supported privilege provider is available for this host.");
    }

    private static CommandRequest Wrap(
        CommandRequest request,
        string executable,
        IEnumerable<string> prefixArguments,
        SensitiveStandardInput? standardInput = null) =>
        request with
        {
            Executable = executable,
            Arguments =
            [
                .. prefixArguments.Select(value => new CommandArgument(value)),
                new(request.Executable),
                .. request.Arguments
            ],
            StandardInput = standardInput
        };
}
