using System.Collections.Immutable;
using Orvian.Core.Commands;

namespace Orvian.Auditing;

public enum AuditStatus
{
    Started,
    Succeeded,
    ValidationDenied,
    PermissionDenied,
    AuditUnavailable,
    Failed,
    Cancelled,
    TimedOut,
    Interrupted,
    CompletionPersistenceFailed
}

public sealed record CommandAuditEntry
{
    public required Guid CommandId { get; init; }

    public required Guid OperationId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required string HostProfileId { get; init; }

    public required string ConnectionId { get; init; }

    public required string RemoteUserName { get; init; }

    public required string PluginId { get; init; }

    public required string PluginVersion { get; init; }

    public required string Executable { get; init; }

    public ImmutableArray<string> RedactedArguments { get; init; } = [];

    public PrivilegeLevel Privilege { get; init; }

    public InvocationSource InvocationSource { get; init; }

    public AuditStatus Status { get; init; }

    public int? ExitCode { get; init; }

    public long StandardOutputBytes { get; init; }

    public long StandardErrorBytes { get; init; }

    public bool OutputTruncated { get; init; }

    public OutputLoggingMode OutputLogging { get; init; }

    public string? RetainedStandardOutput { get; init; }

    public string? RetainedStandardError { get; init; }

    public CommandFailureClassification FailureClassification { get; init; }

    public string? SafeFailureMessage { get; init; }
}

public interface IAuditSink
{
    Task CommandStartedAsync(
        CommandAuditEntry entry,
        CancellationToken cancellationToken = default);

    Task CommandCompletedAsync(
        CommandAuditEntry entry,
        CancellationToken cancellationToken = default);
}

public enum OperationAuditStatus
{
    Started,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Denied,
    Cancelled,
    TimedOut,
    Interrupted,
    CompletionPersistenceFailed
}

public sealed record OperationAuditEntry
{
    public required Guid OperationId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required string HostProfileId { get; init; }

    public required string ConnectionId { get; init; }

    public required string RemoteUserName { get; init; }

    public required string PluginId { get; init; }

    public required string PluginVersion { get; init; }

    public required string Title { get; init; }

    public required string Purpose { get; init; }

    public required string RequiredPermission { get; init; }

    public OperationRisk Risk { get; init; }

    public PrivilegeLevel Privilege { get; init; }

    public InvocationSource InvocationSource { get; init; }

    public string? ResourceLockKey { get; init; }

    public OperationAuditStatus Status { get; init; }

    public int CommandCount { get; init; }

    public string? SafeFailureMessage { get; init; }
}

public interface IOperationAuditSink
{
    Task OperationStartedAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default);

    Task OperationCompletedAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default);
}

public sealed record AuditRetentionPolicy(
    DateTimeOffset DeleteCompletedBefore,
    DateTimeOffset? DeleteOutputBefore = null,
    int BatchSize = 500);

public sealed record AuditRetentionResult(
    int DeletedOperations,
    int DeletedCommands,
    int PurgedCommandOutputs,
    Guid CleanupOperationId);

public interface ICommandAuditDetailReader
{
    Task<CommandAuditEntry?> GetCommandAsync(
        Guid commandId,
        CancellationToken cancellationToken = default);
}

public interface IAuditRetentionService
{
    Task<AuditRetentionResult> CleanupBatchAsync(
        AuditRetentionPolicy policy,
        CancellationToken cancellationToken = default);
}

public enum HostTrustAction
{
    TrustFirstSeen,
    ReplaceChanged
}

public sealed record HostTrustAuditEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string HostProfileId,
    HostTrustAction Action,
    string HostName,
    int Port,
    string? ResolvedAddress,
    string Algorithm,
    string ObservedFingerprint,
    string? PreviousFingerprint);

public sealed record HostTrustAuditQuery(
    string? HostProfileId = null,
    HostTrustAction? Action = null,
    DateTimeOffset? OccurredAtOrAfter = null,
    DateTimeOffset? OccurredBefore = null,
    int Offset = 0,
    int Limit = 100,
    string? SearchText = null);

public interface IHostTrustAuditReader
{
    Task<IReadOnlyList<HostTrustAuditEvent>> QueryHostTrustEventsAsync(
        HostTrustAuditQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record AuditExecutionIdentity(string RemoteUserName);

public interface IAuditExecutionIdentityResolver
{
    Task<AuditExecutionIdentity?> ResolveAsync(
        string hostProfileId,
        CancellationToken cancellationToken = default);
}
