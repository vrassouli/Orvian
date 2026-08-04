using System.Collections.Immutable;

namespace Orvian.Core.Commands;

public enum OperationStatus
{
    Succeeded,
    PartiallySucceeded,
    Failed,
    Denied,
    Cancelled,
    TimedOut,
    Interrupted,
    AuditCompletionFailed
}

public sealed record OperationIntent
{
    public required Guid OperationId { get; init; }

    public required string HostProfileId { get; init; }

    public required string ConnectionId { get; init; }

    public required string PluginId { get; init; }

    public required Version PluginVersion { get; init; }

    public required string Title { get; init; }

    public required string Purpose { get; init; }

    public required string RequiredPermission { get; init; }

    public OperationRisk Risk { get; init; } = OperationRisk.Informational;

    public PrivilegeLevel Privilege { get; init; } = PrivilegeLevel.User;

    public InvocationSource InvocationSource { get; init; } = InvocationSource.UserInterface;

    public string? ResourceLockKey { get; init; }
}

public sealed record OperationRequest(
    OperationIntent Intent,
    ImmutableArray<CommandRequest> Commands);

public sealed record OperationConfirmationRequest(
    OperationIntent Intent,
    ConfirmationRequirement Requirement,
    ImmutableArray<string> RedactedCommands);

public interface IOperationConfirmationService
{
    Task<bool> ConfirmAsync(
        OperationConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OperationResult(
    Guid OperationId,
    OperationStatus Status,
    ImmutableArray<CommandResult> Commands)
{
    public bool IsSuccess => Status == OperationStatus.Succeeded;
}

public interface IOperationCoordinator
{
    Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default);
}
