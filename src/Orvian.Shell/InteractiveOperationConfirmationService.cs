using Avalonia.Threading;
using Orvian.Core.Commands;

namespace Orvian.Shell;

public delegate Task<bool> OperationConfirmationHandler(
    OperationConfirmationRequest request,
    CancellationToken cancellationToken);

public sealed class InteractiveOperationConfirmationService
    : IOperationConfirmationService
{
    public OperationConfirmationHandler? ConfirmationHandler { get; set; }

    public async Task<bool> ConfirmAsync(
        OperationConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        var handler = ConfirmationHandler;
        if (handler is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var confirmationTask = await Dispatcher.UIThread.InvokeAsync(
            () => handler(request, cancellationToken),
            DispatcherPriority.Normal,
            cancellationToken);
        return await confirmationTask;
    }
}
