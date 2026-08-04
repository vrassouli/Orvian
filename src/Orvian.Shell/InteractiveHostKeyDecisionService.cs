using Avalonia.Threading;
using Orvian.Application.Connections;
using Orvian.Security;

namespace Orvian.Shell;

public delegate Task<HostKeyUserDecision> HostKeyDecisionHandler(
    HostKeyVerificationResult verification,
    CancellationToken cancellationToken);

public sealed class InteractiveHostKeyDecisionService : IHostKeyDecisionService
{
    public HostKeyDecisionHandler? DecisionHandler { get; set; }

    public async Task<HostKeyUserDecision> DecideAsync(
        HostKeyVerificationResult verification,
        CancellationToken cancellationToken = default)
    {
        var handler = DecisionHandler;
        if (handler is null)
        {
            return HostKeyUserDecision.Reject;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var decisionTask = await Dispatcher.UIThread.InvokeAsync(
            () => handler(verification, cancellationToken),
            DispatcherPriority.Normal,
            cancellationToken);
        return await decisionTask;
    }
}
